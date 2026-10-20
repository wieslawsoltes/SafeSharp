using SafeSharp.Core.Regions;

namespace SafeSharp.Tests.Regions;

public sealed class RegionVarianceTests
{
    [Theory]
    [InlineData(RegionVariance.Covariant, RegionVariance.Covariant, RegionVariance.Covariant)]
    [InlineData(RegionVariance.Covariant, RegionVariance.Contravariant, RegionVariance.Contravariant)]
    [InlineData(RegionVariance.Contravariant, RegionVariance.Covariant, RegionVariance.Contravariant)]
    [InlineData(RegionVariance.Contravariant, RegionVariance.Contravariant, RegionVariance.Covariant)]
    [InlineData(RegionVariance.Invariant, RegionVariance.Covariant, RegionVariance.Invariant)]
    [InlineData(RegionVariance.Invariant, RegionVariance.Contravariant, RegionVariance.Invariant)]
    [InlineData(RegionVariance.Invariant, RegionVariance.Invariant, RegionVariance.Invariant)]
    [InlineData(RegionVariance.Covariant, RegionVariance.Invariant, RegionVariance.Invariant)]
    [InlineData(RegionVariance.Contravariant, RegionVariance.Invariant, RegionVariance.Invariant)]
    public void CompositionMatchesSubtypePolarity(RegionVariance outer, RegionVariance inner, RegionVariance expected) =>
        Assert.Equal(expected, RegionVarianceRules.Compose(outer, inner));

    [Fact]
    public void InvalidVariancesAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RegionVarianceRules.Compose((RegionVariance)2, RegionVariance.Covariant));
        Assert.Throws<ArgumentOutOfRangeException>(() => RegionVarianceRules.Compose(RegionVariance.Invariant, (RegionVariance)(-2)));
    }
}
