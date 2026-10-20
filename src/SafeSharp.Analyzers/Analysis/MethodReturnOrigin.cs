using System.Collections.Immutable;

namespace SafeSharp.Analyzers.Analysis;

/// <summary>
/// A terminal provenance atom under a bounded number of asynchronous carrier layers. A parameter
/// atom denotes the argument value itself, even when that value is another carrier. Thus wrapping
/// an existing Task preserves its identity, and a null carrier differs from a carrier of null.
/// </summary>
internal readonly struct MethodReturnOrigin : IEquatable<MethodReturnOrigin>
{
    internal MethodReturnOrigin(string source, int payloadDepth = 0) { Source = source; PayloadDepth = payloadDepth; }
    internal string Source { get; }
    internal int PayloadDepth { get; }
    public bool Equals(MethodReturnOrigin other) => Source == other.Source && PayloadDepth == other.PayloadDepth;
    public override bool Equals(object? obj) => obj is MethodReturnOrigin other && Equals(other);
    public override int GetHashCode() => unchecked(StringComparer.Ordinal.GetHashCode(Source) * 397 ^ PayloadDepth);
}

/// <summary>
/// Storage returned by a source-proven ref/ref-readonly method. These are locations, not snapshots
/// of parameter values. Callers map argument targets captured at each argument's evaluation point.
/// External storage cannot establish local ownership and unknown storage invalidates a write proof.
/// </summary>
internal sealed class ReferenceReturnSummary
{
    internal ReferenceReturnSummary(ImmutableArray<int> parameterOrdinals, bool includesExternalStorage, bool includesUnknownStorage)
    { ParameterOrdinals = parameterOrdinals; IncludesExternalStorage = includesExternalStorage; IncludesUnknownStorage = includesUnknownStorage; }
    internal ImmutableArray<int> ParameterOrdinals { get; }
    internal bool IncludesExternalStorage { get; }
    internal bool IncludesUnknownStorage { get; }
}
