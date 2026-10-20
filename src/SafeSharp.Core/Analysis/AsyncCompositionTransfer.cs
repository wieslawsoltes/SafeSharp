using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace SafeSharp.Core;

internal sealed partial class OwnershipTransfer
{
    internal const string AsyncDependencyCachePrefix = "$async-dependencies:";
    internal const string AggregateElementCachePrefix = "$aggregate-element:";
    internal static string AsyncDependencyCache(string id) => AsyncDependencyCachePrefix + id;
    internal static string AggregateElementCache(string id, string key) => AggregateElementCachePrefix + id.Length + ":" + id + key;

    private static bool RetainsAllocation(AbstractState state, string id, string? target, ISet<string> liveAfter) =>
        IncludingFactoryResults(state, liveAfter.Where(place => place != target && (target is null || !PlaceRelations.IsStrictDescendant(place, target)))
            .SelectMany(state.Resolve)).Any(reference => reference.ResourceId == id && !reference.DefinitelyMoved);

    private static void CreateAggregate(AbstractState state, FlowInstruction instruction, int blockId, int index, ISet<string> liveAfter, IssueCollector? issues)
    {
        var id = instruction.ResourceId ?? "aggregate:" + blockId + ":" + index;
        var retained = state.Resources.TryGetValue(id, out var old) && RetainsAllocation(state, id, instruction.Target, liveAfter);
        if (!retained && old is not null)
            foreach (var key in old.ElementKeys)
            {
                var cache = AggregateElementCache(id, key);
                state.Forget(cache);
                state.ValueTasks.Assign(cache, Array.Empty<ReferenceFact>());
            }
        var resource = new ResourceFact(id, ResourceOwnership.Owned, ResourceStatus.Alive, instruction.Site,
            InstructionFlags.SuppressLeak, ImmutableHashSet.Create<string>(StringComparer.Ordinal), kind: ResourceKind.Aggregate,
            aggregateSummary: retained);
        TrackAllocation(state, instruction, resource, false, liveAfter, issues);
    }

    private static void SetAggregateElement(AbstractState state, FlowInstruction instruction, ReferenceFact[] values, ISet<string> liveAfter, IssueCollector? issues)
    {
        var owners = state.Resolve(instruction.Owner).ToArray();
        var protocols = state.ValueTasks.Resolve(instruction.Source).ToArray();
        var external = owners.Length == 0;
        CheckMoved(state, instruction, values, issues);
        foreach (var owner in owners)
        {
            if (owner.Projection.Length != 0 || !state.Resources.TryGetValue(owner.ResourceId, out var aggregate) || aggregate.Kind != ResourceKind.Aggregate)
            { external = true; continue; }
            var key = instruction.Projection ?? "[*]";
            var cache = AggregateElementCache(aggregate.Id, key);
            var weak = aggregate.IsAggregateSummary || !owner.CoversResource || key.IndexOf('*') >= 0;
            CheckOverwrite(state, instruction, cache, values, issues, liveAfter);
            state.Assign(cache, weak ? state.Resolve(cache).Select(value => value.WithAbsence(false)).Concat(values.Select(value => value.WithAbsence(false))) : values,
                state.CanBeNull(instruction.Source) || weak && state.CanBeNull(cache));
            state.ValueTasks.Assign(cache, weak ? state.ValueTasks.Resolve(cache).Select(value => value.WithAbsence(false)).Concat(protocols.Select(value => value.WithAbsence(false))) : protocols);
            state.Resources[aggregate.Id] = aggregate.WithElement(key);
        }
        if (external)
        {
            var transferred = owners.Any(owner => owner.Projection.Length == 0 && state.Resources.TryGetValue(owner.ResourceId, out var resource) && resource.Kind == ResourceKind.Aggregate)
                ? values.Select(value => value.WithAbsence(false)).ToArray() : values;
            CheckUse(state, instruction, transferred, issues);
            CheckBorrowedEscape(state, instruction, transferred, issues);
            CheckLoans(state, instruction, transferred, "SS0005", "An external array cannot retain this owner while a loan remains live.", _ => true, false, issues);
            TransferFactoryResults(state, instruction, transferred, issues);
            foreach (var value in transferred)
                if (value.Projection.Length == 0 && value.LoanId is null && state.Resources.TryGetValue(value.ResourceId, out var resource) && resource.Ownership == ResourceOwnership.Owned)
                    state.Resources[resource.Id] = resource.Transition(TransferStatus(resource.Status, ResourceStatus.Escaped, !value.CoversResource),
                        "escape", "An external array stores this resource beyond the local ownership model.", instruction.Site);
        }
    }

    private static void ReadAggregateElement(AbstractState state, FlowInstruction instruction, ReferenceFact[] owners, ISet<string> liveAfter, IssueCollector? issues)
    {
        var values = new List<ReferenceFact>();
        var protocols = new List<ReferenceFact>();
        var nullable = false;
        var opaque = owners.Length == 0;
        CheckUse(state, instruction, owners, issues);
        foreach (var owner in owners)
        {
            // A structural projection designates a different value. Until its storage is
            // represented, it cannot reuse the enclosing array's canonical element caches.
            if (owner.Projection.Length != 0 || !state.Resources.TryGetValue(owner.ResourceId, out var aggregate) || aggregate.Kind != ResourceKind.Aggregate)
            { opaque = true; continue; }
            var key = instruction.Projection ?? "[*]";
            foreach (var element in aggregate.ElementKeys.Where(element => key.IndexOf('*') >= 0 || element == key || element.IndexOf('*') >= 0))
            {
                var cache = AggregateElementCache(aggregate.Id, element);
                var weak = aggregate.IsAggregateSummary || !owner.CoversResource || key.IndexOf('*') >= 0 && aggregate.ElementKeys.Count > 1 || element.IndexOf('*') >= 0;
                values.AddRange(state.Resolve(cache).Select(value => weak ? value.WithAbsence(false) : value));
                protocols.AddRange(state.ValueTasks.Resolve(cache).Select(value => weak ? value.WithAbsence(false) : value));
                nullable |= state.CanBeNull(cache);
            }
        }
        if (opaque)
        {
            var id = instruction.ResourceId ?? "opaque-element:" + instruction.Site.Start;
            var resource = new ResourceFact(id, ResourceOwnership.External, ResourceStatus.Alive, instruction.Site,
                instruction.Flags, ImmutableHashSet.Create<string>(StringComparer.Ordinal));
            state.Resources[id] = state.Resources.TryGetValue(id, out var old) ? old.Join(resource) : resource;
            values.Add(new ReferenceFact(id, mayBeAbsent: values.Count != 0));
        }
        CheckOverwrite(state, instruction, instruction.Target!, values, issues, liveAfter);
        state.Assign(instruction.Target!, values, nullable);
        state.ValueTasks.Assign(instruction.Target!, protocols);
    }

    private static void InvalidateAggregateContents(AbstractState state, ReferenceFact[] roots)
    {
        // An opaque call can replace an array element while another local still owns its old
        // value. Keep that value as a possible element, without allowing cleanup through the
        // stale array cache to prove that every original owner has been released. Independent
        // by-value snapshots remain exact. No replacement receives an invented fresh identity.
        var aggregates = IncludingFactoryResults(state, roots).Where(reference => reference.Projection.Length == 0 &&
                state.Resources.TryGetValue(reference.ResourceId, out var resource) && resource.Kind == ResourceKind.Aggregate)
            .Select(reference => reference.ResourceId).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var id in aggregates)
        foreach (var key in state.Resources[id].ElementKeys)
        {
            var cache = AggregateElementCache(id, key);
            state.Assign(cache, state.Resolve(cache).Select(value => value.WithAbsence(false)), mayBeNull: true);
            state.ValueTasks.Assign(cache, state.ValueTasks.Resolve(cache).Select(value => value.WithAbsence(false)));
        }
    }

    private static void BeginAsyncComposition(AbstractState state, FlowInstruction instruction, ReferenceFact[] dependencies,
        int blockId, int index, ISet<string> liveAfter, IssueCollector? issues)
    {
        CheckMoved(state, instruction, dependencies, issues);
        var id = instruction.ResourceId ?? "async-composition:" + blockId + ":" + index;
        var cache = AsyncDependencyCache(id);
        if (dependencies.Length == 0)
        {
            var unknownId = "opaque-composite-input:" + id;
            state.Resources[unknownId] = new ResourceFact(unknownId, ResourceOwnership.External, ResourceStatus.Alive, instruction.Site,
                InstructionFlags.SuppressLeak, ImmutableHashSet.Create<string>(StringComparer.Ordinal));
            dependencies = new[] { new ReferenceFact(unknownId) };
        }
        var retained = state.Resources.ContainsKey(id) && RetainsAllocation(state, id, instruction.Target, liveAfter);
        var oldDependencies = retained ? state.Resolve(cache).ToArray() : Array.Empty<ReferenceFact>();
        var evidence = new AnalysisEvidence("async-composition", "This framework operation preserves input identities; observing completion and receiving a result remain separate effects.", instruction.Site);
        var resource = new ResourceFact(id, ResourceOwnership.Owned, ResourceStatus.Pending, instruction.Site,
            instruction.Flags | InstructionFlags.SuppressLeak, ImmutableHashSet.Create<string>(StringComparer.Ordinal),
            ImmutableDictionary.Create<string, AnalysisEvidence>(StringComparer.Ordinal).Add(EvidenceFacts.Key(evidence), evidence),
            ResourceKind.AsyncFactoryCarrier, resultState: AsyncResultState.Deferred, composition: instruction.AsyncComposition);
        if (instruction.AsyncComposition is AsyncCompositionKind.WhenAll or AsyncCompositionKind.WhenAny)
            dependencies = SnapshotAsyncInputs(state, instruction, dependencies, id, retained, blockId, index, liveAfter, issues);
        TrackAllocation(state, instruction, resource, false, liveAfter, issues);
        state.Assign(cache, retained ? oldDependencies.Concat(dependencies).Select(value => value.WithAbsence(false)) : dependencies, state.CanBeNull(instruction.Source));
        if (instruction.AsyncComposition == AsyncCompositionKind.WhenAny)
        {
            // WhenAny selects one of the existing tasks. It never observes or produces the input
            // task's payload. Keep that promise even before awaiting or returning this proxy.
            var candidates = AggregateInputs(state, state.Resolve(cache)).SelectMany(element => element.Values).ToArray();
            var many = candidates.Select(candidate => candidate.ResourceId).Distinct(StringComparer.Ordinal).Skip(1).Any();
            state.Assign(AsyncResultCache(id), candidates.Select(candidate => many ? candidate.WithAbsence(false) : candidate));
            state.Resources[id] = state.Resources[id].WithResultState(AsyncResultState.Materialized |
                (dependencies.Any(dependency => dependency.Projection.Length != 0 || !state.Resources.TryGetValue(dependency.ResourceId, out var aggregate) || aggregate.Kind != ResourceKind.Aggregate)
                    ? AsyncResultState.Deferred : AsyncResultState.None));
        }
    }

    private static ReferenceFact[] SnapshotAsyncInputs(AbstractState state, FlowInstruction instruction, ReferenceFact[] inputs,
        string identity, bool retained, int blockId, int index, ISet<string> liveAfter, IssueCollector? issues)
    {
        var id = "async-inputs:" + identity;
        var place = "$composite-inputs:" + identity;
        var old = retained && state.Resources.TryGetValue(id, out var previous) ? previous : null;
        // Task.WhenAll/WhenAny enumerate their inputs at invocation. Later writes through an
        // alias to the source array must not change the tasks captured by the proxy.
        CreateAggregate(state, new FlowInstruction(InstructionKind.CreateAggregate, instruction.Site, target: place, resourceId: id), blockId, index, liveAfter, issues);
        foreach (var group in AggregateInputs(state, inputs).GroupBy(element => element.Key, StringComparer.Ordinal))
        {
            var cache = AggregateElementCache(id, group.Key);
            var values = group.SelectMany(element => element.Values).ToArray();
            state.Assign(cache, old is null ? values : state.Resolve(cache).Concat(values).Select(value => value.WithAbsence(false)));
            state.Resources[id] = state.Resources[id].WithElement(group.Key);
        }
        var snapshots = new List<ReferenceFact>();
        if (inputs.Any(input => input.Projection.Length == 0 && state.Resources.TryGetValue(input.ResourceId, out var aggregate) && aggregate.Kind == ResourceKind.Aggregate))
            snapshots.Add(new ReferenceFact(id, coversResource: !retained));
        snapshots.AddRange(inputs.Where(input => input.Projection.Length != 0 || !state.Resources.TryGetValue(input.ResourceId, out var aggregate) || aggregate.Kind != ResourceKind.Aggregate));
        state.Forget(place);
        return snapshots.ToArray();
    }

    private static IEnumerable<(string Key, ReferenceFact[] Values)> AggregateInputs(AbstractState state, IEnumerable<ReferenceFact> inputs)
    {
        foreach (var input in inputs)
        {
            if (input.Projection.Length != 0 || !state.Resources.TryGetValue(input.ResourceId, out var aggregate) || aggregate.Kind != ResourceKind.Aggregate) continue;
            foreach (var key in aggregate.ElementKeys.OrderBy(key => key, StringComparer.Ordinal))
                yield return (key, state.Resolve(AggregateElementCache(aggregate.Id, key))
                    .Select(value => input.CoversResource ? value : value.WithAbsence(false)).ToArray());
        }
    }

    private static void CompleteAsyncComposition(AbstractState state, FlowInstruction instruction, ReferenceFact[] references,
        int blockId, int index, ISet<string> liveAfter, IssueCollector? issues)
    {
        var active = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in references)
            if (reference.Projection.Length == 0 && state.Resources.TryGetValue(reference.ResourceId, out var resource) && resource.Composition != AsyncCompositionKind.None)
                ReceiveComposition(state, instruction, reference, blockId, index, liveAfter, issues, active);
    }

    private static void ObserveComposedDisposal(AbstractState state, FlowInstruction instruction, ReferenceFact reference)
    {
        if (reference.Projection.Length != 0 || !state.Resources.TryGetValue(reference.ResourceId, out var resource) || resource.Composition != AsyncCompositionKind.WhenAll) return;
        foreach (var input in AggregateInputs(state, state.Resolve(AsyncDependencyCache(resource.Id))).SelectMany(element => element.Values))
            if (input.Projection.Length == 0 && state.Resources.TryGetValue(input.ResourceId, out var completion) && completion.Kind == ResourceKind.AsyncDisposalCompletion)
                state.Resources[completion.Id] = completion.Transition(TransferStatus(completion.Status, ResourceStatus.Completed,
                    !reference.CoversResource || !input.CoversResource), "completion", "WhenAll observes every disposal completion, including when one faults.", instruction.Site);
    }

    // Rust Future::Output is produced only by a completed future. The concrete fault/cancellation
    // behavior here is .NET's: WaitAsync can stop waiting while the source operation continues;
    // WhenAny returns a winner without consuming any input; WhenAll can fail after siblings succeed.
    // https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/library/core/src/future/future.rs
    // https://learn.microsoft.com/dotnet/api/system.threading.tasks.task.waitasync
    private static void ReceiveComposition(AbstractState state, FlowInstruction instruction, ReferenceFact reference,
        int blockId, int index, ISet<string> liveAfter, IssueCollector? issues, HashSet<string> active)
    {
        if (reference.Projection.Length != 0) return;
        var carrier = state.Resources[reference.ResourceId];
        if (carrier.Composition == AsyncCompositionKind.None || (carrier.ResultState & AsyncResultState.Deferred) == 0 || !active.Add(carrier.Id)) return;
        var resultCache = AsyncResultCache(carrier.Id);
        var prior = (carrier.ResultState & AsyncResultState.Materialized) != 0 ? state.Resolve(resultCache).ToArray() : Array.Empty<ReferenceFact>();
        var dependencies = state.Resolve(AsyncDependencyCache(carrier.Id)).Select(value => reference.CoversResource ? value : value.WithAbsence(false)).ToArray();
        var hasResult = (carrier.Flags & InstructionFlags.HasAsyncResult) != 0;
        var result = new List<ReferenceFact>();
        var resultProtocols = new List<ReferenceFact>();
        var nullable = false;
        if (carrier.Composition is AsyncCompositionKind.WaitAsync or AsyncCompositionKind.Unwrap)
        {
            var received = ReceiveDependencies(state, instruction, dependencies, carrier.Id, blockId, index, liveAfter, issues, active,
                receiveOutput: hasResult || carrier.Composition == AsyncCompositionKind.Unwrap, outputFlags: carrier.Flags);
            if (carrier.Composition == AsyncCompositionKind.Unwrap)
                received = ReceiveDependencies(state, instruction, received.Values, carrier.Id + ":inner", blockId, index, liveAfter, issues, active, hasResult, carrier.Flags);
            if (hasResult) { result.AddRange(received.Values); resultProtocols.AddRange(received.Protocols); nullable = received.Nullable; }
        }
        else
        {
            var elements = AggregateInputs(state, dependencies).ToArray();
            var unknown = dependencies.Any(dependency => dependency.Projection.Length != 0 || !state.Resources.TryGetValue(dependency.ResourceId, out var aggregate) || aggregate.Kind != ResourceKind.Aggregate);
            if (carrier.Composition == AsyncCompositionKind.WhenAny)
            {
                var choices = elements.SelectMany(element => element.Values).ToArray();
                var many = choices.Select(choice => choice.ResourceId).Distinct(StringComparer.Ordinal).Skip(1).Any();
                result.AddRange(choices.Select(value => many ? value.WithAbsence(false) : value));
            }
            else
            {
                var aggregateId = "async-results:" + carrier.Id;
                if (hasResult && !state.Resources.ContainsKey(aggregateId))
                    state.Resources[aggregateId] = new ResourceFact(aggregateId, ResourceOwnership.Owned, ResourceStatus.Alive, instruction.Site,
                        InstructionFlags.SuppressLeak, ImmutableHashSet.Create<string>(StringComparer.Ordinal), kind: ResourceKind.Aggregate);
                foreach (var element in elements)
                {
                    var received = ReceiveDependencies(state, instruction, element.Values, carrier.Id + element.Key, blockId, index, liveAfter, issues, active, hasResult, carrier.Flags);
                    if (!hasResult) continue;
                    var cache = AggregateElementCache(aggregateId, element.Key);
                    state.Assign(cache, state.Resolve(cache).Concat(received.Values), received.Nullable);
                    state.ValueTasks.Assign(cache, state.ValueTasks.Resolve(cache).Concat(received.Protocols));
                    state.Resources[aggregateId] = state.Resources[aggregateId].WithElement(element.Key);
                }
                if (hasResult) result.Add(new ReferenceFact(aggregateId, mayBeAbsent: reference.MayBeAbsent, coversResource: reference.CoversResource));
            }
            if (unknown && hasResult)
            {
                var id = "opaque-composite-result:" + carrier.Id;
                var resource = new ResourceFact(id, ResourceOwnership.External, ResourceStatus.Alive, instruction.Site,
                    carrier.Flags, ImmutableHashSet.Create<string>(StringComparer.Ordinal));
                state.Resources[id] = state.Resources.TryGetValue(id, out var old) ? old.Join(resource) : resource;
                result.Add(new ReferenceFact(id, mayBeAbsent: result.Count != 0));
            }
        }
        state.Assign(resultCache, prior.Concat(result), nullable || prior.Length != 0 && state.CanBeNull(resultCache));
        state.ValueTasks.Assign(resultCache, state.ValueTasks.Resolve(resultCache).Concat(resultProtocols));
        state.Resources[carrier.Id] = carrier.Transition(TransferStatus(carrier.Status, ResourceStatus.Completed, !reference.CoversResource),
            "async-composition-success", "Only this successful continuation receives the represented input results.", instruction.Site)
            .WithResultState(reference.CoversResource ? AsyncResultState.Materialized : carrier.ResultState | AsyncResultState.Materialized);
        active.Remove(carrier.Id);
    }

    private static (ReferenceFact[] Values, ReferenceFact[] Protocols, bool Nullable) ReceiveDependencies(AbstractState state, FlowInstruction instruction,
        ReferenceFact[] dependencies, string identity, int blockId, int index, ISet<string> liveAfter, IssueCollector? issues,
        HashSet<string> active, bool receiveOutput, InstructionFlags outputFlags)
    {
        var result = new List<ReferenceFact>();
        var protocols = new List<ReferenceFact>();
        var nullable = false;
        foreach (var dependency in dependencies)
        {
            if (!state.Resources.TryGetValue(dependency.ResourceId, out var resource)) continue;
            CheckMoved(state, instruction, new[] { dependency }, issues);
            if (dependency.Projection.Length == 0 && resource.Kind == ResourceKind.AsyncDisposalCompletion)
            {
                state.Resources[resource.Id] = resource.Transition(TransferStatus(resource.Status, ResourceStatus.Completed, !dependency.CoversResource),
                    "completion", "Successful completion of the composed task observes this disposal completion.", instruction.Site);
                continue;
            }
            if (dependency.Projection.Length == 0 && resource.Composition != AsyncCompositionKind.None)
            {
                if (active.Contains(resource.Id)) continue; // A finite allocation-site cycle gives no fresh output evidence.
                ReceiveComposition(state, instruction, dependency, blockId, index, liveAfter, issues, active);
            }
            if (!receiveOutput) continue;
            var temporary = "$composition-receive:" + identity;
            ReceiveAsyncResult(state, new FlowInstruction(InstructionKind.ReceiveAsyncResult, instruction.Site, temporary,
                AsyncDependencyCache(identity), resourceId: "external-composition-result:" + identity, flags: outputFlags),
                new[] { dependency }, blockId, index, liveAfter, issues, completeComposition: false);
            result.AddRange(state.Resolve(temporary));
            protocols.AddRange(state.ValueTasks.Resolve(AsyncResultCache(resource.Id)));
            nullable |= state.CanBeNull(temporary);
            state.Forget(temporary);
        }
        return (result.ToArray(), protocols.ToArray(), nullable);
    }
}
