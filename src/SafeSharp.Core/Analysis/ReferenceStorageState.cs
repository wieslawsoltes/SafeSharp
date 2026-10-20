using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace SafeSharp.Core;

/// <summary>
/// Finite, flattened aliases of storage locations, separate from aliases of resource values.
/// Binding a ref snapshots its target locations. Rebinding another ref cannot redirect that
/// snapshot. Conditional targets require weak writes; singleton targets support strong writes.
/// This original C# domain follows rustc's place/value distinction while retaining C# ref aliasing:
/// https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_borrowck/src/places_conflict.rs
/// </summary>
internal sealed class ReferenceStorageState : IEquatable<ReferenceStorageState>
{
    internal const string UnknownTarget = "$unknown-reference-storage";
    internal const int MaximumTargets = 256;
    internal const int MaximumBindings = 4096;
    internal const int MaximumLocationLength = 4096;
    private static readonly ImmutableHashSet<string> UnknownLocations = ImmutableHashSet.Create(StringComparer.Ordinal, UnknownTarget);
    private readonly Dictionary<string, ImmutableHashSet<string>> bindings = new(StringComparer.Ordinal);

    internal bool BudgetExceeded { get; private set; }

    internal bool TryGetTargets(string place, out ImmutableHashSet<string> targets)
    {
        if (bindings.Count == 0) { targets = null!; return false; }
        if (bindings.TryGetValue(place, out targets!)) return true;
        var separator = Math.Max(place.LastIndexOf('.'), place.LastIndexOf('['));
        while (separator > 0)
        {
            if (bindings.TryGetValue(place.Substring(0, separator), out var roots))
            {
                var suffix = place.Substring(separator);
                if (roots.Any(root => root.Length + suffix.Length > MaximumLocationLength))
                {
                    BudgetExceeded = true;
                    targets = UnknownLocations;
                }
                else targets = roots.Select(root => root == UnknownTarget ? root : root + suffix).ToImmutableHashSet(StringComparer.Ordinal);
                return true;
            }
            separator = Math.Max(place.LastIndexOf('.', separator - 1), place.LastIndexOf('[', separator - 1));
        }
        targets = null!;
        return false;
    }

    internal void Bind(string target, string source, bool merge = false)
    {
        var locations = TryGetTargets(source, out var referenced) ? referenced : ImmutableHashSet.Create(StringComparer.Ordinal, source);
        if (merge && bindings.TryGetValue(target, out var previous)) locations = previous.Union(locations);
        if (locations.Count > MaximumTargets || locations.Any(location => location.Length > MaximumLocationLength) ||
            !bindings.ContainsKey(target) && bindings.Count >= MaximumBindings)
        {
            BudgetExceeded = true;
            return;
        }
        bindings[target] = locations;
    }

    internal void Release(string target) => bindings.Remove(target);

    internal void CopyFrom(ReferenceStorageState other)
    {
        bindings.Clear();
        BudgetExceeded = other.BudgetExceeded;
        foreach (var pair in other.bindings) bindings.Add(pair.Key, pair.Value);
    }

    internal void JoinWith(ReferenceStorageState other)
    {
        BudgetExceeded |= other.BudgetExceeded;
        foreach (var key in bindings.Keys.Union(other.bindings.Keys, StringComparer.Ordinal).ToArray())
        {
            var left = bindings.TryGetValue(key, out var known) ? known : ImmutableHashSet.Create(StringComparer.Ordinal, UnknownTarget);
            var right = other.bindings.TryGetValue(key, out known) ? known : ImmutableHashSet.Create(StringComparer.Ordinal, UnknownTarget);
            var locations = left.Union(right);
            if (locations.Count > MaximumTargets || !bindings.ContainsKey(key) && bindings.Count >= MaximumBindings)
            {
                BudgetExceeded = true;
                return;
            }
            bindings[key] = locations;
        }
    }

    internal static IReadOnlyCollection<ReferenceFact> Read(ImmutableHashSet<string> targets,
        Func<string, IReadOnlyCollection<ReferenceFact>> read)
    {
        if (targets.Count == 1) return read(targets.Single());
        var values = new Dictionary<string, ReferenceFact>(StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var target in targets)
        foreach (var reference in read(target))
        {
            values[reference.Key] = values.TryGetValue(reference.Key, out var prior) ? prior.Join(reference) : reference;
            counts[reference.Key] = counts.TryGetValue(reference.Key, out var count) ? count + 1 : 1;
        }
        return values.Values.Select(reference => counts[reference.Key] == targets.Count ? reference : reference.WithAbsence(false)).ToArray();
    }

    internal ImmutableArray<ReferenceStorageSnapshot> Snapshot() => bindings.OrderBy(pair => pair.Key, StringComparer.Ordinal)
        .Select(pair => new ReferenceStorageSnapshot(pair.Key, pair.Value.OrderBy(target => target, StringComparer.Ordinal).ToImmutableArray())).ToImmutableArray();

    public bool Equals(ReferenceStorageState? other) => other is not null && BudgetExceeded == other.BudgetExceeded && bindings.Count == other.bindings.Count &&
        bindings.All(pair => other.bindings.TryGetValue(pair.Key, out var value) && pair.Value.SetEquals(value));
    public override bool Equals(object? obj) => obj is ReferenceStorageState other && Equals(other);
    public override int GetHashCode() => bindings.Count;
}
