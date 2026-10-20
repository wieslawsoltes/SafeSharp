using System.Collections.Immutable;

namespace SafeSharp.Core.Regions;

/// <summary>Evidence that a region component needed a visible upper bound for an invisible placeholder.</summary>
public sealed class RegionUniversePromotion
{
    internal RegionUniversePromotion(RegionVariable variable, RegionVariable placeholder, RegionProof proof,
        RegionVariable visibilityBound, RegionProof visibilityProof, string effectiveBinder, RegionConstraint upperBound)
    {
        Variable = variable; Placeholder = placeholder; Proof = proof; VisibilityBound = visibilityBound;
        VisibilityProof = visibilityProof; EffectiveBinder = effectiveBinder; UpperBound = upperBound;
    }

    public RegionVariable Variable { get; }
    public RegionVariable Placeholder { get; }
    public RegionProof Proof { get; }
    public RegionVariable VisibilityBound { get; }
    public RegionProof VisibilityProof { get; }
    public string EffectiveBinder { get; }
    public RegionConstraint UpperBound { get; }
}

/// <summary>A type-outlives requirement for which the caller supplied no sufficient bound.</summary>
public sealed class TypeOutlivesFailure
{
    internal TypeOutlivesFailure(TypeOutlivesConstraint requirement, string region, RegionProof proof)
    { Requirement = requirement; Region = region; Proof = proof; }

    public TypeOutlivesConstraint Requirement { get; }
    public string Region { get; }
    public RegionProof Proof { get; }
}

/// <summary>A complete higher-ranked proof or an explicit, non-successful incomplete result.</summary>
public sealed class HigherRankedRegionSolution
{
    internal HigherRankedRegionSolution(bool complete, string? reason, int steps,
        ImmutableArray<UniversalRegionFailure> failures, ImmutableArray<RegionUniversePromotion> promotions,
        ImmutableArray<TypeOutlivesFailure> typeFailures, RegionSolution? assumed, RegionSolution? closure)
    {
        IsComplete = complete;
        BudgetReason = reason;
        Steps = steps;
        Failures = failures;
        Promotions = promotions;
        TypeFailures = typeFailures;
        AssumedClosure = assumed;
        Closure = closure;
    }

    public bool IsComplete { get; }
    public bool IsCompatible => IsComplete && Failures.IsEmpty && TypeFailures.IsEmpty;
    public string? BudgetReason { get; }
    public int Steps { get; }
    public ImmutableArray<UniversalRegionFailure> Failures { get; }
    public ImmutableArray<RegionUniversePromotion> Promotions { get; }
    public ImmutableArray<TypeOutlivesFailure> TypeFailures { get; }
    public RegionSolution? AssumedClosure { get; }
    public RegionSolution? Closure { get; }

    internal static HigherRankedRegionSolution Incomplete(string reason, int steps) =>
        new(false, reason, steps, ImmutableArray<UniversalRegionFailure>.Empty,
            ImmutableArray<RegionUniversePromotion>.Empty, ImmutableArray<TypeOutlivesFailure>.Empty, null, null);
}
