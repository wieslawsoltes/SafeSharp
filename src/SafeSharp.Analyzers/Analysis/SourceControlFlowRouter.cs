using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace SafeSharp.Analyzers.Analysis;

/// <summary>
/// Bounded continuation graph for source-value analyses. The transfer function supplies a state at
/// each potentially throwing operation; this router supplies CLR filter, unwind and handler edges.
/// A return operand is evaluated once, before its finally continuation, and exceptional exits never
/// become normal-return postconditions. The routing matches ControlFlowLowerer's ownership graph.
/// </summary>
/// <remarks>
/// This is original C# code. Like rustc MIR's explicit unwind edges and drop elaboration, cleanup is
/// part of the dataflow graph, not a syntactic property of the return expression:
/// https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_mir_transform/src/elaborate_drops.rs
/// CLR filters differ from Rust unwinding: filters run before cleanup; a throwing filter rejects its
/// handler. Roslyn exposes these regions separately from normal basic-block predecessors.
/// </remarks>
internal sealed class SourceControlFlowRouter
{
    private const int Unhandled = -1;
    private readonly ControlFlowGraph graph;
    private readonly Dictionary<string, Context> contexts = new(StringComparer.Ordinal);
    private readonly List<Context> byId = new();
    private readonly Dictionary<(int, bool, ITypeSymbol?, bool), ExceptionPlan> plans = new();
    private readonly int maxContexts;
    private int nextPlan;

    internal SourceControlFlowRouter(ControlFlowGraph graph, int maxContexts = 4096)
    {
        this.graph = graph;
        this.maxContexts = maxContexts;
        Entry = Intern(0, ImmutableArray<ResumePoint>.Empty, false, null, null);
    }

    internal Context Entry { get; }
    internal Context this[int id] => byId[id];
    internal bool IsNormalExit(Context context) => context.Ordinal == graph.Blocks.Length - 1 && !context.Exceptional;
    internal bool IsBoundary(Context context) => context.Ordinal < 0;

    internal bool HasExceptionalTarget(Context context)
    {
        if (context.Filter is not null) return true;
        for (var region = graph.Blocks[context.Ordinal].EnclosingRegion;
             !ReferenceEquals(region, graph.Root) && region.EnclosingRegion is { } parent; region = parent)
            if (region.Kind == ControlFlowRegionKind.Try && parent.Kind is ControlFlowRegionKind.TryAndCatch or ControlFlowRegionKind.TryAndFinally)
                return true;
        return false;
    }

    internal static IEnumerable<ControlFlowBranch> Branches(BasicBlock block)
    {
        if (block.BranchValue?.ConstantValue is { HasValue: true, Value: bool value } && block.ConditionKind != ControlFlowConditionKind.None)
        {
            var chosen = value == (block.ConditionKind == ControlFlowConditionKind.WhenTrue) ? block.ConditionalSuccessor : block.FallThroughSuccessor;
            if (chosen is not null) yield return chosen;
            yield break;
        }
        if (block.FallThroughSuccessor is { } first) yield return first;
        if (block.ConditionalSuccessor is { } second) yield return second;
    }

    internal IReadOnlyList<Context> Successors(Context context, ControlFlowBranch branch)
    {
        var result = new List<Context>();
        var block = graph.Blocks[context.Ordinal];
        if (branch.Semantics is ControlFlowBranchSemantics.ProgramTermination) return result;
        if (branch.Semantics is ControlFlowBranchSemantics.Throw or ControlFlowBranchSemantics.Rethrow)
        {
            var thrown = ThrownType(block, branch);
            return Exceptions(context, false, thrown.Type, thrown.Exact);
        }
        if (branch.Semantics == ControlFlowBranchSemantics.StructuredExceptionHandling && branch.Destination is null)
        {
            if (context.Filter is { } filter && Contains(filter.Region, context.Ordinal))
                Dispatch(filter.Plan, filter.RouteIndex + 1, result);
            else if (!context.Continuation.IsEmpty)
            {
                var resume = context.Continuation[0];
                result.Add(Intern(resume.Ordinal, context.Continuation.RemoveAt(0), resume.Exceptional, resume.Filter, resume.ReturnOperation));
            }
            return result;
        }
        if (branch.Destination is null) return result;
        if (context.Filter is { } accepted && Contains(accepted.Region, context.Ordinal) &&
            branch.Destination.Ordinal == accepted.Plan.Routes[accepted.RouteIndex].Destination)
        {
            result.Add(EnterHandler(accepted.Plan, accepted.Plan.Routes[accepted.RouteIndex]));
            return result;
        }
        var returned = branch.Semantics == ControlFlowBranchSemantics.Return ? block.BranchValue : context.ReturnOperation;
        if (branch.FinallyRegions.IsEmpty)
            result.Add(Intern(branch.Destination.Ordinal, context.Continuation, context.Exceptional, context.Filter, returned));
        else
        {
            var continuation = branch.FinallyRegions.Skip(1)
                .Select(region => new ResumePoint(region.FirstBlockOrdinal, context.Exceptional, context.Filter, returned))
                .Append(new ResumePoint(branch.Destination.Ordinal, context.Exceptional, context.Filter, returned))
                .Concat(context.Continuation).ToImmutableArray();
            result.Add(Intern(branch.FinallyRegions[0].FirstBlockOrdinal, continuation, context.Exceptional, context.Filter, returned));
        }
        return result;
    }

    internal IReadOnlyList<Context> Exceptions(Context context, bool implicitException = true, ITypeSymbol? thrownType = null, bool exact = false)
    {
        var result = new List<Context>();
        if (context.Filter is { } filter && Contains(filter.Region, context.Ordinal))
        {
            Dispatch(filter.Plan, filter.RouteIndex + 1, result);
            return result;
        }
        var key = (context.Id, implicitException, thrownType, exact);
        if (!plans.TryGetValue(key, out var plan))
        {
            var cleanup = ImmutableArray.CreateBuilder<int>();
            var routes = ImmutableArray.CreateBuilder<ExceptionRoute>();
            var handled = false;
            for (var region = graph.Blocks[context.Ordinal].EnclosingRegion;
                 !ReferenceEquals(region, graph.Root) && region.EnclosingRegion is { } parent && !handled; region = parent)
            {
                if (region.Kind != ControlFlowRegionKind.Try) continue;
                if (parent.Kind == ControlFlowRegionKind.TryAndFinally)
                    cleanup.Add(parent.NestedRegions.First(nested => nested.Kind == ControlFlowRegionKind.Finally).FirstBlockOrdinal);
                else if (parent.Kind == ControlFlowRegionKind.TryAndCatch)
                {
                    foreach (var handler in parent.NestedRegions.Where(nested => nested.Kind is ControlFlowRegionKind.Catch or ControlFlowRegionKind.FilterAndHandler))
                    {
                        var match = Match(thrownType, exact, handler.ExceptionType);
                        if (match == ExceptionMatch.Never) continue;
                        var filterRegion = handler.Kind == ControlFlowRegionKind.FilterAndHandler
                            ? handler.NestedRegions.First(nested => nested.Kind == ControlFlowRegionKind.Filter) : null;
                        var destination = handler.Kind == ControlFlowRegionKind.FilterAndHandler
                            ? handler.NestedRegions.First(nested => nested.Kind == ControlFlowRegionKind.Catch).FirstBlockOrdinal : handler.FirstBlockOrdinal;
                        routes.Add(new ExceptionRoute(destination, cleanup.ToImmutable(), filterRegion, match == ExceptionMatch.Always));
                        if (match == ExceptionMatch.Always && filterRegion is null) { handled = true; break; }
                    }
                }
            }
            if (!handled) routes.Add(new ExceptionRoute(Unhandled, cleanup.ToImmutable(), null, true, true));
            plan = new ExceptionPlan(nextPlan++, context, routes.ToImmutable());
            plans.Add(key, plan);
        }
        Dispatch(plan, 0, result);
        return result;
    }

    private void Dispatch(ExceptionPlan plan, int start, List<Context> result)
    {
        for (var index = start; index < plan.Routes.Length; index++)
        {
            var route = plan.Routes[index];
            result.Add(route.Filter is { } filter
                ? Intern(filter.FirstBlockOrdinal, ImmutableArray<ResumePoint>.Empty, true, new FilterContinuation(plan, index, filter), null)
                : EnterHandler(plan, route));
            if (route.Definite) break;
        }
    }

    private Context EnterHandler(ExceptionPlan plan, ExceptionRoute route)
    {
        var origin = plan.Origin;
        var preserve = !route.Unhandled && EnclosingFinally(graph.Blocks[origin.Ordinal].EnclosingRegion) is { } active && Contains(active, route.Destination);
        var tail = preserve ? origin.Continuation : ImmutableArray<ResumePoint>.Empty;
        var exceptional = route.Unhandled || preserve && origin.Exceptional;
        var returned = preserve ? origin.ReturnOperation : null;
        if (route.Cleanup.IsEmpty) return Intern(route.Destination, tail, exceptional, null, returned);
        var continuation = route.Cleanup.Skip(1).Select(ordinal => new ResumePoint(ordinal, true, null, null))
            .Append(new ResumePoint(route.Destination, exceptional, null, returned)).Concat(tail).ToImmutableArray();
        return Intern(route.Cleanup[0], continuation, true, null, null);
    }

    private ControlFlowRegion? EnclosingFinally(ControlFlowRegion region)
    {
        for (var current = region; current is not null && !ReferenceEquals(current, graph.Root); current = current.EnclosingRegion)
            if (current.Kind == ControlFlowRegionKind.Finally) return current;
        return null;
    }

    private (ITypeSymbol? Type, bool Exact) ThrownType(BasicBlock block, ControlFlowBranch branch)
    {
        if (branch.Semantics == ControlFlowBranchSemantics.Rethrow)
        {
            for (var region = block.EnclosingRegion; region is not null && !ReferenceEquals(region, graph.Root); region = region.EnclosingRegion)
                if (region.Kind == ControlFlowRegionKind.Catch) return (region.ExceptionType, false);
            return (null, false);
        }
        var value = block.BranchValue;
        while (value is IConversionOperation conversion) value = conversion.Operand;
        return value?.ConstantValue is { HasValue: true, Value: null } ? (null, false)
            : (value?.Type, value is IObjectCreationOperation || value?.Type?.IsSealed == true);
    }

    private static ExceptionMatch Match(ITypeSymbol? thrown, bool exact, ITypeSymbol? caught)
    {
        if (caught is null || caught.SpecialType == SpecialType.System_Object || caught.ToDisplayString() == "System.Exception") return ExceptionMatch.Always;
        if (thrown is null) return ExceptionMatch.Possible;
        if (IsSubtype(thrown, caught)) return ExceptionMatch.Always;
        return !exact && IsSubtype(caught, thrown) ? ExceptionMatch.Possible : ExceptionMatch.Never;
    }

    private static bool IsSubtype(ITypeSymbol subtype, ITypeSymbol supertype)
    {
        for (var current = subtype; current is not null; current = current.BaseType)
            if (SymbolEqualityComparer.Default.Equals(current, supertype)) return true;
        return false;
    }

    private static bool Contains(ControlFlowRegion region, int ordinal) => region.FirstBlockOrdinal <= ordinal && ordinal <= region.LastBlockOrdinal;

    private Context Intern(int ordinal, ImmutableArray<ResumePoint> continuation, bool exceptional, FilterContinuation? filter, IOperation? returned)
    {
        var key = ordinal + ":" + exceptional + ":" + filter?.Key + ":" + returned?.Syntax.SpanStart + ":" +
            string.Join(";", continuation.Select(point => point.Key));
        if (contexts.TryGetValue(key, out var existing)) return existing;
        if (byId.Count >= maxContexts) throw new AnalysisBudgetException("Source flow exceeded its bounded continuation contexts.");
        var context = new Context(byId.Count, ordinal, continuation, exceptional, filter, returned);
        contexts.Add(key, context);
        byId.Add(context);
        return context;
    }

    internal sealed class Context
    {
        internal Context(int id, int ordinal, ImmutableArray<ResumePoint> continuation, bool exceptional, FilterContinuation? filter, IOperation? returned)
        { Id = id; Ordinal = ordinal; Continuation = continuation; Exceptional = exceptional; Filter = filter; ReturnOperation = returned; }
        internal int Id { get; }
        internal int Ordinal { get; }
        internal ImmutableArray<ResumePoint> Continuation { get; }
        internal bool Exceptional { get; }
        internal FilterContinuation? Filter { get; }
        internal IOperation? ReturnOperation { get; }
    }

    internal sealed class ResumePoint
    {
        internal ResumePoint(int ordinal, bool exceptional, FilterContinuation? filter, IOperation? returned)
        { Ordinal = ordinal; Exceptional = exceptional; Filter = filter; ReturnOperation = returned; Key = ordinal + ":" + exceptional + ":" + filter?.Key + ":" + returned?.Syntax.SpanStart; }
        internal int Ordinal { get; }
        internal bool Exceptional { get; }
        internal FilterContinuation? Filter { get; }
        internal IOperation? ReturnOperation { get; }
        internal string Key { get; }
    }

    internal sealed class FilterContinuation
    {
        internal FilterContinuation(ExceptionPlan plan, int index, ControlFlowRegion region)
        { Plan = plan; RouteIndex = index; Region = region; Key = plan.Id + ":" + index; }
        internal ExceptionPlan Plan { get; }
        internal int RouteIndex { get; }
        internal ControlFlowRegion Region { get; }
        internal string Key { get; }
    }

    internal sealed class ExceptionPlan
    {
        internal ExceptionPlan(int id, Context origin, ImmutableArray<ExceptionRoute> routes) { Id = id; Origin = origin; Routes = routes; }
        internal int Id { get; }
        internal Context Origin { get; }
        internal ImmutableArray<ExceptionRoute> Routes { get; }
    }

    internal sealed class ExceptionRoute
    {
        internal ExceptionRoute(int destination, ImmutableArray<int> cleanup, ControlFlowRegion? filter, bool definite, bool unhandled = false)
        { Destination = destination; Cleanup = cleanup; Filter = filter; Definite = definite; Unhandled = unhandled; }
        internal int Destination { get; }
        internal ImmutableArray<int> Cleanup { get; }
        internal ControlFlowRegion? Filter { get; }
        internal bool Definite { get; }
        internal bool Unhandled { get; }
    }

    private enum ExceptionMatch { Never, Possible, Always }
}
