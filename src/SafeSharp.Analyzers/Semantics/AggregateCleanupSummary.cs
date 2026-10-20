using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace SafeSharp.Analyzers.Semantics;

internal sealed class CleanupMethodSummary
{
    public static readonly CleanupMethodSummary Unknown = new(true, true, ImmutableHashSet<ISymbol>.Empty,
        ImmutableHashSet<ISymbol>.Empty, ImmutableHashSet<ISymbol>.Empty, ImmutableHashSet<ISymbol>.Empty, ImmutableArray<Location>.Empty);
    public CleanupMethodSummary(bool returnsNormally, bool mayThrow, ImmutableHashSet<ISymbol> released,
        ImmutableHashSet<ISymbol> writtenMembers, ImmutableHashSet<ISymbol> outstandingNewMembers,
        ImmutableHashSet<ISymbol> pendingMembers, ImmutableArray<Location> exits,
        ImmutableHashSet<ISymbol>? exceptionalReleased = null, ImmutableHashSet<ISymbol>? exceptionalWrittenMembers = null,
        ImmutableHashSet<ISymbol>? exceptionalOutstandingNewMembers = null, ImmutableHashSet<ISymbol>? exceptionalPendingMembers = null)
    {
        ReturnsNormally = returnsNormally; MayThrow = mayThrow; Released = released;
        WrittenMembers = writtenMembers; OutstandingNewMembers = outstandingNewMembers; PendingMembers = pendingMembers; Exits = exits;
        ExceptionalReleased = exceptionalReleased ?? ImmutableHashSet<ISymbol>.Empty;
        ExceptionalWrittenMembers = exceptionalWrittenMembers ?? ImmutableHashSet<ISymbol>.Empty;
        ExceptionalOutstandingNewMembers = exceptionalOutstandingNewMembers ?? ImmutableHashSet<ISymbol>.Empty;
        ExceptionalPendingMembers = exceptionalPendingMembers ?? ImmutableHashSet<ISymbol>.Empty;
    }
    public bool ReturnsNormally { get; }
    public bool MayThrow { get; }
    public ImmutableHashSet<ISymbol> Released { get; }
    public ImmutableHashSet<ISymbol> WrittenMembers { get; }
    public ImmutableHashSet<ISymbol> OutstandingNewMembers { get; }
    public ImmutableHashSet<ISymbol> PendingMembers { get; }
    public ImmutableArray<Location> Exits { get; }
    public ImmutableHashSet<ISymbol> ExceptionalReleased { get; }
    public ImmutableHashSet<ISymbol> ExceptionalWrittenMembers { get; }
    public ImmutableHashSet<ISymbol> ExceptionalOutstandingNewMembers { get; }
    public ImmutableHashSet<ISymbol> ExceptionalPendingMembers { get; }
}

/// <summary>
/// Per-owner bounded summaries, specialized for constant boolean arguments such as Dispose(true).
/// A recursive SCC contributes no assumed release; a caller can still prove cleanup after that call.
/// Rust drop elaboration similarly reasons about obligations along control-flow paths:
/// https://rustc-dev-guide.rust-lang.org/mir/drop-elaboration.html.
/// </summary>
internal sealed class AggregateCleanupSummary
{
    private readonly Dictionary<IMethodSymbol, ControlFlowGraph?> graphs = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<string, CleanupMethodSummary> summaries = new(StringComparer.Ordinal);
    private readonly HashSet<string> evaluating = new(StringComparer.Ordinal);
    private readonly Dictionary<ISymbol, int> slots = new(SymbolEqualityComparer.Default);
    private int remainingSteps;
    public AggregateCleanupSummary(INamedTypeSymbol owner, IEnumerable<ISymbol> members, ContractSymbols symbols,
        int maximumSteps, CancellationToken cancellationToken)
    {
        Owner = owner; Members = members.ToImmutableHashSet(SymbolEqualityComparer.Default); Symbols = symbols;
        remainingSteps = maximumSteps; CancellationToken = cancellationToken;
        foreach (var member in Members) Slot(member);
    }
    public INamedTypeSymbol Owner { get; }
    public ImmutableHashSet<ISymbol> Members { get; }
    public ContractSymbols Symbols { get; }
    public CancellationToken CancellationToken { get; }
    public bool BudgetExceeded { get; private set; }
    public void ExhaustBudget() { BudgetExceeded = true; remainingSteps = -1; }
    public bool Step()
    {
        CancellationToken.ThrowIfCancellationRequested();
        if (--remainingSteps >= 0) return true;
        BudgetExceeded = true; return false;
    }
    public CleanupResource Initial(ISymbol slot) => new(Slot(slot) + ":entry", slot, true);
    public CleanupResource Assigned(ISymbol slot, SyntaxNode site) => new(Slot(slot) + ":" + site.SyntaxTree.FilePath + ":" + site.SpanStart, slot, false);
    public CleanupResource Completion(ISymbol slot, SyntaxNode site, bool leaked = false) => new(Slot(slot) +
        (leaked ? ":unobserved:" : ":completion:") + site.SyntaxTree.FilePath + ":" + site.SpanStart, slot, false, completion: true);
    private int Slot(ISymbol symbol)
    {
        if (!slots.TryGetValue(symbol, out var index)) slots.Add(symbol, index = slots.Count);
        return index;
    }
    public CleanupMethodSummary Analyze(IMethodSymbol method, IReadOnlyDictionary<ISymbol, bool>? booleans = null)
    {
        method = method.PartialImplementationPart ?? method;
        var key = Slot(method.OriginalDefinition) + ":" + (booleans is null ? "" : string.Join(",", booleans
            .Select(pair => new KeyValuePair<int, bool>(Slot(pair.Key), pair.Value)).OrderBy(pair => pair.Key).Select(pair => pair.Key + "=" + pair.Value)));
        if (summaries.TryGetValue(key, out var cached)) return cached;
        if (evaluating.Contains(key)) return CleanupMethodSummary.Unknown;
        if (evaluating.Count >= 32 || summaries.Count >= 256 || !Step())
        {
            BudgetExceeded = true; return CleanupMethodSummary.Unknown;
        }
        var graph = Graph(method);
        if (graph is null) return CleanupMethodSummary.Unknown;
        evaluating.Add(key);
        try
        {
            var result = new AggregateCleanupFlow(this, method, graph, booleans).Analyze();
            summaries[key] = result; return result;
        }
        finally { evaluating.Remove(key); }
    }
    private ControlFlowGraph? Graph(IMethodSymbol method)
    {
        method = method.OriginalDefinition;
        if (graphs.TryGetValue(method, out var existing)) return existing;
        ControlFlowGraph? graph = null;
        if (method.MethodKind == MethodKind.LocalFunction && method.ContainingSymbol is IMethodSymbol containing)
        {
            var outer = Graph(containing);
            if (outer is not null)
                graph = outer.GetLocalFunctionControlFlowGraph(method, CancellationToken);
        }
        else
        {
            foreach (var reference in method.DeclaringSyntaxReferences)
            {
                var syntax = reference.GetSyntax(CancellationToken);
                var model = Symbols.Compilation.GetSemanticModel(syntax.SyntaxTree);
                var operation = model.GetOperation(syntax, CancellationToken);
                graph = operation switch
                {
                    IMethodBodyOperation body => ControlFlowGraph.Create(body, CancellationToken),
                    IConstructorBodyOperation constructor => ControlFlowGraph.Create(constructor, CancellationToken),
                    IBlockOperation { Parent: null } block => ControlFlowGraph.Create(block, CancellationToken),
                    _ => null,
                };
                if (graph is not null) break;
            }
        }
        graphs[method] = graph; return graph;
    }
    public IMethodSymbol ResolveTarget(IInvocationOperation invocation)
    {
        var target = invocation.TargetMethod;
        if (!invocation.IsVirtual) return target;
        if (target.ContainingType.TypeKind == TypeKind.Interface && Owner.FindImplementationForInterfaceMember(target) is IMethodSymbol implementation)
            target = implementation;
        return ResolveOverride(target);
    }
    public IMethodSymbol ResolveOverride(IMethodSymbol target)
    {
        if (!target.IsVirtual && !target.IsOverride && !target.IsAbstract) return target;
        for (INamedTypeSymbol? current = Owner; current is not null; current = current.BaseType)
        foreach (var candidate in current.GetMembers(target.Name).OfType<IMethodSymbol>())
            for (var overridden = candidate.OverriddenMethod; overridden is not null; overridden = overridden.OverriddenMethod)
                if (ContractSymbols.Equal(overridden.OriginalDefinition, target.OriginalDefinition)) return candidate;
        return target;
    }
}
