using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;
using SafeSharp.Analyzers.Semantics;
using SafeSharp.Core;

namespace SafeSharp.Analyzers.Analysis;

/// <summary>
/// Lowers semantic operations to explicit ownership effects. Syntax is used only for locations and
/// stable IDs; classification uses symbols, interface implementations and contracts.
/// </summary>
internal sealed partial class OperationLowerer
{
    private readonly IMethodSymbol _method;
    private readonly MethodSummaryProvider _summaries;
    private readonly CancellationToken _cancellationToken;
    private readonly Dictionary<ISymbol, string> _places = new(SymbolEqualityComparer.Default);
    private readonly HashSet<string> _seeded = new(StringComparer.Ordinal);
    private readonly List<FlowInstruction> _seeds = new();
    private List<FlowInstruction> _instructions = new();

    public OperationLowerer(IMethodSymbol method, MethodSummaryProvider summaries, CancellationToken cancellationToken)
    { _method = method; _summaries = summaries; _cancellationToken = cancellationToken; SeedCallbackInputs(); SeedParameters(); }

    public const string ReturnPlace = "$return";
    public IReadOnlyList<FlowInstruction> Seeds => _seeds;
    public bool ReturnsBorrowed => _summaries.Get(_method, _cancellationToken).HasBorrowedReturn;

    public (string Place, bool IsNullWhenTrue)? NullTest(BasicBlock block)
    {
        if (block.BranchValue is IIsNullOperation isNull && PurePlace(isNull.Operand) is { } operand)
            return (operand, true);
        if (block.BranchValue is IBinaryOperation binary && binary.OperatorKind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals)
        {
            var other = binary.LeftOperand.ConstantValue is { HasValue: true, Value: null } ? binary.RightOperand :
                binary.RightOperand.ConstantValue is { HasValue: true, Value: null } ? binary.LeftOperand : null;
            if (other is not null && PurePlace(other) is { } place) return (place, binary.OperatorKind == BinaryOperatorKind.Equals);
        }
        return null;
    }

    private string? PurePlace(IOperation operation)
    {
        operation = SymbolFacts.Unwrap(operation);
        return operation switch
        {
            ILocalReferenceOperation local => Place(local.Local),
            IParameterReferenceOperation parameter => Place(parameter.Parameter),
            IFlowCaptureReferenceOperation capture => Capture(capture.Id),
            IInstanceReferenceOperation => "this",
            IFieldReferenceOperation field when field.Field.IsStatic => Place(field.Field),
            IFieldReferenceOperation field => (field.Instance is null ? "this" : PurePlace(field.Instance) ?? "this") + "." + field.Field.Name,
            _ => null
        };
    }

    public List<FlowInstruction> Lower(BasicBlock block)
    {
        _instructions = new List<FlowInstruction>();
        foreach (var operation in block.Operations) Evaluate(operation);
        if (block.BranchValue is { } branchValue)
        {
            var returning = block.FallThroughSuccessor?.Semantics == ControlFlowBranchSemantics.Return ||
                            block.ConditionalSuccessor?.Semantics == ControlFlowBranchSemantics.Return;
            var returningReference = returning && (_method.ReturnsByRef || _method.ReturnsByRefReadonly);
            var source = returningReference ? Address(branchValue) : Evaluate(branchValue, read: !returning);
            if (returning && source is not null)
                Add(returningReference ? InstructionKind.BindReference : InstructionKind.Alias, branchValue,
                    target: ReturnPlace, source: source, description: returningReference
                        ? "capture returned storage before cleanup" : "evaluate return value before cleanup");
        }
        return _instructions;
    }

    private void SeedParameters()
    {
        foreach (var parameter in _method.Parameters)
        {
            // An out parameter has no incoming value; writes target the caller's storage.
            if (parameter.RefKind == RefKind.Out) continue;
            if (!TracksOwnershipValue(parameter.Type) && !IsValueTaskType(parameter.Type) && !HasOwnershipContract(parameter) &&
                !SymbolFacts.Implements(parameter.Type, "System.IServiceProvider")) continue;
            var ownership = SymbolFacts.HasAttribute(parameter, "Owned") || SymbolFacts.HasAttribute(parameter, "Consumes")
                ? ResourceOwnership.Owned : SymbolFacts.HasAttribute(parameter, "Borrowed") || SymbolFacts.HasAttribute(parameter, "BorrowedMut")
                    ? ResourceOwnership.Borrowed : ResourceOwnership.External;
            var location = parameter.Locations.FirstOrDefault(l => l.IsInSource) ?? _method.Locations.FirstOrDefault() ?? Location.None;
            if (ownership == ResourceOwnership.Borrowed)
            {
                var root = "$input:" + parameter.Name;
                Seed(root, parameter.Type, ownership, location);
                _seeds.Add(new FlowInstruction(SymbolFacts.HasAttribute(parameter, "BorrowedMut") ? InstructionKind.BorrowExclusive : InstructionKind.BorrowShared,
                    Site(location), target: Place(parameter), source: root, description: "parameter borrow contract " + parameter.Name));
            }
            else Seed(Place(parameter), parameter.Type, ownership, location);
        }
        if (!_method.IsStatic)
            Seed("this", _method.ContainingType, ResourceOwnership.External, _method.Locations.FirstOrDefault() ?? Location.None);
    }

    private void SeedCallbackInputs()
    {
        var inputs = _summaries.Callbacks.Inputs(_method, _cancellationToken);
        foreach (var group in _summaries.Callbacks.AliasGroups(inputs, _cancellationToken))
        {
            var capture = group.FirstOrDefault(value => SymbolFacts.HasAttribute(value.Symbol, "Borrowed") || SymbolFacts.HasAttribute(value.Symbol, "BorrowedMut")) ??
                          group.FirstOrDefault(value => SymbolFacts.IsDisposable(value.Type)) ?? group.FirstOrDefault(TracksCapture);
            if (capture is null) continue;
            var place = CapturePlace(capture);
            var borrowed = SymbolFacts.HasAttribute(capture.Symbol, "Borrowed") || SymbolFacts.HasAttribute(capture.Symbol, "BorrowedMut");
            if (borrowed)
            {
                var root = "$callback-input:" + place;
                Seed(root, capture.Type, ResourceOwnership.Borrowed, capture.Location);
                _seeds.Add(new FlowInstruction(SymbolFacts.HasAttribute(capture.Symbol, "BorrowedMut") ? InstructionKind.BorrowExclusive : InstructionKind.BorrowShared,
                    Site(capture.Location), target: place, source: root, description: "preserve captured parameter borrow contract"));
            }
            else Seed(place, capture.Type, ResourceOwnership.Owned, capture.Location);
            foreach (var alias in group)
            {
                if (SymbolEqualityComparer.Default.Equals(alias.Symbol, capture.Symbol)) continue;
                var aliasPlace = CapturePlace(alias);
                _seeded.Add(aliasPlace);
                _seeds.Add(new FlowInstruction(InstructionKind.Alias, Site(alias.Location), target: aliasPlace, source: place,
                    description: "preserve stable captured reference alias"));
            }
        }
    }

    private static bool TracksCapture(ConcurrencyCapture capture) => TracksOwnershipValue(capture.Type) || IsValueTaskType(capture.Type) ||
        HasOwnershipContract(capture.Symbol) || SymbolFacts.Implements(capture.Type, "System.IServiceProvider");

    private string CapturePlace(ConcurrencyCapture capture) => capture.Name == "this" && capture.Symbol is ITypeSymbol ? "this" : Place(capture.Symbol);

    private void TransferCallbackCaptures(IInvocationOperation invocation)
    {
        foreach (var transfer in _summaries.Callbacks.Transfers(invocation, _cancellationToken))
        {
            var capture = transfer.Captures.FirstOrDefault(TracksCapture);
            if (capture is null) continue;
            _instructions.Add(new FlowInstruction(InstructionKind.Move, Site(transfer.Boundary), source: CapturePlace(capture),
                flags: transfer.Conditional ? InstructionFlags.Conditional : InstructionFlags.None,
                description: "transfer captured " + capture.Name + " through TransfersToThread"));
        }
    }

    private static bool HasOwnershipContract(ISymbol symbol) => SymbolFacts.HasAttribute(symbol, "Owned") ||
        SymbolFacts.HasAttribute(symbol, "Consumes") || SymbolFacts.HasAttribute(symbol, "Borrowed") || SymbolFacts.HasAttribute(symbol, "BorrowedMut") ||
        SymbolFacts.HasAttribute(symbol, "Lifetime");

    private void Seed(string place, ITypeSymbol? type, ResourceOwnership ownership, Location location)
    {
        if (!_seeded.Add(place)) return;
        _seeds.Add(new FlowInstruction(InstructionKind.Allocate, Site(location), target: place, resourceId: "input:" + place,
            ownership: ownership, flags: ResourceFlags(type) | (type?.IsReferenceType == true && place != "this" ? InstructionFlags.MayBeNull : InstructionFlags.None),
            description: ownership + " input " + place));
        if (IsValueTaskType(type))
            _seeds.Add(new FlowInstruction(InstructionKind.TrackValueTask, Site(location), target: place,
                source: place, resourceId: "value-task-input:" + place, description: "track single-consumption ValueTask input"));
    }

    private static InstructionFlags ResourceFlags(ITypeSymbol? type) =>
        (SymbolFacts.IsDisposable(type) ? InstructionFlags.None : InstructionFlags.SuppressLeak) |
        (SymbolFacts.HasAttribute(type, "NoSuspend") ? InstructionFlags.NoSuspend : InstructionFlags.None) |
        (type?.NullableAnnotation == NullableAnnotation.Annotated ? InstructionFlags.MayBeNull : InstructionFlags.None);

    private string Place(ISymbol symbol)
    {
        if (_places.TryGetValue(symbol, out var place)) return place;
        place = symbol switch
        {
            IParameterSymbol parameter => "arg:" + parameter.Name,
            ILocalSymbol local => local.Name + "@" + (local.Locations.FirstOrDefault()?.SourceSpan.Start ?? _places.Count),
            IFieldSymbol field => (field.IsStatic ? "static:" + field.ContainingType.ToDisplayString() : "this") + "." + field.Name,
            _ => symbol.Name + "@" + _places.Count
        };
        _places.Add(symbol, place);
        return place;
    }

    private static string Temporary(IOperation operation) => "$t:" + operation.Syntax.SpanStart + ":" + operation.Kind;
    private static string Capture(Microsoft.CodeAnalysis.FlowAnalysis.CaptureId id) => "$capture:" + id.GetHashCode();

    private string? Evaluate(IOperation? operation, bool read = true)
    {
        if (operation is null) return null;
        _cancellationToken.ThrowIfCancellationRequested();
        switch (operation)
        {
            case IConversionOperation { OperatorMethod: { } method } conversion:
                return Convert(conversion, method);
            case IConversionOperation conversion: return Evaluate(conversion.Operand, read);
            case IParenthesizedOperation parenthesized: return Evaluate(parenthesized.Operand, read);
            case IExpressionStatementOperation expression: return Evaluate(expression.Operation, read);
            case ILocalReferenceOperation local:
                return Use(Place(local.Local), local, read);
            case IParameterReferenceOperation parameter:
                return Use(Place(parameter.Parameter), parameter, read);
            case IDeclarationExpressionOperation declarationExpression:
                return Evaluate(declarationExpression.Expression, false);
            case IInstanceReferenceOperation instance:
                return Use("this", instance, read);
            case IFlowCaptureReferenceOperation capture:
                return Use(Capture(capture.Id), capture, read);
            case IFlowCaptureOperation capture:
            {
                var value = Evaluate(capture.Value, false);
                CaptureReferenceStorage(capture, value);
                if (value is not null) Add(InstructionKind.Alias, capture, target: Capture(capture.Id), source: value);
                else Add(InstructionKind.Forget, capture, target: Capture(capture.Id));
                ForgetTemporary(value, capture);
                return Capture(capture.Id);
            }
            case IVariableDeclaratorOperation declaration:
            {
                if (declaration.Symbol.RefKind != RefKind.None) return DeclareReference(declaration);
                var value = Evaluate(declaration.Initializer?.Value, false);
                if (value is not null) Add(InstructionKind.Alias, declaration, target: Place(declaration.Symbol), source: value);
                ForgetTemporary(value, declaration);
                return value;
            }
            case ISimpleAssignmentOperation assignment:
                return Assign(assignment);
            case ICompoundAssignmentOperation compound:
            {
                var target = Address(compound.Target);
                Evaluate(compound.Value);
                if (target is not null) Add(InstructionKind.Write, compound, source: target);
                return target;
            }
            case IIncrementOrDecrementOperation increment:
            {
                var target = Address(increment.Target);
                if (target is not null) Add(InstructionKind.Write, increment, source: target);
                return target;
            }
            case ICollectionExpressionOperation collection:
                return CreateCollection(collection);
            case IArrayCreationOperation arrayCreation:
                return CreateArray(arrayCreation);
            case IObjectCreationOperation creation:
            {
                var arguments = EvaluateArguments(creation.Arguments, creation.Constructor);
                ActivateArguments(arguments);
                InvalidateCallAggregates(creation, creation.Constructor, arguments);
                MarkCallMayThrow(creation, creation.Arguments, arguments);
                ApplyOutputArguments(creation.Arguments, arguments.Values, creation.Constructor is { } constructor ? _summaries.Get(constructor, _cancellationToken) : null);
                TouchArguments(arguments);
                var awaitable = KnownAwaitableCreation(creation, arguments);
                EndArguments(arguments);
                if (creation.Initializer is not null) Evaluate(creation.Initializer);
                if (awaitable is not null) return TrackProducedValueTask(creation, awaitable);
                if (!TracksOwnershipValue(creation.Type)) return TrackProducedValueTask(creation, null);
                var target = Temporary(creation);
                Add(InstructionKind.Allocate, creation, target: target, resourceId: "new:" + creation.Syntax.SpanStart,
                    flags: ResourceFlags(creation.Type), description: "acquire " + creation.Type?.ToDisplayString());
                return TrackProducedValueTask(creation, target);
            }
            case IInvocationOperation invocation:
                return Invoke(invocation);
            case IAwaitOperation awaitOperation:
            {
                var value = Evaluate(awaitOperation.Operation, false);
                Add(InstructionKind.Suspend, awaitOperation, description: "await suspension point");
                if (value is not null)
                {
                    if (IsValueTaskType(awaitOperation.Operation.Type))
                        Add(InstructionKind.ConsumeValueTask, awaitOperation, source: value, description: "consume ValueTask once before completion can fault");
                    Add(InstructionKind.CompleteAsyncDispose, awaitOperation, source: value, description: "observe awaited disposal completion");
                    if (IsFrameworkAwaitableType(awaitOperation.Operation.Type))
                        Add(TracksOwnershipValue(awaitOperation.Type) ? InstructionKind.ObserveAsyncFactory : InstructionKind.ObserveAsyncComposition, awaitOperation, source: value,
                            description: "observe factory completion before a possible asynchronous fault");
                }
                // Observing a faulted completion still fulfills the obligation to await it. The fault
                // may then propagate, before a successful factory result becomes available.
                MarkMayThrow(awaitOperation);
                if (IsFrameworkAwaitableType(awaitOperation.Operation.Type)) CompleteComposition(awaitOperation, value);
                var result = IsFrameworkAwaitableType(awaitOperation.Operation.Type)
                    ? ReceiveAwaitedResult(awaitOperation, value, true)
                    : ReceiveCustomAwaitedResult(awaitOperation, value);
                ForgetTemporary(value, awaitOperation);
                return result;
            }
            case IFieldReferenceOperation field:
            {
                var owner = Evaluate(field.Instance);
                var place = field.Field.IsStatic ? Place(field.Field) : (owner ?? "this") + "." + field.Field.Name;
                if (TracksOwnershipValue(field.Type) || IsValueTaskType(field.Type) || HasOwnershipContract(field.Field))
                    Seed(place, field.Type, SymbolFacts.HasAttribute(field.Field, "Borrowed") ? ResourceOwnership.Borrowed : ResourceOwnership.External, field.Syntax.GetLocation());
                return Use(place, field, read);
            }
            case IPropertyReferenceOperation property:
                return Property(property, read);
            case IArrayElementReferenceOperation element:
                return ReadArrayElement(element, read);
            case IIsNullOperation isNull:
                Evaluate(isNull.Operand, false); return null;
            case IBinaryOperation binary when binary.OperatorKind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals &&
                (binary.LeftOperand.ConstantValue is { HasValue: true, Value: null } || binary.RightOperand.ConstantValue is { HasValue: true, Value: null }):
                Evaluate(binary.LeftOperand, false); Evaluate(binary.RightOperand, false); return null;
            case IReturnOperation returned:
            {
                var source = Evaluate(returned.ReturnedValue, false);
                if (returned.Kind == OperationKind.YieldReturn)
                {
                    if (source is not null) Add(InstructionKind.Escape, returned, source: source, description: "yield value escapes to iterator consumer");
                    Add(InstructionKind.Suspend, returned, description: "yield suspension point");
                }
                else if (source is not null) Add(InstructionKind.Alias, returned, target: ReturnPlace, source: source);
                return source;
            }
            case IAnonymousFunctionOperation:
            case IFlowAnonymousFunctionOperation:
            case ILocalFunctionOperation:
                return null; // Nested bodies have their own CFG and independently scoped facts.
            case IArgumentOperation argument:
                return Evaluate(argument.Value, read);
            case IConditionalAccessInstanceOperation:
                return null; // Lowered CFG represents this receiver as a flow capture.
            default:
                foreach (var child in operation.ChildOperations) Evaluate(child);
                return null;
        }
    }

    private string? Assign(ISimpleAssignmentOperation assignment)
    {
        if (!assignment.IsRef && assignment.Target is IArrayElementReferenceOperation element && TracksOwnershipValue(element.Type))
            return AssignArrayElement(assignment, element);
        if (assignment.IsRef) return RebindReference(assignment);
        var target = Address(assignment.Target, writeReceiver: true);
        var source = Evaluate(assignment.Value, false);
        if (target is null) return source;
        if (source is null) Add(InstructionKind.Forget, assignment, target: target);
        else
        {
            Add(InstructionKind.Alias, assignment, target: target, source: source);
            if (assignment.Target is IFieldReferenceOperation or IPropertyReferenceOperation or IArrayElementReferenceOperation ||
                assignment.Target is IParameterReferenceOperation { Parameter.RefKind: RefKind.Out or RefKind.Ref })
                Add(InstructionKind.Escape, assignment, source: source, flags: InstructionFlags.Transfer, description: "store into a longer-lived object");
            ForgetTemporary(source, assignment);
        }
        return target;
    }

    private string? Address(IOperation operation, bool writeReceiver = false)
    {
        operation = SymbolFacts.Unwrap(operation);
        switch (operation)
        {
            case ILocalReferenceOperation local: return Place(local.Local);
            case IParameterReferenceOperation parameter: return Place(parameter.Parameter);
            case IFlowCaptureReferenceOperation capture: return ReferenceCapture(capture.Id);
            case IDiscardOperation: return "$discard:" + operation.Syntax.SpanStart;
            case IFieldReferenceOperation field:
            {
                var owner = Evaluate(field.Instance, false);
                if (owner is not null && writeReceiver) Add(InstructionKind.Write, field, source: owner);
                return field.Field.IsStatic ? Place(field.Field) : (owner ?? "this") + "." + field.Field.Name;
            }
            case IPropertyReferenceOperation property when property.Property.ReturnsByRef || property.Property.ReturnsByRefReadonly:
                return Property(property, false);
            case IPropertyReferenceOperation property:
            {
                var owner = Evaluate(property.Instance, false);
                foreach (var argument in property.Arguments) Evaluate(argument.Value);
                if (owner is not null && writeReceiver) Add(InstructionKind.Write, property, source: owner);
                return (owner ?? "static:" + property.Property.ContainingType.ToDisplayString()) + "." + property.Property.Name;
            }
            case IArrayElementReferenceOperation element:
                return AddressArrayElement(element, writeReceiver);
            default: return Evaluate(operation, false);
        }
    }

    private string? Property(IPropertyReferenceOperation property, bool read)
    {
        var owner = SnapshotCallValue(Evaluate(property.Instance, false), property, "receiver");
        var getter = property.Property.GetMethod;
        if (owner is not null && property.Property.Name == "Result" && property.Arguments.IsEmpty && getter is not null &&
            IsFrameworkAwaitableMethod(getter) && OwnedAwaitablePayload(property.Instance?.Type) is not null)
        {
            if (IsValueTaskType(property.Instance?.Type))
                Add(InstructionKind.ConsumeValueTask, property, source: owner, description: "consume ValueTask through Result");
            Add(InstructionKind.ObserveAsyncFactory, property, source: owner, description: "observe factory completion before reading Result");
            MarkMayThrow(property);
            CompleteComposition(property, owner);
            return ReleaseReceiverSnapshot(owner, property, ReceiveAwaitedResult(property, owner, true));
        }
        var contract = getter is not null ? _summaries.Get(getter, _cancellationToken) : null;
        string? receiverLoan = null;
        if (owner is not null && SymbolFacts.HasAttribute(getter, "Mutates"))
        {
            receiverLoan = "$receiver-loan:" + property.Syntax.SpanStart;
            Add(InstructionKind.ReserveExclusiveBorrow, property, target: receiverLoan, source: owner,
                description: "reserve mutable getter receiver while evaluating index arguments");
        }
        var arguments = EvaluateArguments(property.Arguments, getter, contract);
        if (owner is not null && receiverLoan is null) Add(InstructionKind.Read, property, source: owner);
        if (receiverLoan is not null)
            Add(InstructionKind.ActivateBorrow, property, source: receiverLoan, description: "activate mutable receiver at getter call");
        ActivateArguments(arguments);
        if (receiverLoan is not null) Add(InstructionKind.Write, property, source: receiverLoan);
        InvalidateCallAggregates(property, getter, arguments, owner, property.Instance?.Type);
        MarkMayThrow(property);
        TouchArguments(arguments);
        if (receiverLoan is not null) Add(InstructionKind.Read, property, source: receiverLoan);

        string? Finish(string? result)
        {
            result = TrackProducedValueTask(property, result, preserveExisting: result is not null);
            EndArguments(arguments);
            if (receiverLoan is not null) Add(InstructionKind.ReleaseLoan, property, target: receiverLoan);
            return ReleaseReceiverSnapshot(owner, property, result);
        }

        if (property.Property.Name == "ServiceProvider" && owner is not null && SymbolFacts.Implements(property.Type, "System.IServiceProvider"))
            return Finish(Use(owner, property, read));
        if (getter is not null && contract is not null && ReturnedReference(property, getter, contract, arguments) is { } reference)
            return Finish(reference);
        if (getter is not null && contract is not null && ReconstructAsyncReturn(property, getter, contract, receiverLoan ?? owner, arguments) is { } nested)
            return Finish(nested);
        if (contract?.ReturnsOwned == true)
        {
            var target = Temporary(property);
            var payload = OwnedAwaitablePayload(property.Type);
            Add(payload is not null ? InstructionKind.BeginAsyncFactory : InstructionKind.Allocate,
                property, target: target, resourceId: "property:" + property.Syntax.SpanStart, flags: ResourceFlags(payload ?? property.Type));
            return Finish(target);
        }
        if (contract is not null && getter is not null && ReturnedBorrow(property, getter, contract, receiverLoan ?? owner, arguments) is { } borrowed)
            return Finish(borrowed);
        if (owner is not null && SymbolFacts.IsKnownReceiverView(property.Property, _cancellationToken.ThrowIfCancellationRequested))
        {
            var target = Temporary(property);
            Add(SymbolFacts.IsReadOnlyView(property.Type) ? InstructionKind.BorrowShared : InstructionKind.BorrowExclusive,
                property, target: target, source: receiverLoan ?? owner, description: "borrow framework view " + property.Property.Name);
            return Finish(target);
        }
        if (!TracksOwnershipValue(property.Type)) return Finish(null);
        // An opaque getter may compute an unrelated value. A textual receiver.member projection
        // would invent a lifetime dependency, especially after a local receiver is reassigned.
        var external = Temporary(property);
        Add(InstructionKind.Allocate, property, target: external, resourceId: "external-property:" + property.Syntax.SpanStart,
            ownership: ResourceOwnership.External, flags: ResourceFlags(property.Type), description: "receive uncontracted getter result");
        return Finish(external);
    }

    private string? Invoke(IInvocationOperation invocation)
    {
        var method = invocation.TargetMethod;
        var instance = SnapshotCallValue(Evaluate(invocation.Instance, false), invocation, "receiver");
        if (SymbolFacts.IsDispose(method) || SymbolFacts.IsDisposeAsync(method))
        {
            var asynchronous = SymbolFacts.IsDisposeAsync(method);
            if (instance is not null)
                Add(InstructionKind.Dispose, invocation, source: instance,
                    flags: (invocation.IsImplicit ? InstructionFlags.Implicit : InstructionFlags.None) |
                           (asynchronous ? InstructionFlags.AsyncDispose : InstructionFlags.None));
            // A synchronous throw must not invent an awaitable. The owner's invocation obligation is
            // discharged first; the completion obligation exists only on the successful call edge.
            MarkMayThrow(invocation);
            if (!asynchronous || instance is null) return ReleaseReceiverSnapshot(instance, invocation, null);
            var completion = Temporary(invocation);
            Add(InstructionKind.BeginAsyncDispose, invocation, source: instance, target: completion,
                resourceId: "async-dispose:" + invocation.Syntax.SpanStart,
                flags: InstructionFlags.DisposalAlreadyInvoked | (invocation.IsImplicit ? InstructionFlags.Implicit : InstructionFlags.None),
                description: "track asynchronous disposal completion");
            return ReleaseReceiverSnapshot(instance, invocation, TrackProducedValueTask(invocation, completion));
        }
        if (IsCompletionWrapper(method))
        {
            foreach (var argument in invocation.Arguments) Evaluate(argument.Value);
            if (instance is not null) Add(InstructionKind.Read, invocation, source: instance);
            var reusable = instance is not null && IsValueTaskType(method.ContainingType) && method.Name is "AsTask" or "Preserve";
            if (reusable) Add(InstructionKind.ConsumeValueTask, invocation, source: instance!,
                description: "consume original ValueTask when converting to a reusable awaitable");
            MarkMayThrow(invocation);
            if (reusable)
            {
                var result = Temporary(invocation);
                Add(InstructionKind.PreserveAwaitable, invocation, target: result, source: instance!,
                    description: "retain carrier and payload identity in reusable " + method.Name + " result");
                return ReleaseReceiverSnapshot(instance, invocation, result);
            }
            return ReleaseReceiverSnapshot(instance, invocation, instance);
        }
        if (IsCompletionObservation(method))
        {
            if (instance is not null && IsValueTaskType(method.ContainingType))
                Add(InstructionKind.ConsumeValueTask, invocation, source: instance, description: "consume ValueTask through GetResult");
            if (instance is not null) Add(InstructionKind.CompleteAsyncDispose, invocation, source: instance,
                description: "observe disposal completion through " + method.Name);
            var producesResult = instance is not null && method.Name == "GetResult" && TracksOwnershipValue(invocation.Type);
            if (instance is not null) Add(producesResult ? InstructionKind.ObserveAsyncFactory : InstructionKind.ObserveAsyncComposition, invocation, source: instance,
                description: "observe factory completion before GetResult returns or faults");
            MarkMayThrow(invocation);
            CompleteComposition(invocation, instance);
            return ReleaseReceiverSnapshot(instance, invocation, producesResult ? ReceiveAwaitedResult(invocation, instance, true) : null);
        }
        var summary = _summaries.Get(method, _cancellationToken);
        if (method.ContainingType.ToDisplayString() == "SafeSharp.Ownership" && method.Name is "Move" or "Borrow" or "BorrowMut" && invocation.Arguments.Length == 1)
        {
            var source = Evaluate(invocation.Arguments[0].Value, false);
            if (source is null) return null;
            var target = Temporary(invocation);
            Add(method.Name == "Move" ? InstructionKind.Move : method.Name == "BorrowMut" ? InstructionKind.BorrowExclusive : InstructionKind.BorrowShared,
                invocation, target: target, source: source);
            return target;
        }

        string? receiverLoan = null;
        if (instance is not null && SymbolFacts.HasAttribute(method, "Mutates"))
        {
            receiverLoan = "$receiver-loan:" + invocation.Syntax.SpanStart;
            Add(InstructionKind.ReserveExclusiveBorrow, invocation, target: receiverLoan, source: instance,
                description: "reserve mutable receiver while evaluating arguments");
        }
        var arguments = EvaluateArguments(invocation.Arguments, method, summary);
        if (instance is not null && receiverLoan is null) Add(InstructionKind.Read, invocation, source: instance);
        if (receiverLoan is not null)
            Add(InstructionKind.ActivateBorrow, invocation, source: receiverLoan, description: "activate mutable receiver at call");
        ActivateArguments(arguments);
        if (receiverLoan is not null) Add(InstructionKind.Write, invocation, source: receiverLoan);
        TransferCallbackCaptures(invocation);
        InvalidateCallAggregates(invocation, method, arguments, instance, invocation.Instance?.Type);
        MarkCallMayThrow(invocation, invocation.Arguments, arguments);
        ApplyOutputArguments(invocation.Arguments, arguments.Values, summary);
        TouchArguments(arguments);
        if (receiverLoan is not null) Add(InstructionKind.Read, invocation, source: receiverLoan);

        string? Finish(string? result)
        {
            result = TrackProducedValueTask(invocation, result, preserveExisting: result is not null);
            EndArguments(arguments);
            if (receiverLoan is not null) Add(InstructionKind.ReleaseLoan, invocation, target: receiverLoan);
            return ReleaseReceiverSnapshot(instance, invocation, result);
        }

        if (ComposeAsync(invocation, instance, arguments) is { } composition) return Finish(composition);

        if (IsKnownResultProducer(method) && OwnedAwaitablePayload(invocation.Type) is { } producedType &&
            (TracksOwnershipValue(producedType) || IsValueTaskType(producedType)))
        {
            arguments.Values.TryGetValue(0, out var result);
            return Finish(BindAwaitableResult(invocation, result));
        }

        if (SymbolFacts.IsPoolRent(method))
        {
            var target = Temporary(invocation);
            Add(InstructionKind.Allocate, invocation, target: target, resourceId: "pool:" + invocation.Syntax.SpanStart,
                description: "lease from " + method.ContainingType.ToDisplayString());
            return Finish(target);
        }
        if (SymbolFacts.IsPoolReturn(method) && arguments.Values.TryGetValue(0, out var lease))
        {
            Add(InstructionKind.Dispose, invocation, source: lease, description: "return lease to pool");
            return Finish(null);
        }
        if (SymbolFacts.IsServiceResolution(method))
        {
            var owner = instance ?? (arguments.Values.TryGetValue(0, out var provider) ? provider : null);
            var target = Temporary(invocation);
            Add(InstructionKind.Allocate, invocation, target: target, resourceId: "service:" + invocation.Syntax.SpanStart,
                ownership: ResourceOwnership.Scoped, owner: owner, flags: InstructionFlags.SuppressLeak,
                description: "resolve container-owned service");
            return Finish(target);
        }
        if (ReturnedReference(invocation, method, summary, arguments) is { } reference)
            return Finish(reference);
        if (ReconstructAsyncReturn(invocation, method, summary, receiverLoan ?? instance, arguments) is { } nested)
            return Finish(nested);
        if (ReturnedBorrow(invocation, method, summary, receiverLoan ?? instance, arguments) is { } borrowed)
            return Finish(borrowed);
        // The contract describes T, not Task<T>'s IDisposable implementation. Preserve the
        // carrier through ordinary places and aliases; only successful result reception owns T.
        if (summary.ReturnsOwned && OwnedAwaitablePayload(invocation.Type) is { } payload)
        {
            if (!TracksOwnershipValue(payload)) return Finish(null);
            var target = Temporary(invocation);
            Add(InstructionKind.BeginAsyncFactory, invocation, target: target, resourceId: "async-factory:" + invocation.Syntax.SpanStart,
                flags: ResourceFlags(payload), description: "track deferred owned result of " + method.ToDisplayString());
            return Finish(target);
        }
        if (summary.ReturnsOwned && TracksOwnershipValue(invocation.Type))
        {
            var target = Temporary(invocation);
            Add(InstructionKind.Allocate, invocation, target: target, resourceId: "factory:" + invocation.Syntax.SpanStart, flags: ResourceFlags(invocation.Type),
                description: "acquire result of " + method.ToDisplayString());
            return Finish(target);
        }
        if (TracksOwnershipValue(invocation.Type))
        {
            var target = Temporary(invocation);
            Add(InstructionKind.Allocate, invocation, target: target, resourceId: "external-call:" + invocation.Syntax.SpanStart,
                ownership: ResourceOwnership.External, flags: ResourceFlags(invocation.Type), description: "uncontracted external result");
            return Finish(target);
        }
        return Finish(null);
    }

    private sealed class CallArguments
    {
        internal Dictionary<int, string> Values { get; } = new();
        internal Dictionary<int, string> ParameterLoans { get; } = new();
        internal List<(string Place, IOperation Site)> PassedValues { get; } = new();
        internal List<(string Place, IOperation Site, bool Reserved, bool Mutable)> Loans { get; } = new();
        internal List<(string Place, IOperation Site, string Description, int Order)> Disposals { get; } = new();
        internal List<(string Place, IOperation Site, string Description)> Transfers { get; } = new();
        internal List<(string Place, IOperation Site)> Snapshots { get; } = new();
        internal List<(string Place, IOperation Site)> ReferenceSnapshots { get; } = new();
    }

    private string? SnapshotCallValue(string? source, IOperation operation, string role)
    {
        if (source is null) return null;
        var snapshot = "$t:call-" + role + ":" + operation.Syntax.SpanStart + ":" + operation.Kind;
        Add(InstructionKind.Alias, operation, target: snapshot, source: source,
            description: "capture evaluated " + role + " value until the call completes");
        ForgetTemporary(source, operation);
        return snapshot;
    }

    private string? ReleaseReceiverSnapshot(string? receiver, IOperation operation, string? result)
    {
        if (receiver is null) return result;
        if (result == receiver)
        {
            result = Temporary(operation);
            Add(InstructionKind.Alias, operation, target: result, source: receiver, description: "preserve returned receiver value");
        }
        ReleaseCallSnapshot(receiver, operation);
        return result;
    }

    private void ReleaseCallSnapshot(string snapshot, IOperation operation) =>
        Add(InstructionKind.ReleaseLoan, operation, target: snapshot, source: snapshot,
            description: "end evaluated call value after preserving its result");

    private string? ReturnedBorrow(IOperation operation, IMethodSymbol method, MethodSummary summary, string? receiver, CallArguments arguments)
    {
        if (!summary.HasBorrowedReturn) return null;
        var origins = new List<string>();
        foreach (var from in summary.BorrowedSources)
        {
            var source = from == "this" ? receiver : method.Parameters.FirstOrDefault(parameter => parameter.Name == from) is { } parameter &&
                arguments.Values.TryGetValue(parameter.Ordinal, out var value)
                    ? arguments.ParameterLoans.TryGetValue(parameter.Ordinal, out var loan) ? loan : value : null;
            if (source is not null && !origins.Contains(source)) origins.Add(source);
        }
        if (origins.Count == 0) return null;
        // The declared lifetime may have several possible inputs. Join their references before
        // creating one returned loan family, preserving parent loans without inventing conflicts.
        var union = "$return-origins:" + operation.Syntax.SpanStart;
        Add(InstructionKind.Alias, operation, target: union, source: origins[0]);
        foreach (var source in origins.Skip(1)) Add(InstructionKind.MergeAlias, operation, target: union, source: source);
        var asyncPayload = summary.ReturnsAsyncPayload && OwnedAwaitablePayload(operation.Type) is { } payload && TracksOwnershipValue(payload);
        var target = asyncPayload ? "$async-return-payload:" + operation.Syntax.SpanStart : Temporary(operation);
        Add(summary.MutableBorrow ? InstructionKind.BorrowExclusive : InstructionKind.BorrowShared,
            operation, target: target, source: union, description: "returned borrow bounded by " + string.Join(", ", summary.BorrowedSources));
        Add(InstructionKind.ReleaseLoan, operation, target: union);
        if (asyncPayload)
        {
            var carrier = BindAwaitableResult(operation, target, pending: true);
            Add(InstructionKind.ReleaseLoan, operation, target: target, description: "retain the promised result loan through its carrier");
            return carrier;
        }
        return target;
    }

    private string? Convert(IConversionOperation conversion, IMethodSymbol method)
    {
        var source = Evaluate(conversion.Operand, false);
        var summary = _summaries.Get(method, _cancellationToken);
        var arguments = new CallArguments();
        if (source is not null) AddArgument(arguments, source, method.Parameters.FirstOrDefault(), conversion, method, summary);
        ActivateArguments(arguments);
        InvalidateCallAggregates(conversion, method, arguments);
        MarkMayThrow(conversion);
        TouchArguments(arguments);
        string? result = null;
        if (ReturnedBorrow(conversion, method, summary, null, arguments) is { } borrowed)
        {
            result = borrowed;
        }
        else if (summary.ReturnsOwned && OwnedAwaitablePayload(conversion.Type) is { } payload && TracksOwnershipValue(payload))
        {
            result = Temporary(conversion);
            Add(InstructionKind.BeginAsyncFactory, conversion, target: result, resourceId: "conversion-factory:" + conversion.Syntax.SpanStart,
                flags: ResourceFlags(payload), description: "receive an owned-result carrier from " + method.ToDisplayString());
        }
        else if (TracksOwnershipValue(conversion.Type))
        {
            result = Temporary(conversion);
            Add(InstructionKind.Allocate, conversion, target: result, resourceId: "conversion:" + conversion.Syntax.SpanStart,
                ownership: summary.ReturnsOwned ? ResourceOwnership.Owned : ResourceOwnership.External,
                flags: ResourceFlags(conversion.Type), description: "receive result of " + method.ToDisplayString());
        }
        EndArguments(arguments);
        return result;
    }

    private CallArguments EvaluateArguments(System.Collections.Immutable.ImmutableArray<IArgumentOperation> arguments,
        IMethodSymbol? method, MethodSummary? summary = null)
    {
        var result = new CallArguments();
        foreach (var argument in arguments)
        {
            var source = EvaluateReferenceArgument(argument, result);
            if (source is null) continue;
            AddArgument(result, source, argument.Parameter, argument, method, summary);
        }
        return result;
    }

    private void AddArgument(CallArguments result, string source, IParameterSymbol? parameter, IOperation argument,
        IMethodSymbol? method, MethodSummary? summary)
    {
        if (parameter is null || parameter.RefKind == RefKind.None)
        {
            source = SnapshotCallValue(source, argument, "argument:" + (parameter?.Ordinal ?? -1))!;
            result.Snapshots.Add((source, argument));
        }
        if (parameter is not null) result.Values[parameter.Ordinal] = source;
        // Out slots are written by the completed call. No incoming value is read or consumed.
        if (parameter?.RefKind == RefKind.Out) return;
        if (parameter is not null && (SymbolFacts.HasAttribute(parameter, "Consumes") || SymbolFacts.HasAttribute(parameter, "Owned")))
            result.Transfers.Add((source, argument, "transfer to " + method?.Name + "(" + parameter.Name + ") at call entry"));
        else if (parameter is not null && (SymbolFacts.HasAttribute(parameter, "Borrowed") || SymbolFacts.HasAttribute(parameter, "BorrowedMut") ||
            parameter.RefKind is RefKind.Ref or RefKind.In or RefKind.RefReadOnlyParameter))
        {
            var loan = "$call-loan:" + argument.Syntax.SpanStart + ":" + parameter.Ordinal;
            var reserved = SymbolFacts.HasAttribute(parameter, "BorrowedMut");
            var kind = reserved ? InstructionKind.ReserveExclusiveBorrow : parameter.RefKind == RefKind.Ref
                ? InstructionKind.BorrowExclusive : InstructionKind.BorrowShared;
            Add(kind, argument, target: loan, source: source, description: "borrow for argument " + parameter.Name);
            result.Loans.Add((loan, argument, reserved, reserved || parameter.RefKind == RefKind.Ref));
            result.ParameterLoans[parameter.Ordinal] = loan;
        }
        else if (parameter is not null && summary?.DisposedParameters.Contains(parameter.Ordinal) == true)
        {
            for (var index = 0; index < summary.ParameterDisposalOrder.Length; index++)
                if (summary.ParameterDisposalOrder[index] == parameter.Ordinal)
                    result.Disposals.Add((source, argument, "callee disposes parameter " + parameter.Name, index));
        }
        else
        {
            Add(InstructionKind.Read, argument, source: source);
            result.PassedValues.Add((source, argument));
        }
    }

    private void ActivateArguments(CallArguments arguments)
    {
        foreach (var loan in arguments.Loans.Where(loan => loan.Reserved))
            Add(InstructionKind.ActivateBorrow, loan.Site, source: loan.Place, description: "activate mutable argument at call");
        foreach (var loan in arguments.Loans.Where(loan => loan.Mutable))
            Add(InstructionKind.Write, loan.Site, source: loan.Place, description: "callee may mutate borrowed argument");
        foreach (var transfer in arguments.Transfers)
            Add(InstructionKind.Move, transfer.Site, source: transfer.Place, description: transfer.Description);
        foreach (var disposal in arguments.Disposals.OrderBy(disposal => disposal.Order))
        {
            Add(InstructionKind.Dispose, disposal.Site, source: disposal.Place, description: disposal.Description);
            // A later cleanup in the callee is reached only after earlier cleanup returned.
            // Preserving each boundary also retains duplicate disposal and named-argument order.
            MarkMayThrow(disposal.Site);
        }
    }

    private void TouchArguments(CallArguments arguments)
    {
        // Loans remain live simultaneously through activation and the call. These reads precede
        // returned reborrows: reading an exclusive parent after creating its child would be invalid.
        foreach (var loan in arguments.Loans) Add(InstructionKind.Read, loan.Site, source: loan.Place);
        foreach (var value in arguments.PassedValues) Add(InstructionKind.Read, value.Site, source: value.Place);
    }

    private void EndArguments(CallArguments arguments)
    {
        foreach (var loan in arguments.Loans) Add(InstructionKind.ReleaseLoan, loan.Site, target: loan.Place);
        foreach (var snapshot in arguments.Snapshots) ReleaseCallSnapshot(snapshot.Place, snapshot.Site);
        EndReferenceArguments(arguments);
    }

    private static bool IsFrameworkAwaitableMethod(IMethodSymbol method) => IsFrameworkAwaitableType(method.ContainingType);

    private static bool IsValueTaskType(ITypeSymbol? symbol)
    {
        if (symbol is not INamedTypeSymbol type || !IsFrameworkAwaitableType(type)) return false;
        while (type.ContainingType is { } containing) type = containing;
        return type.Name is "ValueTask" or "ConfiguredValueTaskAwaitable" or "ValueTaskAwaiter";
    }

    private string? TrackProducedValueTask(IOperation operation, string? target, bool preserveExisting = false, string? carrier = null)
    {
        if (!IsValueTaskType(operation.Type)) return target;
        target ??= Temporary(operation);
        Add(InstructionKind.TrackValueTask, operation, target: target, source: target, owner: carrier, resourceId: "value-task:" + operation.Syntax.SpanStart,
            flags: preserveExisting ? InstructionFlags.PreserveExistingCapability : InstructionFlags.None,
            description: "track single-consumption ValueTask result");
        return target;
    }

    private static bool IsFrameworkAwaitableType(ITypeSymbol? symbol)
    {
        if (symbol is not INamedTypeSymbol type) return false;
        var assembly = type.ContainingAssembly.Identity.Name;
        if (assembly is not ("System.Private.CoreLib" or "mscorlib" or "System.Runtime" or "netstandard" or "System.Threading.Tasks" or "System.Threading.Tasks.Extensions")) return false;
        while (type.ContainingType is { } containing) type = containing;
        return type.ContainingNamespace.ToDisplayString() is "System.Threading.Tasks" or "System.Runtime.CompilerServices" &&
            type.Name is "Task" or "ValueTask" or "ConfiguredTaskAwaitable" or "ConfiguredValueTaskAwaitable" or "TaskAwaiter" or "ValueTaskAwaiter";
    }

    private static bool IsCompletionWrapper(IMethodSymbol method) => !method.IsStatic && IsFrameworkAwaitableMethod(method) &&
        (method.Name == "ConfigureAwait" && method.Parameters.Length == 1 || method.Name is "AsTask" or "GetAwaiter" or "Preserve" && method.Parameters.Length == 0);

    private static bool IsCompletionObservation(IMethodSymbol method) => !method.IsStatic && method.Parameters.Length == 0 &&
        method.Name is "GetResult" or "Wait" && IsFrameworkAwaitableMethod(method);

    private static ITypeSymbol? OwnedAwaitablePayload(ITypeSymbol? symbol) => symbol is INamedTypeSymbol { Arity: 1 } type &&
        type.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks" && type.Name is "Task" or "ValueTask" &&
        IsFrameworkAwaitableType(type) ? type.TypeArguments[0] : null;

    private static bool IsKnownResultProducer(IMethodSymbol method) => method.IsStatic && method.Name == "FromResult" &&
        method.Arity == 1 && method.Parameters.Length == 1 && method.ContainingType.Name is "Task" or "ValueTask" &&
        IsFrameworkAwaitableMethod(method);

    private string BindAwaitableResult(IOperation operation, string? result, bool pending = false)
    {
        var target = Temporary(operation);
        Add(InstructionKind.BindAsyncResult, operation, target: target, source: result ?? "$async-null:" + operation.Syntax.SpanStart,
            resourceId: "bound-factory:" + operation.Syntax.SpanStart, flags: pending ? InstructionFlags.DeferredAsyncResult : InstructionFlags.None,
            description: pending ? "retain a promised borrowed result in its asynchronous carrier" : "wrap an existing result in a completed framework carrier");
        return target;
    }

    private string? KnownAwaitableCreation(IObjectCreationOperation creation, CallArguments arguments)
    {
        if (IsValueTaskType(creation.Type) && creation.Constructor is { Parameters.Length: 1 } taskConstructor &&
            taskConstructor.Parameters[0].Type is INamedTypeSymbol { Name: "Task" } && IsFrameworkAwaitableType(taskConstructor.Parameters[0].Type) &&
            arguments.Values.TryGetValue(0, out var task))
        {
            var target = Temporary(creation);
            Add(InstructionKind.Alias, creation, target: target, source: task, description: "preserve Task completion identity through ValueTask construction");
            return target;
        }
        if (creation.Type is not INamedTypeSymbol { Name: "ValueTask" } || creation.Constructor is not { Parameters.Length: 1 } constructor ||
            OwnedAwaitablePayload(creation.Type) is not { } payload || !TracksOwnershipValue(payload)) return null;
        arguments.Values.TryGetValue(0, out var source);
        if (SymbolEqualityComparer.Default.Equals(constructor.Parameters[0].Type, payload))
            return BindAwaitableResult(creation, source);
        if (OwnedAwaitablePayload(constructor.Parameters[0].Type) is { } wrapped && SymbolEqualityComparer.Default.Equals(wrapped, payload) && source is not null)
        {
            var target = Temporary(creation);
            Add(InstructionKind.Alias, creation, target: target, source: source, description: "preserve Task result identity through the ValueTask constructor");
            return target;
        }
        return null;
    }

    private string? ReceiveAwaitedResult(IOperation operation, string? carrier, bool knownFrameworkAwaitable)
    {
        if (!TracksOwnershipValue(operation.Type) && !IsValueTaskType(operation.Type)) return null;
        var target = Temporary(operation);
        if (carrier is not null && knownFrameworkAwaitable)
            Add(InstructionKind.ReceiveAsyncResult, operation, target: target, source: carrier, resourceId: "external-await:" + operation.Syntax.SpanStart,
                flags: ResourceFlags(operation.Type), description: "receive result after successful asynchronous completion");
        else
            Add(InstructionKind.Allocate, operation, target: target, resourceId: "external-await:" + operation.Syntax.SpanStart,
                ownership: ResourceOwnership.External, flags: ResourceFlags(operation.Type), description: "receive opaque awaitable result without assuming ownership or aliases");
        return TrackProducedValueTask(operation, target, preserveExisting: true, carrier: knownFrameworkAwaitable ? carrier : null);
    }

    private void ApplyOutputArguments(System.Collections.Immutable.ImmutableArray<IArgumentOperation> arguments,
        Dictionary<int, string> values, MethodSummary? summary)
    {
        ApplyKnownReferenceOutputs(arguments, values, summary);
        foreach (var argument in arguments.Where(argument => argument.Parameter?.RefKind == RefKind.Out))
        {
            var parameter = argument.Parameter!;
            if (!values.TryGetValue(parameter.Ordinal, out var target)) continue;
            Add(InstructionKind.Forget, argument, target: target, description: "replace caller out slot after successful call");
            if (summary?.OwnedOutputParameters.TryGetValue(parameter.Ordinal, out var nullable) == true)
                Add(InstructionKind.Allocate, argument, target: target, resourceId: "out-owned:" + argument.Syntax.SpanStart,
                    flags: ResourceFlags(parameter.Type) | (nullable ? InstructionFlags.MayBeNull : InstructionFlags.None),
                    description: "receive owned out parameter " + parameter.Name);
            else if (TracksOwnershipValue(parameter.Type))
                Add(InstructionKind.Allocate, argument, target: target, resourceId: "out-external:" + argument.Syntax.SpanStart,
                    ownership: ResourceOwnership.External, flags: ResourceFlags(parameter.Type), description: "receive uncontracted out parameter " + parameter.Name);
        }
        ApplyUnknownReferenceOutputs(arguments, values, summary);
    }

    private string Use(string place, IOperation operation, bool read)
    { if (read) Add(InstructionKind.Read, operation, source: place); return place; }

    private void MarkMayThrow(IOperation operation) => Add(InstructionKind.Nop, operation,
        flags: InstructionFlags.MayThrow, description: "potential exception boundary");

    private void ForgetTemporary(string? place, IOperation operation)
    {
        if (place?.StartsWith("$t:", StringComparison.Ordinal) == true)
            Add(InstructionKind.ReleaseLoan, operation, target: place, description: "end compiler temporary after assignment");
    }

    private void Add(InstructionKind kind, IOperation operation, string? target = null, string? source = null,
        string? resourceId = null, ResourceOwnership ownership = ResourceOwnership.Owned, string? owner = null,
        InstructionFlags flags = InstructionFlags.None, string? description = null, int asyncResultDepth = 1,
        AsyncCompositionKind asyncComposition = AsyncCompositionKind.None, string? projection = null) =>
        _instructions.Add(new FlowInstruction(kind, Site(operation.Syntax.GetLocation()), target, source, resourceId, ownership, owner, flags, description, asyncResultDepth, asyncComposition, projection));

    public static SourceSite Site(Location location)
    {
        if (!location.IsInSource) return new SourceSite("", 0, 0);
        var span = location.GetLineSpan();
        return new SourceSite(span.Path, location.SourceSpan.Start, location.SourceSpan.Length, span.StartLinePosition.Line, span.StartLinePosition.Character);
    }
}
