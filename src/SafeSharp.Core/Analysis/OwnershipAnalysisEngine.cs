using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;

namespace SafeSharp.Core;

/// <summary>
/// Reentrant ownership and loan analysis over a finite control-flow graph. The forward solver reaches a
/// fixed point before reporting diagnostics; a separate backward solver determines non-lexical loan lifetimes.
/// </summary>
/// <remarks>
/// This is an abstract interpretation of explicit frontend effects, not Rust's type system or a proof of C# memory safety.
/// Compiler-independent IR makes alternate frontends, diagnostic hosts and exporters reusable.
/// Rust design reference: https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_borrowck/src/lib.rs
/// </remarks>
public sealed class OwnershipAnalysisEngine
{
    public AnalysisResult Analyze(AnalysisGraph graph, AnalysisOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (graph is null) throw new ArgumentNullException(nameof(graph));
        options ??= new AnalysisOptions();
        cancellationToken.ThrowIfCancellationRequested();
        var allBlocks = graph.Blocks.ToDictionary(block => block.Id);
        var reachable = new HashSet<int>();
        var pending = new Stack<int>();
        pending.Push(graph.EntryId);
        while (pending.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = pending.Pop();
            if (!reachable.Add(id)) continue;
            foreach (var successor in allBlocks[id].Successors) pending.Push(successor);
        }
        var blocks = allBlocks.Where(pair => reachable.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value);
        var predecessors = blocks.Keys.ToDictionary(id => id, _ => new List<int>());
        foreach (var block in blocks.Values)
        foreach (var successor in block.Successors) predecessors[successor].Add(block.Id);

        var resourceCount = blocks.Values.Sum(block => block.Instructions.Sum(instruction => instruction.Kind switch
        {
            InstructionKind.BeginAsyncComposition => 3, // proxy, captured inputs, and result aggregate
            InstructionKind.BeginAsyncFactory => instruction.AsyncResultDepth + 1, // carrier layers and possible terminal result
            InstructionKind.Allocate or InstructionKind.BeginAsyncDispose or InstructionKind.ReceiveAsyncResult or InstructionKind.BindAsyncResult or InstructionKind.TrackValueTask or InstructionKind.CreateAggregate or InstructionKind.ReadAggregateElement => 1,
            _ => 0
        }));
        if (resourceCount > options.MaxTrackedResources) return BudgetResult(graph, "tracked-resource", 0);
        var liveness = PlaceLiveness.Compute(blocks, predecessors, options.MaxIterations, cancellationToken);
        if (liveness.BudgetExceeded) return BudgetResult(graph, "backward liveness", 0);

        var inputs = new Dictionary<int, AbstractState>();
        var outputs = new Dictionary<int, AbstractState>();
        var queue = new Queue<int>();
        var queued = new HashSet<int>();
        queue.Enqueue(graph.EntryId);
        queued.Add(graph.EntryId);
        var iterations = 0;
        var transfer = new OwnershipTransfer(options);
        while (queue.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++iterations > options.MaxIterations) return BudgetResult(graph, "ownership fixed-point", iterations - 1);
            var id = queue.Dequeue();
            queued.Remove(id);
            var incoming = predecessors[id].Where(outputs.ContainsKey).Select(predecessor => outputs[predecessor]).ToList();
            if (id == graph.EntryId) incoming.Add(new AbstractState());
            var input = AbstractState.Join(incoming);
            if (input.ReferenceStorage.BudgetExceeded) return BudgetResult(graph, "reference-storage", iterations);
            if (input.Resources.Count > options.MaxTrackedResources) return BudgetResult(graph, "tracked-resource", iterations);
            input.ExpireLoans(liveness.LiveIn[id]);
            inputs[id] = input;
            var output = input.Clone();
            var block = blocks[id];
            for (var index = 0; index < block.Instructions.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                output.ExpireLoans(liveness.Before[id][index]);
                transfer.Apply(output, block.Instructions[index], id, index, liveness.After[id][index], null);
                output.ExpireLoans(liveness.After[id][index]);
                if (output.ReferenceStorage.BudgetExceeded) return BudgetResult(graph, "reference-storage", iterations);
                if (output.Resources.Count > options.MaxTrackedResources) return BudgetResult(graph, "tracked-resource", iterations);
            }
            if (outputs.TryGetValue(id, out var oldOutput) && oldOutput.Equals(output)) continue;
            outputs[id] = output;
            foreach (var successor in block.Successors)
                if (queued.Add(successor)) queue.Enqueue(successor);
        }

        var collector = new IssueCollector(options.ReportPossibleIssues);
        var snapshots = ImmutableArray.CreateBuilder<BlockAnalysisResult>();
        foreach (var block in graph.Blocks.Where(block => inputs.ContainsKey(block.Id) && !inputs[block.Id].IsUnreachable))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = inputs[block.Id].Clone();
            var entry = options.CaptureSnapshots ? state.Snapshot() : null;
            var steps = ImmutableArray.CreateBuilder<InstructionSnapshot>();
            for (var index = 0; index < block.Instructions.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                state.ExpireLoans(liveness.Before[block.Id][index]);
                var before = options.CaptureSnapshots ? state.Snapshot() : null;
                transfer.Apply(state, block.Instructions[index], block.Id, index, liveness.After[block.Id][index], collector);
                state.ExpireLoans(liveness.After[block.Id][index]);
                if (state.ReferenceStorage.BudgetExceeded) return BudgetResult(graph, "reference-storage", iterations);
                if (state.Resources.Count > options.MaxTrackedResources) return BudgetResult(graph, "tracked-resource", iterations);
                if (options.CaptureSnapshots)
                    steps.Add(new InstructionSnapshot(index, block.Instructions[index], before!, state.Snapshot(),
                        Sorted(liveness.Before[block.Id][index]), Sorted(liveness.After[block.Id][index])));
            }
            if (options.CaptureSnapshots)
                snapshots.Add(new BlockAnalysisResult(block.Id, entry!, state.Snapshot(), steps.ToImmutable(),
                    Sorted(liveness.LiveIn[block.Id]), Sorted(liveness.LiveOut[block.Id])));
        }

        var exits = blocks.Values.Where(block => block.Successors.IsEmpty && outputs.ContainsKey(block.Id)).ToArray();
        if (exits.Length != 0)
        {
            var exitState = AbstractState.Join(exits.Select(block => outputs[block.Id]));
            if (exitState.ReferenceStorage.BudgetExceeded) return BudgetResult(graph, "reference-storage", iterations);
            if (exitState.Resources.Count > options.MaxTrackedResources) return BudgetResult(graph, "tracked-resource", iterations);
            foreach (var resource in exitState.Resources.Values.OrderBy(resource => resource.Id, StringComparer.Ordinal))
            {
                if (resource.Ownership != ResourceOwnership.Owned || (resource.Flags & InstructionFlags.SuppressLeak) != 0
                    || !resource.HasOutstandingObligation) continue;
                var factory = resource.Kind == ResourceKind.AsyncFactoryCarrier;
                var certainty = !factory && resource.Status == resource.OutstandingStatus ? IssueCertainty.Definite : IssueCertainty.Possible;
                var completion = resource.Kind == ResourceKind.AsyncDisposalCompletion;
                var message = factory ? "An asynchronous factory may produce an owned result, but its carrier is not received or transferred before the method exits."
                    : completion
                    ? certainty == IssueCertainty.Definite
                        ? "Asynchronous disposal completion is not observed or transferred before the method exits."
                        : "Asynchronous disposal completion may reach a method exit without being observed or transferred."
                    : certainty == IssueCertainty.Definite
                        ? "Owned resource is not disposed or transferred before the method exits."
                        : "Owned resource may reach a method exit without disposal or ownership transfer.";
                collector.Add(completion ? "SS0017" : "SS0003", message, resource.Origin, certainty,
                    resource.Id, OwnershipTransfer.Evidence(resource).Concat(new[] { new AnalysisEvidence("exit", completion
                        ? "A reachable exit retains a pending disposal completion." : factory
                            ? "A reachable exit abandons the obligation to receive or transfer the factory's possible owned result."
                            : "A reachable exit retains ownership.", graph.Site) }));
            }
        }
        return new AnalysisResult(graph, collector.ToImmutable(), snapshots.ToImmutable(), iterations, false);
    }

    private static ImmutableArray<string> Sorted(IEnumerable<string> values) => values.OrderBy(value => value, StringComparer.Ordinal).ToImmutableArray();

    private static AnalysisResult BudgetResult(AnalysisGraph graph, string phase, int iterations)
    {
        var issue = new AnalysisIssue("SS0015", "Analysis stopped at the configured " + phase + " budget; ownership safety has not been established.",
            graph.Site, IssueCertainty.Definite, evidence: new[] { new AnalysisEvidence("budget", "Increase the analysis budget or reduce method complexity.", graph.Site) });
        return new AnalysisResult(graph, ImmutableArray.Create(issue), ImmutableArray<BlockAnalysisResult>.Empty, iterations, true);
    }
}

internal sealed class IssueCollector
{
    private readonly bool reportPossible;
    private readonly Dictionary<string, AnalysisIssue> issues = new(StringComparer.Ordinal);
    private readonly HashSet<string> safeContexts = new(StringComparer.Ordinal);
    private HashSet<string>? currentChecks;
    private HashSet<string>? currentIssues;
    internal IssueCollector(bool reportPossible) => this.reportPossible = reportPossible;

    internal void BeginInstruction(FlowInstruction instruction, AbstractState state, IEnumerable<ReferenceFact> references)
    {
        var ids = instruction.Kind switch
        {
            InstructionKind.Allocate or InstructionKind.BeginAsyncFactory or InstructionKind.CreateAggregate => new[] { "SS0011", "SS0017" },
            InstructionKind.Alias or InstructionKind.MergeAlias or InstructionKind.PreserveAwaitable => new[] { "SS0004", "SS0011", "SS0017" },
            InstructionKind.Move => new[] { "SS0001", "SS0004", "SS0005", "SS0008", "SS0011", "SS0012", "SS0017", "SS0048" },
            InstructionKind.BorrowShared or InstructionKind.BorrowExclusive or InstructionKind.ReserveExclusiveBorrow => new[] { "SS0001", "SS0004", "SS0006", "SS0011", "SS0012", "SS0017" },
            InstructionKind.ActivateBorrow => new[] { "SS0001", "SS0004", "SS0006", "SS0012" },
            InstructionKind.Read when (instruction.Flags & InstructionFlags.ValidateReturnedResult) != 0 => new[] { "SS0001", "SS0004", "SS0012", "SS0013", "SS0048" },
            InstructionKind.Read => new[] { "SS0001", "SS0004", "SS0012", "SS0013" },
            InstructionKind.Write => new[] { "SS0001", "SS0004", "SS0007", "SS0012" },
            InstructionKind.Dispose => new[] { "SS0002", "SS0004", "SS0009", "SS0012", "SS0014" },
            InstructionKind.BeginAsyncDispose when (instruction.Flags & InstructionFlags.DisposalAlreadyInvoked) != 0 => new[] { "SS0011", "SS0017" },
            InstructionKind.BeginAsyncDispose => new[] { "SS0002", "SS0004", "SS0009", "SS0011", "SS0012", "SS0014", "SS0017" },
            InstructionKind.CompleteAsyncDispose => new[] { "SS0004" },
            InstructionKind.ObserveAsyncFactory or InstructionKind.ObserveAsyncComposition => new[] { "SS0004" },
            InstructionKind.BeginAsyncComposition or InstructionKind.CompleteAsyncComposition => new[] { "SS0004", "SS0011", "SS0017" },
            InstructionKind.ReadAggregateElement => new[] { "SS0001", "SS0004", "SS0011", "SS0012", "SS0017" },
            InstructionKind.SetAggregateElement => new[] { "SS0001", "SS0004", "SS0005", "SS0008", "SS0011", "SS0012", "SS0017", "SS0048" },
            InstructionKind.ReceiveAsyncResult => new[] { "SS0004", "SS0011", "SS0017" },
            InstructionKind.BindAsyncResult => new[] { "SS0004", "SS0011", "SS0017" },
            InstructionKind.Escape or InstructionKind.Return => new[] { "SS0001", "SS0004", "SS0005", "SS0008", "SS0012", "SS0048" },
            InstructionKind.ConsumeValueTask => new[] { "SS0048" },
            InstructionKind.Suspend => new[] { "SS0010", "SS0016" },
            InstructionKind.Forget => new[] { "SS0011", "SS0017" },
            _ => Array.Empty<string>()
        };
        currentChecks = new HashSet<string>(StringComparer.Ordinal);
        currentIssues = new HashSet<string>(StringComparer.Ordinal);
        if (ids.Length == 0) return;
        var checkedReferences = references.Concat(state.Resolve(instruction.Target));
        if (instruction.Kind is InstructionKind.Move or InstructionKind.Escape or InstructionKind.Return or InstructionKind.ReceiveAsyncResult or
            InstructionKind.ReadAggregateElement or InstructionKind.SetAggregateElement or InstructionKind.BeginAsyncComposition or InstructionKind.CompleteAsyncComposition ||
            instruction.Kind == InstructionKind.Read && (instruction.Flags & InstructionFlags.ValidateReturnedResult) != 0)
            checkedReferences = OwnershipTransfer.IncludingFactoryResults(state, checkedReferences);
        var resources = instruction.Kind == InstructionKind.Suspend ? state.Resources.Keys.AsEnumerable()
            : checkedReferences.Select(reference => reference.ResourceId);
        foreach (var resource in resources.Distinct(StringComparer.Ordinal))
        foreach (var id in ids) currentChecks.Add(Key(id, instruction.Site, resource));
        if (ids.Contains("SS0048"))
            foreach (var id in state.ValueTasks.Ids(instruction.Source, state)) currentChecks.Add(Key("SS0048", instruction.Site, id));
    }

    internal void EndInstruction()
    {
        if (currentChecks is not null && currentIssues is not null)
            foreach (var key in currentChecks)
                if (!currentIssues.Contains(key)) safeContexts.Add(key);
        currentChecks = null;
        currentIssues = null;
    }

    internal void Add(string id, string message, SourceSite site, IssueCertainty certainty, string? resourceId,
        IEnumerable<AnalysisEvidence> evidence)
    {
        var key = Key(id, site, resourceId);
        currentIssues?.Add(key);
        if (!reportPossible && certainty == IssueCertainty.Possible) return;
        var facts = evidence.GroupBy(EvidenceFacts.Key, StringComparer.Ordinal).Select(group => group.First())
            .OrderBy(item => item.Site.Path, StringComparer.Ordinal).ThenBy(item => item.Site.Start).ThenBy(item => item.Kind, StringComparer.Ordinal).ToImmutableArray();
        var related = facts.Select(item => item.Site).Where(location => !location.Equals(site)).Distinct().ToImmutableArray();
        var issue = new AnalysisIssue(id, message, site, certainty, resourceId, related, facts);
        if (issues.TryGetValue(key, out var previous))
        {
            // Contextual finally clones can map to one source span; retain the more conservative classification.
            var mergedCertainty = previous.Certainty == IssueCertainty.Possible || certainty == IssueCertainty.Possible ? IssueCertainty.Possible : IssueCertainty.Definite;
            var mergedEvidence = previous.Evidence.Concat(issue.Evidence).GroupBy(EvidenceFacts.Key, StringComparer.Ordinal).Select(group => group.First()).ToImmutableArray();
            issues[key] = new AnalysisIssue(id, previous.Message, site, mergedCertainty, resourceId,
                previous.RelatedLocations.Concat(issue.RelatedLocations).Distinct(), mergedEvidence);
        }
        else issues.Add(key, issue);
    }

    private static string Key(string id, SourceSite site, string? resourceId) => id + ":" + site.Path + ":" + site.Start + ":" + site.Length + ":" + resourceId;

    internal ImmutableArray<AnalysisIssue> ToImmutable() => issues.Values.Select(issue => issue.Certainty == IssueCertainty.Definite && safeContexts.Contains(Key(issue.Id, issue.Site, issue.ResourceId))
            ? new AnalysisIssue(issue.Id, issue.Message, issue.Site, IssueCertainty.Possible, issue.ResourceId, issue.RelatedLocations, issue.Evidence) : issue)
        .Where(issue => reportPossible || issue.Certainty == IssueCertainty.Definite).OrderBy(issue => issue.Site.Path, StringComparer.Ordinal)
        .ThenBy(issue => issue.Site.Start).ThenBy(issue => issue.Id, StringComparer.Ordinal).ThenBy(issue => issue.ResourceId, StringComparer.Ordinal).ToImmutableArray();
}
