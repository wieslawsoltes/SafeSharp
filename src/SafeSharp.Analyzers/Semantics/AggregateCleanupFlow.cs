using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;
using SafeSharp.Analyzers.Analysis;

namespace SafeSharp.Analyzers.Semantics;

/// <summary>
/// Computes remaining obligations at every reachable normal CFG exit. Joins retain any
/// outstanding obligation. FinallyRegions are expanded as explicit cleanup continuations.
/// </summary>
internal sealed class AggregateCleanupFlow
{
    private readonly AggregateCleanupSummary session;
    private readonly IMethodSymbol method;
    private readonly ControlFlowGraph graph;
    private readonly AggregateCleanupState initial = new();
    private readonly Dictionary<string, Node> nodes = new(StringComparer.Ordinal);
    private readonly Queue<Node> queue = new();
    private readonly HashSet<string> queued = new(StringComparer.Ordinal);
    private readonly List<AggregateCleanupState> exits = new();
    private readonly List<AggregateCleanupState> exceptionalExits = new();
    private readonly List<Location> exitSites = new();
    private bool mayThrow;

    public AggregateCleanupFlow(AggregateCleanupSummary session, IMethodSymbol method, ControlFlowGraph graph,
        IReadOnlyDictionary<ISymbol, bool>? booleans)
    {
        this.session = session; this.method = method; this.graph = graph;
        foreach (var member in session.Members) Seed(member, MemberType(member));
        foreach (var parameter in method.OriginalDefinition.Parameters)
            if (session.Symbols.IsDisposable(parameter.Type)) Seed(parameter, parameter.Type);
        if (booleans is not null)
            foreach (var pair in booleans) initial.Symbols[pair.Key] = new CleanupValue(boolean: pair.Value, nullness: CleanupNullness.NotNull);
    }

    public CleanupMethodSummary Analyze()
    {
        Schedule(0, ImmutableArray<int>.Empty, initial);
        while (queue.Count != 0)
        {
            if (!session.Step()) return CleanupMethodSummary.Unknown;
            var node = queue.Dequeue(); queued.Remove(node.Key);
            var state = node.State.Clone(); var block = graph.Blocks[node.Ordinal];
            if (block.Kind == BasicBlockKind.Exit) { exits.Add(state); continue; }
            foreach (var operation in block.Operations)
            {
                Evaluate(operation, state, node);
                if (!state.Reachable) break;
            }
            if (!state.Reachable) continue;
            var value = block.BranchValue is null ? CleanupValue.Unknown : Evaluate(block.BranchValue, state, node);
            if (!state.Reachable) continue;
            foreach (var branch in Branches(block))
            {
                var edge = state.Clone();
                if (block.ConditionKind != ControlFlowConditionKind.None)
                {
                    var condition = branch.IsConditionalSuccessor ? block.ConditionKind == ControlFlowConditionKind.WhenTrue
                        : block.ConditionKind == ControlFlowConditionKind.WhenFalse;
                    if (value.Boolean is { } known && condition != known) continue;
                    Refine(block.BranchValue, condition, edge);
                    if (!edge.Reachable) continue;
                }
                if (branch.Semantics == ControlFlowBranchSemantics.Return)
                {
                    if (IsAsyncLike(method)) edge.Complete(value.Completion);
                    if (block.BranchValue is { } returned) exitSites.Add(returned.Syntax.GetLocation());
                }
                Follow(branch, node, edge);
            }
        }
        var released = initial.Outstanding.Where(resource => exits.All(exit => !exit.Outstanding.Contains(resource)))
            .Select(resource => resource.Slot).ToImmutableHashSet(SymbolEqualityComparer.Default);
        var written = exits.SelectMany(exit => exit.WrittenMembers).ToImmutableHashSet(SymbolEqualityComparer.Default);
        var outstandingNew = exits.SelectMany(exit => exit.Outstanding).Where(resource => !resource.Initial && !resource.Completion)
            .Select(resource => resource.Slot).ToImmutableHashSet(SymbolEqualityComparer.Default);
        var pending = exits.SelectMany(exit => exit.Outstanding).Where(resource => resource.Completion)
            .Select(resource => resource.Slot).ToImmutableHashSet(SymbolEqualityComparer.Default);
        return new CleanupMethodSummary(exits.Count != 0, mayThrow, released, written, outstandingNew, pending,
            exitSites.Concat(method.Locations.Where(location => location.IsInSource)).Distinct().Take(8).ToImmutableArray(),
            initial.Outstanding.Where(resource => exceptionalExits.All(exit => !exit.Outstanding.Contains(resource)))
                .Select(resource => resource.Slot).ToImmutableHashSet(SymbolEqualityComparer.Default),
            exceptionalExits.SelectMany(exit => exit.WrittenMembers).ToImmutableHashSet(SymbolEqualityComparer.Default),
            exceptionalExits.SelectMany(exit => exit.Outstanding).Where(resource => !resource.Initial && !resource.Completion)
                .Select(resource => resource.Slot).ToImmutableHashSet(SymbolEqualityComparer.Default),
            exceptionalExits.SelectMany(exit => exit.Outstanding).Where(resource => resource.Completion)
                .Select(resource => resource.Slot).ToImmutableHashSet(SymbolEqualityComparer.Default));
    }

    private void Seed(ISymbol symbol, ITypeSymbol? type)
    {
        var resource = session.Initial(symbol); initial.Outstanding.Add(resource);
        initial.Symbols[symbol] = new CleanupValue(new[] { resource }, nullness: type?.IsValueType == true
            ? CleanupNullness.NotNull : CleanupNullness.MaybeNull, nullTracksResource: true);
    }

    private CleanupValue Evaluate(IOperation operation, AggregateCleanupState state, Node node)
    {
        if (!state.Reachable) return CleanupValue.Unknown;
        session.CancellationToken.ThrowIfCancellationRequested();
        operation = ContractSymbols.Unwrap(operation);
        if (operation.ConstantValue.HasValue)
        {
            if (operation.ConstantValue.Value is null) return CleanupValue.Null;
            if (operation.ConstantValue.Value is bool boolean) return new CleanupValue(boolean: boolean, nullness: CleanupNullness.NotNull);
        }
        switch (operation)
        {
            case ILocalReferenceOperation local: return state.Get(local.Local);
            case IParameterReferenceOperation parameter: return state.Get(parameter.Parameter.OriginalDefinition);
            case IFlowCaptureReferenceOperation capture: return state.Capture(capture.Id);
            case IFlowCaptureOperation capture: return state.Captures[capture.Id] = Evaluate(capture.Value, state, node);
            case IFieldReferenceOperation field:
                if (field.Instance is not null) Evaluate(field.Instance, state, node);
                return IsOwner(field.Instance) ? state.Get(field.Field.OriginalDefinition) : CleanupValue.Unknown;
            case IPropertyReferenceOperation property:
                if (property.Instance is not null) Evaluate(property.Instance, state, node);
                foreach (var argument in property.Arguments) Evaluate(argument.Value, state, node);
                Exceptional(node, state);
                return IsOwner(property.Instance) && session.Members.Contains(property.Property.OriginalDefinition) ? state.Get(property.Property.OriginalDefinition) : CleanupValue.Unknown;
            case IVariableDeclaratorOperation variable:
                return state.Symbols[variable.Symbol] = variable.Initializer is null ? CleanupValue.Unknown : Evaluate(variable.Initializer.Value, state, node);
            case ISimpleAssignmentOperation assignment:
                var assigned = Evaluate(assignment.Value, state, node);
                Store(assignment.Target, assigned, assignment.Syntax, state); return assigned;
            case IInvocationOperation invocation: return Invoke(invocation, state, node);
            case IAwaitOperation awaited:
                var completion = Evaluate(awaited.Operation, state, node);
                state.Complete(completion.Completion); Exceptional(node, state); return CleanupValue.Unknown;
            case IIsNullOperation isNull:
                var nullable = Evaluate(isNull.Operand, state, node);
                return new CleanupValue(boolean: nullable.Nullness == CleanupNullness.MaybeNull ? null : nullable.Nullness == CleanupNullness.Null);
            case IUnaryOperation unary when unary.OperatorKind == UnaryOperatorKind.Not:
                var operand = Evaluate(unary.Operand, state, node);
                return new CleanupValue(boolean: operand.Boolean is { } known ? !known : null);
            case IBinaryOperation binary:
                var left = Evaluate(binary.LeftOperand, state, node); var right = Evaluate(binary.RightOperand, state, node);
                if (binary.OperatorMethod is not null) { Exceptional(node, state); return CleanupValue.Unknown; }
                return BooleanBinary(binary, left, right);
            case IIsPatternOperation pattern:
                var tested = Evaluate(pattern.Value, state, node);
                return NullPattern(pattern.Pattern, out var nullWhenTrue) && tested.Nullness != CleanupNullness.MaybeNull
                    ? new CleanupValue(boolean: (tested.Nullness == CleanupNullness.Null) == nullWhenTrue) : CleanupValue.Unknown;
            case IConditionalOperation conditional:
                var test = Evaluate(conditional.Condition, state, node);
                if (test.Boolean is true) return Evaluate(conditional.WhenTrue, state, node);
                if (test.Boolean is false) return conditional.WhenFalse is null ? CleanupValue.Unknown : Evaluate(conditional.WhenFalse, state, node);
                var leftState = state.Clone(); var rightState = state.Clone();
                var whenTrue = Evaluate(conditional.WhenTrue, leftState, node);
                var whenFalse = conditional.WhenFalse is null ? CleanupValue.Unknown : Evaluate(conditional.WhenFalse, rightState, node);
                leftState.MergeFrom(rightState); Replace(state, leftState); return CleanupValue.Join(whenTrue, whenFalse);
            case IAnonymousFunctionOperation or IFlowAnonymousFunctionOperation or ILocalFunctionOperation:
                return CleanupValue.Unknown;
            case IObjectCreationOperation creation:
                foreach (var argument in creation.Arguments) Evaluate(argument.Value, state, node);
                Exceptional(node, state);
                if (creation.Initializer is not null) Evaluate(creation.Initializer, state, node);
                return new CleanupValue(nullness: CleanupNullness.NotNull, unknown: true);
            case IArrayCreationOperation array:
                foreach (var dimension in array.DimensionSizes) Evaluate(dimension, state, node);
                var effects = new HashSet<CleanupResource>();
                if (array.Initializer is { } initializer)
                    foreach (var element in initializer.ElementValues) effects.UnionWith(Evaluate(element, state, node).Completion);
                Exceptional(node, state); return new CleanupValue(completion: effects, nullness: CleanupNullness.NotNull);
            case ICollectionExpressionOperation collection:
                var collectionEffects = new HashSet<CleanupResource>();
                foreach (var element in collection.Elements) collectionEffects.UnionWith(Evaluate(element, state, node).Completion);
                return new CleanupValue(completion: collectionEffects, nullness: CleanupNullness.NotNull);
            case ISpreadOperation spread: return Evaluate(spread.Operand, state, node);
            case IReturnOperation returned:
                var result = returned.ReturnedValue is null ? CleanupValue.Unknown : Evaluate(returned.ReturnedValue, state, node);
                if (IsAsyncLike(method)) state.Complete(result.Completion); return result;
            default:
                foreach (var child in operation.ChildOperations) Evaluate(child, state, node);
                return CleanupValue.Unknown;
        }
    }

    private CleanupValue Invoke(IInvocationOperation invocation, AggregateCleanupState state, Node node)
    {
        var instance = invocation.Instance is null ? CleanupValue.Unknown : Evaluate(invocation.Instance, state, node);
        var arguments = invocation.Arguments.Select(argument => Evaluate(argument.Value, state, node)).ToArray();
        var target = invocation.TargetMethod;
        var ownerCall = IsOwner(invocation.Instance) || target.MethodKind == MethodKind.LocalFunction;
        if (ownerCall) target = session.ResolveTarget(invocation);
        if (!ownerCall && SymbolFacts.IsDispose(target)) { state.Release(instance); Exceptional(node, state); return CleanupValue.Unknown; }
        if (!ownerCall && SymbolFacts.IsDisposeAsync(target))
        {
            // The invocation fulfills lifetime release. Only a successfully returned
            // awaitable creates a separate observation obligation (matching SS0017).
            state.Release(instance);
            Exceptional(node, state);
            if (!instance.IsExactResource) return CleanupValue.Unknown;
            var pending = session.Completion(instance.Resources.Single().Slot, invocation.Syntax);
            state.Introduce(pending);
            return new CleanupValue(completion: new[] { pending }, nullness: CleanupNullness.NotNull);
        }
        if (IsCompletionWrapper(target, "ConfigureAwait") || IsCompletionWrapper(target, "AsTask") || IsCompletionWrapper(target, "GetAwaiter")) return instance;
        if (IsCompletionWrapper(target, "GetResult") || IsCompletionWrapper(target, "Wait"))
        { state.Complete(instance.Completion); Exceptional(node, state); return CleanupValue.Unknown; }
        if (target.Name == "WhenAll" && target.ContainingType.ToDisplayString() == "System.Threading.Tasks.Task")
            return new CleanupValue(completion: arguments.SelectMany(argument => argument.Completion), nullness: CleanupNullness.NotNull);
        if (target.DeclaringSyntaxReferences.IsEmpty)
        {
            InvalidateByRefArguments(invocation, state);
            Exceptional(node, state); return CleanupValue.Unknown;
        }
        var constants = new Dictionary<ISymbol, bool>(SymbolEqualityComparer.Default);
        if (target.MethodKind == MethodKind.LocalFunction)
            foreach (var pair in state.Symbols) if (pair.Value.Boolean is { } known) constants[pair.Key] = known;
        for (var index = 0; index < invocation.Arguments.Length; index++)
            if (invocation.Arguments[index].Parameter is { } parameter && arguments[index].Boolean is { } known)
                constants[target.OriginalDefinition.Parameters[parameter.Ordinal]] = known;
        var summary = session.Analyze(target, constants);
        if (ReferenceEquals(summary, CleanupMethodSummary.Unknown)) InvalidateByRefArguments(invocation, state);
        if (summary.MayThrow && !target.IsAsync)
        {
            var exceptional = state.Clone();
            exceptional.Complete(MapReleased(summary.ExceptionalReleased, invocation, arguments, exceptional, ownerCall));
            ApplyWrites(summary.ExceptionalWrittenMembers, summary.ExceptionalOutstandingNewMembers, invocation, exceptional, ownerCall);
            ApplyPending(summary.ExceptionalPendingMembers, invocation, arguments, exceptional, ownerCall);
            Exceptional(node, exceptional);
        }
        if (!summary.ReturnsNormally && !target.IsAsync) { state.Reachable = false; return CleanupValue.Unknown; }
        var releasedSlots = target.IsAsync && summary.MayThrow ? summary.Released.Intersect(summary.ExceptionalReleased) : summary.Released;
        var released = MapReleased(releasedSlots, invocation, arguments, state, ownerCall);
        ApplyPending(summary.PendingMembers, invocation, arguments, state, ownerCall);
        ApplyWrites(summary.WrittenMembers, summary.OutstandingNewMembers, invocation, state, ownerCall);
        if (target.IsAsync && summary.MayThrow)
        {
            ApplyWrites(summary.ExceptionalWrittenMembers, summary.ExceptionalOutstandingNewMembers, invocation, state, ownerCall);
            ApplyPending(summary.ExceptionalPendingMembers, invocation, arguments, state, ownerCall);
        }
        if (IsAsyncLike(target))
        {
            var observation = new HashSet<CleanupResource>(released);
            foreach (var resource in released)
            {
                var pending = session.Completion(resource.Slot, invocation.Syntax); state.Introduce(pending); observation.Add(pending);
            }
            return new CleanupValue(completion: observation, nullness: CleanupNullness.NotNull);
        }
        state.Complete(released);
        return CleanupValue.Unknown;
    }

    private static HashSet<CleanupResource> MapReleased(IEnumerable<ISymbol> slots, IInvocationOperation invocation,
        CleanupValue[] arguments, AggregateCleanupState state, bool ownerCall)
    {
        var released = new HashSet<CleanupResource>();
        foreach (var slot in slots)
        {
            var candidate = MapSlot(slot, invocation, arguments, state, ownerCall);
            if (candidate.IsExactResource) released.UnionWith(candidate.Resources);
        }
        return released;
    }
    private void ApplyPending(IEnumerable<ISymbol> slots, IInvocationOperation invocation, CleanupValue[] arguments,
        AggregateCleanupState state, bool ownerCall)
    {
        foreach (var slot in slots)
        {
            var candidate = MapSlot(slot, invocation, arguments, state, ownerCall);
            if (candidate.IsExactResource) state.Introduce(session.Completion(candidate.Resources.Single().Slot, invocation.Syntax, leaked: true));
        }
    }
    private void ApplyWrites(IEnumerable<ISymbol> writtenSlots, ImmutableHashSet<ISymbol> outstandingNew,
        IInvocationOperation invocation, AggregateCleanupState state, bool ownerCall)
    {
        foreach (var written in writtenSlots)
        {
            ISymbol? storage = null;
            if (written is IParameterSymbol parameter)
            {
                var argument = invocation.Arguments.FirstOrDefault(argument => argument.Parameter?.Ordinal == parameter.Ordinal);
                if (argument is not null) storage = Storage(argument.Value);
            }
            else if (ownerCall) storage = written;
            if (storage is null) continue;
            state.WrittenMembers.Add(storage);
            if (outstandingNew.Contains(written))
            {
                var resource = session.Assigned(storage, invocation.Syntax); state.Introduce(resource);
                state.Symbols[storage] = new CleanupValue(new[] { resource }, nullTracksResource: true);
            }
            else state.Symbols[storage] = CleanupValue.Unknown;
        }
    }

    private static CleanupValue MapSlot(ISymbol symbol, IInvocationOperation invocation, CleanupValue[] arguments,
        AggregateCleanupState state, bool ownerCall)
    {
        if (symbol is not IParameterSymbol parameter) return ownerCall ? state.Get(symbol) : CleanupValue.Unknown;
        var index = Array.FindIndex(invocation.Arguments.ToArray(), argument => argument.Parameter?.Ordinal == parameter.Ordinal);
        return index < 0 ? CleanupValue.Unknown : arguments[index];
    }

    private void InvalidateByRefArguments(IInvocationOperation invocation, AggregateCleanupState state)
    {
        foreach (var argument in invocation.Arguments)
            if (argument.Parameter?.RefKind is RefKind.Ref or RefKind.Out)
                Store(argument.Value, CleanupValue.Unknown, argument.Syntax, state);
    }

    private static ISymbol? Storage(IOperation target) => ContractSymbols.Unwrap(target) switch
    {
        ILocalReferenceOperation local => local.Local, IParameterReferenceOperation parameter => parameter.Parameter.OriginalDefinition,
        IFieldReferenceOperation field when IsOwner(field.Instance) => field.Field.OriginalDefinition,
        IPropertyReferenceOperation property when IsOwner(property.Instance) => property.Property.OriginalDefinition, _ => null,
    };

    private void Store(IOperation target, CleanupValue value, SyntaxNode site, AggregateCleanupState state)
    {
        target = ContractSymbols.Unwrap(target);
        var symbol = Storage(target);
        if (target is IFlowCaptureReferenceOperation capture) { state.Captures[capture.Id] = value; return; }
        if (symbol is null) return;
        if (session.Members.Contains(symbol) || symbol is IParameterSymbol { RefKind: RefKind.Ref or RefKind.Out } parameter && session.Symbols.IsDisposable(parameter.Type))
        {
            state.WrittenMembers.Add(symbol);
            if (value.Resources.IsEmpty && value.Nullness != CleanupNullness.Null)
            {
                var resource = session.Assigned(symbol, site); state.Introduce(resource);
                value = new CleanupValue(new[] { resource }, nullness: value.Nullness, nullTracksResource: true);
            }
        }
        state.Symbols[symbol] = value;
    }

    private void Follow(ControlFlowBranch branch, Node node, AggregateCleanupState state)
    {
        if (branch.Semantics is ControlFlowBranchSemantics.Throw or ControlFlowBranchSemantics.Rethrow) { Exceptional(node, state); return; }
        if (branch.Semantics == ControlFlowBranchSemantics.ProgramTermination) return;
        if (branch.Destination is null)
        {
            if (branch.Semantics == ControlFlowBranchSemantics.StructuredExceptionHandling && !node.Continuation.IsEmpty)
                Schedule(node.Continuation[0], node.Continuation.RemoveAt(0), state);
            return;
        }
        if (branch.FinallyRegions.IsEmpty) Schedule(branch.Destination.Ordinal, node.Continuation, state);
        else Schedule(branch.FinallyRegions[0].FirstBlockOrdinal, branch.FinallyRegions.Skip(1).Select(region => region.FirstBlockOrdinal)
            .Append(branch.Destination.Ordinal).Concat(node.Continuation).ToImmutableArray(), state);
    }

    private void Exceptional(Node node, AggregateCleanupState state)
    {
        if (!state.Reachable) return;
        var finalizers = new List<int>(); var source = graph.Blocks[node.Ordinal].EnclosingRegion;
        // A nested function's root can retain its lexical parent's EnclosingRegion.
        // That parent's block ordinals belong to a different CFG and are never local handlers.
        for (var current = source; !ReferenceEquals(current, graph.Root) && current.EnclosingRegion is { } parent; current = parent)
        {
            if (current.Kind != ControlFlowRegionKind.Try) continue;
            if (parent.Kind == ControlFlowRegionKind.TryAndFinally)
                finalizers.AddRange(parent.NestedRegions.Where(region => region.Kind == ControlFlowRegionKind.Finally).Select(region => region.FirstBlockOrdinal));
            if (parent.Kind != ControlFlowRegionKind.TryAndCatch) continue;
            var catchesAll = false;
            foreach (var handlerRegion in parent.NestedRegions)
            {
                var handler = handlerRegion.Kind == ControlFlowRegionKind.Catch ? handlerRegion
                    : handlerRegion.Kind == ControlFlowRegionKind.FilterAndHandler
                        ? handlerRegion.NestedRegions.FirstOrDefault(region => region.Kind == ControlFlowRegionKind.Catch) : null;
                if (handler is null) continue;
                // Filter effects are not credited. All potentially matching handlers are
                // explored conservatively, including a throwing filter's fallback route.
                var continuation = InsideCurrentFinally(source, handler.FirstBlockOrdinal) ? node.Continuation : ImmutableArray<int>.Empty;
                if (finalizers.Count == 0) Schedule(handler.FirstBlockOrdinal, continuation, state);
                else Schedule(finalizers[0], finalizers.Skip(1).Append(handler.FirstBlockOrdinal).Concat(continuation).ToImmutableArray(), state);
                catchesAll |= handlerRegion.Kind == ControlFlowRegionKind.Catch &&
                    (handler.ExceptionType is null || handler.ExceptionType.ToDisplayString() is "System.Exception" or "object");
            }
            if (catchesAll) return;
        }
        mayThrow = true;
        exceptionalExits.Add(state.Clone());
    }

    private bool InsideCurrentFinally(ControlFlowRegion region, int destination)
    {
        for (var current = region; current is not null && !ReferenceEquals(current, graph.Root); current = current.EnclosingRegion)
            if (current.Kind == ControlFlowRegionKind.Finally && destination >= current.FirstBlockOrdinal && destination <= current.LastBlockOrdinal) return true;
        return false;
    }
    private void Schedule(int ordinal, ImmutableArray<int> continuation, AggregateCleanupState state)
    {
        if (!state.Reachable) return;
        var key = ordinal + ":" + string.Join(",", continuation);
        if (!nodes.TryGetValue(key, out var node))
        {
            if (nodes.Count >= 8192) { session.ExhaustBudget(); return; }
            nodes.Add(key, node = new Node(key, ordinal, continuation, state.Clone()));
        }
        else if (!node.State.MergeFrom(state)) return;
        if (queued.Add(key)) queue.Enqueue(node);
    }
    private static IEnumerable<ControlFlowBranch> Branches(BasicBlock block)
    {
        if (block.FallThroughSuccessor is { } fallthrough) yield return fallthrough;
        if (block.ConditionalSuccessor is { } conditional) yield return conditional;
    }
    private static bool IsOwner(IOperation? instance) => instance is not null && ContractSymbols.Unwrap(instance) is IInstanceReferenceOperation
        { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance };
    private static bool IsAsyncLike(IMethodSymbol target) => target.IsAsync || target.ReturnType is INamedTypeSymbol named &&
        named.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks" && named.Name is "Task" or "ValueTask";
    private static bool IsCompletionWrapper(IMethodSymbol method, string name) => method.Name == name && !method.IsStatic &&
        method.ContainingNamespace.ToDisplayString() is "System.Threading.Tasks" or "System.Runtime.CompilerServices";
    private static ITypeSymbol? MemberType(ISymbol member) => member switch { IFieldSymbol field => field.Type, IPropertySymbol property => property.Type, _ => null };

    private static CleanupValue BooleanBinary(IBinaryOperation binary, CleanupValue left, CleanupValue right)
    {
        bool? result = null;
        if (binary.OperatorKind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals)
        {
            if (left.Boolean.HasValue && right.Boolean.HasValue) result = left.Boolean == right.Boolean;
            else if (left.Nullness == CleanupNullness.Null && right.Nullness != CleanupNullness.MaybeNull) result = right.Nullness == CleanupNullness.Null;
            else if (right.Nullness == CleanupNullness.Null && left.Nullness != CleanupNullness.MaybeNull) result = left.Nullness == CleanupNullness.Null;
            if (result.HasValue && binary.OperatorKind == BinaryOperatorKind.NotEquals) result = !result.Value;
        }
        else if (binary.OperatorKind is BinaryOperatorKind.ConditionalAnd or BinaryOperatorKind.And)
            result = left.Boolean is false || right.Boolean is false ? false : left.Boolean is true && right.Boolean is true ? true : null;
        else if (binary.OperatorKind is BinaryOperatorKind.ConditionalOr or BinaryOperatorKind.Or)
            result = left.Boolean is true || right.Boolean is true ? true : left.Boolean is false && right.Boolean is false ? false : null;
        return new CleanupValue(boolean: result);
    }
    private void Refine(IOperation? test, bool truth, AggregateCleanupState state)
    {
        if (test is null) return;
        test = ContractSymbols.Unwrap(test);
        if (test is IUnaryOperation { OperatorKind: UnaryOperatorKind.Not } unary) { Refine(unary.Operand, !truth, state); return; }
        IOperation? operand = null; var nullWhenTrue = true;
        if (test is IIsNullOperation isNull) operand = isNull.Operand;
        else if (test is IBinaryOperation binary && binary.OperatorKind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals && binary.OperatorMethod is null)
        {
            if (binary.LeftOperand.ConstantValue is { HasValue: true, Value: null }) operand = binary.RightOperand;
            else if (binary.RightOperand.ConstantValue is { HasValue: true, Value: null }) operand = binary.LeftOperand;
            nullWhenTrue = binary.OperatorKind == BinaryOperatorKind.Equals;
        }
        else if (test is IIsPatternOperation pattern && NullPattern(pattern.Pattern, out nullWhenTrue)) operand = pattern.Value;
        if (operand is null) return;
        operand = ContractSymbols.Unwrap(operand); var value = Reference(operand, state); var assumeNull = truth == nullWhenTrue;
        if (value.Nullness == (assumeNull ? CleanupNullness.NotNull : CleanupNullness.Null)) { state.Reachable = false; return; }
        if (assumeNull && value.NullTracksResource) state.Release(value);
        StoreReference(operand, value.Refine(assumeNull ? CleanupNullness.Null : CleanupNullness.NotNull), state);
    }
    private static bool NullPattern(IPatternOperation pattern, out bool nullWhenTrue)
    {
        nullWhenTrue = true;
        if (pattern is INegatedPatternOperation negated) { var recognized = NullPattern(negated.Pattern, out var inner); nullWhenTrue = !inner; return recognized; }
        return pattern is IConstantPatternOperation constant && constant.Value.ConstantValue is { HasValue: true, Value: null };
    }
    private static CleanupValue Reference(IOperation operation, AggregateCleanupState state) => operation switch
    {
        ILocalReferenceOperation local => state.Get(local.Local), IParameterReferenceOperation parameter => state.Get(parameter.Parameter.OriginalDefinition),
        IFieldReferenceOperation field when IsOwner(field.Instance) => state.Get(field.Field.OriginalDefinition),
        IPropertyReferenceOperation property when IsOwner(property.Instance) => state.Get(property.Property.OriginalDefinition),
        IFlowCaptureReferenceOperation capture => state.Capture(capture.Id), _ => CleanupValue.Unknown,
    };
    private static void StoreReference(IOperation operation, CleanupValue value, AggregateCleanupState state)
    {
        switch (operation)
        {
            case ILocalReferenceOperation local: state.Symbols[local.Local] = value; break;
            case IParameterReferenceOperation parameter: state.Symbols[parameter.Parameter.OriginalDefinition] = value; break;
            case IFieldReferenceOperation field when IsOwner(field.Instance): state.Symbols[field.Field.OriginalDefinition] = value; break;
            case IPropertyReferenceOperation property when IsOwner(property.Instance): state.Symbols[property.Property.OriginalDefinition] = value; break;
            case IFlowCaptureReferenceOperation capture: state.Captures[capture.Id] = value; break;
        }
    }
    private static void Replace(AggregateCleanupState target, AggregateCleanupState source)
    {
        target.Symbols.Clear(); foreach (var pair in source.Symbols) target.Symbols.Add(pair.Key, pair.Value);
        target.Captures.Clear(); foreach (var pair in source.Captures) target.Captures.Add(pair.Key, pair.Value);
        target.Outstanding.Clear(); target.Outstanding.UnionWith(source.Outstanding); target.Lost.Clear(); target.Lost.UnionWith(source.Lost);
        target.WrittenMembers.Clear(); target.WrittenMembers.UnionWith(source.WrittenMembers); target.Reachable = source.Reachable;
    }
    private sealed class Node
    {
        public Node(string key, int ordinal, ImmutableArray<int> continuation, AggregateCleanupState state)
        { Key = key; Ordinal = ordinal; Continuation = continuation; State = state; }
        public string Key { get; }
        public int Ordinal { get; }
        public ImmutableArray<int> Continuation { get; }
        public AggregateCleanupState State { get; }
    }
}
