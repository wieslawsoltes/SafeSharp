using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace SafeSharp.Core.Regions;

/// <summary>
/// One binder of universally quantified named regions, assumed outlives bounds, and existential
/// inference regions whose requirements must not strengthen the assumed universal relations.
/// </summary>
/// <remarks>
/// All collections are snapshotted and names are compared ordinally. Universal and inference names
/// must be disjoint. This domain contains outlives relations; it does not encode nested universes,
/// Rust type/trait obligations, or program-point liveness.
/// </remarks>
public sealed class UniversalRegionProblem
{
    public UniversalRegionProblem(RegionProblem assumptions, IEnumerable<string> inferenceRegions,
        IEnumerable<RegionConstraint>? requirements = null)
    {
        Assumptions = assumptions ?? throw new ArgumentNullException(nameof(assumptions));
        if (inferenceRegions is null) throw new ArgumentNullException(nameof(inferenceRegions));
        InferenceRegions = inferenceRegions.Select(name => RegionProblem.RequireName(name, nameof(inferenceRegions)))
            .Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToImmutableArray();
        var universals = new HashSet<string>(assumptions.Regions, StringComparer.Ordinal);
        if (InferenceRegions.Any(universals.Contains))
            throw new ArgumentException("Universal and inference region names must be disjoint.", nameof(inferenceRegions));
        var combinedNames = assumptions.Regions.Concat(InferenceRegions).ToImmutableArray();
        // RegionProblem validates endpoints and retains deterministic original-edge evidence.
        var required = new RegionProblem(combinedNames, requirements);
        Requirements = required.Constraints;
        CombinedProblem = new RegionProblem(combinedNames, assumptions.Constraints.Concat(Requirements));
    }

    public RegionProblem Assumptions { get; }
    public ImmutableArray<string> UniversalRegions => Assumptions.Regions;
    public ImmutableArray<string> InferenceRegions { get; }
    public ImmutableArray<RegionConstraint> Requirements { get; }
    public RegionProblem CombinedProblem { get; }
}
