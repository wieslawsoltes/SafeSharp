using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace SafeSharp.Analyzers.Analysis;

/// <summary>
/// Finite may-origin analysis for return values and caller-visible out slots. Unknown origins
/// are retained at joins: one fresh branch never establishes ownership for an unknown branch.
/// Effects and lifetime validity are checked by the ownership engine independently of origin inference.
/// Compound/deconstruction writes and replacement operators invalidate affected value slots;
/// the pre-update result of a postfix operator remains separately provable.
/// </summary>
internal static class MethodOriginAnalysis
{
    internal const string Fresh = "$fresh";
    internal const string Null = "$null";
    internal const string Unknown = "$unknown";
    internal const string Receiver = "$this";
    private const string Returned = "$return";
    internal static string Parameter(int ordinal) => "$parameter:" + ordinal;

    internal sealed class Result
    {
        internal Result(ImmutableHashSet<string> returns, ImmutableDictionary<int, ImmutableHashSet<string>> outputs)
        { Returns = returns; Outputs = outputs; }
        internal ImmutableHashSet<string> Returns { get; }
        internal ImmutableDictionary<int, ImmutableHashSet<string>> Outputs { get; }
    }

    internal static Result Analyze(IMethodSymbol method, IOperation? operation, Func<IMethodSymbol, MethodSummary> summary,
        CancellationToken cancellationToken)
    {
        var unknown = ImmutableHashSet.Create(StringComparer.Ordinal, Unknown);
        Result Opaque() => new(unknown, ImmutableDictionary<int, ImmutableHashSet<string>>.Empty);
        if (operation is null) return Opaque();
        var graph = ControlFlowLowerer.TryCreate(operation, cancellationToken);
        if (graph is null) return Opaque();
        if (method.MethodKind == MethodKind.LocalFunction)
        {
            graph = FindLocalGraph(graph, method, cancellationToken);
            if (graph is null) return Opaque();
        }
        // A normal-edge origin proof must not omit handlers which can supply another return value.
        // These bodies retain explicit contracts until exception-normalized summary inference is available.
        if (HasHandlers(graph.Root) || HasReferenceStorageAliases(graph, cancellationToken)) return Opaque();
        var states = new Dictionary<string, ImmutableHashSet<string>>?[graph.Blocks.Length];
        states[0] = new Dictionary<string, ImmutableHashSet<string>>(StringComparer.Ordinal);
        foreach (var parameter in method.Parameters.Where(parameter => parameter.RefKind != RefKind.Out))
            states[0]![ParameterSlot(parameter)] = ImmutableHashSet.Create(StringComparer.Ordinal, Parameter(parameter.Ordinal));
        var pending = new Queue<int>();
        var queued = new HashSet<int> { 0 };
        pending.Enqueue(0);
        var iterations = 0;
        var budget = new OperationBudget();
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++iterations > 4096) return Opaque();
            var id = pending.Dequeue(); queued.Remove(id);
            var state = new Dictionary<string, ImmutableHashSet<string>>(states[id]!, StringComparer.Ordinal);
            var block = graph.Blocks[id];
            foreach (var item in block.Operations) Evaluate(item, state, summary, cancellationToken, budget);
            if (block.BranchValue is { } branch)
            {
                var value = Evaluate(branch, state, summary, cancellationToken, budget);
                if (block.FallThroughSuccessor?.Semantics == ControlFlowBranchSemantics.Return ||
                    block.ConditionalSuccessor?.Semantics == ControlFlowBranchSemantics.Return)
                    state[Returned] = value;
            }
            if (budget.Exhausted) return Opaque();
            foreach (var successor in Successors(block))
            {
                if (states[successor] is null)
                {
                    states[successor] = new Dictionary<string, ImmutableHashSet<string>>(state, StringComparer.Ordinal);
                    if (queued.Add(successor)) pending.Enqueue(successor);
                    continue;
                }
                var target = states[successor]!;
                var changed = false;
                foreach (var key in target.Keys.Concat(state.Keys).Distinct(StringComparer.Ordinal).ToArray())
                {
                    var existing = target.TryGetValue(key, out var left) ? left : unknown;
                    var incoming = state.TryGetValue(key, out var right) ? right : unknown;
                    var joined = existing.Union(incoming);
                    if (left is not null && left.SetEquals(joined)) continue;
                    target[key] = joined; changed = true;
                }
                if (changed && queued.Add(successor)) pending.Enqueue(successor);
            }
        }
        var exit = states[graph.Blocks.Length - 1];
        if (exit is null) return Opaque();
        var outputs = ImmutableDictionary.CreateBuilder<int, ImmutableHashSet<string>>();
        foreach (var parameter in method.Parameters.Where(parameter => parameter.RefKind == RefKind.Out))
            if (exit.TryGetValue(ParameterSlot(parameter), out var origins)) outputs[parameter.Ordinal] = origins;
        return new Result(exit.TryGetValue(Returned, out var returned) ? returned : unknown, outputs.ToImmutable());
    }

    private static bool HasHandlers(ControlFlowRegion region) => region.Kind is ControlFlowRegionKind.TryAndCatch or
        ControlFlowRegionKind.TryAndFinally || region.NestedRegions.Any(HasHandlers);

    private static bool HasReferenceStorageAliases(ControlFlowGraph graph, CancellationToken cancellationToken)
    {
        var pending = new Stack<IOperation>(graph.Blocks.SelectMany(block => block.Operations)
            .Concat(graph.Blocks.Where(block => block.BranchValue is not null).Select(block => block.BranchValue!)));
        var remaining = 100_000;
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (--remaining < 0) return true;
            var operation = pending.Pop();
            // The origin domain represents value slots, not aliases of caller/local storage.
            // Retain an opaque summary rather than treating a ref-local write as an independent slot.
            if (operation is ILocalReferenceOperation { Local.RefKind: not RefKind.None } or ISimpleAssignmentOperation { IsRef: true }) return true;
            if (operation is IAnonymousFunctionOperation or IFlowAnonymousFunctionOperation or ILocalFunctionOperation) continue;
            foreach (var child in operation.ChildOperations) pending.Push(child);
        }
        return false;
    }

    private static ControlFlowGraph? FindLocalGraph(ControlFlowGraph graph, IMethodSymbol method, CancellationToken cancellationToken)
    {
        foreach (var local in graph.LocalFunctions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nested = graph.GetLocalFunctionControlFlowGraph(local, cancellationToken);
            if (SymbolEqualityComparer.Default.Equals(local.OriginalDefinition, method.OriginalDefinition)) return nested;
            if (FindLocalGraph(nested, method, cancellationToken) is { } match) return match;
        }
        return null;
    }

    private static IEnumerable<int> Successors(BasicBlock block)
    {
        if (block.BranchValue?.ConstantValue is { HasValue: true, Value: bool condition } && block.ConditionKind != ControlFlowConditionKind.None)
        {
            var selected = condition == (block.ConditionKind == ControlFlowConditionKind.WhenTrue) ? block.ConditionalSuccessor : block.FallThroughSuccessor;
            if (selected?.Destination is { } destination) yield return destination.Ordinal;
            yield break;
        }
        if (block.FallThroughSuccessor?.Destination is { } first) yield return first.Ordinal;
        if (block.ConditionalSuccessor?.Destination is { } second && second.Ordinal != block.FallThroughSuccessor?.Destination?.Ordinal) yield return second.Ordinal;
    }

    private static string ParameterSlot(IParameterSymbol parameter)
    {
        // Local-function CFGs can reference captured outer parameters. Ordinals are only unique
        // inside their declaring method and must not alias an unrelated local-function input.
        var owner = parameter.ContainingSymbol.OriginalDefinition;
        return "parameter-slot:" + owner.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + "@" +
            owner.Locations.FirstOrDefault(location => location.IsInSource)?.SourceSpan.Start + ":" + parameter.Ordinal;
    }

    private static string? Address(IOperation operation)
    {
        operation = UnwrapParentheses(operation);
        return operation switch
        {
            IParameterReferenceOperation parameter => ParameterSlot(parameter.Parameter),
            ILocalReferenceOperation local => "local:" + local.Local.Name + "@" + local.Local.Locations.FirstOrDefault()?.SourceSpan.Start,
            IFlowCaptureReferenceOperation capture => "capture:" + capture.Id.GetHashCode(),
            IDeclarationExpressionOperation declaration => Address(declaration.Expression),
            _ => null
        };
    }

    private static IOperation UnwrapParentheses(IOperation operation)
    {
        while (operation is IParenthesizedOperation parenthesized) operation = parenthesized.Operand;
        return operation;
    }

    private sealed class OperationBudget
    {
        private int _remaining = 100_000;
        internal bool Exhausted { get; private set; }
        internal bool Visit(int depth) => !(Exhausted |= --_remaining < 0 || depth > 128);
    }

    private static ImmutableHashSet<string> Evaluate(IOperation operation, Dictionary<string, ImmutableHashSet<string>> state,
        Func<IMethodSymbol, MethodSummary> summary, CancellationToken cancellationToken, OperationBudget budget, int depth = 0)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!budget.Visit(depth)) return ImmutableHashSet.Create(StringComparer.Ordinal, Unknown);
        operation = UnwrapParentheses(operation);
        ImmutableHashSet<string> One(string value) => ImmutableHashSet.Create(StringComparer.Ordinal, value);
        if (operation.ConstantValue is { HasValue: true, Value: null }) return One(Null);
        if (Address(operation) is { } address)
            return state.TryGetValue(address, out var known) ? known : One(Unknown);
        switch (operation)
        {
            case IConversionOperation conversion:
            {
                var operand = Evaluate(conversion.Operand, state, summary, cancellationToken, budget, depth + 1);
                if (conversion.OperatorMethod is { } method)
                {
                    // A user-defined conversion executes a method. Its returned value can be a
                    // different object, so the operand's origin is not its result postcondition.
                    var effect = summary(method);
                    if (effect.ReturnsOwned) return One(Fresh);
                    return effect.BorrowedSources.Length == 1 && method.Parameters.Length == 1 &&
                        effect.BorrowedFrom == method.Parameters[0].Name ? operand : One(Unknown);
                }
                if (conversion.Conversion.IsIdentity || conversion.Conversion.IsReference)
                    return conversion.IsTryCast ? operand.Add(Null) : operand;
                return One(Unknown);
            }
            case IInstanceReferenceOperation: return One(Receiver);
            case IObjectCreationOperation creation:
            {
                EvaluateCallArguments(creation.Arguments, creation.Constructor is { } constructor ? summary(constructor) : MethodSummary.Unknown,
                    state, summary, cancellationToken, budget, depth + 1);
                if (creation.Initializer is { } initializer) Evaluate(initializer, state, summary, cancellationToken, budget, depth + 1);
                return One(SymbolFacts.IsTracked(creation.Type) ? Fresh : Unknown);
            }
            case IVariableDeclaratorOperation declaration when declaration.Initializer is { } initializer:
            {
                var value = Evaluate(initializer.Value, state, summary, cancellationToken, budget, depth + 1);
                state["local:" + declaration.Symbol.Name + "@" + declaration.Symbol.Locations.FirstOrDefault()?.SourceSpan.Start] = value;
                return value;
            }
            case ISimpleAssignmentOperation assignment:
            {
                // Evaluating an assignment target can itself call a ref-returning method or
                // replace a slot through a receiver/index expression before the RHS is evaluated.
                Evaluate(assignment.Target, state, summary, cancellationToken, budget, depth + 1);
                var value = Evaluate(assignment.Value, state, summary, cancellationToken, budget, depth + 1);
                if (Address(assignment.Target) is { } target) state[target] = value;
                return value;
            }
            case IAssignmentOperation assignment:
            {
                Evaluate(assignment.Target, state, summary, cancellationToken, budget, depth + 1);
                Evaluate(assignment.Value, state, summary, cancellationToken, budget, depth + 1);
                InvalidateTargets(assignment.Target, state, cancellationToken, budget, depth + 1);
                return One(Unknown);
            }
            case IIncrementOrDecrementOperation increment:
            {
                var original = Evaluate(increment.Target, state, summary, cancellationToken, budget, depth + 1);
                InvalidateTargets(increment.Target, state, cancellationToken, budget, depth + 1);
                return increment.IsPostfix ? original : One(Unknown);
            }
            case IFlowCaptureOperation capture:
                return state["capture:" + capture.Id.GetHashCode()] = Evaluate(capture.Value, state, summary, cancellationToken, budget, depth + 1);
            case IConditionalOperation conditional:
                return Evaluate(conditional.WhenTrue, state, summary, cancellationToken, budget, depth + 1).Union(
                    conditional.WhenFalse is { } other ? Evaluate(other, state, summary, cancellationToken, budget, depth + 1) : One(Unknown));
            case ICoalesceOperation coalesce:
                return Evaluate(coalesce.Value, state, summary, cancellationToken, budget, depth + 1).Remove(Null)
                    .Union(Evaluate(coalesce.WhenNull, state, summary, cancellationToken, budget, depth + 1));
            case IAwaitOperation awaited:
                return Evaluate(awaited.Operation, state, summary, cancellationToken, budget, depth + 1);
            case IInvocationOperation invocation:
            {
                var receiver = invocation.Instance is { } instance ? Evaluate(instance, state, summary, cancellationToken, budget, depth + 1) : One(Unknown);
                var effect = summary(invocation.TargetMethod);
                var arguments = EvaluateCallArguments(invocation.Arguments, effect, state, summary, cancellationToken, budget, depth + 1);
                if (effect.ReturnsOwned) return One(Fresh);
                var origins = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
                foreach (var source in effect.BorrowedSources)
                {
                    if (source == "this") origins.UnionWith(receiver);
                    else
                    {
                        var parameter = invocation.TargetMethod.Parameters.FirstOrDefault(parameter => parameter.Name == source);
                        origins.UnionWith(parameter is not null && arguments.TryGetValue(parameter.Ordinal, out var origin) ? origin : One(Unknown));
                    }
                }
                return origins.Count == 0 ? One(Unknown) : origins.ToImmutable();
            }
            case IPropertyReferenceOperation property when property.Property.GetMethod is { } getter:
            {
                var receiver = property.Instance is { } instance ? Evaluate(instance, state, summary, cancellationToken, budget, depth + 1) : One(Unknown);
                foreach (var argument in property.Arguments) Evaluate(argument.Value, state, summary, cancellationToken, budget, depth + 1);
                var effect = summary(getter);
                if (effect.ReturnsOwned) return One(Fresh);
                if (effect.BorrowedFrom == "this") return receiver;
                return One(Unknown);
            }
            case IAnonymousFunctionOperation or IFlowAnonymousFunctionOperation or ILocalFunctionOperation:
                return One(Unknown);
        }
        foreach (var child in operation.ChildOperations) Evaluate(child, state, summary, cancellationToken, budget, depth + 1);
        return One(Unknown);
    }

    private static void InvalidateTargets(IOperation target, Dictionary<string, ImmutableHashSet<string>> state,
        CancellationToken cancellationToken, OperationBudget budget, int depth)
    {
        var pending = new Stack<(IOperation Operation, int Depth)>();
        pending.Push((target, depth));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = pending.Pop();
            if (!budget.Visit(item.Depth)) return;
            var operation = SymbolFacts.Unwrap(item.Operation);
            if (Address(operation) is { } address)
                state[address] = ImmutableHashSet.Create(StringComparer.Ordinal, Unknown);
            else if (operation is ITupleOperation tuple)
                foreach (var element in tuple.Elements) pending.Push((element, item.Depth + 1));
            else if (operation is IDeclarationExpressionOperation declaration)
                pending.Push((declaration.Expression, item.Depth + 1));
        }
    }

    private static Dictionary<int, ImmutableHashSet<string>> EvaluateCallArguments(ImmutableArray<IArgumentOperation> arguments,
        MethodSummary effect, Dictionary<string, ImmutableHashSet<string>> state, Func<IMethodSymbol, MethodSummary> summary,
        CancellationToken cancellationToken, OperationBudget budget, int depth)
    {
        var values = new Dictionary<int, ImmutableHashSet<string>>();
        foreach (var argument in arguments)
            values[argument.Parameter?.Ordinal ?? -1] = Evaluate(argument.Value, state, summary, cancellationToken, budget, depth + 1);
        foreach (var argument in arguments.Where(argument => argument.Parameter?.RefKind is RefKind.Out or RefKind.Ref))
        {
            // A callee can replace a ref/out slot. An input origin is not a postcondition about it.
            var parameter = argument.Parameter!;
            var replacement = effect.OwnedOutputParameters.TryGetValue(parameter.Ordinal, out var nullable)
                ? ImmutableHashSet.Create(StringComparer.Ordinal, Fresh) : ImmutableHashSet.Create(StringComparer.Ordinal, Unknown);
            if (nullable) replacement = replacement.Add(Null);
            if (Address(argument.Value) is { } output) state[output] = replacement;
            values[parameter.Ordinal] = replacement;
        }
        return values;
    }

}
