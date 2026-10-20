using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;
using SafeSharp.Analyzers.Analysis;

namespace SafeSharp.Analyzers.Semantics;

/// <summary>
/// Forward must-capability analysis over exception-normalized Roslyn CFGs. Protected references
/// carry obligations after leaving the original member access; ordinary immutable copies do not.
/// This is an original C# adaptation of guard capabilities, not a port of Rust's type system:
/// https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/library/std/src/sync/poison/mutex.rs
/// Unlike a Rust MutexGuard, C# Monitor ownership can be released explicitly and is thread-affine.
/// </summary>
internal sealed partial class SynchronizationAnalysis
{
    private readonly SynchronizationContracts contracts;
    private readonly ConcurrencyContracts types;
    private readonly ControlFlowGraph graph;
    private readonly IMethodSymbol method;
    private readonly SourceControlFlowRouter router;
    private readonly CancellationToken cancellationToken;
    private readonly Dictionary<ISymbol, int> slots = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<CaptureId, int> captures = new();
    private readonly Dictionary<IOperation, int> allocations = new();
    private readonly Dictionary<IOperation, (int Receiver, GuardValue[] Arguments)> evaluatedTargets = new();
    private readonly Dictionary<ISymbol, int> fieldIds = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<(int Receiver, int Field), int> projections = new();
    private readonly Dictionary<int, string> identities = new();
    private readonly Dictionary<(int Context, string Scopes), SynchronizationState> inputs = new();
    private readonly Dictionary<string, Diagnostic> diagnostics = new(StringComparer.Ordinal);
    private readonly HashSet<ISymbol> capturedLocals = new(SymbolEqualityComparer.Default);
    private readonly HashSet<ISymbol> mutableCapturedLocals = new(SymbolEqualityComparer.Default);
    private readonly SynchronizationState entry = new();
    private readonly Queue<(int Context, string Scopes)> pending = new();
    private readonly HashSet<(int Context, string Scopes)> queued = new();
    private int nextSlot;
    private int nextIdentity = 1;
    private readonly int receiverIdentity;
    private readonly bool[] repeatedBlocks;
    private int steps;
    private readonly int maximumSteps;
    private bool reporting;
    private SourceControlFlowRouter.Context context = null!;

    private SynchronizationAnalysis(SynchronizationContracts contracts, ConcurrencyContracts types,
        ControlFlowGraph graph, IMethodSymbol method, int maximumSteps, CancellationToken cancellationToken)
    {
        this.contracts = contracts; this.types = types; this.graph = graph; this.method = method;
        this.maximumSteps = maximumSteps; this.cancellationToken = cancellationToken;
        router = new SourceControlFlowRouter(graph, Math.Min(maximumSteps, 4096));
        repeatedBlocks = new bool[graph.Blocks.Length];
        foreach (var block in graph.Blocks)
        foreach (var branch in SourceControlFlowRouter.Branches(block))
            if (branch.Destination is { } destination && destination.Ordinal <= block.Ordinal)
                for (var index = destination.Ordinal; index <= block.Ordinal; index++) repeatedBlocks[index] = true;
        DiscoverLexicalLocks();
        receiverIdentity = Identity("this");
        foreach (var parameter in method.Parameters)
            entry.Values[Slot(parameter)] = new GuardValue(Identity(parameter.Name));
        foreach (var contract in contracts.Required(method).Where(value => value.Error is null))
        {
            var identity = Resolve(contract, receiverIdentity, method.Parameters.Select(parameter => entry.Get(Slot(parameter))).ToArray());
            if (identity != 0)
            {
                var key = Key(identity, contract.Protocol, contract.Write);
                entry.Held[key] = entry.Expected[key] = new GuardHold(1, 1, contract.Location);
                if (contract.Protocol == GuardProtocol.SemaphoreSlim) entry.SemaphoreMutexes.Add(identity);
            }
        }
    }

    internal static void Analyze(OperationBlockAnalysisContext context, SynchronizationContracts contracts, ConcurrencyContracts types)
    {
        if (context.OwningSymbol is not IMethodSymbol method || context.OperationBlocks.IsEmpty) return;
        if (!Relevant(context.OperationBlocks, method, contracts, context.CancellationToken)) return;
        var root = context.OperationBlocks.Select(operation => ControlFlowLowerer.TryCreate(operation, context.CancellationToken))
            .FirstOrDefault(candidate => candidate is not null);
        if (root is null) return;
        foreach (var item in ControlFlowLowerer.EnumerateGraphs(root, method, context.CancellationToken))
        {
            var maximum = ConcurrencyContracts.MaximumSteps(context.Options.AnalyzerConfigOptionsProvider, item.Graph.OriginalOperation.Syntax.SyntaxTree);
            if (!Relevant(item.Graph, item.Method, contracts, context.CancellationToken)) continue;
            try
            {
                var analysis = new SynchronizationAnalysis(contracts, types, item.Graph, item.Method, maximum, context.CancellationToken);
                foreach (var diagnostic in analysis.Run()) context.ReportDiagnostic(diagnostic);
            }
            catch (AnalysisBudgetException)
            {
                context.ReportDiagnostic(Diagnostic.Create(ConcurrencyDiagnostics.IncompleteContractAnalysis,
                    item.Method.Locations.FirstOrDefault() ?? item.Graph.OriginalOperation.Syntax.GetLocation(), item.Method.Name,
                    "synchronization control-flow/operation"));
            }
        }
    }

    private static bool Relevant(ControlFlowGraph graph, IMethodSymbol method, SynchronizationContracts contracts, CancellationToken cancellationToken)
    {
        return Relevant(graph.Blocks.SelectMany(block => block.BranchValue is { } branch ? block.Operations.Add(branch) : block.Operations),
            method, contracts, cancellationToken);
    }

    private static bool Relevant(IEnumerable<IOperation> operations, IMethodSymbol method, SynchronizationContracts contracts, CancellationToken cancellationToken)
    {
        if (!contracts.Required(method).IsEmpty || !contracts.Returns(method).IsEmpty) return true;
        var pending = new Stack<IOperation>(operations);
        while (pending.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var operation = pending.Pop();
            if (operation is ILockOperation) return true;
            if (operation is IObjectCreationOperation { Constructor: { } constructor } && !contracts.Required(constructor).IsEmpty) return true;
            if (operation is IMethodReferenceOperation reference && !contracts.Required(reference.Method).IsEmpty) return true;
            if (operation is IPropertyReferenceOperation property &&
                (property.Property.GetMethod is { } getter && !contracts.Required(getter).IsEmpty ||
                 property.Property.SetMethod is { } setter && !contracts.Required(setter).IsEmpty)) return true;
            var operatorMethod = operation switch
            {
                IConversionOperation conversion => conversion.OperatorMethod,
                IBinaryOperation binary => binary.OperatorMethod,
                IUnaryOperation unary => unary.OperatorMethod,
                ICompoundAssignmentOperation assignment => assignment.OperatorMethod,
                IIncrementOrDecrementOperation increment => increment.OperatorMethod,
                _ => null
            };
            if (operatorMethod is not null && !contracts.Required(operatorMethod).IsEmpty) return true;
            if (operation is IMemberReferenceOperation member && !contracts.Members(member.Member).IsEmpty) return true;
            if (operation is IInvocationOperation call && (contracts.IsMonitor(call.TargetMethod) || contracts.IsThreadingLock(call.TargetMethod) ||
                contracts.IsReaderWriter(call.TargetMethod) || contracts.IsSemaphore(call.TargetMethod) || !contracts.Required(call.TargetMethod).IsEmpty || !contracts.Returns(call.TargetMethod).IsEmpty)) return true;
            foreach (var child in operation.ChildOperations) pending.Push(child);
        }
        return false;
    }

    private IEnumerable<Diagnostic> Run()
    {
        CollectCaptures();
        var initial = FlowPoint(router.Entry, entry); inputs.Add(initial, entry.Clone()); Enqueue(initial);
        while (pending.Count != 0)
        {
            Step(); var id = pending.Dequeue(); queued.Remove(id); context = router[id.Context];
            if (router.IsBoundary(context) || router.IsNormalExit(context)) continue;
            Transfer(inputs[id].Clone());
        }
        // Emit only from the fixed point. Early worklist states are not diagnostic evidence.
        reporting = true;
        foreach (var input in inputs.OrderBy(pair => pair.Key.Context).ThenBy(pair => pair.Key.Scopes, StringComparer.Ordinal).ToArray())
        {
            Step(); context = router[input.Key.Context];
            if (router.IsBoundary(context) || router.IsNormalExit(context)) ValidateExit(input.Value, context.ReturnOperation);
            else Transfer(input.Value.Clone());
        }
        return diagnostics.Values.OrderBy(value => value.Location.SourceSpan.Start).ThenBy(value => value.GetMessage(), StringComparer.Ordinal);
    }

    private void Transfer(SynchronizationState state)
    {
        var block = graph.Blocks[context.Ordinal];
        foreach (var operation in block.Operations) Evaluate(operation, state);
        var returnedByReference = method.ReturnsByRef || method.ReturnsByRefReadonly;
        var value = block.BranchValue is null ? GuardValue.Unknown : Evaluate(block.BranchValue, state,
            preserve: returnedByReference && block.FallThroughSuccessor?.Semantics == ControlFlowBranchSemantics.Return);
        foreach (var branch in SourceControlFlowRouter.Branches(block))
        {
            var next = state.Clone();
            if (block.ConditionKind != ControlFlowConditionKind.None)
            {
                var truth = ReferenceEquals(branch, block.ConditionalSuccessor)
                    ? block.ConditionKind == ControlFlowConditionKind.WhenTrue : block.ConditionKind == ControlFlowConditionKind.WhenFalse;
                if (!Assume(next, value, truth)) continue;
            }
            if (branch.Semantics == ControlFlowBranchSemantics.Return) next.Returned = value;
            foreach (var destination in router.Successors(context, branch)) Propagate(destination, next);
        }
    }

    private GuardValue Evaluate(IOperation operation, SynchronizationState state, bool write = false, bool preserve = false, int depth = 0)
    {
        Step(); CloseLexicalScopes(state, operation); if (depth > 256) throw new AnalysisBudgetException("Synchronization operation depth exceeded 256.");
        GuardValue Child(IOperation value, bool mutation = false, bool keep = false) => Evaluate(value, state, mutation, keep, depth + 1);
        switch (operation)
        {
            case INameOfOperation: return GuardValue.Unknown;
            case ILiteralOperation literal: return new GuardValue(0, boolean: literal.ConstantValue.Value as bool?);
            case IDefaultValueOperation: return new GuardValue(0, boolean: operation.Type?.SpecialType == SpecialType.System_Boolean ? false : null);
            case IInstanceReferenceOperation: return new GuardValue(receiverIdentity);
            case ILocalReferenceOperation local:
            {
                var value = state.Get(Slot(local.Local)); Validate(value, operation, state, write);
                return !preserve && local.Local.RefKind != RefKind.None && types.IsKnownImmutable(local.Type!) ?
                    new GuardValue(value.Identity, boolean: value.Boolean, condition: value.Condition, scope: value.Scope) : value;
            }
            case IParameterReferenceOperation parameter:
            {
                var value = state.Get(Slot(parameter.Parameter)); Validate(value, operation, state, write);
                return !preserve && parameter.Parameter.RefKind != RefKind.None && types.IsKnownImmutable(parameter.Type!) ?
                    new GuardValue(value.Identity, boolean: value.Boolean, condition: value.Condition, scope: value.Scope) : value;
            }
            case IFlowCaptureReferenceOperation capture:
            {
                var value = preserve && state.CaptureTargets.TryGetValue(Slot(capture.Id), out var address)
                    ? state.Read(address) : state.Get(Slot(capture.Id));
                Validate(value, operation, state, write); return value;
            }
            case IFlowCaptureOperation capture:
            {
                var value = Child(capture.Value, keep: true);
                state.CaptureTargets[Slot(capture.Id)] = Address(capture.Value, value, state);
                var snapshot = !preserve && capture.Value.Type is { } type && types.IsKnownImmutable(type)
                    ? new GuardValue(value.Identity, boolean: value.Boolean, condition: value.Condition, scope: value.Scope) : value;
                state.Values[Slot(capture.Id)] = snapshot;
                return snapshot;
            }
            case IConversionOperation conversion:
            {
                var value = Child(conversion.Operand, write, preserve);
                if (conversion.OperatorMethod is { } converter)
                {
                    Require(converter, 0, new[] { value }, operation, state);
                    Escape(value, operation, state, "a user-defined conversion can retain the protected reference");
                    InvalidateCapturedCells(state); MayThrow(state);
                    return new GuardValue(0, ReturnObligations(converter, 0, new[] { value }));
                }
                if (conversion.Operand.Type?.IsValueType == true && conversion.Type?.IsReferenceType == true) return new GuardValue(Allocation(operation));
                if (!conversion.IsImplicit) MayThrow(state);
                return value;
            }
            case IParenthesizedOperation parentheses: return Child(parentheses.Operand, write, preserve);
            case IFieldReferenceOperation field:
            {
                var receiver = field.Instance is null ? GuardValue.Unknown : Child(field.Instance, write);
                if (field.Instance is not null and not IInstanceReferenceOperation) MayThrow(state);
                // Only readonly reference projections denote a stable value across repeated reads.
                // A mutable field read is an evaluated snapshot, unique to this read site; a local
                // can retain that snapshot, but a later field read cannot reuse its lock proof.
                var id = StableReadonlyProjection(field.Field) && field.Field.Type.IsReferenceType
                    ? field.Field.IsStatic ? Project(0, field.Field) : receiver.Identity == 0 ? 0 : Project(receiver.Identity, field.Field)
                    : field.Field.Type.IsReferenceType ? Allocation(operation) : 0;
                if (write) evaluatedTargets[operation] = (receiver.Identity, Array.Empty<GuardValue>());
                var requirements = Obligations(field.Field, receiver.Identity, Array.Empty<GuardValue>()).AddRange(receiver.Obligations);
                if (id != 0 && contracts.IsSemaphoreMutexField(field.Field, cancellationToken)) state.SemaphoreMutexes.Add(id);
                var value = new GuardValue(id, requirements);
                Validate(value, operation, state, write);
                return preserve || !types.IsKnownImmutable(field.Field.Type) ? value : new GuardValue(id);
            }
            case IPropertyReferenceOperation property:
            {
                var receiver = property.Instance is null ? GuardValue.Unknown : Child(property.Instance, write);
                var arguments = property.Arguments.Select(argument => Child(argument.Value)).ToArray();
                if (write) evaluatedTargets[operation] = (receiver.Identity, arguments);
                var accessor = write ? property.Property.SetMethod : property.Property.GetMethod;
                if (accessor is not null) Require(accessor, receiver.Identity, arguments, operation, state);
                if (TrySemaphoreAwaitableProperty(property, receiver, state, out var semaphoreResult)) return semaphoreResult;
                if (!SynchronizationContracts.Same(property.Property.ContainingType, contracts.ThreadingLock) &&
                    !SynchronizationContracts.Same(property.Property.ContainingType, contracts.ReaderWriter) &&
                    !SynchronizationContracts.Same(property.Property.ContainingType, contracts.SemaphoreSlim)) InvalidateCapturedCells(state);
                MayThrow(state);
                if (TryLockProperty(property, receiver, state, out var fact)) return fact;
                var value = new GuardValue(0, Obligations(property.Property, receiver.Identity, arguments).AddRange(receiver.Obligations));
                if (accessor is not null) value = new GuardValue(0, value.Obligations.AddRange(ReturnObligations(accessor, receiver.Identity, arguments)));
                Validate(value, operation, state, write);
                return preserve || !types.IsKnownImmutable(property.Property.Type) ? value : GuardValue.Unknown;
            }
            case IVariableDeclaratorOperation declarator:
            {
                var value = declarator.Initializer is null ? GuardValue.Unknown : Child(declarator.Initializer.Value, keep: declarator.Symbol.RefKind != RefKind.None);
                if (declarator.Symbol.RefKind != RefKind.None && declarator.Initializer is { } initializer)
                    state.References[Slot(declarator.Symbol)] = Address(initializer.Value, value, state);
                else AssignLocal(declarator.Symbol, value, operation, state);
                return value;
            }
            case ISimpleAssignmentOperation assignment:
            {
                PrepareTarget(assignment.Target, state, depth + 1);
                var value = Child(assignment.Value, keep: assignment.IsRef);
                if (assignment.IsRef) BindReference(assignment.Target, Address(assignment.Value, value, state), state);
                else Assign(assignment.Target, value, operation, state, false);
                return value;
            }
            case ICompoundAssignmentOperation compound:
            {
                var targetValue = Child(compound.Target, true, true); var operandValue = Child(compound.Value);
                if (compound.OperatorMethod is { } target) Require(target, 0, new[] { targetValue, operandValue }, operation, state);
                if (compound.OperatorMethod is not null) InvalidateCapturedCells(state);
                if (compound.IsChecked || compound.OperatorMethod is not null || compound.OperatorKind is BinaryOperatorKind.Divide or BinaryOperatorKind.Remainder) MayThrow(state);
                return GuardValue.Unknown;
            }
            case IIncrementOrDecrementOperation increment:
            {
                var value = Child(increment.Target, true, true);
                if (increment.OperatorMethod is { } target) { Require(target, 0, new[] { value }, operation, state); InvalidateCapturedCells(state); }
                if (increment.IsChecked || increment.OperatorMethod is not null) MayThrow(state);
                return GuardValue.Unknown;
            }
            case IInvocationOperation invocation: return Invoke(invocation, state, depth + 1);
            case IDynamicInvocationOperation or IDynamicMemberReferenceOperation or IDynamicIndexerAccessOperation or IDynamicObjectCreationOperation:
                foreach (var child in operation.ChildOperations)
                    Escape(Child(child), child, state, "dynamic dispatch can retain the protected reference");
                InvalidateCapturedCells(state); MayThrow(state); return GuardValue.Unknown;
            case IInvalidOperation invalid when TryEnterLexicalLock(invalid, state, depth, out var locked): return locked;
            case IObjectCreationOperation creation:
            {
                var identity = Allocation(operation);
                var values = Enumerable.Repeat(GuardValue.Unknown, creation.Constructor?.Parameters.Length ?? 0).ToArray();
                var addresses = new Dictionary<int, GuardStorage>();
                foreach (var argument in creation.Arguments)
                {
                    var byReference = argument.Parameter?.RefKind is not null and not RefKind.None;
                    var value = Child(argument.Value, ArgumentWrites(argument.Parameter), byReference);
                    if (argument.Parameter is { } parameter && parameter.Ordinal < values.Length)
                    {
                        values[parameter.Ordinal] = value;
                        if (byReference) addresses[parameter.Ordinal] = Address(argument.Value, value, state);
                    }
                    if (argument.Parameter is null || !SymbolFacts.HasAttribute(argument.Parameter, "DoesNotEscape"))
                        Escape(value, argument, state, "a constructor can retain the protected reference");
                }
                foreach (var address in addresses) values[address.Key] = state.Read(address.Value);
                if (creation.Constructor is { } constructor) Require(constructor, identity, values, creation, state);
                InvalidateCapturedCells(state);
                var outputs = creation.Arguments.Where(argument => argument.Parameter?.RefKind is RefKind.Ref or RefKind.Out).ToArray();
                var exceptional = outputs.Length == 0 ? state : state.Clone();
                foreach (var argument in outputs)
                    if (argument.Parameter is { } parameter && addresses.TryGetValue(parameter.Ordinal, out var address))
                        exceptional.Write(address, new GuardValue(0, exceptional.Read(address).Obligations));
                MayThrow(exceptional);
                foreach (var argument in outputs) AssignArgument(argument, GuardValue.Unknown, creation, state, addresses);
                if (creation.Initializer is not null) Child(creation.Initializer);
                if (identity != 0 && contracts.IsSemaphoreMutexCreation(creation)) state.SemaphoreMutexes.Add(identity);
                return new GuardValue(identity);
            }
            case IArrayCreationOperation array:
                foreach (var size in array.DimensionSizes) Child(size);
                if (array.Initializer is not null)
                    foreach (var element in array.Initializer.ElementValues) Escape(Child(element), element, state, "an array retains the protected reference");
                MayThrow(state); return new GuardValue(Allocation(operation));
            case IArrayElementReferenceOperation element:
            {
                var array = Child(element.ArrayReference, write);
                foreach (var index in element.Indices) Child(index);
                MayThrow(state); return preserve || !types.IsKnownImmutable(element.Type!) ? new GuardValue(0, array.Obligations) : GuardValue.Unknown;
            }
            case IAwaitOperation awaited:
            {
                var value = Child(awaited.Operation);
                Suspend(operation, state);
                if (value.PendingWait != 0) return CompleteSemaphoreWait(value.PendingWait, awaited, state);
                MayThrow(state); return GuardValue.Unknown;
            }
            case IReturnOperation returned:
            {
                var value = returned.ReturnedValue is null ? GuardValue.Unknown : Child(returned.ReturnedValue, keep: returnedByReference());
                if (returned.Kind is OperationKind.YieldReturn or OperationKind.YieldBreak)
                { Suspend(operation, state); Escape(value, operation, state, "an iterator element escapes its protected region"); }
                else state.Returned = value;
                return value;
                bool returnedByReference() => method.ReturnsByRef || method.ReturnsByRefReadonly;
            }
            case IUnaryOperation unary:
            {
                var value = Child(unary.Operand);
                if (unary.OperatorMethod is { } target)
                {
                    Require(target, 0, new[] { value }, operation, state);
                    InvalidateCapturedCells(state); MayThrow(state);
                    return new GuardValue(0, ReturnObligations(target, 0, new[] { value }));
                }
                if (unary.IsChecked) MayThrow(state);
                return unary.OperatorKind == UnaryOperatorKind.Not
                    ? new GuardValue(0, boolean: value.Boolean.HasValue ? !value.Boolean : null, condition: value.Condition?.Negate()) : GuardValue.Unknown;
            }
            case IBinaryOperation binary:
            {
                var left = Child(binary.LeftOperand); var right = Child(binary.RightOperand);
                if (binary.OperatorMethod is { } binaryTarget) Require(binaryTarget, 0, new[] { left, right }, operation, state);
                if (binary.OperatorMethod is null && binary.OperatorKind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals)
                {
                    if (right.Boolean is { } rightBool) return BooleanComparison(left, rightBool, binary.OperatorKind == BinaryOperatorKind.Equals);
                    if (left.Boolean is { } leftBool) return BooleanComparison(right, leftBool, binary.OperatorKind == BinaryOperatorKind.Equals);
                }
                if (binary.OperatorMethod is not null) { Escape(left, operation, state, "an operator can retain protected arguments"); Escape(right, operation, state, "an operator can retain protected arguments"); InvalidateCapturedCells(state); }
                if (binary.IsChecked || binary.OperatorMethod is not null || binary.OperatorKind is BinaryOperatorKind.Divide or BinaryOperatorKind.Remainder) MayThrow(state);
                return GuardValue.Unknown;
            }
            case IDelegateCreationOperation creation:
                Child(creation.Target); return new GuardValue(Allocation(operation));
            case IFlowAnonymousFunctionOperation anonymous:
                Capture(anonymous, state); return new GuardValue(Allocation(operation));
            case ILocalFunctionOperation: return GuardValue.Unknown;
            case IMethodReferenceOperation reference:
            {
                var value = reference.Instance is null ? GuardValue.Unknown : Child(reference.Instance);
                Escape(value, operation, state, "a delegate retains its protected receiver");
                var invoke = (reference.Parent as IDelegateCreationOperation)?.Type is INamedTypeSymbol delegateType ? delegateType.DelegateInvokeMethod : null;
                foreach (var required in contracts.Required(reference.Method))
                    if (invoke is null || !contracts.Required(invoke).Any(available => available.SamePath(required) &&
                        (available.Write || !required.Write) && required.Parameter >= 0))
                        Report(operation, reference.Method.Name, required.Text,
                            "the delegate signature does not preserve the target's lock precondition", state, required.Protocol.ToString(), required.Location);
                return GuardValue.Unknown;
            }
            case IThrowOperation thrown:
                if (thrown.Exception is not null) Child(thrown.Exception); MayThrow(state); return GuardValue.Unknown;
            default:
                GuardValue result = GuardValue.Unknown;
                foreach (var child in operation.ChildOperations) result = Child(child, write, preserve);
                return result;
        }
    }

    private static bool ArgumentWrites(IParameterSymbol? parameter) => parameter is null ||
        parameter.RefKind is RefKind.Ref or RefKind.Out || !SymbolFacts.HasAttribute(parameter, "Borrowed") || SymbolFacts.HasAttribute(parameter, "BorrowedMut");

    private GuardValue Invoke(IInvocationOperation call, SynchronizationState state, int depth)
    {
        var known = contracts.IsMonitor(call.TargetMethod) || contracts.IsThreadingLock(call.TargetMethod) || contracts.IsReaderWriter(call.TargetMethod) || contracts.IsSemaphore(call.TargetMethod);
        var receiver = call.Instance is null ? GuardValue.Unknown : Evaluate(call.Instance, state, !known, depth: depth);
        if (call.Instance is not null && !known) Validate(receiver, call.Instance, state, true);
        var values = new GuardValue[call.TargetMethod.Parameters.Length];
        var addresses = new Dictionary<int, GuardStorage>();
        foreach (var argument in call.Arguments)
        {
            var byReference = argument.Parameter?.RefKind is not null and not RefKind.None;
            var value = Evaluate(argument.Value, state, argument.Parameter?.RefKind is RefKind.Ref or RefKind.Out,
                preserve: byReference, depth: depth);
            if (argument.Parameter is { } parameter && parameter.Ordinal < values.Length)
            {
                values[parameter.Ordinal] = value;
                if (byReference) addresses[parameter.Ordinal] = Address(argument.Value, value, state);
            }
            Validate(value, argument, state, ArgumentWrites(argument.Parameter));
        }
        // Locations are evaluated left-to-right; a ref parameter reads its incoming value after
        // later arguments have run, without re-resolving an earlier ref local's target.
        foreach (var address in addresses) values[address.Key] = state.Read(address.Value);
        for (var index = 0; index < values.Length; index++) values[index] ??= GuardValue.Unknown;
        Require(call.TargetMethod, receiver.Identity, values, call, state);
        if (TrySemaphoreAwaitableCall(call, receiver, state, out var waitResult)) return waitResult;
        // A call may throw before it grants an acquisition or publishes an out flag.
        PrepareAcquisitionFlag(call, values, state, addresses);
        if (!known && receiver.Scope is null) InvalidateCapturedCells(state);
        if (!EstablishedRelease(call, receiver, values, state))
        {
            var exceptional = state;
            if (!known && call.TargetMethod.Parameters.Any(parameter => parameter.RefKind is RefKind.Ref or RefKind.Out))
            {
                exceptional = state.Clone();
                foreach (var parameter in call.TargetMethod.Parameters.Where(parameter => parameter.RefKind is RefKind.Ref or RefKind.Out))
                    if (addresses.TryGetValue(parameter.Ordinal, out var address))
                        exceptional.Write(address, new GuardValue(0, exceptional.Read(address).Obligations));
            }
            MayThrow(exceptional);
        }
        if (TryIntrinsic(call, receiver, values, state, addresses, out var result)) return result;
        var summary = addresses.Count == 0 ? null : contracts.Summaries.Get(call.TargetMethod, cancellationToken);
        foreach (var argument in call.Arguments)
        {
            if (argument.Parameter is not { } parameter) continue;
            var value = values[parameter.Ordinal];
            if (SymbolFacts.Attribute(parameter, "DoesNotEscape") is null)
                Escape(value, argument, state, "the parameter has no DoesNotEscape contract for the protected reference");
            if (parameter.RefKind is RefKind.Ref or RefKind.Out && summary?.PreservedRefParameters.Contains(parameter.Ordinal) != true)
                AssignArgument(argument, GuardValue.Unknown, call, state, addresses);
        }
        var returned = new GuardValue(0, ReturnObligations(call.TargetMethod, receiver.Identity, values));
        return call.TargetMethod.ReturnsByRef || call.TargetMethod.ReturnsByRefReadonly
            ? ReferenceCallResult(call, values, addresses, returned, state) : returned;
    }

    private bool EstablishedRelease(IInvocationOperation call, GuardValue receiver, GuardValue[] values, SynchronizationState state)
    {
        var name = call.TargetMethod.Name;
        if (name == "Dispose" && receiver.Scope is { } scope) return state.Get(scope).Minimum > 0;
        if (contracts.IsMonitor(call.TargetMethod) && name == "Exit")
            return state.Get(new GuardKey(values.FirstOrDefault()?.Identity ?? 0, GuardLockKind.Monitor)).Minimum > 0;
        if (contracts.IsThreadingLock(call.TargetMethod) && name == "Exit")
            return state.Get(new GuardKey(receiver.Identity, GuardLockKind.ThreadingLock)).Minimum > 0;
        if (contracts.IsSemaphore(call.TargetMethod) && name == "Release")
            return SemaphoreReleaseCount(call) is { } count && count > 0 && state.Get(new GuardKey(receiver.Identity, GuardLockKind.SemaphoreSlim)).Minimum >= count;
        if (!contracts.IsReaderWriter(call.TargetMethod)) return false;
        var kind = name switch { "ExitReadLock" => GuardLockKind.Read, "ExitWriteLock" => GuardLockKind.Write,
            "ExitUpgradeableReadLock" => GuardLockKind.UpgradeableRead, _ => (GuardLockKind?)null };
        return kind is { } held && state.Get(new GuardKey(receiver.Identity, held)).Minimum > 0;
    }

    private bool TryIntrinsic(IInvocationOperation call, GuardValue receiver, GuardValue[] values, SynchronizationState state, Dictionary<int, GuardStorage> addresses, out GuardValue result)
    {
        result = GuardValue.Unknown;
        var method = call.TargetMethod;
        var monitor = contracts.IsMonitor(method);
        var threading = contracts.IsThreadingLock(method);
        var readerWriter = contracts.IsReaderWriter(method);
        if (contracts.IsSemaphore(method)) return SemaphoreIntrinsic(call, receiver, state, out result);
        // The ref struct Scope is a separate symbol; Dispose releases its recorded capability.
        if (method.Name == "Dispose" && receiver.Scope is { } scope)
        {
            Release(scope, call, state);
            if (call.Instance is not null) Assign(call.Instance, new GuardValue(receiver.Identity, receiver.Obligations), call, state, false);
            return true;
        }
        if (!monitor && !threading && !readerWriter) return false;
        var id = monitor ? values.FirstOrDefault()?.Identity ?? 0 : receiver.Identity;
        var name = method.Name;
        var kind = monitor ? GuardLockKind.Monitor : threading ? GuardLockKind.ThreadingLock :
            name.IndexOf("UpgradeableRead", StringComparison.Ordinal) >= 0 ? GuardLockKind.UpgradeableRead :
            name.IndexOf("Read", StringComparison.Ordinal) >= 0 ? GuardLockKind.Read : GuardLockKind.Write;
        var key = new GuardKey(id, kind);
        if (name is "Enter" or "EnterScope" or "EnterReadLock" or "EnterWriteLock" or "EnterUpgradeableReadLock")
        {
            var before = state.Get(key);
            Acquire(key, call, state, conditional: false);
            var condition = new GuardCondition(key, before, before.Acquire(call.Syntax.GetLocation()));
            foreach (var argument in call.Arguments.Where(argument => argument.Parameter?.Type.SpecialType == SpecialType.System_Boolean && argument.Parameter.RefKind == RefKind.Ref))
                AssignArgument(argument, new GuardValue(0, boolean: true, condition: condition), call, state, addresses);
            if (name == "EnterScope") result = new GuardValue(Allocation(call), scope: key);
            return true;
        }
        if (name is "TryEnter" or "TryEnterReadLock" or "TryEnterWriteLock" or "TryEnterUpgradeableReadLock")
        {
            var before = state.Get(key); var acquired = before.Acquire(call.Syntax.GetLocation());
            Acquire(key, call, state, conditional: true);
            result = new GuardValue(0, condition: new GuardCondition(key, before, acquired));
            foreach (var argument in call.Arguments.Where(argument => argument.Parameter?.Type.SpecialType == SpecialType.System_Boolean && argument.Parameter.RefKind == RefKind.Ref))
                AssignArgument(argument, result, call, state, addresses);
            return true;
        }
        if (name is "Exit" or "ExitReadLock" or "ExitWriteLock" or "ExitUpgradeableReadLock")
        { Release(key, call, state); return true; }
        if (monitor && name is "Wait" or "Pulse" or "PulseAll")
        {
            RequireKey(key, call, state, "this monitor operation requires ownership by the current thread");
            // Wait releases and reacquires this same monitor before a normal return.
            return true;
        }
        if (name == "IsEntered")
        { result = Observed(key, state); return true; }
        return false;
    }

    private bool TryLockProperty(IPropertyReferenceOperation property, GuardValue receiver, SynchronizationState state, out GuardValue result)
    {
        result = GuardValue.Unknown;
        var type = property.Property.ContainingType;
        if (SynchronizationContracts.Same(type, contracts.ThreadingLock) && property.Property.Name == "IsHeldByCurrentThread")
        { result = Observed(new GuardKey(receiver.Identity, GuardLockKind.ThreadingLock), state); return true; }
        if (!SynchronizationContracts.Same(type, contracts.ReaderWriter)) return false;
        var kind = property.Property.Name switch
        { "IsReadLockHeld" => GuardLockKind.Read, "IsUpgradeableReadLockHeld" => GuardLockKind.UpgradeableRead, "IsWriteLockHeld" => GuardLockKind.Write, _ => (GuardLockKind?)null };
        if (kind is null) return false;
        result = Observed(new GuardKey(receiver.Identity, kind.Value), state); return true;
    }

    private static GuardValue Observed(GuardKey key, SynchronizationState state)
    {
        var hold = state.Get(key);
        // An IsEntered test can establish a capability supplied by an external caller, but it
        // does not acquire one. A false result cannot discharge an acquisition on another path.
        var held = new GuardHold(Math.Max(1, hold.Minimum), Math.Max(1, hold.Maximum), hold.Acquisition);
        return new GuardValue(0, boolean: hold.Minimum > 0 ? true : null,
            condition: new GuardCondition(key, default, held, observed: hold.Maximum == 0));
    }

    private void Acquire(GuardKey key, IOperation operation, SynchronizationState state, bool conditional)
    {
        if (key.Identity == 0) { Report(operation, "lock", "unresolved guard", "the lock expression has no stable receiver identity", state); return; }
        if (key.Kind == GuardLockKind.Write && state.Get(new GuardKey(key.Identity, GuardLockKind.Read)).Minimum > 0)
            Report(operation, "write lock", Name(key.Identity), "a read lock cannot be upgraded directly; use an upgradeable-read capability", state);
        if (state.Held.Count >= 256 && !state.Held.ContainsKey(key))
            throw new AnalysisBudgetException("Synchronization tracked-capability budget exceeded 256.");
        var before = state.Get(key); var after = before.Acquire(operation.Syntax.GetLocation());
        state.Set(key, conditional ? before.Join(after) : after);
    }

    private void Release(GuardKey key, IOperation operation, SynchronizationState state)
    {
        RequireKey(key, operation, state, "a matching acquisition is not established on every incoming path");
        if (state.Expected.TryGetValue(key, out var expected) && expected.Minimum > 0 && state.Get(key).Minimum <= expected.Minimum)
            Report(operation, method.Name, Name(key.Identity), "the operation releases a caller-supplied synchronization capability", state, key.Kind.ToString(), expected.Acquisition);
        state.Set(key, state.Get(key).Release());
    }

    private void RequireKey(GuardKey key, IOperation operation, SynchronizationState state, string reason)
    {
        if (key.Identity == 0 || state.Get(key).Minimum == 0) Report(operation, operation.Syntax.ToString(), Name(key.Identity), reason, state, key.Kind.ToString(), requiredIdentity: key.Identity);
    }

    private void Require(IMethodSymbol target, int receiver, GuardValue[] arguments, IOperation operation, SynchronizationState state)
    {
        foreach (var contract in contracts.Required(target))
        {
            if (contract.Error is not null) { Report(operation, target.Name, contract.Text, contract.Error, state, contract.Protocol.ToString(), contract.Location); continue; }
            var id = Resolve(contract, receiver, arguments);
            if (!state.Has(id, contract.Protocol, contract.Write)) Report(operation, target.Name, contract.Text,
                "the declared " + (contract.Write ? "write/exclusive" : "read") + " lock precondition is not established for this receiver", state, contract.Protocol.ToString(), contract.Location, id, contract.Write ? "Write" : "Read");
        }
    }

    private void Validate(GuardValue value, IOperation operation, SynchronizationState state, bool write)
    {
        foreach (var obligation in value.Obligations)
            if (!state.Has(obligation, write)) Report(operation, obligation.Member.Name, obligation.Contract.Text,
                obligation.Contract.Error ?? (write ? "mutation requires an exclusive/write capability on every incoming path" :
                    "a matching guard is not held on every incoming path"), state, obligation.Contract.Protocol.ToString(), obligation.Contract.Location, obligation.Identity, write ? "Write" : "Read");
    }

    private void Escape(GuardValue value, IOperation operation, SynchronizationState state, string reason,
        ImmutableArray<GuardObligation> destination = default)
    {
        foreach (var obligation in value.Obligations)
        {
            if (!destination.IsDefault && destination.Any(other => other.Identity != 0 && other.Identity == obligation.Identity && other.Contract.Protocol == obligation.Contract.Protocol)) continue;
            Report(operation, obligation.Member.Name, obligation.Contract.Text, reason, state, obligation.Contract.Protocol.ToString(), obligation.Contract.Location, obligation.Identity, "Escape");
        }
    }

    private void PrepareTarget(IOperation target, SynchronizationState state, int depth)
    {
        if (target is ILocalReferenceOperation or IParameterReferenceOperation or IFlowCaptureReferenceOperation) return;
        Evaluate(target, state, write: true, preserve: true, depth: depth);
    }

    private void Assign(IOperation target, GuardValue value, IOperation operation, SynchronizationState state, bool byReference)
    {
        switch (target)
        {
            case ILocalReferenceOperation local:
                if (local.Local.RefKind != RefKind.None && !byReference)
                {
                    Validate(state.Get(Slot(local.Local)), target, state, true);
                    var storage = state.Address(Slot(local.Local));
                    if (storage.External is not null) Escape(value, operation, state, "a reference may retain the protected value outside local storage", storage.External.Obligations);
                    state.Write(storage, value); return;
                }
                AssignLocal(local.Local, value, operation, state); break;
            case IParameterReferenceOperation parameter:
                if (parameter.Parameter.RefKind is RefKind.Ref or RefKind.Out) Escape(value, operation, state, "a caller-visible reference slot retains the protected value");
                state.Assign(Slot(parameter.Parameter), value); break;
            case IFlowCaptureReferenceOperation capture:
                if (state.CaptureTargets.TryGetValue(Slot(capture.Id), out var targets))
                {
                    Validate(state.Read(targets), target, state, true);
                    if (targets.External is not null) Escape(value, operation, state, "a captured reference may retain the protected value", targets.External.Obligations);
                    state.Write(targets, value);
                }
                else state.Values[Slot(capture.Id)] = value;
                break;
            case IFieldReferenceOperation field:
            {
                var receiver = evaluatedTargets.TryGetValue(target, out var prepared) ? prepared.Receiver : TargetIdentity(field.Instance, state);
                var obligations = Obligations(field.Field, receiver, Array.Empty<GuardValue>());
                Validate(new GuardValue(0, obligations), target, state, true);
                Escape(value, operation, state, "storage without a matching GuardedBy contract retains the protected value", obligations);
                MayThrow(state); break;
            }
            case IPropertyReferenceOperation property:
            {
                var prepared = evaluatedTargets.TryGetValue(target, out var captured) ? captured : (TargetIdentity(property.Instance, state), Array.Empty<GuardValue>());
                var obligations = Obligations(property.Property, prepared.Item1, prepared.Item2);
                Validate(new GuardValue(0, obligations), target, state, true);
                if (property.Property.SetMethod is { } setter) Require(setter, prepared.Item1, prepared.Item2, target, state);
                Escape(value, operation, state, "storage without a matching GuardedBy contract retains the protected value", obligations);
                InvalidateCapturedCells(state); MayThrow(state); break;
            }
            default: Escape(value, operation, state, "the assignment can retain the protected reference"); break;
        }
    }

    private void AssignLocal(ILocalSymbol local, GuardValue value, IOperation operation, SynchronizationState state)
    {
        if (capturedLocals.Contains(local)) Escape(value, operation, state, "a closure can retain the protected local after the guard is released");
        state.Values[Slot(local)] = value;
    }

    private int TargetIdentity(IOperation? operation, SynchronizationState state) => operation switch
    {
        null => 0,
        IInstanceReferenceOperation => receiverIdentity,
        ILocalReferenceOperation local => state.Get(Slot(local.Local)).Identity,
        IParameterReferenceOperation parameter => state.Get(Slot(parameter.Parameter)).Identity,
        IFlowCaptureReferenceOperation capture => state.Get(Slot(capture.Id)).Identity,
        IConversionOperation conversion => TargetIdentity(conversion.Operand, state),
        IFieldReferenceOperation field when StableReadonlyProjection(field.Field) && field.Field.IsStatic => Project(0, field.Field),
        IFieldReferenceOperation field when StableReadonlyProjection(field.Field) && TargetIdentity(field.Instance, state) is var id && id != 0 => Project(id, field.Field),
        _ => 0
    };

    private bool StableReadonlyProjection(IFieldSymbol field) => field.IsReadOnly && !field.IsVolatile &&
        (!SynchronizationContracts.Same(field.Type, contracts.SemaphoreSlim) || !CanReplaceReadonlyStorage);

    // Constructor/init writes and whole-struct reassignment can change a readonly field's
    // object identity. A local by-value capture remains stable; the field projection does not.
    private bool CanReplaceReadonlyStorage => method.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor ||
        method.IsInitOnly || !method.IsStatic && method.ContainingType.TypeKind == TypeKind.Struct && !method.IsReadOnly;

    private void Suspend(IOperation operation, SynchronizationState state)
    {
        foreach (var pair in state.Held.Where(pair => pair.Value.Maximum > 0 && pair.Key.Kind != GuardLockKind.SemaphoreSlim))
            Report(operation, "suspension", Name(pair.Key.Identity), "a thread-affine synchronization capability may cross await/yield", state, pair.Key.Kind.ToString(), pair.Value.Acquisition);
        // Continuing on another thread must never reuse a former thread's proof.
        foreach (var key in state.Held.Keys.Where(key => key.Kind != GuardLockKind.SemaphoreSlim).ToArray())
            state.Set(key, new GuardHold(0, state.Get(key).Maximum, state.Get(key).Acquisition));
    }

    private void ValidateExit(SynchronizationState state, IOperation? returned)
    {
        state = state.Clone();
        CloseLexicalScopes(state, null);
        var site = returned ?? graph.OriginalOperation;
        if (!context.Exceptional && returned is not null)
            Escape(state.Returned, returned, state, "the returned reference has no matching GuardedBy return contract",
                ReturnObligations(method, receiverIdentity, method.Parameters.Select(parameter => state.Get(Slot(parameter))).ToArray()));
        ValidatePendingSemaphoreWaits(state);
        foreach (var key in state.Held.Keys.Union(state.Expected.Keys))
        {
            var before = state.Expected.TryGetValue(key, out var expected) ? expected : default; var after = state.Get(key);
            if (after.Minimum < before.Minimum)
                Report(site, method.Name, Name(key.Identity), "the method releases a caller-supplied lock before exiting", state, key.Kind.ToString(), before.Acquisition);
            else if (after.Maximum > before.Maximum)
                ReportAt(after.Acquisition ?? site.Syntax.GetLocation(), method.Name, Name(key.Identity),
                    "an acquisition can leave the method without a matching release" + (context.Exceptional ? " on an exception path" : ""), state, key.Kind.ToString());
        }
    }

    private void MayThrow(SynchronizationState state)
    {
        if (state.Held.Count == 0 && !state.PendingWaits.Values.Any(wait => wait.IsPending) && !router.HasExceptionalTarget(context)) return;
        foreach (var destination in router.Exceptions(context)) Propagate(destination, state);
    }

    private void Propagate(SourceControlFlowRouter.Context destination, SynchronizationState state)
    {
        if (reporting) return;
        state = state.Clone();
        CloseLexicalScopesAtDestination(state, destination);
        var point = FlowPoint(destination, state);
        if (!inputs.TryGetValue(point, out var existing))
        {
            if (inputs.Count >= 4096) throw new AnalysisBudgetException("Synchronization continuation budget exceeded 4096.");
            inputs.Add(point, state); Enqueue(point);
        }
        else if (existing.Join(state)) Enqueue(point);
    }
    private static (int Context, string Scopes) FlowPoint(SourceControlFlowRouter.Context point, SynchronizationState state) =>
        (point.Id, state.LexicalScopes.Count == 0 ? "" : string.Join(";", state.LexicalScopes.OrderBy(pair => pair.Key)
            .Select(pair => pair.Key + ":" + pair.Value.Key.Identity)));
    private void Enqueue((int Context, string Scopes) id) { if (queued.Add(id)) pending.Enqueue(id); }
    private void Step()
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (++steps > maximumSteps) throw new AnalysisBudgetException("Synchronization dataflow exceeded its configured step budget.");
    }

    private static bool Assume(SynchronizationState state, GuardValue condition, bool truth)
    {
        if (condition.Boolean is { } known && known != truth) return false;
        if (condition.Condition is { } fact)
        {
            var acquired = truth == fact.WhenTrue;
            state.Refine(fact.Key, acquired ? fact.Acquired : fact.Before);
            foreach (var slot in state.Values.Keys.ToArray())
            {
                var value = state.Values[slot];
                if (value.Condition is not { } alias || !alias.Key.Equals(fact.Key) || !alias.Before.Equals(fact.Before) || !alias.Acquired.Equals(fact.Acquired)) continue;
                state.Values[slot] = new GuardValue(value.Identity, value.Obligations, acquired == alias.WhenTrue, alias, value.Scope, value.PendingWait);
            }
            foreach (var token in state.PendingWaits.Keys.ToArray())
            {
                var wait = state.PendingWaits[token];
                if (wait.Result.Condition is { } alias && alias.Key.Equals(fact.Key) && alias.Before.Equals(fact.Before) && alias.Acquired.Equals(fact.Acquired))
                    state.PendingWaits[token] = wait.WithResult(new GuardValue(0, boolean: acquired == alias.WhenTrue, condition: alias));
            }
            if (fact.Observed && truth == fact.WhenTrue) state.Expected[fact.Key] = fact.Acquired;
        }
        return true;
    }
    private static GuardValue BooleanComparison(GuardValue value, bool constant, bool equality)
    {
        var same = constant == equality;
        return new GuardValue(0, boolean: value.Boolean is { } boolean ? same == boolean : null,
            condition: same ? value.Condition : value.Condition?.Negate());
    }

    private ImmutableArray<GuardObligation> Obligations(ISymbol member, int receiver, GuardValue[] arguments) =>
        contracts.Members(member).Select(contract => new GuardObligation(Resolve(contract, receiver, arguments), contract, member)).ToImmutableArray();
    private ImmutableArray<GuardObligation> ReturnObligations(IMethodSymbol target, int receiver, GuardValue[] arguments) =>
        contracts.Returns(target).Select(contract => new GuardObligation(Resolve(contract, receiver, arguments), contract, target)).ToImmutableArray();
    private int Resolve(GuardContract contract, int receiver, GuardValue[] arguments)
    {
        if (contract.Error is not null) return 0;
        var id = contract.Parameter >= 0 ? contract.Parameter < arguments.Length ? arguments[contract.Parameter].Identity : 0 : receiver;
        if (contract.Parameter == -2) id = 0;
        foreach (var field in contract.Fields)
        {
            if (!field.IsStatic && id == 0) return 0;
            if (SynchronizationContracts.Same(field.Type, contracts.SemaphoreSlim) && !StableReadonlyProjection(field)) return 0;
            id = Project(field.IsStatic ? 0 : id, field);
        }
        return id;
    }
    private int Slot(ISymbol symbol)
    { if (!slots.TryGetValue(symbol, out var id)) slots.Add(symbol, id = nextSlot++); return id; }
    private int Slot(CaptureId capture)
    { if (!captures.TryGetValue(capture, out var id)) captures.Add(capture, id = nextSlot++); return id; }
    private int Identity(string name) { var id = nextIdentity++; identities.Add(id, name); return id; }
    private string Name(int identity) => identities.TryGetValue(identity, out var name) ? name : "unresolved guard";
    private int Project(int receiver, IFieldSymbol field)
    {
        if (!fieldIds.TryGetValue(field, out var fieldId)) fieldIds.Add(field, fieldId = fieldIds.Count);
        var key = (receiver, fieldId);
        if (!projections.TryGetValue(key, out var id)) projections.Add(key, id = Identity((field.IsStatic ? field.ContainingType.Name : Name(receiver)) + "." + field.Name));
        return id;
    }
    private int Allocation(IOperation operation)
    {
        // An allocation site in a loop represents many runtime objects. Its identity cannot prove
        // that a saved receiver from an earlier iteration is the currently acquired guard.
        if (context is not null && repeatedBlocks[context.Ordinal]) return 0;
        if (!allocations.TryGetValue(operation, out var id)) allocations.Add(operation, id = Identity("allocation@" + operation.Syntax.SpanStart));
        return id;
    }
    private static GuardKey Key(int id, GuardProtocol protocol, bool write) => new(id, protocol switch
    { GuardProtocol.ThreadingLock => GuardLockKind.ThreadingLock, GuardProtocol.ReaderWriter => write ? GuardLockKind.Write : GuardLockKind.Read,
        GuardProtocol.SemaphoreSlim => GuardLockKind.SemaphoreSlim, _ => GuardLockKind.Monitor });

    private void CollectCaptures()
    {
        foreach (var nested in ControlFlowLowerer.EnumerateGraphs(graph, method, cancellationToken).Skip(1))
        foreach (var block in nested.Graph.Blocks)
        {
            var operations = new Stack<IOperation>(block.Operations);
            if (block.BranchValue is { } branch) operations.Push(branch);
            while (operations.Count != 0)
            {
                Step(); var operation = operations.Pop();
                if (operation is ILocalReferenceOperation local && !SynchronizationContracts.Same(local.Local.ContainingSymbol, nested.Method))
                {
                    capturedLocals.Add(local.Local);
                    if (local.Parent is IAssignmentOperation assignment && ReferenceEquals(assignment.Target, local) ||
                        local.Parent is IIncrementOrDecrementOperation || local.Parent is IArgumentOperation { Parameter.RefKind: RefKind.Ref or RefKind.Out })
                        mutableCapturedLocals.Add(local.Local);
                }
                foreach (var child in operation.ChildOperations) operations.Push(child);
            }
        }
    }
    private void Capture(IFlowAnonymousFunctionOperation anonymous, SynchronizationState state)
    {
        foreach (var local in capturedLocals)
            if (slots.TryGetValue(local, out var slot)) Escape(state.Get(slot), anonymous, state, "a closure can retain the protected local after the guard is released");
    }

    private void Report(IOperation operation, string member, string guard, string reason, SynchronizationState state,
        string? protocol = null, Location? declaration = null, int requiredIdentity = 0, string? accessMode = null) => ReportAt(operation.Syntax.GetLocation(), member, guard, reason, state, protocol, declaration, requiredIdentity, accessMode);
    private void ReportAt(Location location, string member, string guard, string reason, SynchronizationState state,
        string? protocol = null, Location? declaration = null, int requiredIdentity = 0, string? accessMode = null)
    {
        if (!reporting) return;
        var key = location.SourceSpan.Start + ":" + member + ":" + guard + ":" + reason;
        if (diagnostics.ContainsKey(key)) return;
        var heldRecords = state.Held.OrderBy(pair => Name(pair.Key.Identity), StringComparer.Ordinal).ThenBy(pair => pair.Key.Kind).ToArray();
        var held = heldRecords.Select(pair => Name(pair.Key.Identity) + " [" + pair.Key.Kind + ": " + pair.Value.Minimum + ".." +
                (pair.Value.Maximum == int.MaxValue ? "many" : pair.Value.Maximum.ToString(System.Globalization.CultureInfo.InvariantCulture)) + "]");
        var properties = ImmutableDictionary<string, string?>.Empty
            .Add("SafeSharp.SynchronizationRequired", guard).Add("SafeSharp.SynchronizationProtocol", protocol)
            .Add("SafeSharp.SynchronizationHeld", string.Join("; ", held)).Add("SafeSharp.SynchronizationReason", reason)
            .Add("SafeSharp.SynchronizationAccess", member).Add("SafeSharp.SynchronizationAccessMode", accessMode)
            .Add("SafeSharp.SynchronizationRequiredIdentity", requiredIdentity == 0 ? null : Name(requiredIdentity))
            .Add("SafeSharp.SynchronizationHeldCount", heldRecords.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        for (var index = 0; index < heldRecords.Length; index++)
        {
            var pair = heldRecords[index];
            var prefix = "SafeSharp.SynchronizationHeld." + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".";
            properties = properties.Add(prefix + "Name", Name(pair.Key.Identity)).Add(prefix + "Protocol", pair.Key.Kind.ToString())
                .Add(prefix + "Minimum", pair.Value.Minimum.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Add(prefix + "Maximum", pair.Value.Maximum == int.MaxValue ? "many" : pair.Value.Maximum.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        var evidence = state.Held.Values.Select(value => value.Acquisition).Append(declaration)
            .OfType<Location>().Where(value => value.IsInSource && !value.Equals(location)).Distinct().Take(32);
        diagnostics.Add(key, Diagnostic.Create(ConcurrencyDiagnostics.MissingSynchronizationGuard, location, evidence, properties, member, guard, reason));
    }
}
