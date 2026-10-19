using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;
using SafeSharp.Core;

namespace SafeSharp.Analyzers.Analysis;

/// <summary>
/// Lowers semantic operations to explicit ownership effects. Syntax is used only for locations and
/// stable IDs; classification uses symbols, interface implementations and contracts.
/// </summary>
internal sealed class OperationLowerer
{
    private readonly IMethodSymbol _method;
    private readonly MethodSummaryProvider _summaries;
    private readonly CancellationToken _cancellationToken;
    private readonly Dictionary<ISymbol, string> _places = new(SymbolEqualityComparer.Default);
    private readonly HashSet<string> _seeded = new(StringComparer.Ordinal);
    private readonly List<FlowInstruction> _seeds = new();
    private List<FlowInstruction> _instructions = new();

    public OperationLowerer(IMethodSymbol method, MethodSummaryProvider summaries, CancellationToken cancellationToken)
    { _method = method; _summaries = summaries; _cancellationToken = cancellationToken; SeedParameters(); }

    public const string ReturnPlace = "$return";
    public IReadOnlyList<FlowInstruction> Seeds => _seeds;
    public bool ReturnsBorrowed => _summaries.Get(_method, _cancellationToken).BorrowedFrom is not null;

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
            var source = Evaluate(branchValue, read: !returning);
            if (returning && source is not null)
                Add(InstructionKind.Alias, branchValue, target: ReturnPlace, source: source, description: "evaluate return value before cleanup");
        }
        return _instructions;
    }

    private void SeedParameters()
    {
        foreach (var parameter in _method.Parameters)
        {
            if (!SymbolFacts.IsTracked(parameter.Type) && !HasOwnershipContract(parameter) &&
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

    private static bool HasOwnershipContract(ISymbol symbol) => SymbolFacts.HasAttribute(symbol, "Owned") ||
        SymbolFacts.HasAttribute(symbol, "Consumes") || SymbolFacts.HasAttribute(symbol, "Borrowed") || SymbolFacts.HasAttribute(symbol, "BorrowedMut");

    private void Seed(string place, ITypeSymbol? type, ResourceOwnership ownership, Location location)
    {
        if (!_seeded.Add(place)) return;
        _seeds.Add(new FlowInstruction(InstructionKind.Allocate, Site(location), target: place, resourceId: "input:" + place,
            ownership: ownership, flags: ResourceFlags(type) | (type?.IsReferenceType == true && place != "this" ? InstructionFlags.MayBeNull : InstructionFlags.None),
            description: ownership + " input " + place));
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

    private string? Evaluate(IOperation? operation, bool read = true, bool awaited = false)
    {
        if (operation is null) return null;
        _cancellationToken.ThrowIfCancellationRequested();
        switch (operation)
        {
            case IConversionOperation conversion: return Evaluate(conversion.Operand, read, awaited);
            case IParenthesizedOperation parenthesized: return Evaluate(parenthesized.Operand, read, awaited);
            case IExpressionStatementOperation expression: return Evaluate(expression.Operation, read, awaited);
            case ILocalReferenceOperation local:
                return Use(Place(local.Local), local, read);
            case IParameterReferenceOperation parameter:
                return Use(Place(parameter.Parameter), parameter, read);
            case IInstanceReferenceOperation instance:
                return Use("this", instance, read);
            case IFlowCaptureReferenceOperation capture:
                return Use(Capture(capture.Id), capture, read);
            case IFlowCaptureOperation capture:
            {
                var value = Evaluate(capture.Value, false);
                if (value is not null) Add(InstructionKind.Alias, capture, target: Capture(capture.Id), source: value);
                else Add(InstructionKind.Forget, capture, target: Capture(capture.Id));
                ForgetTemporary(value, capture);
                return Capture(capture.Id);
            }
            case IVariableDeclaratorOperation declaration:
            {
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
            case IObjectCreationOperation creation:
            {
                EvaluateArguments(creation.Arguments, creation.Constructor);
                MarkMayThrow(creation);
                if (creation.Initializer is not null) Evaluate(creation.Initializer);
                if (!SymbolFacts.IsTracked(creation.Type)) return null;
                var target = Temporary(creation);
                Add(InstructionKind.Allocate, creation, target: target, resourceId: "new:" + creation.Syntax.SpanStart,
                    flags: ResourceFlags(creation.Type), description: "acquire " + creation.Type?.ToDisplayString());
                return target;
            }
            case IInvocationOperation invocation:
                return Invoke(invocation, awaited);
            case IAwaitOperation awaitOperation:
            {
                var value = Evaluate(awaitOperation.Operation, false, true);
                // Task<T>/ValueTask<T> factory results become owned only once awaited.
                if (value is null && SymbolFacts.IsTracked(awaitOperation.Type) && SymbolFacts.Unwrap(awaitOperation.Operation) is IInvocationOperation factory &&
                    _summaries.Get(factory.TargetMethod, _cancellationToken).ReturnsOwned)
                {
                    value = Temporary(awaitOperation);
                    Add(InstructionKind.Allocate, awaitOperation, target: value, resourceId: "await:" + awaitOperation.Syntax.SpanStart,
                        flags: ResourceFlags(awaitOperation.Type));
                }
                Add(InstructionKind.Suspend, awaitOperation, description: "await suspension point");
                return value;
            }
            case IFieldReferenceOperation field:
            {
                var owner = Evaluate(field.Instance);
                var place = field.Field.IsStatic ? Place(field.Field) : (owner ?? "this") + "." + field.Field.Name;
                if (SymbolFacts.IsTracked(field.Type) || HasOwnershipContract(field.Field))
                    Seed(place, field.Type, SymbolFacts.HasAttribute(field.Field, "Borrowed") ? ResourceOwnership.Borrowed : ResourceOwnership.External, field.Syntax.GetLocation());
                return Use(place, field, read);
            }
            case IPropertyReferenceOperation property:
                return Property(property, read);
            case IArrayElementReferenceOperation element:
            {
                var owner = Evaluate(element.ArrayReference);
                foreach (var index in element.Indices) Evaluate(index);
                return owner is null ? null : Use(owner + "[" + string.Join(",", element.Indices.Select(i => i.ConstantValue.HasValue ? i.ConstantValue.Value?.ToString() ?? "null" : "*")) + "]", element, read);
            }
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
        var target = Address(assignment.Target, writeReceiver: true);
        var source = Evaluate(assignment.Value, false);
        if (target is null) return source;
        if (source is null) Add(InstructionKind.Forget, assignment, target: target);
        else
        {
            Add(InstructionKind.Alias, assignment, target: target, source: source);
            if (assignment.Target is IFieldReferenceOperation or IPropertyReferenceOperation or IArrayElementReferenceOperation)
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
            case IFlowCaptureReferenceOperation capture: return Capture(capture.Id);
            case IDiscardOperation: return "$discard:" + operation.Syntax.SpanStart;
            case IFieldReferenceOperation field:
            {
                var owner = Evaluate(field.Instance, false);
                if (owner is not null && writeReceiver) Add(InstructionKind.Write, field, source: owner);
                return field.Field.IsStatic ? Place(field.Field) : (owner ?? "this") + "." + field.Field.Name;
            }
            case IPropertyReferenceOperation property:
            {
                var owner = Evaluate(property.Instance, false);
                foreach (var argument in property.Arguments) Evaluate(argument.Value);
                if (owner is not null && writeReceiver) Add(InstructionKind.Write, property, source: owner);
                return (owner ?? "static:" + property.Property.ContainingType.ToDisplayString()) + "." + property.Property.Name;
            }
            case IArrayElementReferenceOperation element:
            {
                var owner = Evaluate(element.ArrayReference, false);
                foreach (var index in element.Indices) Evaluate(index);
                if (owner is not null && writeReceiver) Add(InstructionKind.Write, element, source: owner);
                return owner is null ? null : owner + "[" + string.Join(",", element.Indices.Select(i => i.ConstantValue.HasValue ? i.ConstantValue.Value?.ToString() ?? "null" : "*")) + "]";
            }
            default: return Evaluate(operation, false);
        }
    }

    private string? Property(IPropertyReferenceOperation property, bool read)
    {
        var owner = Evaluate(property.Instance, false);
        foreach (var argument in property.Arguments) Evaluate(argument.Value);
        MarkMayThrow(property);
        if (property.Property.Name == "ServiceProvider" && owner is not null && SymbolFacts.Implements(property.Type, "System.IServiceProvider"))
            return Use(owner, property, read);
        var contract = property.Property.GetMethod is { } getter ? _summaries.Get(getter, _cancellationToken) : null;
        if (contract?.ReturnsOwned == true)
        {
            if (owner is not null) Add(InstructionKind.Read, property, source: owner);
            var target = Temporary(property);
            Add(InstructionKind.Allocate, property, target: target, resourceId: "property:" + property.Syntax.SpanStart, flags: ResourceFlags(property.Type));
            return target;
        }
        if (owner is not null && (SymbolFacts.IsMemoryView(property.Type) || contract?.BorrowedFrom == "this"))
        {
            var target = Temporary(property);
            Add(SymbolFacts.IsReadOnlyView(property.Type) || contract is { MutableBorrow: false } ? InstructionKind.BorrowShared : InstructionKind.BorrowExclusive,
                property, target: target, source: owner, description: "borrow view " + property.Property.Name);
            return target;
        }
        if (owner is not null) Add(InstructionKind.Read, property, source: owner);
        var place = (owner ?? "static:" + property.Property.ContainingType.ToDisplayString()) + "." + property.Property.Name;
        if (SymbolFacts.IsTracked(property.Type)) Seed(place, property.Type, ResourceOwnership.External, property.Syntax.GetLocation());
        return SymbolFacts.IsTracked(property.Type) ? Use(place, property, read) : null;
    }

    private string? Invoke(IInvocationOperation invocation, bool awaited)
    {
        var method = invocation.TargetMethod;
        var instance = Evaluate(invocation.Instance, false);
        if (SymbolFacts.IsDispose(method) || SymbolFacts.IsDisposeAsync(method))
        {
            if (instance is not null && (SymbolFacts.IsDispose(method) || awaited || invocation.IsImplicit))
                Add(InstructionKind.Dispose, invocation, source: instance,
                    flags: (invocation.IsImplicit ? InstructionFlags.Implicit : InstructionFlags.None) |
                           (SymbolFacts.IsDisposeAsync(method) ? InstructionFlags.AsyncDispose : InstructionFlags.None));
            // The caller's release obligation is discharged by invoking cleanup. Whether a throwing
            // implementation physically completed cleanup is outside this call-boundary contract.
            MarkMayThrow(invocation);
            return null;
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
        if (instance is not null) Add(SymbolFacts.HasAttribute(method, "Mutates") ? InstructionKind.Write : InstructionKind.Read, invocation, source: instance);
        var arguments = EvaluateArguments(invocation.Arguments, method, summary);
        MarkMayThrow(invocation);
        if (SymbolFacts.IsPoolRent(method))
        {
            var target = Temporary(invocation);
            Add(InstructionKind.Allocate, invocation, target: target, resourceId: "pool:" + invocation.Syntax.SpanStart,
                description: "lease from " + method.ContainingType.ToDisplayString());
            return target;
        }
        if (SymbolFacts.IsPoolReturn(method) && arguments.TryGetValue(0, out var lease))
        {
            Add(InstructionKind.Dispose, invocation, source: lease, description: "return lease to pool");
            return null;
        }
        if (SymbolFacts.IsServiceResolution(method))
        {
            var owner = instance ?? (arguments.TryGetValue(0, out var provider) ? provider : null);
            var target = Temporary(invocation);
            Add(InstructionKind.Allocate, invocation, target: target, resourceId: "service:" + invocation.Syntax.SpanStart,
                ownership: ResourceOwnership.Scoped, owner: owner, flags: InstructionFlags.SuppressLeak,
                description: "resolve container-owned service");
            return target;
        }
        if (summary.BorrowedFrom is { } from)
        {
            var source = from == "this" ? instance : method.Parameters.FirstOrDefault(p => p.Name == from) is { } parameter &&
                arguments.TryGetValue(parameter.Ordinal, out var value) ? value : null;
            if (source is not null)
            {
                var target = Temporary(invocation);
                Add(summary.MutableBorrow ? InstructionKind.BorrowExclusive : InstructionKind.BorrowShared, invocation, target: target, source: source);
                return target;
            }
        }
        if (summary.ReturnsOwned && SymbolFacts.IsTracked(invocation.Type))
        {
            var target = Temporary(invocation);
            Add(InstructionKind.Allocate, invocation, target: target, resourceId: "factory:" + invocation.Syntax.SpanStart, flags: ResourceFlags(invocation.Type),
                description: "acquire result of " + method.ToDisplayString());
            return target;
        }
        if (SymbolFacts.IsTracked(invocation.Type))
        {
            var target = Temporary(invocation);
            Add(InstructionKind.Allocate, invocation, target: target, resourceId: "external-call:" + invocation.Syntax.SpanStart,
                ownership: ResourceOwnership.External, flags: ResourceFlags(invocation.Type), description: "uncontracted external result");
            return target;
        }
        return null;
    }

    private Dictionary<int, string> EvaluateArguments(System.Collections.Immutable.ImmutableArray<IArgumentOperation> arguments, IMethodSymbol? method, MethodSummary? summary = null)
    {
        var values = new Dictionary<int, string>();
        var loans = new List<string>();
        foreach (var argument in arguments)
        {
            var source = Evaluate(argument.Value, false);
            if (source is null) continue;
            var parameter = argument.Parameter;
            if (parameter is not null) values[parameter.Ordinal] = source;
            if (parameter is not null && (SymbolFacts.HasAttribute(parameter, "Consumes") || SymbolFacts.HasAttribute(parameter, "Owned")))
                Add(InstructionKind.Move, argument, source: source, description: "transfer to " + method?.Name + "(" + parameter.Name + ")");
            else if (parameter is not null && (SymbolFacts.HasAttribute(parameter, "Borrowed") || SymbolFacts.HasAttribute(parameter, "BorrowedMut")))
            {
                var loan = "$call-loan:" + argument.Syntax.SpanStart + ":" + parameter.Ordinal;
                Add(SymbolFacts.HasAttribute(parameter, "BorrowedMut") ? InstructionKind.BorrowExclusive : InstructionKind.BorrowShared,
                    argument, target: loan, source: source, description: "borrow for argument " + parameter.Name);
                loans.Add(loan);
            }
            else if (parameter is not null && summary?.DisposedParameters.Contains(parameter.Ordinal) == true)
                Add(InstructionKind.Dispose, argument, source: source, description: "callee disposes parameter " + parameter.Name);
            else if (parameter?.RefKind == RefKind.Ref)
                Add(InstructionKind.Write, argument, source: source, description: "mutable ref argument");
            else if (parameter?.RefKind != RefKind.Out)
                Add(InstructionKind.Read, argument, source: source);
            if (parameter?.RefKind == RefKind.Out)
                Add(InstructionKind.Forget, argument, target: source);
        }
        // Keep all argument loans live simultaneously through the call; otherwise backward liveness
        // would incorrectly expire the first argument before checking the second argument's alias.
        foreach (var loan in loans)
            _instructions.Add(new FlowInstruction(InstructionKind.Read, _instructions.Last().Site, source: loan));
        foreach (var loan in loans)
            _instructions.Add(new FlowInstruction(InstructionKind.ReleaseLoan, _instructions.Last().Site, target: loan));
        return values;
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
        InstructionFlags flags = InstructionFlags.None, string? description = null) =>
        _instructions.Add(new FlowInstruction(kind, Site(operation.Syntax.GetLocation()), target, source, resourceId, ownership, owner, flags, description));

    public static SourceSite Site(Location location)
    {
        if (!location.IsInSource) return new SourceSite("", 0, 0);
        var span = location.GetLineSpan();
        return new SourceSite(span.Path, location.SourceSpan.Start, location.SourceSpan.Length, span.StartLinePosition.Line, span.StartLinePosition.Character);
    }
}
