using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace SafeSharp.Core.Regions;

/// <summary>A caller-supplied outlives edge, retaining its original source evidence.</summary>
public sealed class RegionConstraint
{
    public RegionConstraint(string longer, string shorter, SourceSite? site = null, string? reason = null)
    {
        Longer = RegionProblem.RequireName(longer, nameof(longer));
        Shorter = RegionProblem.RequireName(shorter, nameof(shorter));
        Site = site;
        Reason = reason ?? "Declared outlives constraint.";
    }

    public string Longer { get; }
    public string Shorter { get; }
    public SourceSite? Site { get; }
    public string Reason { get; }
}

/// <summary>An immutable, explicitly declared set of named regions and outlives constraints.</summary>
public sealed class RegionProblem
{
    public RegionProblem(IEnumerable<string> regions, IEnumerable<RegionConstraint>? constraints = null)
    {
        if (regions is null) throw new ArgumentNullException(nameof(regions));
        Regions = regions.Select(name => RequireName(name, nameof(regions))).Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal).ToImmutableArray();
        var known = new HashSet<string>(Regions, StringComparer.Ordinal);
        var edges = (constraints ?? Array.Empty<RegionConstraint>()).ToArray();
        if (edges.Any(edge => edge is null)) throw new ArgumentException("A constraint cannot be null.", nameof(constraints));
        foreach (var edge in edges)
            if (!known.Contains(edge.Longer) || !known.Contains(edge.Shorter))
                throw new ArgumentException("Constraint endpoints must be declared regions: " + edge.Longer + " : " + edge.Shorter, nameof(constraints));
        Constraints = edges.OrderBy(edge => edge.Longer, StringComparer.Ordinal).ThenBy(edge => edge.Shorter, StringComparer.Ordinal)
            .ThenBy(edge => edge.Site?.Path ?? string.Empty, StringComparer.Ordinal).ThenBy(edge => edge.Site?.Start ?? -1)
            .ThenBy(edge => edge.Site?.Length ?? -1).ThenBy(edge => edge.Reason, StringComparer.Ordinal).ToImmutableArray();
    }

    public ImmutableArray<string> Regions { get; }
    public ImmutableArray<RegionConstraint> Constraints { get; }

    internal static string RequireName(string name, string parameterName)
    {
        if (name is null) throw new ArgumentNullException(parameterName);
        if (string.IsNullOrWhiteSpace(name) || name.IndexOf('\0') >= 0)
            throw new ArgumentException("A region needs a nonempty name without a null character.", parameterName);
        return name;
    }
}

/// <summary>Finite resource limits for SCC discovery, implication closure, and shortest-path predecessor construction.</summary>
/// <remarks>Complete diagnostic paths are expanded lazily from the bounded predecessor forest.</remarks>
public sealed class RegionSolverOptions
{
    public RegionSolverOptions(int maxRegions = 256, int maxConstraints = 4096, int maxSteps = 100000)
    {
        if (maxRegions <= 0) throw new ArgumentOutOfRangeException(nameof(maxRegions));
        if (maxConstraints <= 0) throw new ArgumentOutOfRangeException(nameof(maxConstraints));
        if (maxSteps <= 0) throw new ArgumentOutOfRangeException(nameof(maxSteps));
        MaxRegions = maxRegions;
        MaxConstraints = maxConstraints;
        MaxSteps = maxSteps;
    }

    public int MaxRegions { get; }
    public int MaxConstraints { get; }
    public int MaxSteps { get; }
}
