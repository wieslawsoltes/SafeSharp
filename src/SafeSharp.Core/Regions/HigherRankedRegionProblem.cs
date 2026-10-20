using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace SafeSharp.Core.Regions;

/// <summary>A lexical lifetime binder. Its universe can name regions in itself and its ancestors.</summary>
public sealed class RegionBinder
{
    public RegionBinder(string name, string? parent = null)
    {
        Name = RegionProblem.RequireName(name, nameof(name));
        Parent = parent is null ? null : RegionProblem.RequireName(parent, nameof(parent));
    }

    public string Name { get; }
    public string? Parent { get; }
}

/// <summary>The quantification of a region in a higher-ranked constraint problem.</summary>
public enum RegionVariableKind
{
    /// <summary>An arbitrary caller-supplied region in the root universe.</summary>
    Universal,
    /// <summary>An arbitrary region introduced by entering a nested universal binder.</summary>
    Placeholder,
    /// <summary>A region whose value is inferred within its declared universe.</summary>
    Inference,
    /// <summary>The root-visible upper bound which outlives every region.</summary>
    Static,
}

/// <summary>A stable region identity with explicit quantification and universe.</summary>
public sealed class RegionVariable
{
    public RegionVariable(string name, string binder, RegionVariableKind kind)
    {
        Name = RegionProblem.RequireName(name, nameof(name));
        Binder = RegionProblem.RequireName(binder, nameof(binder));
        if (kind < RegionVariableKind.Universal || kind > RegionVariableKind.Static) throw new ArgumentOutOfRangeException(nameof(kind));
        Kind = kind;
    }

    public string Name { get; }
    public string Binder { get; }
    public RegionVariableKind Kind { get; }
}

/// <summary>An explicit opaque type's borrowed contents must outlive a region.</summary>
/// <remarks>
/// The adapter supplies the type identity and decomposes concrete type constructors. No assumption
/// that all CLR generic arguments contain Rust references is made by this domain.
/// </remarks>
public sealed class TypeOutlivesConstraint
{
    public TypeOutlivesConstraint(string type, string region, SourceSite? site = null, string? reason = null)
    {
        Type = RegionProblem.RequireName(type, nameof(type));
        Region = RegionProblem.RequireName(region, nameof(region));
        Site = site;
        Reason = reason ?? "Explicit type-outlives obligation.";
    }

    public string Type { get; }
    public string Region { get; }
    public SourceSite? Site { get; }
    public string Reason { get; }
}

/// <summary>An immutable, lexically scoped higher-ranked outlives and type-outlives problem.</summary>
/// <remarks>
/// Universe ancestry is indexed once using iterative traversal. Unlike rustc's monotonic universe
/// indices, this reusable public API also checks sibling visibility because its callers can build
/// arbitrary trees rather than relying on the compiler's scoped inference-context discipline.
/// https://rustc-dev-guide.rust-lang.org/borrow-check/region-inference/placeholders-and-universes.html
/// </remarks>
public sealed class HigherRankedRegionProblem
{
    private readonly Dictionary<string, int> binderIndices;
    private readonly int[] entered;
    private readonly int[] exited;
    private readonly int[] depths;
    private readonly int[][] ancestors;

    public HigherRankedRegionProblem(IEnumerable<RegionBinder> binders, IEnumerable<RegionVariable> regions,
        IEnumerable<RegionConstraint>? assumptions = null, IEnumerable<RegionConstraint>? requirements = null,
        IEnumerable<TypeOutlivesConstraint>? assumedTypeBounds = null,
        IEnumerable<TypeOutlivesConstraint>? requiredTypeBounds = null)
    {
        if (binders is null) throw new ArgumentNullException(nameof(binders));
        if (regions is null) throw new ArgumentNullException(nameof(regions));
        Binders = binders.OrderBy(binder => binder?.Name, StringComparer.Ordinal).ToImmutableArray();
        if (Binders.Any(binder => binder is null)) throw new ArgumentException("A binder cannot be null.", nameof(binders));
        if (Binders.Select(binder => binder.Name).Distinct(StringComparer.Ordinal).Count() != Binders.Length)
            throw new ArgumentException("Binder names must be unique.", nameof(binders));
        var roots = Binders.Where(binder => binder.Parent is null).ToArray();
        if (roots.Length != 1) throw new ArgumentException("Exactly one root binder is required.", nameof(binders));
        RootBinder = roots[0].Name;
        binderIndices = Binders.Select((binder, index) => (binder.Name, index)).ToDictionary(item => item.Name, item => item.index, StringComparer.Ordinal);
        var children = Enumerable.Range(0, Binders.Length).Select(_ => new List<int>()).ToArray();
        for (var index = 0; index < Binders.Length; index++)
        {
            if (Binders[index].Parent is not { } parent) continue;
            if (!binderIndices.TryGetValue(parent, out var parentIndex))
                throw new ArgumentException("A parent binder must be declared: " + parent, nameof(binders));
            children[parentIndex].Add(index);
        }
        entered = new int[Binders.Length];
        exited = new int[Binders.Length];
        depths = new int[Binders.Length];
        var traversal = new Stack<(int Index, bool Exit)>();
        traversal.Push((binderIndices[RootBinder], false));
        var clock = 0;
        var visited = 0;
        while (traversal.Count != 0)
        {
            var item = traversal.Pop();
            if (item.Exit) { exited[item.Index] = clock++; continue; }
            entered[item.Index] = clock++;
            visited++;
            traversal.Push((item.Index, true));
            for (var child = children[item.Index].Count - 1; child >= 0; child--)
            {
                depths[children[item.Index][child]] = depths[item.Index] + 1;
                traversal.Push((children[item.Index][child], false));
            }
        }
        if (visited != Binders.Length) throw new ArgumentException("Binder ancestry cannot contain a cycle.", nameof(binders));
        var levels = 1;
        while ((1L << levels) <= Binders.Length) levels++;
        ancestors = new int[levels][];
        ancestors[0] = Binders.Select(binder => binder.Parent is null ? binderIndices[binder.Name] : binderIndices[binder.Parent]).ToArray();
        for (var level = 1; level < levels; level++)
        {
            ancestors[level] = new int[Binders.Length];
            for (var index = 0; index < Binders.Length; index++) ancestors[level][index] = ancestors[level - 1][ancestors[level - 1][index]];
        }

        var declared = regions.OrderBy(region => region?.Name, StringComparer.Ordinal).ToList();
        if (declared.Any(region => region is null)) throw new ArgumentException("A region cannot be null.", nameof(regions));
        if (declared.Select(region => region.Name).Distinct(StringComparer.Ordinal).Count() != declared.Count)
            throw new ArgumentException("Region identities must be unique across binders.", nameof(regions));
        foreach (var region in declared)
        {
            if (!binderIndices.ContainsKey(region.Binder)) throw new ArgumentException("A region's binder must be declared: " + region.Binder, nameof(regions));
            if ((region.Kind == RegionVariableKind.Universal || region.Kind == RegionVariableKind.Static) && region.Binder != RootBinder)
                throw new ArgumentException("Free universal and static regions must belong to the root binder.", nameof(regions));
        }
        var statics = declared.Where(region => region.Kind == RegionVariableKind.Static).ToArray();
        if (statics.Length > 1) throw new ArgumentException("At most one static region can be declared.", nameof(regions));
        if (statics.Length == 0)
        {
            var name = "$static";
            var known = new HashSet<string>(declared.Select(region => region.Name), StringComparer.Ordinal);
            while (known.Contains(name)) name += "$";
            StaticRegion = name;
            declared.Add(new RegionVariable(name, RootBinder, RegionVariableKind.Static));
        }
        else StaticRegion = statics[0].Name;
        Regions = declared.OrderBy(region => region.Name, StringComparer.Ordinal).ToImmutableArray();
        RigidRegions = Regions.Where(region => region.Kind != RegionVariableKind.Inference).ToImmutableArray();
        var allNames = Regions.Select(region => region.Name).ToArray();
        Assumptions = new RegionProblem(RigidRegions.Select(region => region.Name), assumptions).Constraints;
        Requirements = new RegionProblem(allNames, requirements).Constraints;
        AssumedTypeBounds = Bounds(assumedTypeBounds, new HashSet<string>(RigidRegions.Select(region => region.Name), StringComparer.Ordinal), nameof(assumedTypeBounds));
        RequiredTypeBounds = Bounds(requiredTypeBounds, new HashSet<string>(allNames, StringComparer.Ordinal), nameof(requiredTypeBounds));
    }

    public string RootBinder { get; }
    public string StaticRegion { get; }
    public ImmutableArray<RegionBinder> Binders { get; }
    public ImmutableArray<RegionVariable> Regions { get; }
    public ImmutableArray<RegionVariable> RigidRegions { get; }
    public ImmutableArray<RegionConstraint> Assumptions { get; }
    public ImmutableArray<RegionConstraint> Requirements { get; }
    public ImmutableArray<TypeOutlivesConstraint> AssumedTypeBounds { get; }
    public ImmutableArray<TypeOutlivesConstraint> RequiredTypeBounds { get; }

    /// <summary>Whether a region inferred in <paramref name="observer"/> can name a placeholder in <paramref name="owner"/>.</summary>
    public bool CanName(string observer, string owner)
    {
        RegionProblem.RequireName(observer, nameof(observer));
        RegionProblem.RequireName(owner, nameof(owner));
        if (!binderIndices.TryGetValue(observer, out var observing)) throw new ArgumentException("Unknown binder.", nameof(observer));
        if (!binderIndices.TryGetValue(owner, out var named)) throw new ArgumentException("Unknown binder.", nameof(owner));
        return entered[named] <= entered[observing] && exited[observing] <= exited[named];
    }

    // Binary lifting intersects visible-name sets in O(log B), including incomparable siblings.
    internal string CommonBinder(string first, string second)
    {
        var left = binderIndices[first];
        var right = binderIndices[second];
        if (depths[left] < depths[right]) { var swap = left; left = right; right = swap; }
        var difference = depths[left] - depths[right];
        for (var level = 0; difference != 0; level++, difference >>= 1)
            if ((difference & 1) != 0) left = ancestors[level][left];
        if (left == right) return Binders[left].Name;
        for (var level = ancestors.Length - 1; level >= 0; level--)
            if (ancestors[level][left] != ancestors[level][right])
            { left = ancestors[level][left]; right = ancestors[level][right]; }
        return Binders[ancestors[0][left]].Name;
    }

    private static ImmutableArray<TypeOutlivesConstraint> Bounds(IEnumerable<TypeOutlivesConstraint>? bounds, HashSet<string> regions, string parameter)
    {
        var result = (bounds ?? Array.Empty<TypeOutlivesConstraint>()).ToArray();
        foreach (var bound in result)
            if (bound is null || !regions.Contains(bound.Region))
                throw new ArgumentException("A type bound must name a declared region with the required quantification.", parameter);
        return result.OrderBy(bound => bound.Type, StringComparer.Ordinal).ThenBy(bound => bound.Region, StringComparer.Ordinal)
            .ThenBy(bound => bound.Site?.Path, StringComparer.Ordinal).ThenBy(bound => bound.Site?.Start ?? -1)
            .ThenBy(bound => bound.Reason, StringComparer.Ordinal).ToImmutableArray();
    }
}
