using SafeSharp.Core;

namespace SafeSharp.Tests.Core;

public sealed class TwoPhaseBorrowTests
{
    private static SourceSite Site(int offset) => new("two-phase.cs", offset, 1, offset, 0);
    private static FlowInstruction Op(InstructionKind kind, int offset, string? source = null, string? target = null,
        InstructionFlags flags = InstructionFlags.None) => new(kind, Site(offset), target, source, flags: flags);
    private static FlowInstruction New() => new(InstructionKind.Allocate, Site(0), "owner", resourceId: "resource");
    private static FlowInstruction Reserve(int offset = 1, string source = "owner", string target = "reservation",
        InstructionFlags flags = InstructionFlags.None) => Op(InstructionKind.ReserveExclusiveBorrow, offset, source, target, flags);
    private static FlowInstruction Activate(int offset = 4, string source = "reservation", InstructionFlags flags = InstructionFlags.None)
        => Op(InstructionKind.ActivateBorrow, offset, source, flags: flags);
    private static FlowBlock Block(int id, int[] successors, params FlowInstruction[] instructions) => new(id, instructions, successors);
    private static AnalysisResult Analyze(params FlowInstruction[] instructions) => AnalyzeBlocks(Block(0, [], instructions));
    private static AnalysisResult AnalyzeBlocks(params FlowBlock[] blocks) => new OwnershipAnalysisEngine().Analyze(
        new AnalysisGraph("TwoPhase", blocks, 0, Site(100)), new AnalysisOptions(captureSnapshots: true));
    private static AnalysisIssue IssueAt(AnalysisResult result, string id, int offset) => Assert.Single(result.Issues,
        issue => issue.Id == id && issue.Site.Start == offset);

    [Fact]
    public void ReceiverReservationPermitsNestedReadAndTemporarySharedArgument()
    {
        var result = Analyze(New(), Reserve(), Op(InstructionKind.Read, 2, "owner"),
            Op(InstructionKind.BorrowShared, 3, "owner", "argument"), Op(InstructionKind.Read, 4, "argument"),
            Activate(5), Op(InstructionKind.Write, 6, "reservation"), Op(InstructionKind.Dispose, 7, "owner"));
        Assert.Empty(result.Issues);
        var steps = Assert.Single(result.Blocks).Steps;
        Assert.Equal(BorrowPhase.Reserved, Assert.Single(steps[1].After.Loans).Phase);
        Assert.Equal(BorrowPhase.Active, Assert.Single(steps[5].After.Loans).Phase);
        Assert.Equal(5, Assert.Single(Assert.Single(steps[5].After.Loans).ActivationSites).Start);
        Assert.Empty(steps[6].After.Loans);
    }

    [Fact]
    public void ExistingSharedLoanMayEndBetweenReservationAndActivation()
    {
        var result = Analyze(New(), Op(InstructionKind.BorrowShared, 1, "owner", "reader"), Reserve(2),
            Op(InstructionKind.Read, 3, "reader"), Activate(), Op(InstructionKind.Write, 5, "reservation"),
            Op(InstructionKind.Dispose, 6, "owner"));
        Assert.Empty(result.Issues);
    }

    [Theory]
    [InlineData(InstructionKind.Write, "SS0007")]
    [InlineData(InstructionKind.Move, "SS0005")]
    [InlineData(InstructionKind.Dispose, "SS0014")]
    public void ReservationPreventsOwnerInvalidation(InstructionKind effect, string id)
    {
        var result = Analyze(New(), Reserve(), Op(effect, 2, "owner"), Op(InstructionKind.Read, 3, "reservation"));
        Assert.Equal(IssueCertainty.Definite, IssueAt(result, id, 2).Certainty);
    }

    [Theory]
    [InlineData(InstructionKind.BorrowExclusive)]
    [InlineData(InstructionKind.ReserveExclusiveBorrow)]
    public void ReservationPreventsCompetingExclusiveLoans(InstructionKind competing)
    {
        var result = Analyze(New(), Reserve(), Op(competing, 2, "owner", "competing"),
            Op(InstructionKind.Read, 3, "reservation"), Op(InstructionKind.Read, 4, "competing"), Op(InstructionKind.Dispose, 5, "owner"));
        Assert.Equal(IssueCertainty.Definite, IssueAt(result, "SS0006", 2).Certainty);
    }

    [Fact]
    public void LiveImmediateExclusiveLoanPreventsReservation()
    {
        var result = Analyze(New(), Op(InstructionKind.BorrowExclusive, 1, "owner", "exclusive"), Reserve(2),
            Op(InstructionKind.Read, 3, "exclusive"), Op(InstructionKind.Read, 4, "reservation"), Op(InstructionKind.Dispose, 5, "owner"));
        Assert.Equal(IssueCertainty.Definite, IssueAt(result, "SS0006", 2).Certainty);
    }

    [Fact]
    public void ActivationRejectsStillLiveSharedLoanAndRetainsBothOrigins()
    {
        var result = Analyze(New(), Reserve(), Op(InstructionKind.BorrowShared, 2, "owner", "reader"),
            Activate(3), Op(InstructionKind.Read, 4, "reader"), Op(InstructionKind.Write, 5, "reservation"),
            Op(InstructionKind.Dispose, 6, "owner"));
        var issue = IssueAt(result, "SS0006", 3);
        Assert.Equal(IssueCertainty.Definite, issue.Certainty);
        Assert.Contains(issue.RelatedLocations, site => site.Start == 1);
        Assert.Contains(issue.RelatedLocations, site => site.Start == 2);
        Assert.DoesNotContain(result.Issues, item => item.Id == "SS0006" && item.Site.Start == 2);
    }

    [Fact]
    public void OwnerReadBecomesInvalidOnlyAfterActivation()
    {
        var result = Analyze(New(), Reserve(), Op(InstructionKind.Read, 2, "owner"), Activate(3),
            Op(InstructionKind.Read, 4, "owner"), Op(InstructionKind.Read, 5, "reservation"), Op(InstructionKind.Dispose, 6, "owner"));
        var issue = Assert.Single(result.Issues);
        Assert.Equal("SS0013", issue.Id);
        Assert.Equal(4, issue.Site.Start);
        Assert.Contains(issue.Evidence, evidence => evidence.Kind == "activation" && evidence.Site.Start == 3);
    }

    [Fact]
    public void ReservationHolderCannotMutateBeforeActivation()
    {
        var result = Analyze(New(), Reserve(), Op(InstructionKind.Write, 2, "reservation"), Activate(3),
            Op(InstructionKind.Write, 4, "reservation"), Op(InstructionKind.Dispose, 5, "owner"));
        var issue = Assert.Single(result.Issues);
        Assert.Equal("SS0007", issue.Id);
        Assert.Equal(2, issue.Site.Start);
    }

    [Theory]
    [InlineData(InstructionKind.BorrowExclusive)]
    [InlineData(InstructionKind.ReserveExclusiveBorrow)]
    public void ReservationCannotBeUsedToCreateAnotherExclusiveLoan(InstructionKind kind)
    {
        var result = Analyze(New(), Reserve(), Op(kind, 2, "reservation", "child"),
            Op(InstructionKind.Read, 3, "child"), Op(InstructionKind.Dispose, 4, "owner"));
        IssueAt(result, "SS0006", 2);
    }

    [Fact]
    public void SharedChildMustExpireBeforeParentReservationActivates()
    {
        var result = Analyze(New(), Reserve(), Op(InstructionKind.BorrowShared, 2, "reservation", "child"),
            Op(InstructionKind.Read, 3, "child"), Activate(), Op(InstructionKind.Write, 5, "reservation"),
            Op(InstructionKind.Dispose, 6, "owner"));
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void ActivationThroughAliasUpdatesOriginalLoanIdentity()
    {
        var result = Analyze(New(), Reserve(), Op(InstructionKind.Alias, 2, "reservation", "alias"), Activate(3, "alias"),
            Op(InstructionKind.Read, 4, "owner"), Op(InstructionKind.Write, 5, "reservation"), Op(InstructionKind.Dispose, 6, "owner"));
        Assert.Equal(IssueCertainty.Definite, IssueAt(result, "SS0013", 4).Certainty);
        Assert.Equal(BorrowPhase.Active, Assert.Single(result.Blocks[0].Steps[3].After.Loans).Phase);
    }

    [Fact]
    public void DeadReservationExpiresWithoutActivation()
    {
        var result = Analyze(New(), Reserve(), Op(InstructionKind.Read, 2, "reservation"),
            Op(InstructionKind.Write, 3, "owner"), Op(InstructionKind.Dispose, 4, "owner"));
        Assert.Empty(result.Issues);
        Assert.Empty(result.Blocks[0].Steps[2].After.Loans);
    }

    [Fact]
    public void ExplicitReleaseCancelsReservation()
    {
        var result = Analyze(New(), Reserve(), Op(InstructionKind.ReleaseLoan, 2, target: "reservation"),
            Op(InstructionKind.Write, 3, "owner"), Op(InstructionKind.Dispose, 4, "owner"));
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void ConditionalActivationRetainsBothPhasesAndPossibleReadConflict()
    {
        var result = Analyze(New(), Reserve(), Activate(2, flags: InstructionFlags.Conditional),
            Op(InstructionKind.Read, 3, "owner"), Op(InstructionKind.Read, 4, "reservation"), Op(InstructionKind.Dispose, 5, "owner"));
        Assert.Equal(IssueCertainty.Possible, IssueAt(result, "SS0013", 3).Certainty);
        Assert.Equal(BorrowPhase.Reserved | BorrowPhase.Active, Assert.Single(result.Blocks[0].Steps[2].After.Loans).Phase);
    }

    [Fact]
    public void ConditionalActivationMakesMutationThroughHolderOnlyPossiblyLegal()
    {
        var result = Analyze(New(), Reserve(), Activate(2, flags: InstructionFlags.Conditional),
            Op(InstructionKind.Write, 3, "reservation"), Op(InstructionKind.Dispose, 4, "owner"));
        Assert.Equal(IssueCertainty.Possible, IssueAt(result, "SS0007", 3).Certainty);
    }

    [Fact]
    public void ActivationOnOneBranchIsNotDefiniteAtJoin()
    {
        var result = AnalyzeBlocks(Block(0, [1, 2], New(), Reserve()), Block(1, [3], Activate(2)), Block(2, [3]),
            Block(3, [], Op(InstructionKind.Read, 3, "owner"), Op(InstructionKind.Read, 4, "reservation"), Op(InstructionKind.Dispose, 5, "owner")));
        Assert.Equal(IssueCertainty.Possible, IssueAt(result, "SS0013", 3).Certainty);
        Assert.Equal(BorrowPhase.Reserved | BorrowPhase.Active, Assert.Single(result.Blocks.Single(block => block.Id == 3).Entry.Loans).Phase);
    }

    [Fact]
    public void ActivationOnBothBranchesIsDefiniteAndPreservesBothSites()
    {
        var result = AnalyzeBlocks(Block(0, [1, 2], New(), Reserve()), Block(1, [3], Activate(2)), Block(2, [3], Activate(3)),
            Block(3, [], Op(InstructionKind.Read, 4, "owner"), Op(InstructionKind.Write, 5, "reservation"), Op(InstructionKind.Dispose, 6, "owner")));
        Assert.Equal(IssueCertainty.Definite, IssueAt(result, "SS0013", 4).Certainty);
        var loan = Assert.Single(result.Blocks.Single(block => block.Id == 3).Entry.Loans);
        Assert.Equal(BorrowPhase.Active, loan.Phase);
        Assert.Equal(new[] { 2, 3 }, loan.ActivationSites.Select(site => site.Start));
    }

    [Fact]
    public void SharedBorrowAfterMixedPhaseJoinHasPossibleConflict()
    {
        var result = Analyze(New(), Reserve(), Activate(2, flags: InstructionFlags.Conditional),
            Op(InstructionKind.BorrowShared, 3, "owner", "reader"), Op(InstructionKind.Read, 4, "reservation"),
            Op(InstructionKind.Read, 5, "reader"), Op(InstructionKind.Dispose, 6, "owner"));
        Assert.Equal(IssueCertainty.Possible, IssueAt(result, "SS0006", 3).Certainty);
    }

    [Fact]
    public void LoopReusesReservationSiteWithoutLeakingActivePhaseIntoNextIteration()
    {
        var result = AnalyzeBlocks(Block(0, [1], New()),
            Block(1, [1, 2], Reserve(), Op(InstructionKind.Read, 2, "owner"), Activate(3), Op(InstructionKind.Write, 4, "reservation")),
            Block(2, [], Op(InstructionKind.Dispose, 5, "owner")));
        Assert.Empty(result.Issues);
        Assert.False(result.BudgetExceeded);
        Assert.InRange(result.Iterations, 3, 20);
    }

    [Theory]
    [InlineData(InstructionFlags.None, true)]
    [InlineData(InstructionFlags.AllowsSuspension, false)]
    public void ReservationHonorsSuspensionPolicy(InstructionFlags flags, bool expected)
    {
        var result = Analyze(New(), Reserve(flags: flags), Op(InstructionKind.Suspend, 2), Activate(3),
            Op(InstructionKind.Write, 4, "reservation"), Op(InstructionKind.Dispose, 5, "owner"));
        Assert.Equal(expected, result.Issues.Any(issue => issue.Id == "SS0010"));
    }

    [Fact]
    public void DisjointFieldReservationsCanActivateIndependently()
    {
        var result = Analyze(New(), Reserve(1, "owner.left", "left"), Reserve(2, "owner.right", "right"),
            Activate(3, "left"), Op(InstructionKind.Write, 4, "left"), Activate(5, "right"),
            Op(InstructionKind.Write, 6, "right"), Op(InstructionKind.Dispose, 7, "owner"));
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void ExistingExplicitExclusiveBorrowRemainsImmediatelyActive()
    {
        var result = Analyze(New(), Op(InstructionKind.BorrowExclusive, 1, "owner", "exclusive"),
            Op(InstructionKind.Read, 2, "owner"), Op(InstructionKind.Read, 3, "exclusive"), Op(InstructionKind.Dispose, 4, "owner"));
        Assert.Equal(BorrowPhase.Active, Assert.Single(result.Blocks[0].Steps[1].After.Loans).Phase);
        Assert.Equal(IssueCertainty.Definite, IssueAt(result, "SS0013", 2).Certainty);
    }

    [Fact]
    public void SharedLoanCannotBeActivated()
    {
        var result = Analyze(New(), Op(InstructionKind.BorrowShared, 1, "owner", "shared"), Activate(2, "shared"),
            Op(InstructionKind.Read, 3, "shared"), Op(InstructionKind.Dispose, 4, "owner"));
        IssueAt(result, "SS0006", 2);
    }

    [Theory]
    [InlineData(InstructionKind.BorrowShared, true)]
    [InlineData(InstructionKind.ReserveExclusiveBorrow, true)]
    [InlineData(InstructionKind.BorrowExclusive, false)]
    public void MutationThroughChildRequiresActiveExclusivePermissionFromEveryAncestor(InstructionKind parentKind, bool violation)
    {
        var result = Analyze(New(), Op(parentKind, 1, "owner", "parent"),
            Op(InstructionKind.BorrowExclusive, 2, "parent", "child"), Op(InstructionKind.Write, 3, "child"),
            Op(InstructionKind.Dispose, 4, "owner"));
        Assert.Equal(violation, result.Issues.Any(issue => issue.Id == "SS0007" && issue.Site.Start == 3));
        if (!violation) Assert.Empty(result.Issues);
    }

    [Fact]
    public void ActivatingInvalidReservationDoesNotGrantMutationThroughSharedAncestor()
    {
        var result = Analyze(New(), Op(InstructionKind.BorrowShared, 1, "owner", "shared"), Reserve(2, "shared"),
            Activate(3), Op(InstructionKind.Write, 4, "reservation"), Op(InstructionKind.Dispose, 5, "owner"));
        Assert.Equal(IssueCertainty.Definite, IssueAt(result, "SS0007", 4).Certainty);
    }

    [Theory]
    [InlineData(InstructionKind.ReserveExclusiveBorrow, null, "reservation")]
    [InlineData(InstructionKind.ReserveExclusiveBorrow, "owner", null)]
    [InlineData(InstructionKind.ActivateBorrow, null, null)]
    public void ProtocolInstructionsRequireTheirOperands(InstructionKind kind, string? source, string? target)
    {
        Assert.Throws<ArgumentException>(() => Op(kind, 0, source, target));
    }
}
