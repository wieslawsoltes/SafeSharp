using System.Collections.Immutable;

namespace SafeSharp.Core;

/// <summary>A ref location and every storage location it may designate in the current state.</summary>
public sealed class ReferenceStorageSnapshot
{
    internal ReferenceStorageSnapshot(string reference, ImmutableArray<string> targets)
    { Reference = reference; Targets = targets; }
    public string Reference { get; }
    public ImmutableArray<string> Targets { get; }
}
