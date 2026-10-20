using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using SafeSharp.Core;

namespace SafeSharp.Analyzers.Analysis;

internal sealed partial class OperationLowerer
{
    // Arrays are represented only when an element has an ownership or consumption identity.
    // Scalar arrays remain outside this domain and keep the normal lightweight lowering path.
    private static bool TracksOwnershipValue(ITypeSymbol? type) => SymbolFacts.IsTracked(type) || IsValueTaskType(type) ||
        type is IArrayTypeSymbol array && TracksOwnershipValue(array.ElementType);

    private string? ComposeAsync(IInvocationOperation invocation, string? receiver, CallArguments arguments)
    {
        var kind = _summaries.AsyncResults.ClassifyComposition(invocation.TargetMethod);
        if (kind == AsyncCompositionKind.None) return null;
        var target = Temporary(invocation);
        string? source;
        if (kind == AsyncCompositionKind.WaitAsync) source = receiver;
        else if (kind == AsyncCompositionKind.Unwrap)
            source = receiver ?? (arguments.Values.TryGetValue(0, out var outer) ? outer : null);
        else if (invocation.Arguments.Length == 2 && invocation.Arguments.All(argument => IsFrameworkAwaitableType(argument.Value.Type)))
        {
            // The two-task overload avoids allocating an input array at runtime, but its analysis
            // representation has the same immutable positional inputs as the params overload.
            source = "$composite-arguments:" + invocation.Syntax.SpanStart;
            Add(InstructionKind.CreateAggregate, invocation, target: source, resourceId: "composite-arguments:" + invocation.Syntax.SpanStart);
            for (var ordinal = 0; ordinal < invocation.Arguments.Length; ordinal++)
                if (arguments.Values.TryGetValue(ordinal, out var argument))
                    Add(InstructionKind.SetAggregateElement, invocation, source: argument, owner: source,
                        projection: "[" + ordinal.ToString(CultureInfo.InvariantCulture) + "]");
        }
        else source = arguments.Values.TryGetValue(0, out var input) ? input : null;
        Add(InstructionKind.BeginAsyncComposition, invocation, target: target,
            source: source ?? "$opaque-composite-input:" + invocation.Syntax.SpanStart,
            resourceId: "async-composition:" + invocation.Syntax.SpanStart, asyncComposition: kind,
            flags: OwnedAwaitablePayload(invocation.Type) is { } payload ? InstructionFlags.HasAsyncResult | ResourceFlags(payload) : InstructionFlags.None,
            description: "capture " + kind + " inputs without assuming successful completion");
        if (source?.StartsWith("$composite-arguments:", StringComparison.Ordinal) == true)
            Add(InstructionKind.ReleaseLoan, invocation, target: source);
        return target;
    }

    private string? CreateCollection(ICollectionExpressionOperation collection)
    {
        var represented = collection.ConstructMethod is null &&
            (collection.Type is IArrayTypeSymbol array && TracksOwnershipValue(array.ElementType) ||
             collection.Type is INamedTypeSymbol { Arity: 1 } span && SymbolFacts.IsMemoryView(span) && TracksOwnershipValue(span.TypeArguments[0]));
        if (!represented || collection.Elements.Any(element => element is ISpreadOperation))
        {
            foreach (var element in collection.Elements) Evaluate(element);
            MarkMayThrow(collection);
            return null;
        }
        // C# 13 prefers the framework params ReadOnlySpan overload. Roslyn represents those
        // implicit params values as collection expressions, not array-creation operations.
        // https://learn.microsoft.com/dotnet/core/compatibility/core-libraries/9.0/params-overloads
        var target = Temporary(collection);
        MarkMayThrow(collection);
        Add(InstructionKind.CreateAggregate, collection, target: target, resourceId: "collection:" + collection.Syntax.SpanStart,
            description: "track fixed collection-expression element identities");
        for (var index = 0; index < collection.Elements.Length; index++)
        {
            var element = collection.Elements[index];
            var source = Evaluate(element, false);
            Add(InstructionKind.SetAggregateElement, element, source: source ?? "$null-element:" + element.Syntax.SpanStart,
                owner: target, projection: "[" + index.ToString(CultureInfo.InvariantCulture) + "]");
            ForgetTemporary(source, element);
        }
        return target;
    }

    private string? CreateArray(IArrayCreationOperation creation)
    {
        foreach (var size in creation.DimensionSizes) Evaluate(size);
        MarkMayThrow(creation);
        if (!TracksOwnershipValue(creation.Type))
        {
            if (creation.Initializer is not null) foreach (var element in creation.Initializer.ElementValues) Evaluate(element);
            return null;
        }
        var target = Temporary(creation);
        Add(InstructionKind.CreateAggregate, creation, target: target, resourceId: "array:" + creation.Syntax.SpanStart,
            description: "track array element identities at this allocation site");
        if (creation.Initializer is not null) InitializeArray(creation.Initializer, target, new List<int>());
        return target;
    }

    private void InitializeArray(IArrayInitializerOperation initializer, string target, List<int> indices)
    {
        for (var index = 0; index < initializer.ElementValues.Length; index++)
        {
            var element = initializer.ElementValues[index];
            indices.Add(index);
            if (element is IArrayInitializerOperation nested) InitializeArray(nested, target, indices);
            else
            {
                var source = Evaluate(element, false);
                Add(InstructionKind.SetAggregateElement, element, source: source ?? "$null-element:" + element.Syntax.SpanStart,
                    owner: target, projection: "[" + string.Join(",", indices.Select(value => value.ToString(CultureInfo.InvariantCulture))) + "]",
                    description: "initialize represented array element");
                ForgetTemporary(source, element);
            }
            indices.RemoveAt(indices.Count - 1);
        }
    }

    private string? ReadArrayElement(IArrayElementReferenceOperation element, bool read)
    {
        var owner = SnapshotCallValue(Evaluate(element.ArrayReference, false), element, "array");
        foreach (var index in element.Indices) Evaluate(index);
        if (owner is not null) Add(InstructionKind.Read, element, source: owner);
        MarkMayThrow(element);
        if (!TracksOwnershipValue(element.Type)) return ReleaseReceiverSnapshot(owner, element, null);
        var target = Temporary(element);
        Add(InstructionKind.ReadAggregateElement, element, target: target, source: owner ?? "$opaque-array:" + element.Syntax.SpanStart,
            resourceId: "array-element:" + element.Syntax.SpanStart, flags: ResourceFlags(element.Type),
            projection: ArrayIndex(element), description: "read the canonical array element identity");
        if (read) Add(InstructionKind.Read, element, source: target);
        target = TrackProducedValueTask(element, target, preserveExisting: true)!;
        return ReleaseReceiverSnapshot(owner, element, target);
    }

    private string? AssignArrayElement(ISimpleAssignmentOperation assignment, IArrayElementReferenceOperation element)
    {
        var owner = SnapshotCallValue(Evaluate(element.ArrayReference, false), element, "array");
        foreach (var index in element.Indices) Evaluate(index);
        var source = Evaluate(assignment.Value, false);
        if (owner is not null) Add(InstructionKind.Write, element, source: owner);
        MarkMayThrow(element);
        Add(InstructionKind.SetAggregateElement, assignment, source: source ?? "$null-element:" + assignment.Syntax.SpanStart,
            owner: owner ?? "$opaque-array:" + element.Syntax.SpanStart, projection: ArrayIndex(element),
            description: "update the canonical array element identity");
        return ReleaseReceiverSnapshot(owner, assignment, source);
    }

    private static string ArrayIndex(IArrayElementReferenceOperation element) => "[" + string.Join(",", element.Indices.Select(index =>
        index.ConstantValue.HasValue ? System.Convert.ToString(index.ConstantValue.Value, CultureInfo.InvariantCulture) ?? "null" : "*")) + "]";

    private void CompleteComposition(IOperation operation, string? carrier)
    {
        if (carrier is not null)
            Add(InstructionKind.CompleteAsyncComposition, operation, source: carrier,
                description: "receive composed inputs only after successful completion");
    }
}
