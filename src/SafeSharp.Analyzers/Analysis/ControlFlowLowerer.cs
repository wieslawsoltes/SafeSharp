using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;
using SafeSharp.Core;

namespace SafeSharp.Analyzers.Analysis;

/// <summary>
/// Expands Roslyn's implicit finally continuations into explicit edges, retaining the continuation
/// context for each clone. A return operand is evaluated before cleanup and validated after cleanup.
/// This plays the same normalization role as MIR drop elaboration, without imposing Rust drop semantics on C#.
/// </summary>
internal static class ControlFlowLowerer
{
    public static ControlFlowGraph? TryCreate(IOperation operation, CancellationToken cancellationToken)
    {
        while (operation.Parent is not null) operation = operation.Parent;
        return operation switch
        {
            IMethodBodyOperation body => ControlFlowGraph.Create(body, cancellationToken),
            IConstructorBodyOperation constructor => ControlFlowGraph.Create(constructor, cancellationToken),
            IBlockOperation block => ControlFlowGraph.Create(block, cancellationToken),
            _ => null
        };
    }

    public static IEnumerable<(ControlFlowGraph Graph, IMethodSymbol Method)> EnumerateGraphs(
        ControlFlowGraph root, IMethodSymbol method, CancellationToken cancellationToken)
    {
        yield return (root, method);
        foreach (var local in root.LocalFunctions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var graph in EnumerateGraphs(root.GetLocalFunctionControlFlowGraph(local, cancellationToken), local, cancellationToken))
                yield return graph;
        }
        foreach (var block in root.Blocks)
        foreach (var operation in block.Operations.Concat(block.BranchValue is null ? Enumerable.Empty<IOperation>() : new[] { block.BranchValue }))
        foreach (var lambda in Descendants(operation).OfType<IFlowAnonymousFunctionOperation>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var graph in EnumerateGraphs(root.GetAnonymousFunctionControlFlowGraph(lambda, cancellationToken), lambda.Symbol, cancellationToken))
                yield return graph;
        }
    }

    private static IEnumerable<IOperation> Descendants(IOperation operation)
    {
        yield return operation;
        foreach (var child in operation.ChildOperations)
        foreach (var descendant in Descendants(child)) yield return descendant;
    }

    public static AnalysisGraph Lower(ControlFlowGraph graph, IMethodSymbol method, MethodSummaryProvider summaries, CancellationToken cancellationToken)
    {
        var lowerer = new OperationLowerer(method, summaries, cancellationToken);
        var lowered = graph.Blocks.ToDictionary(b => b.Ordinal, lowerer.Lower);
        return new GraphExpansion(graph, method, lowerer, lowered, cancellationToken).Expand();
    }

    private static IEnumerable<ControlFlowBranch> Branches(BasicBlock block)
    {
        var constant = block.BranchValue?.ConstantValue;
        if (block.ConditionKind != ControlFlowConditionKind.None && constant is { HasValue: true, Value: bool value })
        {
            var conditional = block.ConditionKind == ControlFlowConditionKind.WhenTrue ? value : !value;
            var branch = conditional ? block.ConditionalSuccessor : block.FallThroughSuccessor;
            if (branch is not null) yield return branch;
            yield break;
        }
        if (block.FallThroughSuccessor is { } fallthrough) yield return fallthrough;
        if (block.ConditionalSuccessor is { } conditionalBranch) yield return conditionalBranch;
    }

    /// <summary>
    /// Roslyn records protected regions and finally continuations separately from normal CFG edges.
    /// This expansion implements the two exception passes: filters observe the state before unwind,
    /// then finally blocks execute before the selected handler. Exceptions in filters mean rejection.
    /// </summary>
    // Roslyn region contract: https://learn.microsoft.com/dotnet/api/microsoft.codeanalysis.flowanalysis.controlflowregionkind
    private sealed class GraphExpansion
    {
        private const int ImplicitExceptionBoundary = -1;
        private const int TerminationBoundary = -2;
        private readonly ControlFlowGraph _graph;
        private readonly IMethodSymbol _method;
        private readonly OperationLowerer _lowerer;
        private readonly Dictionary<int, List<FlowInstruction>> _lowered;
        private readonly CancellationToken _cancellationToken;
        private readonly SourceSite _site;
        private readonly Dictionary<string, Context> _contexts = new(StringComparer.Ordinal);
        private readonly Queue<Context> _pending = new();
        private readonly List<FlowBlock> _blocks = new();
        private int _nextId;
        private int _nextPlanId;

        internal GraphExpansion(ControlFlowGraph graph, IMethodSymbol method, OperationLowerer lowerer,
            Dictionary<int, List<FlowInstruction>> lowered, CancellationToken cancellationToken)
        {
            _graph = graph;
            _method = method;
            _lowerer = lowerer;
            _lowered = lowered;
            _cancellationToken = cancellationToken;
            _site = OperationLowerer.Site(graph.OriginalOperation.Syntax.GetLocation());
        }

        internal AnalysisGraph Expand()
        {
            var entry = Intern(0, ImmutableArray<ResumePoint>.Empty, false, false);
            while (_pending.Count != 0)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var context = _pending.Dequeue();
                if (context.Ordinal < 0)
                {
                    // These analysis boundaries do not contribute normal-exit disposal obligations.
                    // SS0030 reports the separate unhandled-implicit-exception proof boundary.
                    var description = context.Ordinal == ImplicitExceptionBoundary
                        ? "unhandled implicit exception boundary; exception safety is reported separately"
                        : "program termination boundary";
                    _blocks.Add(new FlowBlock(context.Id, new[] { new FlowInstruction(InstructionKind.Nop, _site, description: description) }, new[] { context.Id }));
                    continue;
                }

                var block = _graph.Blocks[context.Ordinal];
                var lowered = _lowered[context.Ordinal];
                var instructions = new List<FlowInstruction>();
                if (context.Id == entry.Id) instructions.AddRange(_lowerer.Seeds);
                var successors = new List<int>();
                var split = false;
                for (var index = context.Offset; index < lowered.Count; index++)
                {
                    var instruction = lowered[index];
                    instructions.Add(instruction);
                    if ((instruction.Flags & InstructionFlags.MayThrow) == 0 || !HasExceptionalTarget(context, block.EnclosingRegion)) continue;
                    successors.Add(Intern(context.Ordinal, context.Continuation, context.Returning, context.Exceptional,
                        context.ImplicitException, context.Filter, index + 1, context.ReturnSite).Id);
                    AddExceptionSuccessors(context, block, true, null, false, successors);
                    split = true;
                    break;
                }

                if (!split)
                {
                    if (block.Kind == BasicBlockKind.Exit)
                    {
                        if (context.Returning && !context.Exceptional)
                            instructions.Add(new FlowInstruction(_lowerer.ReturnsBorrowed ? InstructionKind.Read : InstructionKind.Return, context.ReturnSite ?? _site,
                                source: OperationLowerer.ReturnPlace, description: "validate return after finally cleanup"));
                    }
                    else
                    {
                        foreach (var branch in Branches(block))
                        {
                            var destinations = new List<int>();
                            ExpandBranch(context, block, branch, destinations);
                            foreach (var destination in destinations)
                                successors.Add(RefineNullBranch(context, block, branch, destination));
                        }
                    }
                }
                _blocks.Add(new FlowBlock(context.Id, instructions, successors));
            }
            return new AnalysisGraph(_method.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat), _blocks.OrderBy(block => block.Id), entry.Id, _site);
        }

        private void ExpandBranch(Context context, BasicBlock block, ControlFlowBranch branch, List<int> successors)
        {
            if (branch.Semantics == ControlFlowBranchSemantics.ProgramTermination)
            {
                successors.Add(Intern(TerminationBoundary, ImmutableArray<ResumePoint>.Empty, false, false).Id);
                return;
            }
            if (branch.Semantics is ControlFlowBranchSemantics.Throw or ControlFlowBranchSemantics.Rethrow)
            {
                var thrown = ThrownType(block, branch);
                AddExceptionSuccessors(context, block, false, thrown.Type, thrown.Exact, successors);
                return;
            }
            if (branch.Semantics == ControlFlowBranchSemantics.StructuredExceptionHandling && branch.Destination is null)
            {
                if (context.Filter is { } filter && Contains(filter.Region, context.Ordinal))
                    Dispatch(filter.Plan, filter.RouteIndex + 1, successors);
                else if (context.Continuation.Length != 0)
                {
                    var resume = context.Continuation[0];
                    successors.Add(Intern(resume.Ordinal, context.Continuation.RemoveAt(0), resume.Returning, resume.Exceptional,
                        resume.ImplicitException, resume.Filter, returnSite: resume.ReturnSite).Id);
                }
                return;
            }
            if (branch.Destination is null) return;
            if (context.Filter is { } accepted && Contains(accepted.Region, context.Ordinal)
                && branch.Destination.Ordinal == accepted.Plan.Routes[accepted.RouteIndex].Destination)
            {
                successors.Add(EnterHandler(accepted.Plan, accepted.Plan.Routes[accepted.RouteIndex]));
                return;
            }
            var returning = context.Returning || branch.Semantics == ControlFlowBranchSemantics.Return;
            var returnSite = branch.Semantics == ControlFlowBranchSemantics.Return && block.BranchValue is { } value
                ? OperationLowerer.Site(value.Syntax.GetLocation()) : context.ReturnSite;
            if (branch.FinallyRegions.IsEmpty)
                successors.Add(Intern(branch.Destination.Ordinal, context.Continuation, returning, context.Exceptional,
                    context.ImplicitException, context.Filter, returnSite: returnSite).Id);
            else
            {
                var continuation = branch.FinallyRegions.Skip(1).Select(region => new ResumePoint(region.FirstBlockOrdinal, returning,
                    context.Exceptional, context.ImplicitException, context.Filter, returnSite))
                    .Append(new ResumePoint(branch.Destination.Ordinal, returning, context.Exceptional, context.ImplicitException, context.Filter, returnSite))
                    .Concat(context.Continuation).ToImmutableArray();
                successors.Add(Intern(branch.FinallyRegions[0].FirstBlockOrdinal, continuation, returning, context.Exceptional,
                    context.ImplicitException, context.Filter, returnSite: returnSite).Id);
            }
        }

        private int RefineNullBranch(Context context, BasicBlock block, ControlFlowBranch branch, int destination)
        {
            if (block.ConditionKind == ControlFlowConditionKind.None || _lowerer.NullTest(block) is not { } test) return destination;
            var conditional = ReferenceEquals(branch, block.ConditionalSuccessor);
            var conditionIsTrue = conditional ? block.ConditionKind == ControlFlowConditionKind.WhenTrue : block.ConditionKind == ControlFlowConditionKind.WhenFalse;
            var isNull = conditionIsTrue == test.IsNullWhenTrue;
            var id = NextId();
            _blocks.Add(new FlowBlock(id, new[] { new FlowInstruction(isNull ? InstructionKind.AssumeNull : InstructionKind.AssumeNotNull,
                OperationLowerer.Site(block.BranchValue!.Syntax.GetLocation()), source: test.Place,
                description: "edge assumes " + test.Place + (isNull ? " is null" : " is not null")) }, new[] { destination }));
            return id;
        }

        private bool HasExceptionalTarget(Context context, ControlFlowRegion region)
        {
            if (context.Filter is not null) return true;
            for (var current = region; current.EnclosingRegion is { } parent; current = parent)
                if (current.Kind == ControlFlowRegionKind.Try && parent.Kind is ControlFlowRegionKind.TryAndCatch or ControlFlowRegionKind.TryAndFinally)
                    return true;
            return false;
        }

        private void AddExceptionSuccessors(Context context, BasicBlock block, bool implicitException,
            ITypeSymbol? thrownType, bool exactType, List<int> successors)
        {
            if (context.Filter is { } filter && Contains(filter.Region, context.Ordinal))
            {
                // CLR treats an exception thrown while evaluating a filter as a false result.
                Dispatch(filter.Plan, filter.RouteIndex + 1, successors);
                return;
            }
            var cleanup = ImmutableArray.CreateBuilder<int>();
            var routes = ImmutableArray.CreateBuilder<ExceptionRoute>();
            var handled = false;
            for (var current = block.EnclosingRegion; current.EnclosingRegion is { } parent && !handled; current = parent)
            {
                if (current.Kind != ControlFlowRegionKind.Try) continue;
                if (parent.Kind == ControlFlowRegionKind.TryAndFinally)
                {
                    var finalizer = parent.NestedRegions.First(region => region.Kind == ControlFlowRegionKind.Finally);
                    cleanup.Add(finalizer.FirstBlockOrdinal);
                }
                else if (parent.Kind == ControlFlowRegionKind.TryAndCatch)
                {
                    foreach (var handler in parent.NestedRegions.Where(region => region.Kind is ControlFlowRegionKind.Catch or ControlFlowRegionKind.FilterAndHandler))
                    {
                        var match = MatchException(thrownType, exactType, handler.ExceptionType);
                        if (match == ExceptionMatch.Never) continue;
                        var filterRegion = handler.Kind == ControlFlowRegionKind.FilterAndHandler
                            ? handler.NestedRegions.First(region => region.Kind == ControlFlowRegionKind.Filter) : null;
                        var destination = handler.Kind == ControlFlowRegionKind.FilterAndHandler
                            ? handler.NestedRegions.First(region => region.Kind == ControlFlowRegionKind.Catch).FirstBlockOrdinal : handler.FirstBlockOrdinal;
                        routes.Add(new ExceptionRoute(destination, cleanup.ToImmutable(), filterRegion, match == ExceptionMatch.Always));
                        if (match == ExceptionMatch.Always && filterRegion is null) { handled = true; break; }
                    }
                }
            }
            if (!handled)
                routes.Add(new ExceptionRoute(implicitException ? ImplicitExceptionBoundary : _graph.Blocks.Length - 1,
                    cleanup.ToImmutable(), null, true, true));
            Dispatch(new ExceptionPlan(_nextPlanId++, context, routes.ToImmutable(), implicitException), 0, successors);
        }

        private void Dispatch(ExceptionPlan plan, int start, List<int> successors)
        {
            for (var index = start; index < plan.Routes.Length; index++)
            {
                var route = plan.Routes[index];
                if (route.Filter is { } filter)
                    successors.Add(Intern(filter.FirstBlockOrdinal, ImmutableArray<ResumePoint>.Empty, false, true, plan.ImplicitException,
                        new FilterContinuation(plan, index, filter)).Id);
                else successors.Add(EnterHandler(plan, route));
                if (route.DefiniteMatch) return;
            }
        }

        private int EnterHandler(ExceptionPlan plan, ExceptionRoute route)
        {
            var origin = plan.Origin;
            var preserve = !route.Unhandled && EnclosingFinally(_graph.Blocks[origin.Ordinal].EnclosingRegion) is { } active
                && Contains(active, route.Destination);
            var tail = preserve ? origin.Continuation : ImmutableArray<ResumePoint>.Empty;
            var returning = preserve && origin.Returning;
            var exceptional = route.Unhandled || preserve && origin.Exceptional;
            var implicitException = route.Unhandled ? plan.ImplicitException : preserve && origin.ImplicitException;
            var returnSite = preserve ? origin.ReturnSite : null;
            if (route.Cleanup.IsEmpty)
                return Intern(route.Destination, tail, returning, exceptional, implicitException, returnSite: returnSite).Id;
            var continuation = route.Cleanup.Skip(1).Select(ordinal => new ResumePoint(ordinal, false, true, plan.ImplicitException))
                .Append(new ResumePoint(route.Destination, returning, exceptional, implicitException, returnSite: returnSite)).Concat(tail).ToImmutableArray();
            return Intern(route.Cleanup[0], continuation, false, true, plan.ImplicitException).Id;
        }

        private static ControlFlowRegion? EnclosingFinally(ControlFlowRegion region)
        {
            for (var current = region; current is not null; current = current.EnclosingRegion)
                if (current.Kind == ControlFlowRegionKind.Finally) return current;
            return null;
        }

        private static bool Contains(ControlFlowRegion region, int ordinal) => region.FirstBlockOrdinal <= ordinal && ordinal <= region.LastBlockOrdinal;

        private static (ITypeSymbol? Type, bool Exact) ThrownType(BasicBlock block, ControlFlowBranch branch)
        {
            if (branch.Semantics == ControlFlowBranchSemantics.Rethrow)
            {
                for (var region = block.EnclosingRegion; region is not null; region = region.EnclosingRegion)
                    if (region.Kind == ControlFlowRegionKind.Catch) return (region.ExceptionType, false);
                return (null, false);
            }
            var value = block.BranchValue;
            while (value is IConversionOperation conversion) value = conversion.Operand;
            if (value?.ConstantValue is { HasValue: true, Value: null }) return (null, false);
            return (value?.Type, value is IObjectCreationOperation || value?.Type?.IsSealed == true);
        }

        private static ExceptionMatch MatchException(ITypeSymbol? thrown, bool exact, ITypeSymbol? caught)
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

        private Context Intern(int ordinal, ImmutableArray<ResumePoint> continuation, bool returning, bool exceptional,
            bool implicitException = false, FilterContinuation? filter = null, int offset = 0, SourceSite? returnSite = null)
        {
            var key = ordinal + ":" + offset + ":" + returning + ":" + exceptional + ":" + implicitException + ":" + filter?.Key
                + ":" + returnSite?.Start + ":" + returnSite?.Length
                + ":" + string.Join(";", continuation.Select(point => point.Key));
            if (_contexts.TryGetValue(key, out var existing)) return existing;
            var context = new Context(NextId(), ordinal, offset, continuation, returning, exceptional, implicitException, filter, returnSite);
            _contexts.Add(key, context);
            _pending.Enqueue(context);
            return context;
        }

        private int NextId()
        {
            if (_nextId >= 20000) throw new AnalysisBudgetException("Control-flow normalization exceeded 20,000 continuation contexts.");
            return _nextId++;
        }

        private enum ExceptionMatch { Never, Possible, Always }

        private sealed class ExceptionRoute
        {
            internal ExceptionRoute(int destination, ImmutableArray<int> cleanup, ControlFlowRegion? filter, bool definiteMatch, bool unhandled = false)
            { Destination = destination; Cleanup = cleanup; Filter = filter; DefiniteMatch = definiteMatch; Unhandled = unhandled; }
            internal int Destination { get; }
            internal ImmutableArray<int> Cleanup { get; }
            internal ControlFlowRegion? Filter { get; }
            internal bool DefiniteMatch { get; }
            internal bool Unhandled { get; }
        }

        private sealed class ExceptionPlan
        {
            internal ExceptionPlan(int id, Context origin, ImmutableArray<ExceptionRoute> routes, bool implicitException)
            { Id = id; Origin = origin; Routes = routes; ImplicitException = implicitException; }
            internal int Id { get; }
            internal Context Origin { get; }
            internal ImmutableArray<ExceptionRoute> Routes { get; }
            internal bool ImplicitException { get; }
        }

        private sealed class FilterContinuation
        {
            internal FilterContinuation(ExceptionPlan plan, int routeIndex, ControlFlowRegion region)
            { Plan = plan; RouteIndex = routeIndex; Region = region; Key = plan.Id + ":" + routeIndex; }
            internal ExceptionPlan Plan { get; }
            internal int RouteIndex { get; }
            internal ControlFlowRegion Region { get; }
            internal string Key { get; }
        }

        private sealed class ResumePoint
        {
            internal ResumePoint(int ordinal, bool returning, bool exceptional, bool implicitException, FilterContinuation? filter = null, SourceSite? returnSite = null)
            {
                Ordinal = ordinal; Returning = returning; Exceptional = exceptional; ImplicitException = implicitException; Filter = filter; ReturnSite = returnSite;
                Key = ordinal + ":" + returning + ":" + exceptional + ":" + implicitException + ":" + filter?.Key + ":" + returnSite?.Start + ":" + returnSite?.Length;
            }
            internal int Ordinal { get; }
            internal bool Returning { get; }
            internal bool Exceptional { get; }
            internal bool ImplicitException { get; }
            internal FilterContinuation? Filter { get; }
            internal SourceSite? ReturnSite { get; }
            internal string Key { get; }
        }

        private sealed class Context
        {
            internal Context(int id, int ordinal, int offset, ImmutableArray<ResumePoint> continuation, bool returning, bool exceptional,
                bool implicitException, FilterContinuation? filter, SourceSite? returnSite)
            { Id = id; Ordinal = ordinal; Offset = offset; Continuation = continuation; Returning = returning; Exceptional = exceptional; ImplicitException = implicitException; Filter = filter; ReturnSite = returnSite; }
            internal int Id { get; }
            internal int Ordinal { get; }
            internal int Offset { get; }
            internal ImmutableArray<ResumePoint> Continuation { get; }
            internal bool Returning { get; }
            internal bool Exceptional { get; }
            internal bool ImplicitException { get; }
            internal FilterContinuation? Filter { get; }
            internal SourceSite? ReturnSite { get; }
        }
    }
}

internal sealed class AnalysisBudgetException : Exception
{
    public AnalysisBudgetException(string message) : base(message) { }
}
