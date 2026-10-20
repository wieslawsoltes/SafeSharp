using System;

namespace SafeSharp.Core.Regions;

/// <summary>How a borrowed type position relates the lifetimes of a subtype and supertype.</summary>
public enum RegionVariance
{
    Invariant = 0,
    Covariant = 1,
    Contravariant = -1,
}

/// <summary>Composition of variance through explicit borrowed type constructors.</summary>
public static class RegionVarianceRules
{
    /// <summary>Composes an enclosing position with a constructor's declared variance.</summary>
    public static RegionVariance Compose(RegionVariance outer, RegionVariance inner)
    {
        if (outer < RegionVariance.Contravariant || outer > RegionVariance.Covariant) throw new ArgumentOutOfRangeException(nameof(outer));
        if (inner < RegionVariance.Contravariant || inner > RegionVariance.Covariant) throw new ArgumentOutOfRangeException(nameof(inner));
        return (RegionVariance)((int)outer * (int)inner);
    }
}
