using SafeSharp.Core;

namespace SafeSharp.Tests.Core;

public sealed class OwnershipEngineTests
{
    private static SourceSite Site(int offset) => new("test.cs", offset, 1, offset, 0);
    private static FlowInstruction Op(InstructionKind kind, int offset, string? source = null, string? target = null,
        string? resource = null, ResourceOwnership ownership = ResourceOwnership.Owned,
        InstructionFlags flags = InstructionFlags.None, string? owner = null)
        => new(kind, Site(offset), target, source, resource, ownership, owner, flags);
    private static FlowInstruction New(string target = "owner", int offset = 0, string? id = null,
        ResourceOwnership ownership = ResourceOwnership.Owned, InstructionFlags flags = InstructionFlags.None, string? parent = null)
        => Op(InstructionKind.Allocate, offset, target: target, resource: id ?? target, ownership: ownership, flags: flags, owner: parent);
    private static FlowBlock Block(int id, int[] successors, params FlowInstruction[] instructions) => new(id, instructions, successors);
    private static AnalysisResult Analyze(params FlowInstruction[] instructions) => AnalyzeBlocks(Block(0, [], instructions));
    private static AnalysisResult AnalyzeBlocks(params FlowBlock[] blocks) => new OwnershipAnalysisEngine().Analyze(new("M", blocks, 0, Site(100)), new(captureSnapshots: true));
    private static AnalysisIssue Issue(AnalysisResult result, string id) => Assert.Single(result.Issues, issue => issue.Id == id);
    private static void NoIssue(AnalysisResult result, string id) => Assert.DoesNotContain(result.Issues, issue => issue.Id == id);

    [Fact]
    public void DisposedResourceHasNoLeak()
    {
        Assert.Empty(Analyze(New(), Op(InstructionKind.Dispose, 1, "owner")).Issues);
    }

    [Fact]
    public void LiveResourceReportsDefiniteLeakAtAcquisition()
    {
        var issue = Issue(Analyze(New()), "SS0003");
        Assert.Equal(IssueCertainty.Definite, issue.Certainty);
        Assert.Equal(0, issue.Site.Start);
        Assert.Contains(issue.Evidence, fact => fact.Kind == "origin");
        Assert.Contains(issue.Evidence, fact => fact.Kind == "exit");
    }

    [Theory]
    [InlineData(ResourceOwnership.Borrowed)]
    [InlineData(ResourceOwnership.External)]
    [InlineData(ResourceOwnership.Scoped)]
    public void NonOwnedResourceDoesNotCreateLocalDisposalObligation(ResourceOwnership ownership)
    {
        NoIssue(Analyze(New(ownership: ownership)), "SS0003");
    }

    [Fact]
    public void SuppressLeakKeepsLifecycleChecksEnabled()
    {
        var result = Analyze(New(flags: InstructionFlags.SuppressLeak), Op(InstructionKind.Dispose, 1, "owner"), Op(InstructionKind.Read, 2, "owner"));
        Issue(result, "SS0001");
        NoIssue(result, "SS0003");
    }

    [Fact]
    public void AliasDisposalInvalidatesEveryAlias()
    {
        var issue = Issue(Analyze(New(), Op(InstructionKind.Alias, 1, "owner", "alias"),
            Op(InstructionKind.Dispose, 2, "alias"), Op(InstructionKind.Read, 3, "owner")), "SS0001");
        Assert.Equal(IssueCertainty.Definite, issue.Certainty);
        Assert.Contains(issue.RelatedLocations, site => site.Start == 2);
    }

    [Fact]
    public void SecondDisposalReportsDoubleDisposal()
    {
        var result = Analyze(New(), Op(InstructionKind.Dispose, 1, "owner"), Op(InstructionKind.Dispose, 2, "owner"));
        Assert.Equal(IssueCertainty.Definite, Issue(result, "SS0002").Certainty);
        NoIssue(result, "SS0001");
    }

    [Fact]
    public void DisposalOnOneBranchProducesPossibleUseAfterDisposeAndLeak()
    {
        var result = AnalyzeBlocks(Block(0, [1, 2], New()),
            Block(1, [3], Op(InstructionKind.Dispose, 1, "owner")), Block(2, [3]), Block(3, [], Op(InstructionKind.Read, 3, "owner")));
        Assert.Equal(IssueCertainty.Possible, Issue(result, "SS0001").Certainty);
        Assert.Equal(IssueCertainty.Possible, Issue(result, "SS0003").Certainty);
    }

    [Fact]
    public void DisposalOnBothBranchesProvesNoLeak()
    {
        var result = AnalyzeBlocks(Block(0, [1, 2], New()),
            Block(1, [3], Op(InstructionKind.Dispose, 1, "owner")), Block(2, [3], Op(InstructionKind.Dispose, 2, "owner")), Block(3, []));
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void LoopReachesFixedPointAndReportsPossibleRepeatedDisposal()
    {
        var result = AnalyzeBlocks(Block(0, [1], New()), Block(1, [1, 2], Op(InstructionKind.Dispose, 1, "owner")), Block(2, []));
        Assert.False(result.BudgetExceeded);
        Assert.InRange(result.Iterations, 3, 15);
        Assert.Equal(IssueCertainty.Possible, Issue(result, "SS0002").Certainty);
        NoIssue(result, "SS0003");
    }

    [Fact]
    public void AllocationDisposedEachLoopIterationDoesNotInheritPreviousDisposal()
    {
        var result = AnalyzeBlocks(Block(0, [1]), Block(1, [1, 2], New(), Op(InstructionKind.Read, 1, "owner"), Op(InstructionKind.Dispose, 2, "owner")), Block(2, []));
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void UnreachableResourceDoesNotReportLeak()
    {
        var result = AnalyzeBlocks(Block(0, []), Block(1, [], New()));
        Assert.Empty(result.Issues);
        Assert.Single(result.Blocks);
    }

    [Fact]
    public void MoveInvalidatesAllExistingAliasesButDestinationRemainsUsable()
    {
        var result = Analyze(New(), Op(InstructionKind.Alias, 1, "owner", "alias"), Op(InstructionKind.Move, 2, "owner", "destination"),
            Op(InstructionKind.Read, 3, "alias"), Op(InstructionKind.Read, 4, "destination"), Op(InstructionKind.Dispose, 5, "destination"));
        var issue = Issue(result, "SS0004");
        Assert.Equal(3, issue.Site.Start);
        Assert.Equal(IssueCertainty.Definite, issue.Certainty);
        NoIssue(result, "SS0003");
    }

    [Fact]
    public void ConsumingMoveDischargesObligationAndInvalidatesSource()
    {
        var result = Analyze(New(), Op(InstructionKind.Move, 1, "owner"), Op(InstructionKind.Read, 2, "owner"));
        Issue(result, "SS0004");
        NoIssue(result, "SS0003");
    }

    [Fact]
    public void ReinitializationAfterMoveIsLegal()
    {
        var result = Analyze(New(id: "first"), Op(InstructionKind.Move, 1, "owner"), New(offset: 2, id: "second"),
            Op(InstructionKind.Read, 3, "owner"), Op(InstructionKind.Dispose, 4, "owner"));
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void ImplicitUsingCleanupAfterMoveReportsUseAfterMove()
    {
        Issue(Analyze(New(), Op(InstructionKind.Move, 1, "owner"), Op(InstructionKind.Dispose, 2, "owner", flags: InstructionFlags.Implicit)), "SS0004");
    }

    [Fact]
    public void MoveOnOneBranchIsOnlyPossiblyInvalidAtJoin()
    {
        var result = AnalyzeBlocks(Block(0, [1, 2], New(flags: InstructionFlags.SuppressLeak)),
            Block(1, [3], Op(InstructionKind.Move, 1, "owner")), Block(2, [3]), Block(3, [], Op(InstructionKind.Read, 3, "owner")));
        Assert.Equal(IssueCertainty.Possible, Issue(result, "SS0004").Certainty);
    }

    [Fact]
    public void MultipleSharedLoansCanCoexist()
    {
        var result = Analyze(New(), Op(InstructionKind.BorrowShared, 1, "owner", "a"), Op(InstructionKind.BorrowShared, 2, "owner", "b"),
            Op(InstructionKind.Read, 3, "a"), Op(InstructionKind.Read, 4, "b"), Op(InstructionKind.Dispose, 5, "owner"));
        Assert.Empty(result.Issues);
    }

    [Theory]
    [InlineData(InstructionKind.BorrowShared, InstructionKind.BorrowExclusive)]
    [InlineData(InstructionKind.BorrowExclusive, InstructionKind.BorrowShared)]
    [InlineData(InstructionKind.BorrowExclusive, InstructionKind.BorrowExclusive)]
    public void IncompatibleLiveLoansConflict(InstructionKind first, InstructionKind second)
    {
        var result = Analyze(New(), Op(first, 1, "owner", "a"), Op(second, 2, "owner", "b"),
            Op(InstructionKind.Read, 3, "a"), Op(InstructionKind.Read, 4, "b"), Op(InstructionKind.Dispose, 5, "owner"));
        Assert.Equal(2, Issue(result, "SS0006").Site.Start);
    }

    [Fact]
    public void NonLexicalLifetimeEndsAfterLastUse()
    {
        var result = Analyze(New(), Op(InstructionKind.BorrowShared, 1, "owner", "borrow"), Op(InstructionKind.Read, 2, "borrow"),
            Op(InstructionKind.Write, 3, "owner"), Op(InstructionKind.Dispose, 4, "owner"));
        Assert.Empty(result.Issues);
        var steps = Assert.Single(result.Blocks).Steps;
        Assert.Single(steps[1].After.Loans);
        Assert.Empty(steps[2].After.Loans);
        Assert.Contains("borrow", steps[1].LiveAfter);
        Assert.DoesNotContain("borrow", steps[2].LiveAfter);
    }

    [Fact]
    public void AliasKeepsLoanLiveAfterOriginalBorrowVariableDies()
    {
        var result = Analyze(New(), Op(InstructionKind.BorrowShared, 1, "owner", "borrow"), Op(InstructionKind.Alias, 2, "borrow", "alias"),
            Op(InstructionKind.Write, 3, "owner"), Op(InstructionKind.Read, 4, "alias"), Op(InstructionKind.Dispose, 5, "owner"));
        Assert.Equal(3, Issue(result, "SS0007").Site.Start);
    }

    [Fact]
    public void DeadBorrowOnOtherBranchDoesNotBlockDisposal()
    {
        var result = AnalyzeBlocks(Block(0, [1, 2], New(), Op(InstructionKind.BorrowShared, 1, "owner", "borrow")),
            Block(1, [], Op(InstructionKind.Read, 2, "borrow"), Op(InstructionKind.Dispose, 3, "owner")),
            Block(2, [], Op(InstructionKind.Dispose, 4, "owner")));
        Assert.Empty(result.Issues);
    }

    [Theory]
    [InlineData(InstructionKind.Move, "SS0005")]
    [InlineData(InstructionKind.Write, "SS0007")]
    [InlineData(InstructionKind.Dispose, "SS0014")]
    public void OwnerCannotBeInvalidatedWhileBorrowLives(InstructionKind kind, string id)
    {
        var result = Analyze(New(), Op(InstructionKind.BorrowShared, 1, "owner", "borrow"), Op(kind, 2, "owner"), Op(InstructionKind.Read, 3, "borrow"));
        Assert.Equal(2, Issue(result, id).Site.Start);
    }

    [Fact]
    public void OwnerCannotBeReadDuringExclusiveLoan()
    {
        var result = Analyze(New(), Op(InstructionKind.BorrowExclusive, 1, "owner", "borrow"),
            Op(InstructionKind.Read, 2, "owner"), Op(InstructionKind.Read, 3, "borrow"), Op(InstructionKind.Dispose, 4, "owner"));
        Assert.Equal(2, Issue(result, "SS0013").Site.Start);
    }

    [Fact]
    public void ExclusiveBorrowAllowsItsOwnReadsAndWrites()
    {
        var result = Analyze(New(), Op(InstructionKind.BorrowExclusive, 1, "owner", "borrow"),
            Op(InstructionKind.Read, 2, "borrow"), Op(InstructionKind.Write, 3, "borrow"), Op(InstructionKind.Dispose, 4, "owner"));
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void SharedBorrowDoesNotAllowItsOwnMutation()
    {
        var result = Analyze(New(), Op(InstructionKind.BorrowShared, 1, "owner", "borrow"), Op(InstructionKind.Write, 2, "borrow"), Op(InstructionKind.Dispose, 3, "owner"));
        Issue(result, "SS0007");
    }

    [Fact]
    public void MutableReborrowTemporarilySuspendsParentAccess()
    {
        var result = Analyze(New(), Op(InstructionKind.BorrowExclusive, 1, "owner", "parent"), Op(InstructionKind.BorrowExclusive, 2, "parent", "child"),
            Op(InstructionKind.Write, 3, "parent"), Op(InstructionKind.Write, 4, "child"), Op(InstructionKind.Write, 5, "parent"), Op(InstructionKind.Dispose, 6, "owner"));
        NoIssue(result, "SS0006");
        Assert.Equal(3, Issue(result, "SS0007").Site.Start);
    }

    [Fact]
    public void SharedLoanCannotBeStrengthenedIntoExclusiveLoan()
    {
        var result = Analyze(New(), Op(InstructionKind.BorrowShared, 1, "owner", "parent"), Op(InstructionKind.BorrowExclusive, 2, "parent", "child"),
            Op(InstructionKind.Read, 3, "child"), Op(InstructionKind.Dispose, 4, "owner"));
        Issue(result, "SS0006");
    }

    [Fact]
    public void DisjointFieldLoansDoNotConflict()
    {
        var result = Analyze(New(flags: InstructionFlags.SuppressLeak), Op(InstructionKind.BorrowExclusive, 1, "owner.left", "left"),
            Op(InstructionKind.BorrowExclusive, 2, "owner.right", "right"), Op(InstructionKind.Write, 3, "left"), Op(InstructionKind.Write, 4, "right"));
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void AggregateAndFieldLoanOverlap()
    {
        var result = Analyze(New(flags: InstructionFlags.SuppressLeak), Op(InstructionKind.BorrowShared, 1, "owner", "whole"),
            Op(InstructionKind.BorrowExclusive, 2, "owner.left", "left"), Op(InstructionKind.Read, 3, "whole"), Op(InstructionKind.Read, 4, "left"));
        Issue(result, "SS0006");
    }

    [Fact]
    public void MovingOneFieldKeepsSiblingFieldUsableButInvalidatesAggregate()
    {
        var result = Analyze(New(flags: InstructionFlags.SuppressLeak), Op(InstructionKind.Move, 1, "owner.left", "moved"),
            Op(InstructionKind.Read, 2, "owner.right"), Op(InstructionKind.Read, 3, "owner"), Op(InstructionKind.Read, 4, "moved"));
        Assert.Equal(3, Issue(result, "SS0004").Site.Start);
    }

    [Theory]
    [InlineData(InstructionKind.Return)]
    [InlineData(InstructionKind.Escape)]
    [InlineData(InstructionKind.Move)]
    public void BorrowedParameterCannotEscapeOrBeConsumed(InstructionKind kind)
    {
        Issue(Analyze(New(ownership: ResourceOwnership.Borrowed), Op(kind, 1, "owner")), "SS0008");
    }

    [Fact]
    public void ExplicitLoanCannotEscape()
    {
        Issue(Analyze(New(), Op(InstructionKind.BorrowShared, 1, "owner", "loan"), Op(InstructionKind.Return, 2, "loan")), "SS0008");
    }

    [Theory]
    [InlineData(ResourceOwnership.Borrowed)]
    [InlineData(ResourceOwnership.Scoped)]
    public void NonOwningDisposalIsRejected(ResourceOwnership ownership)
    {
        Issue(Analyze(New(ownership: ownership), Op(InstructionKind.Dispose, 1, "owner")), "SS0009");
    }

    [Fact]
    public void ExternalUnannotatedParameterAllowsDisposal()
    {
        Assert.Empty(Analyze(New(ownership: ResourceOwnership.External), Op(InstructionKind.Dispose, 1, "owner")).Issues);
    }

    [Fact]
    public void LoanAcrossSuspensionIsReportedAtSuspension()
    {
        var result = Analyze(New(), Op(InstructionKind.BorrowShared, 1, "owner", "loan"), Op(InstructionKind.Suspend, 2),
            Op(InstructionKind.Read, 3, "loan"), Op(InstructionKind.Dispose, 4, "owner"));
        Assert.Equal(2, Issue(result, "SS0010").Site.Start);
    }

    [Fact]
    public void DeadLoanDoesNotCrossSuspension()
    {
        var result = Analyze(New(), Op(InstructionKind.BorrowShared, 1, "owner", "loan"), Op(InstructionKind.Read, 2, "loan"),
            Op(InstructionKind.Suspend, 3), Op(InstructionKind.Dispose, 4, "owner"));
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void ExplicitSuspensionSafeLoanIsPermitted()
    {
        var result = Analyze(New(), Op(InstructionKind.BorrowShared, 1, "owner", "loan", flags: InstructionFlags.AllowsSuspension),
            Op(InstructionKind.Suspend, 2), Op(InstructionKind.Read, 3, "loan"), Op(InstructionKind.Dispose, 4, "owner"));
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void NoSuspendContractChecksLiveOwnedValue()
    {
        var result = Analyze(New(flags: InstructionFlags.NoSuspend), Op(InstructionKind.Suspend, 1), Op(InstructionKind.Dispose, 2, "owner"));
        Issue(result, "SS0016");
    }

    [Fact]
    public void NoSuspendValueDisposedBeforeAwaitIsPermitted()
    {
        Assert.Empty(Analyze(New(flags: InstructionFlags.NoSuspend), Op(InstructionKind.Dispose, 1, "owner"), Op(InstructionKind.Suspend, 2)).Issues);
    }

    [Fact]
    public void ScopeDisposalInvalidatesResolvedService()
    {
        var result = Analyze(New("scope"), New("service", 1, ownership: ResourceOwnership.Scoped, parent: "scope"),
            Op(InstructionKind.Dispose, 2, "scope"), Op(InstructionKind.Read, 3, "service"));
        var issue = Issue(result, "SS0012");
        Assert.Equal(3, issue.Site.Start);
        Assert.Contains(issue.RelatedLocations, site => site.Start == 2);
    }

    [Fact]
    public void NestedScopeInvalidationIsTransitive()
    {
        var result = Analyze(New("scope"), New("nested", 1, ownership: ResourceOwnership.Scoped, parent: "scope"),
            New("service", 2, ownership: ResourceOwnership.Scoped, parent: "nested"), Op(InstructionKind.Dispose, 3, "scope"), Op(InstructionKind.Read, 4, "service"));
        Issue(result, "SS0012");
    }

    [Fact]
    public void ScopeServiceCannotEscape()
    {
        Issue(Analyze(New("scope"), New("service", 1, ownership: ResourceOwnership.Scoped, parent: "scope"),
            Op(InstructionKind.Return, 2, "service"), Op(InstructionKind.Dispose, 3, "scope")), "SS0012");
    }

    [Fact]
    public void ScopeServiceMayBeUsedBeforeScopeDisposal()
    {
        Assert.Empty(Analyze(New("scope"), New("service", 1, ownership: ResourceOwnership.Scoped, parent: "scope"),
            Op(InstructionKind.Read, 2, "service"), Op(InstructionKind.Dispose, 3, "scope")).Issues);
    }

    [Fact]
    public void OwnedReturnDischargesLeakObligation()
    {
        Assert.Empty(Analyze(New(), Op(InstructionKind.Return, 1, "owner")).Issues);
    }

    [Fact]
    public void ReturnAfterExplicitCleanupRejectsDisposedResult()
    {
        Issue(Analyze(New(), Op(InstructionKind.Alias, 1, "owner", "$return"), Op(InstructionKind.Dispose, 2, "owner"),
            Op(InstructionKind.Return, 3, "$return")), "SS0001");
    }

    [Fact]
    public void EscapeTransfersLocalResponsibilityWhilePreservingAliasReadability()
    {
        Assert.Empty(Analyze(New(), Op(InstructionKind.Escape, 1, "owner", flags: InstructionFlags.Transfer), Op(InstructionKind.Read, 2, "owner")).Issues);
    }

    [Fact]
    public void ReassignmentReportsLostLastOwner()
    {
        var result = Analyze(New(id: "first"), New(offset: 1, id: "second"), Op(InstructionKind.Dispose, 2, "owner"));
        Assert.Equal(1, Issue(result, "SS0011").Site.Start);
        Assert.Equal("first", Issue(result, "SS0003").ResourceId);
    }

    [Fact]
    public void RetainedAliasPreventsLostOwnerDiagnostic()
    {
        var result = Analyze(New(id: "first"), Op(InstructionKind.Alias, 1, "owner", "alias"), New(offset: 2, id: "second"),
            Op(InstructionKind.Dispose, 3, "owner"), Op(InstructionKind.Dispose, 4, "alias"));
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void ForgettingLastOwnerReportsOverwriteAndLeak()
    {
        var result = Analyze(New(), Op(InstructionKind.Forget, 1, target: "owner"));
        Issue(result, "SS0011");
        Issue(result, "SS0003");
    }

    [Fact]
    public void ConditionalDisposalIsPossible()
    {
        var result = Analyze(New(), Op(InstructionKind.Dispose, 1, "owner", flags: InstructionFlags.Conditional), Op(InstructionKind.Read, 2, "owner"));
        Assert.Equal(IssueCertainty.Possible, Issue(result, "SS0001").Certainty);
        Assert.Equal(IssueCertainty.Possible, Issue(result, "SS0003").Certainty);
    }

    [Theory]
    [InlineData(InstructionKind.Return)]
    [InlineData(InstructionKind.Dispose)]
    [InlineData(InstructionKind.Move)]
    public void MutuallyExclusiveAllocationsKeepExistenceCorrelation(InstructionKind consume)
    {
        var result = AnalyzeBlocks(Block(0, [1, 2]), Block(1, [3], New(id: "a")), Block(2, [3], New(id: "b")),
            Block(3, [], Op(consume, 3, "owner")));
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void ConditionalAliasDoesNotDisposeBothExistingResources()
    {
        var result = AnalyzeBlocks(Block(0, [1, 2], New("a", 0), New("b", 1)),
            Block(1, [3], Op(InstructionKind.Alias, 2, "a", "selected")), Block(2, [3], Op(InstructionKind.Alias, 3, "b", "selected")),
            Block(3, [], Op(InstructionKind.Dispose, 4, "selected")));
        Assert.Equal(2, result.Issues.Count(issue => issue.Id == "SS0003"));
        Assert.All(result.Issues, issue => Assert.Equal(IssueCertainty.Possible, issue.Certainty));
    }

    [Fact]
    public void BudgetFailureIsExplicitAndDoesNotPublishUnstableSafetyFindings()
    {
        var graph = new AnalysisGraph("M", [Block(0, [1], New()), Block(1, [])], 0, Site(100));
        var result = new OwnershipAnalysisEngine().Analyze(graph, new(maxIterations: 1));
        Assert.True(result.BudgetExceeded);
        Assert.Equal("SS0015", Assert.Single(result.Issues).Id);
    }

    [Fact]
    public void ResourceBudgetFailureIsExplicit()
    {
        var graph = new AnalysisGraph("M", [Block(0, [], New("a"), New("b", 1))], 0, Site(100));
        Assert.True(new OwnershipAnalysisEngine().Analyze(graph, new(maxTrackedResources: 1)).BudgetExceeded);
    }

    [Fact]
    public void CancellationIsObserved()
    {
        var graph = new AnalysisGraph("M", [Block(0, [], New())], 0, Site(100));
        Assert.Throws<OperationCanceledException>(() => new OwnershipAnalysisEngine().Analyze(graph, cancellationToken: new CancellationToken(true)));
    }

    [Fact]
    public void SnapshotsAreOptionalAndContainNoStatesWhenDisabled()
    {
        var graph = new AnalysisGraph("M", [Block(0, [], New())], 0, Site(100));
        Assert.Empty(new OwnershipAnalysisEngine().Analyze(graph).Blocks);
    }

    [Fact]
    public void PossibleIssuePolicyDoesNotSuppressDefiniteIssues()
    {
        var graph = new AnalysisGraph("M", [Block(0, [], New(), Op(InstructionKind.Dispose, 1, "owner", flags: InstructionFlags.Conditional),
            Op(InstructionKind.Read, 2, "owner"))], 0, Site(100));
        Assert.Empty(new OwnershipAnalysisEngine().Analyze(graph, new(reportPossibleIssues: false)).Issues);
    }

    [Fact]
    public void InvalidGraphIsRejectedBeforeAnalysis()
    {
        Assert.Throws<ArgumentException>(() => new AnalysisGraph("M", [Block(0, [42])], 0, Site(100)));
        Assert.Throws<ArgumentException>(() => new AnalysisGraph("M", [Block(0, []), Block(0, [])], 0, Site(100)));
    }

    [Fact]
    public void EngineIsReentrantAndDeterministic()
    {
        var engine = new OwnershipAnalysisEngine();
        var graph = new AnalysisGraph("M", [Block(0, [], New(), Op(InstructionKind.Dispose, 1, "owner"), Op(InstructionKind.Read, 2, "owner"))], 0, Site(100));
        var results = Enumerable.Range(0, 16).AsParallel().Select(_ => engine.Analyze(graph)).ToArray();
        Assert.All(results, result => Assert.Equal("SS0001", Assert.Single(result.Issues).Id));
        Assert.Single(results.Select(result => result.Iterations).Distinct());
    }

    [Fact]
    public void NullGuardEliminatesImpossibleSkipDisposalForNewResource()
    {
        var result = AnalyzeBlocks(Block(0, [1, 2], New()),
            Block(1, [3], Op(InstructionKind.AssumeNull, 1, "owner")),
            Block(2, [3], Op(InstructionKind.AssumeNotNull, 2, "owner"), Op(InstructionKind.Dispose, 3, "owner")), Block(3, []));
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void NullableResourceNullGuardDisposesEveryExistingInstance()
    {
        var result = AnalyzeBlocks(Block(0, [1, 2], New(flags: InstructionFlags.MayBeNull)),
            Block(1, [3], Op(InstructionKind.AssumeNull, 1, "owner")),
            Block(2, [3], Op(InstructionKind.AssumeNotNull, 2, "owner"), Op(InstructionKind.Dispose, 3, "owner")), Block(3, []));
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void DistinctNonNullBranchAllocationsRemainNonNullAfterJoin()
    {
        var result = AnalyzeBlocks(Block(0, [1, 2]), Block(1, [3], New(id: "a")), Block(2, [3], New(id: "b")),
            Block(3, [4, 5]), Block(4, [6], Op(InstructionKind.AssumeNull, 4, "owner")),
            Block(5, [6], Op(InstructionKind.AssumeNotNull, 5, "owner"), Op(InstructionKind.Dispose, 6, "owner")), Block(6, []));
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void UnknownArrayIndicesConservativelyOverlap()
    {
        var result = Analyze(New(flags: InstructionFlags.SuppressLeak), Op(InstructionKind.BorrowExclusive, 1, "owner[0]", "first"),
            Op(InstructionKind.BorrowExclusive, 2, "owner[*]", "second"), Op(InstructionKind.Read, 3, "first"), Op(InstructionKind.Read, 4, "second"));
        Issue(result, "SS0006");
    }

    [Fact]
    public void ConsumingProjectedFieldDoesNotDischargeContainingResource()
    {
        Issue(Analyze(New(), Op(InstructionKind.Move, 1, "owner.field")), "SS0003");
    }

    [Fact]
    public void PrunedNullBranchSnapshotExplicitlyMarksInfeasibility()
    {
        var result = AnalyzeBlocks(Block(0, [1, 2], New()), Block(1, [], Op(InstructionKind.AssumeNull, 1, "owner")),
            Block(2, [], Op(InstructionKind.AssumeNotNull, 2, "owner"), Op(InstructionKind.Dispose, 3, "owner")));
        var nullBranch = Assert.Single(result.Blocks, block => block.Id == 1);
        Assert.True(nullBranch.Entry.IsReachable);
        Assert.False(nullBranch.Exit.IsReachable);
        Assert.Empty(result.Issues);
    }

    [Theory]
    [InlineData(InstructionKind.Dispose, InstructionKind.Read)]
    [InlineData(InstructionKind.Move, InstructionKind.Read)]
    [InlineData(InstructionKind.Dispose, InstructionKind.Move)]
    [InlineData(InstructionKind.Move, InstructionKind.Move)]
    public void JoinResultsAreIndependentOfPredecessorEnumerationOrder(InstructionKind left, InstructionKind right)
    {
        var blocks = new[] {
            Block(0, [1, 2], New(flags: InstructionFlags.SuppressLeak)),
            Block(1, [3], Op(left, 1, "owner")), Block(2, [3], Op(right, 2, "owner")),
            Block(3, [], Op(InstructionKind.Read, 3, "owner")) };
        static string Signature(AnalysisIssue issue) => issue.Id + ":" + issue.Site.Start + ":" + issue.Certainty + ":" + issue.ResourceId;
        var original = AnalyzeBlocks(blocks).Issues.Select(Signature).ToArray();
        var reversed = AnalyzeBlocks(blocks.Reverse().ToArray()).Issues.Select(Signature).ToArray();
        Assert.Equal(original, reversed);
    }
}
