using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using SafeSharp.Analyzers.Semantics;
using SafeSharp.Core;

namespace SafeSharp.Analyzers.Analysis;

internal sealed partial class OperationLowerer
{
    private string? ReceiveCustomAwaitedResult(IAwaitOperation operation, string? receiver)
    {
        var pattern = _summaries.AsyncResults.AwaitPattern(operation.Syntax);
        if (pattern.GetResult is null) return ReceiveAwaitedResult(operation, receiver, false);
        var summary = _summaries.Get(pattern.GetResult, _cancellationToken);
        // Awaitable patterns use the actual compiler-selected GetResult symbol. A namesake type,
        // GetAwaiter allocation, or custom async builder does not imply ownership of its result.
        if (summary.ReturnsOwned && (SymbolFacts.IsTracked(operation.Type) || OwnedAwaitablePayload(operation.Type) is not null))
        {
            var nested = ReconstructAsyncReturn(operation, pattern.GetResult, summary, null, new CallArguments());
            if (nested is not null) return nested;
            var target = Temporary(operation);
            var payload = OwnedAwaitablePayload(operation.Type);
            Add(payload is null ? InstructionKind.Allocate : InstructionKind.BeginAsyncFactory, operation, target: target,
                resourceId: "custom-await-result:" + operation.Syntax.SpanStart, flags: ResourceFlags(payload ?? operation.Type),
                description: "receive result promised by the compiler-selected custom GetResult ownership contract");
            return TrackProducedValueTask(operation, target);
        }
        if (receiver is not null && pattern.GetAwaiter is { } getAwaiter && summary.HasBorrowedReturn &&
            summary.BorrowedSources.Length == 1 && summary.BorrowedSources[0] == "this")
        {
            var awaiter = _summaries.Get(getAwaiter, _cancellationToken);
            var receiverBound = awaiter.HasBorrowedReturn && awaiter.BorrowedSources.Length == 1 &&
                (awaiter.BorrowedSources[0] == "this" || getAwaiter.IsExtensionMethod && getAwaiter.Parameters.Length > 0 &&
                    awaiter.BorrowedSources[0] == getAwaiter.Parameters[0].Name);
            if (receiverBound)
                return ReturnedBorrow(operation, pattern.GetResult, summary, receiver, new CallArguments());
        }
        return ReceiveAwaitedResult(operation, receiver, false);
    }

    /// <summary>Reconstructs source-proven carrier layers without flattening their terminal provenance.</summary>
    private string? ReconstructAsyncReturn(IOperation operation, IMethodSymbol method, MethodSummary summary, string? receiver, CallArguments arguments)
    {
        if (OwnedAwaitablePayload(operation.Type) is null || summary.ReturnOrigins.IsEmpty ||
            summary.ReturnOrigins.Any(origin => origin.Source == MethodOriginAnalysis.Unknown)) return null;

        IParameterSymbol? Parameter(MethodReturnOrigin origin) => method.Parameters.FirstOrDefault(parameter =>
            MethodOriginAnalysis.Parameter(parameter.Ordinal) == origin.Source);

        // Preserve the established one-layer borrowed/owned contract path. This path handles the
        // richer shapes that a boolean ReturnsAsyncPayload flag cannot describe: nested carriers
        // and a carrier parameter returned or wrapped without changing its payload identity.
        if (!summary.ReturnOrigins.Any(origin => origin.PayloadDepth > 1 ||
            Parameter(origin) is { } parameter && OwnedAwaitablePayload(parameter.Type) is not null)) return null;

        var alternatives = new List<string>();
        foreach (var origin in summary.ReturnOrigins)
        {
            var terminalType = operation.Type;
            for (var depth = 0; depth < origin.PayloadDepth; depth++) terminalType = OwnedAwaitablePayload(terminalType);
            if (terminalType is null) return null;
            if (origin.Source == MethodOriginAnalysis.Fresh && origin.PayloadDepth == 0) return null;
            if (origin.Source is not (MethodOriginAnalysis.Fresh or MethodOriginAnalysis.Null) &&
                !(origin.Source == MethodOriginAnalysis.Receiver && receiver is not null) &&
                !(Parameter(origin) is { } parameter && arguments.Values.ContainsKey(parameter.Ordinal))) return null;
        }

        var ordinal = 0;
        foreach (var origin in summary.ReturnOrigins)
        {
            var prefix = "$async-summary:" + operation.Syntax.SpanStart + ":" + ordinal++;
            var conditional = summary.ReturnOrigins.Length > 1 ? InstructionFlags.Conditional : InstructionFlags.None;
            var terminalType = operation.Type;
            for (var depth = 0; depth < origin.PayloadDepth; depth++) terminalType = OwnedAwaitablePayload(terminalType);
            string value;
            if (origin.Source == MethodOriginAnalysis.Fresh && origin.PayloadDepth > 0)
            {
                value = prefix;
                Add(InstructionKind.BeginAsyncFactory, operation, target: value, resourceId: "nested-factory:" + prefix,
                    flags: ResourceFlags(terminalType) | conditional, asyncResultDepth: origin.PayloadDepth,
                    description: "defer " + origin.PayloadDepth + " proven asynchronous layers before the owned terminal result");
                alternatives.Add(value);
                continue;
            }
            if (origin.Source == MethodOriginAnalysis.Null)
            {
                value = prefix + ":null";
                Add(InstructionKind.Forget, operation, target: value, description: "retain null terminal provenance in asynchronous summary");
            }
            else
            {
                var parameter = Parameter(origin);
                var source = origin.Source == MethodOriginAnalysis.Receiver ? receiver : parameter is not null &&
                    arguments.Values.TryGetValue(parameter.Ordinal, out var argument) ? argument : null;
                if (source is null) return null;
                value = prefix + ":origin";
                var sourceIsCarrier = parameter is not null && OwnedAwaitablePayload(parameter.Type) is not null;
                Add(sourceIsCarrier ? InstructionKind.Alias : summary.MutableBorrow ? InstructionKind.BorrowExclusive : InstructionKind.BorrowShared,
                    operation, target: value, source: source, flags: conditional,
                    description: sourceIsCarrier ? "preserve returned carrier argument identity" : "retain nested asynchronous terminal borrow");
            }
            for (var layer = 0; layer < origin.PayloadDepth; layer++)
            {
                var wrapper = prefix + ":layer:" + layer;
                Add(InstructionKind.BindAsyncResult, operation, target: wrapper, source: value,
                    resourceId: "nested-bound:" + wrapper, flags: InstructionFlags.DeferredAsyncResult | conditional,
                    description: "retain promised asynchronous carrier layer without claiming successful completion");
                Add(InstructionKind.ReleaseLoan, operation, target: value, description: "retain nested result only through its wrapper");
                value = wrapper;
            }
            alternatives.Add(value);
        }
        if (alternatives.Count == 0) return null;
        var result = Temporary(operation);
        Add(InstructionKind.Alias, operation, target: result, source: alternatives[0]);
        foreach (var alternative in alternatives.Skip(1)) Add(InstructionKind.MergeAlias, operation, target: result, source: alternative);
        foreach (var alternative in alternatives) Add(InstructionKind.ReleaseLoan, operation, target: alternative);
        return result;
    }
}
