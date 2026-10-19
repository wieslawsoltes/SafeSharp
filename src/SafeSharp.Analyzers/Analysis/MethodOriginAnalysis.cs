using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace SafeSharp.Analyzers.Analysis;

/// <summary>
/// Finite may-origin analysis for return values and caller-visible out slots. Value snapshots and
/// aliases of storage have separate domains: assigning through a ref changes its target, whereas
/// ref reassignment changes which target is selected. Unknown origins survive control-flow joins.
/// Effects and lifetime validity are checked by the ownership engine independently of origin inference.
/// </summary>
/// <remarks>
/// Original C# implementation of place-oriented dataflow ideas, not translated Rust source.
/// Rust 1.91.0 uses indexed MIR move paths and separate initialization effects:
/// https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_mir_dataflow/src/move_paths/mod.rs
/// https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_mir_dataflow/src/impls/initialized.rs
/// C# permits aliasing writable ref parameters; their may-alias effects are therefore retained.
/// </remarks>
internal static class MethodOriginAnalysis
{
    internal const string Fresh = "$fresh";
    internal const string Null = "$null";
    internal const string Unknown = "$unknown";
    internal const string Receiver = "$this";
    private const int Returned = -1;
    private const int ExternalStorage = -2;
    private const int UnknownStorage = -3;
    private static readonly ImmutableHashSet<string> UnknownOrigins = Origin(Unknown);
    private static readonly ImmutableHashSet<string> FreshOrigins = Origin(Fresh);
    private static readonly ImmutableHashSet<string> NullOrigins = Origin(Null);
    private static readonly ImmutableHashSet<string> ReceiverOrigins = Origin(Receiver);
    private static readonly ImmutableHashSet<int> ExternalTargets = ImmutableHashSet.Create(ExternalStorage);
    private static readonly ImmutableHashSet<int> UnknownTargets = ImmutableHashSet.Create(UnknownStorage);
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
        if (operation is null || method.ReturnsByRef || method.ReturnsByRefReadonly) return Opaque();
        var graph = ControlFlowLowerer.TryCreate(operation, cancellationToken);
        if (graph is null) return Opaque();
        if (method.MethodKind == MethodKind.LocalFunction)
        {
            graph = FindLocalGraph(graph, method, cancellationToken);
            if (graph is null) return Opaque();
        }
        // A normal-edge proof cannot omit handlers which can return another value or replace an
        // out slot during cleanup. Explicit contracts remain usable for these bodies.
        if (HasHandlers(graph.Root)) return Opaque();
        return new Solver(method, graph, summary, cancellationToken).Analyze();
    }

    private static Result Opaque() => new(UnknownOrigins, ImmutableDictionary<int, ImmutableHashSet<string>>.Empty);
    private static ImmutableHashSet<string> Origin(string value) => ImmutableHashSet.Create(StringComparer.Ordinal, value);
    private static bool HasHandlers(ControlFlowRegion region) => region.Kind is ControlFlowRegionKind.TryAndCatch or
        ControlFlowRegionKind.TryAndFinally || region.NestedRegions.Any(HasHandlers);

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

    private static IOperation UnwrapParentheses(IOperation operation)
    {
        while (operation is IParenthesizedOperation parenthesized) operation = parenthesized.Operand;
        return operation;
    }

    private sealed class State
    {
        internal Dictionary<int, ImmutableHashSet<string>> Values { get; }
        internal Dictionary<int, ImmutableHashSet<int>> References { get; }
        internal Dictionary<int, ImmutableHashSet<int>> CaptureTargets { get; }

        internal State()
        {
            Values = new Dictionary<int, ImmutableHashSet<string>>();
            References = new Dictionary<int, ImmutableHashSet<int>>();
            CaptureTargets = new Dictionary<int, ImmutableHashSet<int>>();
        }

        private State(State source)
        {
            Values = new Dictionary<int, ImmutableHashSet<string>>(source.Values);
            References = new Dictionary<int, ImmutableHashSet<int>>(source.References);
            CaptureTargets = new Dictionary<int, ImmutableHashSet<int>>(source.CaptureTargets);
        }

        internal State Clone() => new(this);
    }

    private sealed class Solver
    {
        private readonly IMethodSymbol method;
        private readonly ControlFlowGraph graph;
        private readonly Func<IMethodSymbol, MethodSummary> summary;
        private readonly CancellationToken cancellationToken;
        // Symbols and captures are interned once per method. Reads avoid allocating display strings
        // or hashing source paths, and singleton storage target sets are reused across transfers.
        private readonly Dictionary<ISymbol, int> symbols = new(SymbolEqualityComparer.Default);
        private readonly Dictionary<CaptureId, int> captures = new();
        private readonly List<ImmutableHashSet<int>> singletons = new();
        private readonly HashSet<int> callerReferences = new();
        private readonly HashSet<int> capturedStorage = new();
        private int remaining = 100_000;
        private bool opaque;

        internal Solver(IMethodSymbol method, ControlFlowGraph graph, Func<IMethodSymbol, MethodSummary> summary, CancellationToken cancellationToken)
        { this.method = method; this.graph = graph; this.summary = summary; this.cancellationToken = cancellationToken; }

        internal Result Analyze()
        {
            CollectCapturedStorage();
            if (opaque) return Opaque();
            var states = new State?[graph.Blocks.Length];
            var initial = new State();
            foreach (var parameter in method.Parameters)
            {
                var slot = Slot(parameter);
                if (parameter.RefKind != RefKind.Out) initial.Values[slot] = Origin(Parameter(parameter.Ordinal));
                if (parameter.RefKind != RefKind.None) callerReferences.Add(slot);
            }
            states[0] = initial;
            var pending = new Queue<int>();
            var queued = new bool[graph.Blocks.Length];
            pending.Enqueue(0); queued[0] = true;
            var iterations = 0;
            while (pending.Count > 0)
            {
                if (!Step() || ++iterations > 4096) return Opaque();
                var id = pending.Dequeue(); queued[id] = false;
                var state = states[id]!.Clone();
                var block = graph.Blocks[id];
                foreach (var item in block.Operations) Evaluate(item, state);
                if (block.BranchValue is { } branch)
                {
                    var value = Evaluate(branch, state);
                    if (block.FallThroughSuccessor?.Semantics == ControlFlowBranchSemantics.Return ||
                        block.ConditionalSuccessor?.Semantics == ControlFlowBranchSemantics.Return)
                        state.Values[Returned] = value;
                }
                if (opaque) return Opaque();
                foreach (var successor in Successors(block))
                {
                    var changed = states[successor] is null;
                    if (changed) states[successor] = state.Clone();
                    else changed = Join(states[successor]!, state);
                    if (changed && !queued[successor]) { pending.Enqueue(successor); queued[successor] = true; }
                }
            }
            if (opaque || states[states.Length - 1] is not { } exit) return Opaque();
            var outputs = ImmutableDictionary.CreateBuilder<int, ImmutableHashSet<string>>();
            foreach (var parameter in method.Parameters)
                if (parameter.RefKind == RefKind.Out && exit.Values.TryGetValue(Slot(parameter), out var origins)) outputs[parameter.Ordinal] = origins;
            return new Result(exit.Values.TryGetValue(Returned, out var returned) ? returned : UnknownOrigins, outputs.ToImmutable());
        }

        private int Slot(ISymbol symbol)
        {
            if (symbols.TryGetValue(symbol, out var slot)) return slot;
            slot = NewSlot(); symbols.Add(symbol, slot); return slot;
        }

        private int CaptureSlot(CaptureId capture)
        {
            if (captures.TryGetValue(capture, out var slot)) return slot;
            slot = NewSlot(); captures.Add(capture, slot); return slot;
        }

        private int NewSlot()
        { var slot = singletons.Count; singletons.Add(ImmutableHashSet.Create(slot)); return slot; }

        private int? Address(IOperation operation) => UnwrapParentheses(operation) switch
        {
            IParameterReferenceOperation parameter => Slot(parameter.Parameter),
            ILocalReferenceOperation local => Slot(local.Local),
            IFlowCaptureReferenceOperation capture => CaptureSlot(capture.Id),
            IDeclarationExpressionOperation declaration => Address(declaration.Expression),
            _ => null
        };

        private bool Step(int count = 1, int depth = 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (count > remaining || depth > 128) opaque = true;
            else remaining -= count;
            return !opaque;
        }

        private ImmutableHashSet<string> Read(int slot, State state) => state.References.TryGetValue(slot, out var targets)
            ? ReadTargets(targets, state) : state.Values.TryGetValue(slot, out var value) ? value : UnknownOrigins;

        private ImmutableHashSet<string> ReadTargets(ImmutableHashSet<int> targets, State state)
        {
            var value = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
            foreach (var target in targets)
            {
                if (!Step()) return UnknownOrigins;
                value = value.Union(target >= 0 && state.Values.TryGetValue(target, out var known) ? known : UnknownOrigins);
            }
            return value.IsEmpty ? UnknownOrigins : value;
        }

        private ImmutableHashSet<string> Evaluate(IOperation operation, State state, int depth = 0)
        {
            if (!Step(depth: depth)) return UnknownOrigins;
            operation = UnwrapParentheses(operation);
            if (operation.ConstantValue is { HasValue: true, Value: null }) return NullOrigins;
            if (Address(operation) is { } address) return Read(address, state);
            switch (operation)
            {
                case IConversionOperation conversion:
                {
                    var operand = Evaluate(conversion.Operand, state, depth + 1);
                    if (conversion.OperatorMethod is { } converter)
                    {
                        InvalidateCapturedStorage(state);
                        var effect = summary(converter);
                        if (effect.ReturnsOwned) return FreshOrigins;
                        return effect.BorrowedSources.Length == 1 && converter.Parameters.Length == 1 &&
                            effect.BorrowedFrom == converter.Parameters[0].Name ? operand : UnknownOrigins;
                    }
                    if (conversion.Conversion.IsIdentity || conversion.Conversion.IsReference)
                        return conversion.IsTryCast ? operand.Add(Null) : operand;
                    return UnknownOrigins;
                }
                case IInstanceReferenceOperation: return ReceiverOrigins;
                case IObjectCreationOperation creation:
                {
                    EvaluateCallArguments(creation.Arguments, creation.Constructor is { } constructor ? summary(constructor) : MethodSummary.Unknown, state, depth + 1);
                    if (creation.Initializer is { } initializer) Evaluate(initializer, state, depth + 1);
                    return SymbolFacts.IsTracked(creation.Type) ? FreshOrigins : UnknownOrigins;
                }
                case IVariableDeclaratorOperation declaration when declaration.Initializer is { } initializer:
                {
                    if (declaration.Symbol.RefKind != RefKind.None)
                    {
                        var targets = Targets(initializer.Value, state, depth + 1);
                        state.References[Slot(declaration.Symbol)] = targets;
                        return ReadTargets(targets, state);
                    }
                    return state.Values[Slot(declaration.Symbol)] = Evaluate(initializer.Value, state, depth + 1);
                }
                case ISimpleAssignmentOperation assignment when assignment.IsRef:
                {
                    var targets = Targets(assignment.Value, state, depth + 1);
                    if (Address(assignment.Target) is { } target) state.References[target] = targets;
                    else opaque = true;
                    return ReadTargets(targets, state);
                }
                case ISimpleAssignmentOperation assignment:
                {
                    // C# fixes the target storage before evaluating the RHS. A ref reassignment
                    // or another call in that RHS must not redirect this pending write.
                    var targets = Targets(assignment.Target, state, depth + 1);
                    var value = Evaluate(assignment.Value, state, depth + 1);
                    if (assignment.Target is IPropertyReferenceOperation) InvalidateCapturedStorage(state);
                    Write(targets, value, state);
                    return value;
                }
                case IAssignmentOperation assignment:
                {
                    var targets = AssignmentTargets(assignment.Target, state, depth + 1);
                    Evaluate(assignment.Value, state, depth + 1);
                    InvalidateCapturedStorage(state);
                    Write(targets, UnknownOrigins, state);
                    return UnknownOrigins;
                }
                case IIncrementOrDecrementOperation increment:
                {
                    var targets = Targets(increment.Target, state, depth + 1);
                    var original = ReadTargets(targets, state);
                    if (increment.OperatorMethod is not null) InvalidateCapturedStorage(state);
                    Write(targets, UnknownOrigins, state);
                    return increment.IsPostfix ? original : UnknownOrigins;
                }
                case IFlowCaptureOperation capture:
                {
                    var value = Evaluate(capture.Value, state, depth + 1);
                    var slot = CaptureSlot(capture.Id);
                    state.Values[slot] = value;
                    // A capture's ordinary read is an evaluated value snapshot. Only lvalue/ref
                    // consumers use these saved targets; later ref rebinding cannot change them.
                    state.CaptureTargets[slot] = Targets(capture.Value, state, depth + 1, evaluate: false);
                    return value;
                }
                case IConditionalOperation conditional:
                {
                    Evaluate(conditional.Condition, state, depth + 1);
                    var alternate = state.Clone();
                    var first = Evaluate(conditional.WhenTrue, state, depth + 1);
                    var second = conditional.WhenFalse is { } other ? Evaluate(other, alternate, depth + 1) : UnknownOrigins;
                    Join(state, alternate);
                    return first.Union(second);
                }
                case ICoalesceOperation coalesce:
                {
                    var first = Evaluate(coalesce.Value, state, depth + 1).Remove(Null);
                    var alternate = state.Clone();
                    var second = Evaluate(coalesce.WhenNull, alternate, depth + 1);
                    Join(state, alternate);
                    return first.Union(second);
                }
                case IAwaitOperation awaited:
                {
                    var value = Evaluate(awaited.Operation, state, depth + 1);
                    InvalidateCapturedStorage(state);
                    return value;
                }
                case IInvocationOperation invocation:
                {
                    var receiver = invocation.Instance is { } instance ? Evaluate(instance, state, depth + 1) : UnknownOrigins;
                    var effect = summary(invocation.TargetMethod);
                    var arguments = EvaluateCallArguments(invocation.Arguments, effect, state, depth + 1);
                    if (invocation.TargetMethod.ReturnsByRef || invocation.TargetMethod.ReturnsByRefReadonly) { opaque = true; return UnknownOrigins; }
                    return CallResult(invocation.TargetMethod, effect, receiver, arguments);
                }
                case IPropertyReferenceOperation property when property.Property.GetMethod is { } getter:
                {
                    var receiver = property.Instance is { } instance ? Evaluate(instance, state, depth + 1) : UnknownOrigins;
                    var effect = summary(getter);
                    var arguments = EvaluateCallArguments(property.Arguments, effect, state, depth + 1);
                    if (getter.ReturnsByRef || getter.ReturnsByRefReadonly) { opaque = true; return UnknownOrigins; }
                    return CallResult(getter, effect, receiver, arguments);
                }
                case IUnaryOperation unary:
                    Evaluate(unary.Operand, state, depth + 1);
                    if (unary.OperatorMethod is not null) InvalidateCapturedStorage(state);
                    return UnknownOrigins;
                case IBinaryOperation binary:
                    Evaluate(binary.LeftOperand, state, depth + 1); Evaluate(binary.RightOperand, state, depth + 1);
                    if (binary.OperatorMethod is not null) InvalidateCapturedStorage(state);
                    return UnknownOrigins;
                case IEventAssignmentOperation eventAssignment:
                    Evaluate(eventAssignment.EventReference, state, depth + 1);
                    Evaluate(eventAssignment.HandlerValue, state, depth + 1);
                    InvalidateCapturedStorage(state);
                    return UnknownOrigins;
                case IDynamicInvocationOperation or IDynamicObjectCreationOperation or IDynamicMemberReferenceOperation or
                    IDynamicIndexerAccessOperation or IFunctionPointerInvocationOperation or IAddressOfOperation:
                    opaque = true; return UnknownOrigins;
                case IAnonymousFunctionOperation or IFlowAnonymousFunctionOperation or ILocalFunctionOperation:
                    return UnknownOrigins;
            }
            foreach (var child in operation.ChildOperations) Evaluate(child, state, depth + 1);
            return UnknownOrigins;
        }

        private ImmutableHashSet<string> CallResult(IMethodSymbol callee, MethodSummary effect, ImmutableHashSet<string> receiver,
            Dictionary<int, ImmutableHashSet<string>> arguments)
        {
            if (effect.ReturnsOwned) return FreshOrigins;
            var origins = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
            foreach (var source in effect.BorrowedSources)
            {
                if (source == "this") origins.UnionWith(receiver);
                else
                {
                    var parameter = callee.Parameters.FirstOrDefault(parameter => parameter.Name == source);
                    origins.UnionWith(parameter is not null && arguments.TryGetValue(parameter.Ordinal, out var origin) ? origin : UnknownOrigins);
                }
            }
            return origins.Count == 0 ? UnknownOrigins : origins.ToImmutable();
        }

        private ImmutableHashSet<int> Targets(IOperation operation, State state, int depth, bool evaluate = true)
        {
            if (!Step(depth: depth)) return UnknownTargets;
            operation = UnwrapParentheses(operation);
            if (Address(operation) is { } address)
            {
                if (operation is IFlowCaptureReferenceOperation)
                    return state.CaptureTargets.TryGetValue(address, out var captured) ? captured : UnknownTargets;
                return state.References.TryGetValue(address, out var referenced) ? referenced : singletons[address];
            }
            switch (operation)
            {
                case IDiscardOperation: return ImmutableHashSet<int>.Empty;
                case ISimpleAssignmentOperation { IsRef: true } assignment:
                    if (evaluate) Evaluate(assignment, state, depth + 1);
                    return Address(assignment.Target) is { } target && state.References.TryGetValue(target, out var targets) ? targets : UnknownTargets;
                case IConditionalOperation { IsRef: true } conditional:
                {
                    if (evaluate) Evaluate(conditional.Condition, state, depth + 1);
                    var alternate = state.Clone();
                    var first = Targets(conditional.WhenTrue, state, depth + 1, evaluate);
                    var second = conditional.WhenFalse is { } other ? Targets(other, alternate, depth + 1, evaluate) : UnknownTargets;
                    Join(state, alternate);
                    return first.Union(second);
                }
                case IFieldReferenceOperation field:
                    if (evaluate && field.Instance is { } fieldInstance) Evaluate(fieldInstance, state, depth + 1);
                    return ExternalTargets;
                case IPropertyReferenceOperation property:
                    if (property.Property.ReturnsByRef || property.Property.ReturnsByRefReadonly) opaque = true;
                    if (evaluate)
                    {
                        if (property.Instance is { } propertyInstance) Evaluate(propertyInstance, state, depth + 1);
                        foreach (var argument in property.Arguments) Evaluate(argument.Value, state, depth + 1);
                    }
                    return ExternalTargets;
                case IArrayElementReferenceOperation element:
                    if (evaluate)
                    {
                        Evaluate(element.ArrayReference, state, depth + 1);
                        foreach (var index in element.Indices) Evaluate(index, state, depth + 1);
                    }
                    return ExternalTargets;
                case IInvocationOperation { TargetMethod.ReturnsByRef: true } or IInvocationOperation { TargetMethod.ReturnsByRefReadonly: true }:
                    opaque = true; return UnknownTargets;
                default:
                    if (evaluate) Evaluate(operation, state, depth + 1);
                    return UnknownTargets;
            }
        }

        private ImmutableHashSet<int> AssignmentTargets(IOperation target, State state, int depth)
        {
            target = UnwrapParentheses(target);
            if (target is IDeclarationExpressionOperation declaration) return AssignmentTargets(declaration.Expression, state, depth + 1);
            if (target is not ITupleOperation tuple) return Targets(target, state, depth);
            var targets = ImmutableHashSet<int>.Empty;
            foreach (var element in tuple.Elements) targets = targets.Union(AssignmentTargets(element, state, depth + 1));
            return targets;
        }

        private void Write(ImmutableHashSet<int> targets, ImmutableHashSet<string> value, State state)
        {
            if (targets.Contains(UnknownStorage)) { opaque = true; return; }
            var direct = targets.Count == 1;
            foreach (var target in targets)
            {
                if (!Step()) return;
                if (target >= 0)
                    state.Values[target] = direct ? value : value.Union(state.Values.TryGetValue(target, out var prior) ? prior : UnknownOrigins);
            }
            if (targets.Contains(ExternalStorage) || targets.Any(callerReferences.Contains))
                foreach (var target in callerReferences)
                {
                    // Different C# ref/in/out formals may denote the same caller storage. A write
                    // definitely replaces its selected formal and may replace all other formals.
                    if (targets.Contains(target)) continue;
                    state.Values[target] = value.Union(state.Values.TryGetValue(target, out var prior) ? prior : UnknownOrigins);
                }
        }

        private Dictionary<int, ImmutableHashSet<string>> EvaluateCallArguments(ImmutableArray<IArgumentOperation> arguments,
            MethodSummary effect, State state, int depth)
        {
            var values = new Dictionary<int, ImmutableHashSet<string>>();
            var references = new Dictionary<int, ImmutableHashSet<int>>();
            var writes = new Dictionary<int, ImmutableHashSet<string>>();
            var definite = new HashSet<int>();
            foreach (var argument in arguments)
            {
                var ordinal = argument.Parameter?.Ordinal ?? -1;
                if (argument.Parameter is { RefKind: not RefKind.None } parameter && IsAddressable(argument.Value))
                {
                    var targets = Targets(argument.Value, state, depth + 1);
                    references[ordinal] = targets;
                    if (parameter.RefKind is RefKind.Out or RefKind.Ref)
                    {
                        var replacement = effect.OwnedOutputParameters.TryGetValue(ordinal, out var nullable) ? FreshOrigins : UnknownOrigins;
                        if (nullable) replacement = replacement.Add(Null);
                        AddWrites(targets, replacement, writes, definite);
                    }
                }
                else values[ordinal] = Evaluate(argument.Value, state, depth + 1);
            }
            InvalidateCapturedStorage(state);
            // Apply normal-return postconditions together. The order of formal parameters says
            // nothing about a callee's write order when several ref/out actuals share storage.
            foreach (var pair in writes)
            {
                if (!Step()) break;
                state.Values[pair.Key] = definite.Contains(pair.Key) ? pair.Value :
                    pair.Value.Union(state.Values.TryGetValue(pair.Key, out var previous) ? previous : UnknownOrigins);
            }
            foreach (var pair in references) values[pair.Key] = ReadTargets(pair.Value, state);
            return values;
        }

        private void AddWrites(ImmutableHashSet<int> targets, ImmutableHashSet<string> value,
            Dictionary<int, ImmutableHashSet<string>> writes, HashSet<int> definite)
        {
            if (targets.Contains(UnknownStorage)) { opaque = true; return; }
            foreach (var target in targets)
            {
                if (target < 0) continue;
                writes[target] = writes.TryGetValue(target, out var other) ? other.Union(value) : value;
                if (targets.Count == 1) definite.Add(target);
            }
            if (targets.Contains(ExternalStorage) || targets.Any(callerReferences.Contains))
                foreach (var target in callerReferences)
                    writes[target] = writes.TryGetValue(target, out var other) ? other.Union(value) : value;
        }

        private static bool IsAddressable(IOperation operation) => UnwrapParentheses(operation) is
            ILocalReferenceOperation or IParameterReferenceOperation or IFlowCaptureReferenceOperation or IDeclarationExpressionOperation or
            IFieldReferenceOperation or IPropertyReferenceOperation or IArrayElementReferenceOperation or
            IConditionalOperation { IsRef: true } or ISimpleAssignmentOperation { IsRef: true } or
            IInvocationOperation { TargetMethod.ReturnsByRef: true } or IInvocationOperation { TargetMethod.ReturnsByRefReadonly: true };

        private bool Join(State target, State incoming)
        {
            var changed = JoinMap(target.Values, incoming.Values, UnknownOrigins);
            changed |= JoinMap(target.References, incoming.References, UnknownTargets);
            changed |= JoinMap(target.CaptureTargets, incoming.CaptureTargets, UnknownTargets);
            return changed;
        }

        private bool JoinMap<T>(Dictionary<int, ImmutableHashSet<T>> target, Dictionary<int, ImmutableHashSet<T>> incoming, ImmutableHashSet<T> unknown)
        {
            var changed = false;
            // Snapshot only keys missing from this predecessor; updating a Dictionary during its
            // enumeration is unsupported in analyzer hosts running on the .NET Framework.
            List<int>? missing = null;
            foreach (var pair in target)
            {
                if (!Step(pair.Value.Count + 1)) return changed;
                if (!incoming.ContainsKey(pair.Key) && !unknown.IsSubsetOf(pair.Value)) (missing ??= new List<int>()).Add(pair.Key);
            }
            if (missing is not null)
                foreach (var key in missing) { target[key] = target[key].Union(unknown); changed = true; }
            foreach (var pair in incoming)
            {
                if (!Step(pair.Value.Count + 1)) return changed;
                var existing = target.TryGetValue(pair.Key, out var known) ? known : unknown;
                var joined = existing.Union(pair.Value);
                if (known is not null && known.SetEquals(joined)) continue;
                target[pair.Key] = joined; changed = true;
            }
            return changed;
        }

        private void CollectCapturedStorage()
        {
            var graphs = new Stack<(ControlFlowGraph Graph, bool Nested)>();
            graphs.Push((graph, false));
            while (graphs.Count > 0)
            {
                if (!Step()) return;
                var item = graphs.Pop();
                foreach (var local in item.Graph.LocalFunctions)
                    graphs.Push((item.Graph.GetLocalFunctionControlFlowGraph(local, cancellationToken), true));
                var operations = new Stack<IOperation>();
                foreach (var block in item.Graph.Blocks)
                {
                    foreach (var operation in block.Operations) operations.Push(operation);
                    if (block.BranchValue is { } branch) operations.Push(branch);
                }
                while (operations.Count > 0)
                {
                    if (!Step()) return;
                    var operation = operations.Pop();
                    if (operation is IFlowAnonymousFunctionOperation anonymous)
                    { graphs.Push((item.Graph.GetAnonymousFunctionControlFlowGraph(anonymous, cancellationToken), true)); continue; }
                    if (operation is IAnonymousFunctionOperation or ILocalFunctionOperation) continue;
                    if (item.Nested)
                    {
                        var symbol = operation switch
                        {
                            ILocalReferenceOperation local => (ISymbol)local.Local,
                            IParameterReferenceOperation parameter => parameter.Parameter,
                            _ => null
                        };
                        if (symbol is not null && SymbolEqualityComparer.Default.Equals(symbol.ContainingSymbol.OriginalDefinition, method.OriginalDefinition))
                            capturedStorage.Add(Slot(symbol));
                    }
                    foreach (var child in operation.ChildOperations) operations.Push(child);
                }
            }
        }

        private void InvalidateCapturedStorage(State state)
        {
            // A local function or previously escaped delegate can rewrite a captured variable.
            // Until callback mutation summaries exist, an executable call/suspension invalidates
            // captured cells while already-evaluated argument/receiver snapshots remain intact.
            foreach (var slot in capturedStorage)
                if (state.Values.ContainsKey(slot)) state.Values[slot] = UnknownOrigins;
        }
    }
}
