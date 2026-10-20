using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace SafeSharp.Analyzers.Analysis;

/// <summary>
/// Finite may-origin analysis for return values and caller-visible out slots. Value snapshots and
/// aliases of storage have separate domains: assigning through a ref changes its target, whereas
/// ref reassignment changes which target is selected. Unknown origins survive control-flow joins.
/// Cleanup and consuming effects invalidate provenance before caller-visible postconditions are inferred.
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
    private static readonly ImmutableHashSet<string> NullOrigins = Origin(Null);
    private static readonly ImmutableHashSet<string> ReceiverOrigins = Origin(Receiver);
    private static readonly ImmutableHashSet<string> EmptyOrigins = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
    private static readonly ImmutableHashSet<int> ExternalTargets = ImmutableHashSet.Create(ExternalStorage);
    private static readonly ImmutableHashSet<int> UnknownTargets = ImmutableHashSet.Create(UnknownStorage);
    internal static string Parameter(int ordinal) => "$parameter:" + ordinal;

    internal sealed class Result
    {
        internal Result(ImmutableHashSet<string> returns, ImmutableDictionary<int, ImmutableHashSet<string>> outputs,
            bool returnsAsyncPayload = false, ImmutableArray<MethodReturnOrigin> returnOrigins = default,
            ReferenceReturnSummary? referenceReturn = null, ImmutableHashSet<int>? preservedRefParameters = null)
        {
            Returns = returns; Outputs = outputs; ReturnsAsyncPayload = returnsAsyncPayload;
            ReturnOrigins = returnOrigins.IsDefault ? ImmutableArray<MethodReturnOrigin>.Empty : returnOrigins;
            ReferenceReturn = referenceReturn; PreservedRefParameters = preservedRefParameters ?? ImmutableHashSet<int>.Empty;
        }
        internal ImmutableHashSet<string> Returns { get; }
        internal ImmutableDictionary<int, ImmutableHashSet<string>> Outputs { get; }
        internal bool ReturnsAsyncPayload { get; }
        internal ImmutableArray<MethodReturnOrigin> ReturnOrigins { get; }
        internal ReferenceReturnSummary? ReferenceReturn { get; }
        internal ImmutableHashSet<int> PreservedRefParameters { get; }
    }

    internal static Result Analyze(IMethodSymbol method, IOperation? operation, Func<IMethodSymbol, MethodSummary> summary,
        AsyncResultOriginModel asyncResults, CancellationToken cancellationToken)
    {
        if (operation is null) return Opaque();
        var graph = ControlFlowLowerer.TryCreate(operation, cancellationToken);
        if (graph is null) return Opaque();
        if (method.MethodKind == MethodKind.LocalFunction)
        {
            graph = FindLocalGraph(graph, method, cancellationToken);
            if (graph is null) return Opaque();
        }
        try { return new Solver(method, graph, summary, asyncResults, cancellationToken).Analyze(); }
        catch (AnalysisBudgetException) { return Opaque(); }
    }

    private static Result Opaque() => new(UnknownOrigins, ImmutableDictionary<int, ImmutableHashSet<string>>.Empty);
    private static ImmutableHashSet<string> Origin(string value) => ImmutableHashSet.Create(StringComparer.Ordinal, value);

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
        internal Dictionary<int, ImmutableHashSet<string>> Payloads { get; }
        internal HashSet<int> WrittenStorage { get; }
        internal HashSet<string> InvalidOrigins { get; }
        internal HashSet<string> DisposedOrigins { get; }

        internal State()
        {
            Values = new Dictionary<int, ImmutableHashSet<string>>();
            References = new Dictionary<int, ImmutableHashSet<int>>();
            CaptureTargets = new Dictionary<int, ImmutableHashSet<int>>();
            Payloads = new Dictionary<int, ImmutableHashSet<string>>();
            WrittenStorage = new HashSet<int>();
            InvalidOrigins = new HashSet<string>(StringComparer.Ordinal);
            DisposedOrigins = new HashSet<string>(StringComparer.Ordinal);
        }

        private State(State source)
        {
            Values = new Dictionary<int, ImmutableHashSet<string>>(source.Values);
            References = new Dictionary<int, ImmutableHashSet<int>>(source.References);
            CaptureTargets = new Dictionary<int, ImmutableHashSet<int>>(source.CaptureTargets);
            Payloads = new Dictionary<int, ImmutableHashSet<string>>(source.Payloads);
            WrittenStorage = new HashSet<int>(source.WrittenStorage);
            InvalidOrigins = new HashSet<string>(source.InvalidOrigins, StringComparer.Ordinal);
            DisposedOrigins = new HashSet<string>(source.DisposedOrigins, StringComparer.Ordinal);
        }

        internal State Clone() => new(this);
    }

    private sealed class Solver
    {
        private readonly IMethodSymbol method;
        private readonly ControlFlowGraph graph;
        private readonly Func<IMethodSymbol, MethodSummary> summary;
        private readonly AsyncResultOriginModel asyncResults;
        private readonly CancellationToken cancellationToken;
        // Symbols and captures are interned once per method. Reads avoid allocating display strings
        // or hashing source paths, and singleton storage target sets are reused across transfers.
        private readonly Dictionary<ISymbol, int> symbols = new(SymbolEqualityComparer.Default);
        private readonly Dictionary<CaptureId, int> captures = new();
        private readonly Dictionary<IOperation, int> referenceCallSlots = new();
        private readonly List<ImmutableHashSet<int>> singletons = new();
        private readonly HashSet<int> callerReferences = new();
        private readonly HashSet<int> capturedStorage = new();
        private bool capturedMayInvalidateResources;
        // Allocation-site identities bound the carrier graph even when a loop repeatedly wraps
        // an earlier carrier. Nested payloads are edges in this graph, never growing string paths.
        private readonly Dictionary<(IOperation Operation, int Layer), int> carrierSites = new();
        private readonly Dictionary<IOperation, ImmutableHashSet<string>> freshSites = new();
        private readonly HashSet<string> freshTokens = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ImmutableHashSet<string>> movedOrigins = new(StringComparer.Ordinal);
        private SourceControlFlowRouter? router;
        private SourceControlFlowRouter.Context? currentContext;
        private Action<SourceControlFlowRouter.Context, State>? propagate;
        private readonly Dictionary<string, int> carrierTokens = new(StringComparer.Ordinal);
        private readonly List<ImmutableHashSet<string>> carrierOrigins = new();
        private int remaining = 100_000;
        private bool opaque;

        internal Solver(IMethodSymbol method, ControlFlowGraph graph, Func<IMethodSymbol, MethodSummary> summary,
            AsyncResultOriginModel asyncResults, CancellationToken cancellationToken)
        { this.method = method; this.graph = graph; this.summary = summary; this.asyncResults = asyncResults; this.cancellationToken = cancellationToken; }

        internal Result Analyze()
        {
            CollectCapturedStorage();
            if (opaque) return Opaque();
            router = new SourceControlFlowRouter(graph);
            var states = new Dictionary<int, State>();
            var initial = new State();
            foreach (var parameter in method.Parameters)
            {
                var slot = Slot(parameter);
                if (parameter.RefKind != RefKind.Out) initial.Values[slot] = Origin(Parameter(parameter.Ordinal));
                if (parameter.RefKind != RefKind.None) callerReferences.Add(slot);
            }
            var pending = new Queue<int>();
            var queued = new HashSet<int>();
            propagate = (context, incoming) =>
            {
                if (router.IsBoundary(context)) return;
                var changed = !states.TryGetValue(context.Id, out var previous);
                if (changed) states[context.Id] = incoming.Clone();
                else changed = Join(previous!, incoming);
                if (changed && queued.Add(context.Id)) pending.Enqueue(context.Id);
            };
            propagate(router.Entry, initial);
            var iterations = 0;
            while (pending.Count > 0)
            {
                if (!Step() || ++iterations > 8192) return Opaque();
                var id = pending.Dequeue(); queued.Remove(id);
                currentContext = router[id];
                var state = states[id].Clone();
                var block = graph.Blocks[currentContext.Ordinal];
                foreach (var item in block.Operations) Evaluate(item, state);
                if (block.BranchValue is { } branch)
                {
                    if (block.FallThroughSuccessor?.Semantics == ControlFlowBranchSemantics.Return ||
                        block.ConditionalSuccessor?.Semantics == ControlFlowBranchSemantics.Return)
                    {
                        if (method.ReturnsByRef || method.ReturnsByRefReadonly)
                        {
                            state.References[Returned] = Targets(branch, state, 0);
                            state.Values[Returned] = ReadTargets(state.References[Returned], state);
                        }
                        else state.Values[Returned] = Evaluate(branch, state);
                    }
                    else Evaluate(branch, state);
                }
                if (opaque) return Opaque();
                foreach (var outgoing in SourceControlFlowRouter.Branches(block))
                    foreach (var successor in router.Successors(currentContext, outgoing)) propagate(successor, state);
            }
            currentContext = null;
            State? exit = null;
            foreach (var pair in states)
            {
                if (!router.IsNormalExit(router[pair.Key])) continue;
                if (exit is null) exit = pair.Value.Clone(); else Join(exit, pair.Value);
            }
            if (opaque || exit is null) return Opaque();
            var outputs = ImmutableDictionary.CreateBuilder<int, ImmutableHashSet<string>>();
            foreach (var parameter in method.Parameters)
                if (parameter.RefKind is RefKind.Ref or RefKind.Out && exit.Values.TryGetValue(Slot(parameter), out var origins))
                    outputs[parameter.Ordinal] = ExportValues(origins, exit);
            var returned = exit.Values.TryGetValue(Returned, out var returnedValue) ? returnedValue : UnknownOrigins;
            if ((method.ReturnsByRef || method.ReturnsByRefReadonly) && exit.References.TryGetValue(Returned, out var returnedTargets))
                returned = ReadTargets(returnedTargets, exit);
            var shapes = ImmutableHashSet.CreateBuilder<MethodReturnOrigin>();
            ExportOrigins(returned, exit, method.IsAsync && asyncResults.IsCarrier(method.ReturnType) ? 1 : 0,
                new HashSet<int>(), shapes);
            var resultShape = shapes.OrderBy(origin => origin.PayloadDepth).ThenBy(origin => origin.Source, StringComparer.Ordinal).ToImmutableArray();
            var returnsPayload = resultShape.Any(origin => origin.PayloadDepth > 0);
            // Compatibility projections describe exactly one effective-result layer. Deeper
            // structures remain available through ReturnOrigins and cannot manufacture freshness.
            returned = resultShape.Select(origin => origin.PayloadDepth == (returnsPayload ? 1 : 0) ? origin.Source : Unknown)
                .ToImmutableHashSet(StringComparer.Ordinal);
            ReferenceReturnSummary? reference = null;
            if (method.ReturnsByRef || method.ReturnsByRefReadonly)
            {
                var targets = exit.References.TryGetValue(Returned, out var known) ? known : UnknownTargets;
                var parameters = method.Parameters.Where(parameter => parameter.RefKind != RefKind.None && targets.Contains(Slot(parameter)))
                    .Select(parameter => parameter.Ordinal).ToImmutableArray();
                reference = new ReferenceReturnSummary(parameters, targets.Contains(ExternalStorage),
                    targets.Contains(UnknownStorage) || targets.Any(target => target >= 0 && !callerReferences.Contains(target)));
            }
            var preserved = method.Parameters.Where(parameter => parameter.RefKind != RefKind.None && !exit.WrittenStorage.Contains(Slot(parameter)))
                .Select(parameter => parameter.Ordinal).ToImmutableHashSet();
            return opaque ? Opaque() : new Result(returned, outputs.ToImmutable(), returnsPayload, resultShape, reference, preserved);
        }

        private void Fault(State state)
        {
            if (router is null || currentContext is null || propagate is null || !router.HasExceptionalTarget(currentContext)) return;
            foreach (var successor in router.Exceptions(currentContext)) propagate(successor, state);
        }

        private ImmutableHashSet<string> FreshAt(IOperation operation)
        {
            if (freshSites.TryGetValue(operation, out var value)) return value;
            var token = "$allocation:" + freshSites.Count;
            value = Origin(token); freshSites.Add(operation, value); freshTokens.Add(token); return value;
        }

        private ImmutableHashSet<string> ExportValues(ImmutableHashSet<string> origins, State state) => origins
            .Select(origin => state.InvalidOrigins.Contains(origin) ? Unknown : freshTokens.Contains(origin) ? Fresh : carrierTokens.ContainsKey(origin) ? Unknown : origin)
            .ToImmutableHashSet(StringComparer.Ordinal);

        private void ExportOrigins(ImmutableHashSet<string> origins, State state, int depth, HashSet<int> visiting,
            ImmutableHashSet<MethodReturnOrigin>.Builder result)
        {
            foreach (var origin in origins)
            {
                if (!Step()) return;
                if (state.InvalidOrigins.Contains(origin)) { result.Add(new MethodReturnOrigin(Unknown, depth)); continue; }
                if (carrierTokens.TryGetValue(origin, out var carrier))
                {
                    if (depth >= 16 || !visiting.Add(carrier) || !state.Payloads.TryGetValue(carrier, out var payload))
                        result.Add(new MethodReturnOrigin(Unknown, depth));
                    else { ExportOrigins(payload, state, depth + 1, visiting, result); visiting.Remove(carrier); }
                }
                else result.Add(new MethodReturnOrigin(freshTokens.Contains(origin) ? Fresh : origin, depth));
            }
        }

        private void InvalidateOrigins(ImmutableHashSet<string> origins, State state, bool disposed = false)
        {
            foreach (var origin in origins)
                if (origin != Null) state.InvalidOrigins.Add(origin);
            if (!disposed) return;
            var resources = ResourceOrigins(origins);
            state.DisposedOrigins.UnionWith(resources);
            state.InvalidOrigins.UnionWith(resources);
            // Move changes the ownership capability, not the object identity. Disposing an old
            // C# alias still invalidates the value transferred into a suspended return snapshot.
            foreach (var pair in movedOrigins)
                if (pair.Value.Overlaps(resources)) state.InvalidOrigins.Add(pair.Key);
        }

        private ImmutableHashSet<string> ResourceOrigins(ImmutableHashSet<string> origins)
        {
            var result = origins;
            foreach (var origin in origins)
                if (movedOrigins.TryGetValue(origin, out var source)) result = result.Union(source);
            return result;
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

        private int ReferenceCallSlot(IOperation operation)
        {
            if (referenceCallSlots.TryGetValue(operation, out var slot)) return slot;
            slot = NewSlot(); referenceCallSlots.Add(operation, slot); return slot;
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
                        Fault(state);
                        return CallResult(conversion, converter, effect, UnknownOrigins,
                            new Dictionary<int, ImmutableHashSet<string>> { [0] = operand }, state);
                    }
                    // Roslyn represents target-typed new() with an object-creation conversion
                    // which is neither identity nor reference. Its equal typed operand is still
                    // the same freshly constructed value; no additional user code is executed.
                    if (!conversion.IsImplicit && !conversion.IsTryCast) Fault(state);
                    if (conversion.Conversion.IsIdentity || conversion.Conversion.IsReference ||
                        conversion.Operand is IObjectCreationOperation &&
                        SymbolEqualityComparer.Default.Equals(conversion.Operand.Type, conversion.Type))
                        return conversion.IsTryCast ? operand.Add(Null) : operand;
                    return UnknownOrigins;
                }
                case IInstanceReferenceOperation: return ReceiverOrigins;
                case IObjectCreationOperation creation:
                {
                    var arguments = EvaluateCallArguments(creation.Arguments, creation.Constructor is { } constructor ? summary(constructor) : MethodSummary.Unknown, state, depth + 1, out var references);
                    CallFault(creation.Arguments, references, state);
                    if (creation.Initializer is { } initializer) Evaluate(initializer, state, depth + 1);
                    if (creation.Constructor is { } created && arguments.TryGetValue(0, out var argument))
                    {
                        var kind = asyncResults.Classify(created);
                        if (kind == AsyncResultOriginOperation.WrapArgument) return Carrier(creation, argument, state);
                        if (kind == AsyncResultOriginOperation.PreserveArgument)
                            return KnownCarrier(argument) ? argument : UnknownOrigins;
                    }
                    if (asyncResults.IsCarrierAllocation(creation.Type)) return UnknownOrigins;
                    return SymbolFacts.IsTracked(creation.Type) ? FreshAt(creation) : UnknownOrigins;
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
                    if (assignment.Target is IPropertyReferenceOperation or IArrayElementReferenceOperation or IFieldReferenceOperation) Fault(state);
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
                    if (increment.OperatorMethod is not null) { InvalidateCapturedStorage(state); Fault(state); }
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
                    Fault(state);
                    return asyncResults.IsAwaitable(awaited.Operation.Type) ? Payload(value, state) : UnknownOrigins;
                }
                case IInvocationOperation invocation:
                {
                    var receiver = invocation.Instance is { } instance ? Evaluate(instance, state, depth + 1) : UnknownOrigins;
                    var effect = summary(invocation.TargetMethod);
                    var arguments = EvaluateCallArguments(invocation.Arguments, effect, state, depth + 1, out var references);
                    if (SymbolFacts.IsDispose(invocation.TargetMethod) || SymbolFacts.IsDisposeAsync(invocation.TargetMethod))
                        InvalidateOrigins(receiver, state, disposed: true);
                    foreach (var parameter in invocation.TargetMethod.Parameters)
                        if ((effect.DisposedParameters.Contains(parameter.Ordinal) || parameter.RefKind != RefKind.Out && (SymbolFacts.HasAttribute(parameter, "Consumes") || SymbolFacts.HasAttribute(parameter, "Owned"))) &&
                            arguments.TryGetValue(parameter.Ordinal, out var consumed)) InvalidateOrigins(consumed, state);
                    CallFault(invocation.Arguments, references, state);
                    if (invocation.TargetMethod.ReturnsByRef || invocation.TargetMethod.ReturnsByRefReadonly)
                        return ReferenceResult(invocation, effect, references, state);
                    return CallResult(invocation, invocation.TargetMethod, effect, receiver, arguments, state);
                }
                case IPropertyReferenceOperation property when property.Property.GetMethod is { } getter:
                {
                    var receiver = property.Instance is { } instance ? Evaluate(instance, state, depth + 1) : UnknownOrigins;
                    var effect = summary(getter);
                    var arguments = EvaluateCallArguments(property.Arguments, effect, state, depth + 1, out var references);
                    CallFault(property.Arguments, references, state);
                    if (getter.ReturnsByRef || getter.ReturnsByRefReadonly) return ReferenceResult(property, effect, references, state);
                    return CallResult(property, getter, effect, receiver, arguments, state);
                }
                case IUnaryOperation unary:
                    Evaluate(unary.Operand, state, depth + 1);
                    if (unary.OperatorMethod is not null) { InvalidateCapturedStorage(state); Fault(state); }
                    return UnknownOrigins;
                case IBinaryOperation binary:
                    Evaluate(binary.LeftOperand, state, depth + 1); Evaluate(binary.RightOperand, state, depth + 1);
                    if (binary.OperatorMethod is not null) { InvalidateCapturedStorage(state); Fault(state); }
                    else if (binary.IsChecked || binary.OperatorKind is BinaryOperatorKind.Divide or BinaryOperatorKind.Remainder) Fault(state);
                    return UnknownOrigins;
                case IEventAssignmentOperation eventAssignment:
                    Evaluate(eventAssignment.EventReference, state, depth + 1);
                    Evaluate(eventAssignment.HandlerValue, state, depth + 1);
                    InvalidateCapturedStorage(state);
                    Fault(state);
                    return UnknownOrigins;
                case IFieldReferenceOperation field:
                    if (field.Instance is { } fieldInstance) Evaluate(fieldInstance, state, depth + 1);
                    Fault(state);
                    return UnknownOrigins;
                case IArrayElementReferenceOperation element:
                    Evaluate(element.ArrayReference, state, depth + 1);
                    foreach (var index in element.Indices) Evaluate(index, state, depth + 1);
                    Fault(state);
                    return UnknownOrigins;
                case IArrayCreationOperation array:
                    foreach (var child in array.ChildOperations) Evaluate(child, state, depth + 1);
                    Fault(state);
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

        private ImmutableHashSet<string> CallResult(IOperation operation, IMethodSymbol callee, MethodSummary effect,
            ImmutableHashSet<string> receiver, Dictionary<int, ImmutableHashSet<string>> arguments, State state)
        {
            if (callee.ContainingType.ToDisplayString() == "SafeSharp.Ownership" && callee.Name == "Move" && arguments.TryGetValue(0, out var transferred))
            {
                var moved = FreshAt(operation);
                var token = moved.Single();
                var resources = ResourceOrigins(transferred);
                movedOrigins[token] = movedOrigins.TryGetValue(token, out var previous) ? previous.Union(resources) : resources;
                if (state.DisposedOrigins.Overlaps(resources)) state.InvalidOrigins.Add(token);
                return moved;
            }
            switch (asyncResults.Classify(callee))
            {
                case AsyncResultOriginOperation.WrapArgument:
                    return arguments.TryGetValue(0, out var argument) ? Carrier(operation, argument, state) : UnknownOrigins;
                case AsyncResultOriginOperation.PreserveReceiver: return receiver;
                case AsyncResultOriginOperation.UnwrapReceiver: return Payload(receiver, state);
            }
            if (!effect.ReturnOrigins.IsEmpty)
            {
                var result = EmptyOrigins;
                foreach (var origin in effect.ReturnOrigins)
                {
                    var payload = origin.Source == Fresh ? FreshAt(operation) : origin.Source == Receiver ? receiver :
                        origin.Source == Null ? NullOrigins : arguments.FirstOrDefault(pair => Parameter(pair.Key) == origin.Source).Value ?? UnknownOrigins;
                    for (var layer = 0; layer < origin.PayloadDepth; layer++) payload = Carrier(operation, payload, state, layer);
                    result = result.Union(payload);
                }
                return result.IsEmpty ? UnknownOrigins : result;
            }
            if (effect.ReturnsOwned) return effect.ReturnsAsyncPayload && asyncResults.IsCarrier(callee.ReturnType)
                ? Carrier(operation, FreshAt(operation), state) : FreshAt(operation);
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
            if (origins.Count == 0) return UnknownOrigins;
            var value = origins.ToImmutable();
            return effect.ReturnsAsyncPayload && asyncResults.IsCarrier(callee.ReturnType) ? Carrier(operation, value, state) : value;
        }

        private ImmutableHashSet<string> ReferenceResult(IOperation operation, MethodSummary effect,
            Dictionary<int, ImmutableHashSet<int>> references, State state)
        {
            var result = ImmutableHashSet<int>.Empty;
            if (effect.ReferenceReturn is not { } returned) result = UnknownTargets;
            else
            {
                foreach (var ordinal in returned.ParameterOrdinals)
                    result = result.Union(references.TryGetValue(ordinal, out var targets) ? targets : UnknownTargets);
                if (returned.IncludesExternalStorage) result = result.Union(ExternalTargets);
                if (returned.IncludesUnknownStorage) result = result.Union(UnknownTargets);
            }
            if (result.IsEmpty) result = UnknownTargets;
            state.CaptureTargets[ReferenceCallSlot(operation)] = result;
            return ReadTargets(result, state);
        }

        private void CallFault(ImmutableArray<IArgumentOperation> arguments, Dictionary<int, ImmutableHashSet<int>> references, State state)
        {
            if (router is null || currentContext is null || !router.HasExceptionalTarget(currentContext)) return;
            var exceptional = state.Clone();
            // Normal out postconditions do not hold if a call throws after partially writing its
            // arguments. Locations are the snapshots captured before evaluating later arguments.
            foreach (var argument in arguments)
                if (argument.Parameter is { RefKind: RefKind.Ref or RefKind.Out } parameter && references.TryGetValue(parameter.Ordinal, out var targets))
                    Write(targets, UnknownOrigins, exceptional);
            Fault(exceptional);
        }

        private ImmutableHashSet<string> Carrier(IOperation operation, ImmutableHashSet<string> payload, State state, int layer = 0)
        {
            if (!Step(payload.Count + 1)) return UnknownOrigins;
            if (!carrierSites.TryGetValue((operation, layer), out var slot))
            {
                slot = carrierSites.Count;
                var token = "$carrier:" + slot;
                carrierSites.Add((operation, layer), slot); carrierTokens.Add(token, slot); carrierOrigins.Add(Origin(token));
            }
            // A prior loop iteration's carrier can still be reachable through another local.
            // Preserve every possible payload for this allocation site instead of overwriting it.
            state.Payloads[slot] = state.Payloads.TryGetValue(slot, out var previous) ? previous.Union(payload) : payload;
            return carrierOrigins[slot];
        }

        private bool KnownCarrier(ImmutableHashSet<string> origins)
        {
            if (!Step(origins.Count + 1)) return false;
            return origins.Any(carrierTokens.ContainsKey) && origins.All(origin => origin == Null || carrierTokens.ContainsKey(origin));
        }

        private ImmutableHashSet<string> Payload(ImmutableHashSet<string> origins, State state)
        {
            var result = EmptyOrigins;
            foreach (var origin in origins)
            {
                if (!Step()) return UnknownOrigins;
                result = result.Union(origin == Null ? NullOrigins : carrierTokens.TryGetValue(origin, out var slot) &&
                    state.Payloads.TryGetValue(slot, out var payload) ? payload : UnknownOrigins);
            }
            return result.IsEmpty ? UnknownOrigins : result;
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
                    if (evaluate)
                    {
                        if (field.Instance is { } fieldInstance) Evaluate(fieldInstance, state, depth + 1);
                        Fault(state);
                    }
                    return ExternalTargets;
                case IPropertyReferenceOperation property when property.Property.ReturnsByRef || property.Property.ReturnsByRefReadonly:
                    if (evaluate) Evaluate(property, state, depth + 1);
                    return state.CaptureTargets.TryGetValue(ReferenceCallSlot(property), out var propertyTargets) ? propertyTargets : UnknownTargets;
                case IPropertyReferenceOperation property:
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
                        Fault(state);
                    }
                    return ExternalTargets;
                case IInvocationOperation { TargetMethod.ReturnsByRef: true } or IInvocationOperation { TargetMethod.ReturnsByRefReadonly: true }:
                    if (evaluate) Evaluate(operation, state, depth + 1);
                    return state.CaptureTargets.TryGetValue(ReferenceCallSlot(operation), out var callTargets) ? callTargets : UnknownTargets;
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
            state.WrittenStorage.UnionWith(targets);
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
                    state.WrittenStorage.Add(target);
                    state.Values[target] = value.Union(state.Values.TryGetValue(target, out var prior) ? prior : UnknownOrigins);
                }
        }

        private Dictionary<int, ImmutableHashSet<string>> EvaluateCallArguments(ImmutableArray<IArgumentOperation> arguments,
            MethodSummary effect, State state, int depth, out Dictionary<int, ImmutableHashSet<int>> references)
        {
            var values = new Dictionary<int, ImmutableHashSet<string>>();
            references = new Dictionary<int, ImmutableHashSet<int>>();
            foreach (var argument in arguments)
            {
                var ordinal = argument.Parameter?.Ordinal ?? -1;
                if (argument.Parameter is { RefKind: not RefKind.None } && IsAddressable(argument.Value))
                    references[ordinal] = Targets(argument.Value, state, depth + 1);
                else values[ordinal] = Evaluate(argument.Value, state, depth + 1);
            }
            InvalidateCapturedStorage(state);
            // Addresses are selected left-to-right. Their incoming values are read when the call
            // starts, after all argument expressions have run, and before any output is installed.
            foreach (var pair in references) values[pair.Key] = ReadTargets(pair.Value, state);
            var writes = new Dictionary<int, ImmutableHashSet<string>>();
            var definite = new HashSet<int>();
            foreach (var argument in arguments)
            {
                if (argument.Parameter is not { RefKind: RefKind.Ref or RefKind.Out } parameter ||
                    effect.PreservedRefParameters.Contains(parameter.Ordinal) || !references.TryGetValue(parameter.Ordinal, out var targets)) continue;
                var replacement = UnknownOrigins;
                if (effect.OwnedOutputParameters.TryGetValue(parameter.Ordinal, out var nullable))
                {
                    replacement = FreshAt(argument);
                    if (nullable) replacement = replacement.Add(Null);
                }
                else if (effect.ReferenceOutputs.TryGetValue(parameter.Ordinal, out var output))
                {
                    replacement = EmptyOrigins;
                    foreach (var origin in output)
                    {
                        if (origin == Null) replacement = replacement.Add(Null);
                        else if (origin.StartsWith("$parameter:", StringComparison.Ordinal) &&
                            int.TryParse(origin.Substring(11), out var source) && values.TryGetValue(source, out var value))
                            replacement = replacement.Union(value);
                        else replacement = replacement.Add(Unknown);
                    }
                    if (replacement.IsEmpty) replacement = UnknownOrigins;
                }
                AddWrites(targets, replacement, writes, definite);
            }
            // Apply normal-return postconditions together. The order of formal parameters says
            // nothing about a callee's write order when several ref/out actuals share storage.
            foreach (var pair in writes)
            {
                if (!Step()) break;
                state.WrittenStorage.Add(pair.Key);
                state.Values[pair.Key] = definite.Contains(pair.Key) ? pair.Value :
                    pair.Value.Union(state.Values.TryGetValue(pair.Key, out var previous) ? previous : UnknownOrigins);
            }
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
            var writtenCount = target.WrittenStorage.Count;
            target.WrittenStorage.UnionWith(incoming.WrittenStorage);
            changed |= target.WrittenStorage.Count != writtenCount;
            var invalidCount = target.InvalidOrigins.Count;
            target.InvalidOrigins.UnionWith(incoming.InvalidOrigins);
            changed |= target.InvalidOrigins.Count != invalidCount;
            var disposedCount = target.DisposedOrigins.Count;
            target.DisposedOrigins.UnionWith(incoming.DisposedOrigins);
            changed |= target.DisposedOrigins.Count != disposedCount;
            // A payload entry absent on one predecessor represents an absent allocation site,
            // not an unknown payload on the carriers which exist on the other predecessor.
            changed |= JoinMap(target.Payloads, incoming.Payloads, EmptyOrigins);
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
                        if (operation is IInvocationOperation nestedCall)
                        {
                            var target = nestedCall.TargetMethod;
                            capturedMayInvalidateResources |= SymbolFacts.IsDispose(target) || SymbolFacts.IsDisposeAsync(target) ||
                                target.MethodKind == MethodKind.DelegateInvoke || target.Parameters.Any(parameter => parameter.RefKind != RefKind.Out &&
                                    (SymbolFacts.HasAttribute(parameter, "Consumes") || SymbolFacts.HasAttribute(parameter, "Owned"))) ||
                                !summary(target).DisposedParameters.IsEmpty;
                        }
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
                if (state.Values.TryGetValue(slot, out var captured))
                {
                    if (capturedMayInvalidateResources) InvalidateOrigins(captured, state, disposed: true);
                    state.Values[slot] = UnknownOrigins;
                }
        }
    }
}
