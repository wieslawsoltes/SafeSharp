using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;
using SafeSharp.Core;

namespace SafeSharp.Analyzers.Analysis;

internal sealed partial class OperationLowerer
{
    private const string UnknownReferenceStorage = "$unknown-reference-storage";
    private static string ReferenceCapture(CaptureId capture) => "$reference-capture:" + capture.GetHashCode();

    private string? DeclareReference(IVariableDeclaratorOperation declaration)
    {
        var target = Place(declaration.Symbol);
        var source = declaration.Initializer is { } initializer ? Address(initializer.Value) : null;
        Add(InstructionKind.BindReference, declaration, target: target, source: source ?? UnknownReferenceStorage,
            description: "bind reference to storage, preserving its current target locations");
        return target;
    }

    private string? RebindReference(ISimpleAssignmentOperation assignment)
    {
        var target = Address(assignment.Target);
        var source = Address(assignment.Value);
        if (target is not null)
            Add(InstructionKind.BindReference, assignment, target: target, source: source ?? UnknownReferenceStorage,
                description: "rebind reference without replacing the value in its former storage");
        return target;
    }

    private void CaptureReferenceStorage(IFlowCaptureOperation capture, string? evaluated)
    {
        var value = SymbolFacts.Unwrap(capture.Value);
        string? source = value switch
        {
            ILocalReferenceOperation local => Place(local.Local),
            IParameterReferenceOperation parameter => Place(parameter.Parameter),
            IFlowCaptureReferenceOperation previous => ReferenceCapture(previous.Id),
            IDeclarationExpressionOperation declaration => Address(declaration.Expression),
            IFieldReferenceOperation or IArrayElementReferenceOperation => evaluated,
            IPropertyReferenceOperation property when property.Property.ReturnsByRef || property.Property.ReturnsByRefReadonly => evaluated,
            IInvocationOperation invocation when invocation.TargetMethod.ReturnsByRef || invocation.TargetMethod.ReturnsByRefReadonly => evaluated,
            ISimpleAssignmentOperation { IsRef: true } => evaluated,
            _ => null
        };
        if (source is null) Add(InstructionKind.ReleaseReference, capture, target: ReferenceCapture(capture.Id));
        else Add(InstructionKind.BindReference, capture, target: ReferenceCapture(capture.Id), source: source,
            description: "snapshot a captured storage target independently of its evaluated value");
    }

    private string? EvaluateReferenceArgument(IArgumentOperation argument, CallArguments arguments)
    {
        if (argument.Parameter?.RefKind is null or RefKind.None) return Evaluate(argument.Value, false);
        var source = Address(argument.Value);
        if (source is null) return null;
        var snapshot = "$reference-argument:" + argument.Syntax.SpanStart + ":" + argument.Parameter.Ordinal;
        Add(InstructionKind.BindReference, argument, target: snapshot, source: source,
            description: "snapshot argument storage before later argument expressions execute");
        arguments.ReferenceSnapshots.Add((snapshot, argument));
        return snapshot;
    }

    private string? ReturnedReference(IOperation operation, IMethodSymbol method, MethodSummary summary, CallArguments arguments)
    {
        if (!method.ReturnsByRef && !method.ReturnsByRefReadonly) return null;
        var locations = new List<string>();
        if (summary.ReferenceReturn is { } returned)
        {
            foreach (var ordinal in returned.ParameterOrdinals)
                if (arguments.Values.TryGetValue(ordinal, out var source)) locations.Add(source);
            if (returned.IncludesExternalStorage || returned.IncludesUnknownStorage) locations.Add(UnknownReferenceStorage);
        }
        else
        {
            // An opaque ref-return can designate any supplied reference argument. Retaining all
            // of them prevents an unknown identity from bypassing writes to a caller's local.
            foreach (var parameter in method.Parameters.Where(parameter => parameter.RefKind != RefKind.None))
                if (arguments.Values.TryGetValue(parameter.Ordinal, out var source)) locations.Add(source);
            locations.Add(UnknownReferenceStorage);
        }
        if (locations.Count == 0) locations.Add(UnknownReferenceStorage);
        var target = "$reference-return:" + operation.Syntax.SpanStart + ":" + operation.Kind;
        Add(InstructionKind.BindReference, operation, target: target, source: locations[0], description: "receive source-proven returned storage");
        foreach (var source in locations.Skip(1))
            Add(InstructionKind.MergeReference, operation, target: target, source: source, description: "retain every conditional returned storage target");
        return target;
    }

    private void EndReferenceArguments(CallArguments arguments)
    {
        foreach (var snapshot in arguments.ReferenceSnapshots)
            Add(InstructionKind.ReleaseReference, snapshot.Site, target: snapshot.Place,
                description: "end the argument's storage snapshot without overwriting its target");
    }

    private static bool TryReferenceOutputSource(string origin, Dictionary<int, string> arguments, out string source)
    {
        source = UnknownReferenceStorage;
        if (origin == MethodOriginAnalysis.Null) return true;
        return origin.StartsWith("$parameter:", StringComparison.Ordinal) && int.TryParse(origin.Substring(11), out var ordinal) &&
            arguments.TryGetValue(ordinal, out source!);
    }

    private static bool HasKnownReferenceOutput(MethodSummary? summary, int ordinal, Dictionary<int, string> arguments) =>
        summary?.ReferenceOutputs.TryGetValue(ordinal, out var origins) == true && !origins.IsEmpty &&
        origins.All(origin => TryReferenceOutputSource(origin, arguments, out _));

    private void ApplyKnownReferenceOutputs(System.Collections.Immutable.ImmutableArray<IArgumentOperation> arguments,
        Dictionary<int, string> values, MethodSummary? summary)
    {
        if (summary is null) return;
        var pending = new List<(IArgumentOperation Argument, string Target, string Snapshot)>();
        foreach (var argument in arguments)
        {
            if (argument.Parameter is not { RefKind: RefKind.Ref } parameter || summary.PreservedRefParameters.Contains(parameter.Ordinal) ||
                !values.TryGetValue(parameter.Ordinal, out var target) || !HasKnownReferenceOutput(summary, parameter.Ordinal, values)) continue;
            var snapshot = "$t:reference-output:" + argument.Syntax.SpanStart + ":" + parameter.Ordinal;
            var sources = summary.ReferenceOutputs[parameter.Ordinal].OrderBy(origin => origin, StringComparer.Ordinal)
                .Select(origin => { TryReferenceOutputSource(origin, values, out var source); return source; }).Distinct(StringComparer.Ordinal).ToArray();
            Add(InstructionKind.Alias, argument, target: snapshot, source: sources[0],
                description: "capture the proven normal ref output before installing any caller writes");
            foreach (var source in sources.Skip(1)) Add(InstructionKind.MergeAlias, argument, target: snapshot, source: source);
            pending.Add((argument, target, snapshot));
        }
        foreach (var output in pending)
            Add(InstructionKind.Alias, output.Argument, target: output.Target, source: output.Snapshot,
                description: "replace caller ref storage from source-proven normal output provenance");
        foreach (var argument in arguments)
        {
            if (argument.Parameter is not { RefKind: RefKind.Ref } parameter || !values.TryGetValue(parameter.Ordinal, out var target) ||
                !summary.OwnedOutputParameters.TryGetValue(parameter.Ordinal, out var nullable)) continue;
            Add(InstructionKind.Allocate, argument, target: target, resourceId: "ref-owned:" + argument.Syntax.SpanStart,
                flags: ResourceFlags(parameter.Type) | (nullable ? InstructionFlags.MayBeNull : InstructionFlags.None),
                description: "receive a proven fresh owner through the single ref output");
        }
        foreach (var output in pending) ReleaseCallSnapshot(output.Snapshot, output.Argument);
    }

    private void ApplyUnknownReferenceOutputs(System.Collections.Immutable.ImmutableArray<IArgumentOperation> arguments,
        Dictionary<int, string> values, MethodSummary? summary)
    {
        foreach (var argument in arguments)
        {
            if (argument.Parameter is not { RefKind: RefKind.Ref } parameter ||
                summary?.PreservedRefParameters.Contains(parameter.Ordinal) == true ||
                summary?.OwnedOutputParameters.ContainsKey(parameter.Ordinal) == true ||
                HasKnownReferenceOutput(summary, parameter.Ordinal, values) || !values.TryGetValue(parameter.Ordinal, out var target)) continue;
            // The call may retain or replace this value. A weak unknown write preserves aliases
            // needed to report disposal/move violations without pretending the cell is unchanged.
            Add(InstructionKind.Forget, argument, target: target, flags: InstructionFlags.Conditional,
                description: "retain both unchanged and opaque replacement outcomes for writable ref storage");
        }
    }

}
