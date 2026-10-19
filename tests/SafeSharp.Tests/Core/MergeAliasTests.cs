using SafeSharp.Core;

namespace SafeSharp.Tests.Core;

public sealed class MergeAliasTests
{
    private static SourceSite Site(int offset) => new("alias-choice.cs", offset, 1, offset, 0);
    private static FlowInstruction Op(InstructionKind kind, int offset, string? source = null, string? target = null)
        => new(kind, Site(offset), target, source);
    private static FlowInstruction New(string target, int offset) => new(InstructionKind.Allocate, Site(offset), target, resourceId: target);
    private static AnalysisResult Analyze(params FlowInstruction[] instructions) => new OwnershipAnalysisEngine().Analyze(
        new AnalysisGraph("AliasChoice", [new FlowBlock(0, instructions, [])], 0, Site(100)), new AnalysisOptions(captureSnapshots: true));

    [Fact]
    public void MergePreservesBothAlternativeResourceOriginsWithoutInventingDefinitePresence()
    {
        var result = Analyze(New("left", 0), New("right", 1), Op(InstructionKind.Alias, 2, "left", "choice"),
            Op(InstructionKind.MergeAlias, 3, "right", "choice"), Op(InstructionKind.Read, 4, "choice"),
            Op(InstructionKind.Dispose, 5, "left"), Op(InstructionKind.Dispose, 6, "right"));
        Assert.Empty(result.Issues);
        var place = result.Blocks[0].Steps[3].After.Places.Single(place => place.Place == "choice");
        Assert.Equal(new[] { "left", "right" }, place.References.Select(reference => reference.ResourceId));
        Assert.All(place.References, reference =>
        {
            Assert.True(reference.MayBeAbsent);
            Assert.False(reference.CoversResource);
        });
        Assert.False(place.MayBeNull);
    }

    [Fact]
    public void UnselectedBorrowTemporaryIsOnlyPossiblyLiveAfterMerge()
    {
        var result = Analyze(New("left", 0), New("right", 1),
            Op(InstructionKind.BorrowShared, 2, "left", "choice"), Op(InstructionKind.BorrowShared, 3, "right", "candidate"),
            Op(InstructionKind.MergeAlias, 4, "candidate", "choice"), Op(InstructionKind.Dispose, 5, "left"),
            Op(InstructionKind.Read, 6, "choice"), Op(InstructionKind.Dispose, 7, "right"));
        var issue = Assert.Single(result.Issues, issue => issue.Id == "SS0014");
        Assert.Equal(IssueCertainty.Possible, issue.Certainty);
        Assert.All(result.Blocks[0].Steps[4].After.Loans, loan => Assert.True(loan.MayBeAbsent));
        Assert.DoesNotContain("candidate", result.Blocks[0].Steps[4].LiveAfter);
    }

    [Fact]
    public void IndependentlyUsedCandidateLoanRemainsDefinitelyLive()
    {
        var result = Analyze(New("left", 0), New("right", 1),
            Op(InstructionKind.BorrowShared, 2, "left", "choice"), Op(InstructionKind.BorrowShared, 3, "right", "candidate"),
            Op(InstructionKind.MergeAlias, 4, "candidate", "choice"), Op(InstructionKind.Dispose, 5, "right"),
            Op(InstructionKind.Read, 6, "candidate"), Op(InstructionKind.Read, 7, "choice"), Op(InstructionKind.Dispose, 8, "left"));
        var issue = Assert.Single(result.Issues, issue => issue.Id == "SS0014");
        Assert.Equal(IssueCertainty.Definite, issue.Certainty);
        var loan = result.Blocks[0].Steps[4].After.Loans.Single(loan => loan.ResourceId == "right");
        Assert.False(loan.MayBeAbsent);
    }

    [Fact]
    public void BackwardLivenessTreatsPreviousTargetAsInput()
    {
        var result = Analyze(New("left", 0), New("right", 1),
            Op(InstructionKind.BorrowShared, 2, "left", "choice"), Op(InstructionKind.Write, 3, "left"),
            Op(InstructionKind.BorrowShared, 4, "right", "candidate"), Op(InstructionKind.MergeAlias, 5, "candidate", "choice"),
            Op(InstructionKind.Read, 6, "choice"), Op(InstructionKind.Dispose, 7, "left"), Op(InstructionKind.Dispose, 8, "right"));
        Assert.Equal(3, Assert.Single(result.Issues, issue => issue.Id == "SS0007").Site.Start);
        Assert.Contains("choice", result.Blocks[0].Steps[5].LiveBefore);
        Assert.Contains("candidate", result.Blocks[0].Steps[5].LiveBefore);
    }

    [Fact]
    public void MergingEquivalentAliasesRetainsDefiniteResourceCorrelation()
    {
        var result = Analyze(New("owner", 0), Op(InstructionKind.Alias, 1, "owner", "choice"),
            Op(InstructionKind.Alias, 2, "owner", "candidate"), Op(InstructionKind.MergeAlias, 3, "candidate", "choice"),
            Op(InstructionKind.Dispose, 4, "choice"));
        Assert.Empty(result.Issues);
        var reference = Assert.Single(result.Blocks[0].Steps[3].After.Places.Single(place => place.Place == "choice").References);
        Assert.True(reference.CoversResource);
        Assert.False(reference.MayBeAbsent);
    }

    [Fact]
    public void MissingInitialTargetRemainsANullAlternative()
    {
        var result = Analyze(New("owner", 0), Op(InstructionKind.MergeAlias, 1, "owner", "choice"),
            Op(InstructionKind.Dispose, 2, "owner"));
        var place = result.Blocks[0].Steps[1].After.Places.Single(place => place.Place == "choice");
        Assert.True(place.MayBeNull);
        Assert.True(Assert.Single(place.References).MayBeAbsent);
    }

    [Fact]
    public void ChoosingBetweenTwoPendingCompletionsCannotDischargeBoth()
    {
        var result = Analyze(New("left", 0), New("right", 1),
            Op(InstructionKind.BeginAsyncDispose, 2, "left", "choice"),
            Op(InstructionKind.BeginAsyncDispose, 3, "right", "candidate"),
            Op(InstructionKind.MergeAlias, 4, "candidate", "choice"),
            Op(InstructionKind.CompleteAsyncDispose, 5, "choice"));
        var outstanding = result.Issues.Where(issue => issue.Id == "SS0017" && issue.Site.Start != 4).ToArray();
        Assert.Equal(2, outstanding.Length);
        Assert.All(outstanding, issue => Assert.Equal(IssueCertainty.Possible, issue.Certainty));
    }

    [Fact]
    public void MergingAliasesOfOneCompletionAllowsDefiniteObservation()
    {
        var result = Analyze(New("owner", 0), Op(InstructionKind.BeginAsyncDispose, 1, "owner", "choice"),
            Op(InstructionKind.Alias, 2, "choice", "candidate"), Op(InstructionKind.MergeAlias, 3, "candidate", "choice"),
            Op(InstructionKind.CompleteAsyncDispose, 4, "choice"));
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void ChainedMergeDoesNotDropAnEarlierLifetimeOrigin()
    {
        var result = Analyze(New("first", 0), New("second", 1), New("third", 2),
            Op(InstructionKind.Alias, 3, "first", "choice"), Op(InstructionKind.MergeAlias, 4, "second", "choice"),
            Op(InstructionKind.MergeAlias, 5, "third", "choice"), Op(InstructionKind.Read, 6, "choice"),
            Op(InstructionKind.Dispose, 7, "first"), Op(InstructionKind.Dispose, 8, "second"), Op(InstructionKind.Dispose, 9, "third"));
        Assert.Empty(result.Issues);
        Assert.Equal(new[] { "first", "second", "third" },
            result.Blocks[0].Steps[5].After.Places.Single(place => place.Place == "choice").References.Select(reference => reference.ResourceId).OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public void ExistingAliasInstructionStillReplacesItsTarget()
    {
        var result = Analyze(New("first", 0), New("second", 1), Op(InstructionKind.Alias, 2, "first", "choice"),
            Op(InstructionKind.Alias, 3, "second", "choice"), Op(InstructionKind.Dispose, 4, "first"), Op(InstructionKind.Dispose, 5, "second"));
        Assert.Empty(result.Issues);
        Assert.Equal("second", Assert.Single(result.Blocks[0].Steps[3].After.Places.Single(place => place.Place == "choice").References).ResourceId);
    }

    [Theory]
    [InlineData(null, "target")]
    [InlineData("source", null)]
    public void MergeRequiresBothPlaces(string? source, string? target)
    {
        Assert.Throws<ArgumentException>(() => Op(InstructionKind.MergeAlias, 0, source, target));
    }
}
