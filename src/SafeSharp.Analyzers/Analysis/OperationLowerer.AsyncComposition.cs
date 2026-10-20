using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using SafeSharp.Core;

namespace SafeSharp.Analyzers.Analysis;

internal sealed partial class OperationLowerer
{
    // Arrays are represented only when an element has an ownership or consumption identity.
    // Scalar arrays remain outside this domain and keep the normal lightweight lowering path.
    private static bool TracksOwnershipValue(ITypeSymbol? type) => SymbolFacts.IsTracked(type) || IsValueTaskType(type) ||
        type is IArrayTypeSymbol array && TracksOwnershipValue(array.ElementType);

    private void InvalidateCallAggregates(IOperation operation, IMethodSymbol? method, CallArguments arguments,
        string? receiver = null, ITypeSymbol? receiverType = null)
    {
        if (method is null || _summaries.AsyncResults.Classify(method) != AsyncResultOriginOperation.None ||
            _summaries.AsyncResults.ClassifyComposition(method) != AsyncCompositionKind.None) return;
        foreach (var argument in arguments.Values)
        {
            if (argument.Key < 0 || argument.Key >= method.Parameters.Length) continue;
            var parameter = method.Parameters[argument.Key];
            if (parameter.RefKind == RefKind.Out || !MayRepresentAggregate(parameter.Type) ||
                SymbolFacts.HasAttribute(parameter, "Borrowed") || SymbolFacts.HasAttribute(parameter, "Consumes") || SymbolFacts.HasAttribute(parameter, "Owned")) continue;
            Add(InstructionKind.InvalidateAggregateContents, operation, source: argument.Value,
                description: "retain possible original elements across an unmodeled call mutation");
        }
        if (receiver is not null && MayRepresentAggregate(receiverType) && !IsArrayQuery(method, receiverType))
            Add(InstructionKind.InvalidateAggregateContents, operation, source: receiver,
                description: "retain possible original elements across an unmodeled receiver mutation");
    }

    private static bool MayRepresentAggregate(ITypeSymbol? type) => type is IArrayTypeSymbol ||
        type?.SpecialType is SpecialType.System_Object or SpecialType.System_Array || type?.TypeKind is TypeKind.Interface or TypeKind.TypeParameter ||
        type is INamedTypeSymbol named && (SymbolFacts.IsMemoryView(named) ||
            OwnedAwaitablePayload(named) is { } payload && MayRepresentAggregate(payload));

    private static bool IsArrayQuery(IMethodSymbol method, ITypeSymbol? receiverType)
    {
        // Arrays have no user-defined instance implementation. Only these framework queries
        // and copies have an explicit non-mutation model; SetValue and Initialize do not.
        var type = method.ContainingType;
        if (type.ContainingAssembly.Identity.Name is not ("System.Private.CoreLib" or "mscorlib" or "System.Runtime" or "netstandard")) return false;
        return type.SpecialType == SpecialType.System_Array && method.Name is "GetValue" or "GetLength" or "GetLongLength" or
            "GetLowerBound" or "GetUpperBound" or "get_Length" or "get_LongLength" or "get_Rank" or "get_IsReadOnly" or
            "get_IsFixedSize" or "get_IsSynchronized" or "get_SyncRoot" or "GetEnumerator" or "Clone" or "CopyTo" ||
            receiverType is IArrayTypeSymbol && type.SpecialType == SpecialType.System_Object;
    }

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
        // A conditional ref's CFG capture evaluates a location. Retain its storage as well as
        // its current value; a by-value capture takes the independent snapshot below instead.
        if (IsArrayElementAddressSyntax(element)) return AddressArrayElement(element, read: read);
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

    private string? AddressArrayElement(IArrayElementReferenceOperation element, bool writeReceiver = false, bool read = false)
    {
        var owner = SnapshotCallValue(Evaluate(element.ArrayReference, false), element, "array-address");
        foreach (var index in element.Indices) Evaluate(index);
        if (owner is not null) Add(writeReceiver ? InstructionKind.Write : InstructionKind.Read, element, source: owner);
        MarkMayThrow(element);
        if (!TracksOwnershipValue(element.Type))
            return ReleaseReceiverSnapshot(owner, element, owner is null ? null : owner + ArrayIndex(element));
        var target = "$element-reference:" + element.Syntax.SpanStart;
        Add(InstructionKind.BindAggregateElementReference, element, target: target, source: owner ?? UnknownReferenceStorage,
            projection: ArrayIndex(element), description: "capture canonical array element storage independently of the caller's array variable");
        if (read) Add(InstructionKind.Read, element, source: target);
        return ReleaseReceiverSnapshot(owner, element, target);
    }

    private static bool IsArrayElementAddressSyntax(IArrayElementReferenceOperation element)
    {
        var syntax = element.Syntax;
        while (syntax.Parent is ParenthesizedExpressionSyntax parenthesized) syntax = parenthesized;
        return syntax.Parent is RefExpressionSyntax || syntax.Parent is ArgumentSyntax argument && argument.RefKindKeyword.RawKind != 0;
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
