using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FlowAnalysis;

namespace SafeSharp.Analyzers.Semantics;

internal sealed class CleanupResource : IEquatable<CleanupResource>
{
    public CleanupResource(string id, ISymbol slot, bool initial, bool completion = false) { Id = id; Slot = slot; Initial = initial; Completion = completion; }
    public string Id { get; }
    public ISymbol Slot { get; }
    public bool Initial { get; }
    public bool Completion { get; }
    public bool Equals(CleanupResource? other) => other is not null && Id == other.Id;
    public override bool Equals(object? obj) => obj is CleanupResource other && Equals(other);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Id);
}

internal enum CleanupNullness { MaybeNull, Null, NotNull }

/// <summary>Resource identities are may-alias facts; completion effects are must facts.</summary>
internal sealed class CleanupValue
{
    public static readonly CleanupValue Unknown = new(unknown: true);
    public static readonly CleanupValue Null = new(nullness: CleanupNullness.Null);
    public CleanupValue(IEnumerable<CleanupResource>? resources = null, IEnumerable<CleanupResource>? completion = null,
        CleanupNullness nullness = CleanupNullness.MaybeNull, bool unknown = false, bool nullTracksResource = false, bool? boolean = null)
    {
        Resources = resources?.ToImmutableHashSet() ?? ImmutableHashSet<CleanupResource>.Empty;
        Completion = completion?.ToImmutableHashSet() ?? ImmutableHashSet<CleanupResource>.Empty;
        Nullness = nullness; IsUnknown = unknown; NullTracksResource = nullTracksResource; Boolean = boolean;
    }
    public ImmutableHashSet<CleanupResource> Resources { get; }
    public ImmutableHashSet<CleanupResource> Completion { get; }
    public CleanupNullness Nullness { get; }
    public bool IsUnknown { get; }
    public bool NullTracksResource { get; }
    public bool? Boolean { get; }
    public bool IsExactResource => !IsUnknown && Resources.Count == 1;
    public CleanupValue Refine(CleanupNullness nullness) => new(Resources, Completion, nullness, IsUnknown, NullTracksResource, Boolean);
    public static CleanupValue Join(CleanupValue left, CleanupValue right) => new(left.Resources.Union(right.Resources),
        left.Completion.Intersect(right.Completion), left.Nullness == right.Nullness ? left.Nullness : CleanupNullness.MaybeNull,
        left.IsUnknown || right.IsUnknown, left.NullTracksResource && right.NullTracksResource && left.Resources.SetEquals(right.Resources),
        left.Boolean == right.Boolean ? left.Boolean : null);
    public bool Equivalent(CleanupValue other) => Nullness == other.Nullness && IsUnknown == other.IsUnknown &&
        NullTracksResource == other.NullTracksResource && Boolean == other.Boolean && Resources.SetEquals(other.Resources) && Completion.SetEquals(other.Completion);
}

internal sealed class AggregateCleanupState
{
    public Dictionary<ISymbol, CleanupValue> Symbols { get; } = new(SymbolEqualityComparer.Default);
    public Dictionary<CaptureId, CleanupValue> Captures { get; } = new();
    public HashSet<CleanupResource> Outstanding { get; } = new();
    public HashSet<CleanupResource> Lost { get; } = new();
    public HashSet<ISymbol> WrittenMembers { get; } = new(SymbolEqualityComparer.Default);
    public bool Reachable { get; set; } = true;
    public AggregateCleanupState Clone()
    {
        var copy = new AggregateCleanupState { Reachable = Reachable };
        foreach (var pair in Symbols) copy.Symbols.Add(pair.Key, pair.Value);
        foreach (var pair in Captures) copy.Captures.Add(pair.Key, pair.Value);
        copy.Outstanding.UnionWith(Outstanding); copy.Lost.UnionWith(Lost); copy.WrittenMembers.UnionWith(WrittenMembers);
        return copy;
    }
    public CleanupValue Get(ISymbol symbol) => Symbols.TryGetValue(symbol, out var value) ? value : CleanupValue.Unknown;
    public CleanupValue Capture(CaptureId id) => Captures.TryGetValue(id, out var value) ? value : CleanupValue.Unknown;
    public void Release(CleanupValue value)
    {
        if (value.IsExactResource) Complete(value.Resources);
    }
    public void Complete(IEnumerable<CleanupResource> resources)
    {
        foreach (var resource in resources) if (!Lost.Contains(resource)) Outstanding.Remove(resource);
    }
    public void Introduce(CleanupResource resource)
    {
        // A loop that overwrites an outstanding instance at the same allocation site
        // has lost an earlier obligation; disposing the current instance cannot erase it.
        if (Outstanding.Contains(resource)) Lost.Add(resource);
        Outstanding.Add(resource);
    }
    public bool MergeFrom(AggregateCleanupState incoming)
    {
        if (!incoming.Reachable) return false;
        var changed = false;
        foreach (var resource in incoming.Outstanding) changed |= Outstanding.Add(resource);
        foreach (var resource in incoming.Lost) changed |= Lost.Add(resource);
        foreach (var member in incoming.WrittenMembers) changed |= WrittenMembers.Add(member);
        foreach (var key in Symbols.Keys.Concat(incoming.Symbols.Keys).Distinct(SymbolEqualityComparer.Default).ToArray())
        {
            var old = Get(key); var next = CleanupValue.Join(old, incoming.Get(key));
            if (!old.Equivalent(next)) { Symbols[key] = next; changed = true; }
        }
        foreach (var key in Captures.Keys.Concat(incoming.Captures.Keys).Distinct().ToArray())
        {
            var old = Capture(key); var next = CleanupValue.Join(old, incoming.Capture(key));
            if (!old.Equivalent(next)) { Captures[key] = next; changed = true; }
        }
        return changed;
    }
}
