using SafeSharp.Core;

namespace SafeSharp.Tests.Core;

public sealed class AsyncDisposalCompletionTests
{
    private static SourceSite Site(int offset) => new("async-disposal.cs", offset, 1, offset, 0);
    private static FlowInstruction Op(InstructionKind kind, int offset, string? source = null, string? target = null,
        InstructionFlags flags = InstructionFlags.None) => new(kind, Site(offset), target, source, flags: flags);
    private static FlowInstruction New(string target = "owner", int offset = 0, ResourceOwnership ownership = ResourceOwnership.Owned,
        string? parent = null) => new(InstructionKind.Allocate, Site(offset), target, resourceId: target, ownership: ownership, owner: parent);
    private static FlowInstruction Begin(int offset = 1, string source = "owner", string target = "completion",
        InstructionFlags flags = InstructionFlags.None) => new(InstructionKind.BeginAsyncDispose, Site(offset), target, source,
            resourceId: "completion:" + offset, flags: flags);
    private static FlowInstruction Complete(int offset = 2, string source = "completion", InstructionFlags flags = InstructionFlags.None)
        => Op(InstructionKind.CompleteAsyncDispose, offset, source, flags: flags);
    private static FlowBlock Block(int id, int[] successors, params FlowInstruction[] instructions) => new(id, instructions, successors);
    private static AnalysisResult Analyze(params FlowInstruction[] instructions) => AnalyzeBlocks(Block(0, [], instructions));
    private static AnalysisResult AnalyzeBlocks(params FlowBlock[] blocks) => new OwnershipAnalysisEngine().Analyze(
        new AnalysisGraph("AsyncDisposal", blocks, 0, Site(100)), new AnalysisOptions(captureSnapshots: true));
    private static AnalysisIssue Issue(AnalysisResult result, string id) => Assert.Single(result.Issues, issue => issue.Id == id);

    [Fact]
    public void IgnoredCompletionIsAnObligationSeparateFromOwnerDisposal()
    {
        var result = Analyze(New(), Begin());
        var issue = Assert.Single(result.Issues);
        Assert.Equal("SS0017", issue.Id);
        Assert.Equal(IssueCertainty.Definite, issue.Certainty);
        Assert.Equal(1, issue.Site.Start);
        Assert.Contains(issue.RelatedLocations, site => site.Start == 0);
        Assert.Contains(issue.Evidence, evidence => evidence.Kind == "async-disposal");
        Assert.Contains(issue.Evidence, evidence => evidence.Kind == "exit");
        var resources = result.Blocks[0].Exit.Resources;
        Assert.Equal(ResourceStatus.Disposed, resources.Single(resource => resource.Id == "owner").Status);
        var completion = resources.Single(resource => resource.Kind == ResourceKind.AsyncDisposalCompletion);
        Assert.Equal(ResourceStatus.Pending, completion.Status);
        Assert.Equal(new[] { "owner" }, completion.DisposedResourceIds);
        Assert.Empty(completion.OwnerResourceIds);
    }

    [Fact]
    public void ObservedCompletionDischargesTheCompletionObligation()
    {
        var result = Analyze(New(), Begin(), Complete());
        Assert.Empty(result.Issues);
        var completion = result.Blocks[0].Exit.Resources.Single(resource => resource.Kind == ResourceKind.AsyncDisposalCompletion);
        Assert.Equal(ResourceStatus.Completed, completion.Status);
    }

    [Fact]
    public void OwnerIsUnavailableImmediatelyAfterInvocationBeforeObservation()
    {
        var result = Analyze(New(), Begin(), Op(InstructionKind.Read, 2, "owner"), Complete(3));
        var issue = Assert.Single(result.Issues);
        Assert.Equal("SS0001", issue.Id);
        Assert.Equal(2, issue.Site.Start);
    }

    [Fact]
    public void CompletionCanBeObservedThroughMultipleOrdinaryAliases()
    {
        var result = Analyze(New(), Begin(), Op(InstructionKind.Alias, 2, "completion", "configured"),
            Op(InstructionKind.Alias, 3, "configured", "asTask"), Complete(4, "asTask"), Op(InstructionKind.Read, 5, "completion"));
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void CompletedTaskCanBeObservedAgainWithoutInventingValueTaskMultiplicityRules()
    {
        Assert.Empty(Analyze(New(), Begin(), Complete(), Complete(3)).Issues);
    }

    [Theory]
    [InlineData(InstructionKind.Return, ResourceStatus.Transferred)]
    [InlineData(InstructionKind.Escape, ResourceStatus.Escaped)]
    [InlineData(InstructionKind.Move, ResourceStatus.Transferred)]
    public void BoundaryTransferPassesTheCompletionObligationToTheReceiver(InstructionKind kind, ResourceStatus expected)
    {
        var result = Analyze(New(), Begin(), Op(kind, 2, "completion"));
        Assert.Empty(result.Issues);
        Assert.Equal(expected, result.Blocks[0].Exit.Resources.Single(resource => resource.Kind == ResourceKind.AsyncDisposalCompletion).Status);
    }

    [Fact]
    public void LocalMoveRetainsObligationAtDestination()
    {
        var result = Analyze(New(), Begin(), Op(InstructionKind.Move, 2, "completion", "destination"), Complete(3, "destination"));
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void ReadingCompletionThroughMovedAliasRemainsAnOwnershipViolation()
    {
        var result = Analyze(New(), Begin(), Op(InstructionKind.Move, 2, "completion", "destination"),
            Complete(3, "destination"), Op(InstructionKind.Read, 4, "completion"));
        Assert.Equal(4, Issue(result, "SS0004").Site.Start);
        Assert.DoesNotContain(result.Issues, issue => issue.Id == "SS0017");
    }

    [Fact]
    public void DisposingTheAwaitableDoesNotObserveItsCompletion()
    {
        var result = Analyze(New(), Begin(), Op(InstructionKind.Dispose, 2, "completion"));
        Issue(result, "SS0017");
        Assert.DoesNotContain(result.Issues, issue => issue.Id == "SS0003");
    }

    [Fact]
    public void CompletionCanBeObservedAfterTheOwningScopeHasBeenDisposed()
    {
        var result = Analyze(New("scope"), Begin(source: "scope"), Complete());
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void BeginningScopeDisposalInvalidatesScopeServicesBeforeCompletion()
    {
        var result = Analyze(New("scope"), New("service", 1, ResourceOwnership.Scoped, "scope"),
            Begin(2, "scope"), Op(InstructionKind.Read, 3, "service"), Complete(4));
        Assert.Equal(3, Issue(result, "SS0012").Site.Start);
        Assert.DoesNotContain(result.Issues, issue => issue.Id == "SS0017");
    }

    [Fact]
    public void BorrowedOwnerDisposalStillViolatesItsOwnershipContract()
    {
        var result = Analyze(New(ownership: ResourceOwnership.Borrowed), Begin(), Complete());
        Assert.Equal(1, Issue(result, "SS0009").Site.Start);
    }

    [Fact]
    public void BeginningDisposalDuringLiveLoanIsRejected()
    {
        var result = Analyze(New(), Op(InstructionKind.BorrowShared, 1, "owner", "reader"), Begin(2),
            Op(InstructionKind.Read, 3, "reader"), Complete(4));
        Assert.Equal(2, Issue(result, "SS0014").Site.Start);
    }

    [Fact]
    public void SecondDisposalInvocationStillReportsDoubleDisposal()
    {
        var result = Analyze(New(), Begin(), Complete(), Begin(3), Complete(4));
        Assert.Equal(3, Issue(result, "SS0002").Site.Start);
        Assert.DoesNotContain(result.Issues, issue => issue.Id == "SS0017");
    }

    [Fact]
    public void ObservationOnOneBranchLeavesPossiblePendingCompletion()
    {
        var result = AnalyzeBlocks(Block(0, [1, 2], New(), Begin()), Block(1, [3], Complete()), Block(2, [3]), Block(3, []));
        Assert.Equal(IssueCertainty.Possible, Issue(result, "SS0017").Certainty);
        var completion = result.Blocks.Single(block => block.Id == 3).Entry.Resources.Single(resource => resource.Kind == ResourceKind.AsyncDisposalCompletion);
        Assert.Equal(ResourceStatus.Pending | ResourceStatus.Completed, completion.Status);
    }

    [Fact]
    public void ObservationOnEveryBranchProvesTheObligationDischarged()
    {
        var result = AnalyzeBlocks(Block(0, [1, 2], New(), Begin()), Block(1, [3], Complete()), Block(2, [3], Complete(3)), Block(3, []));
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void CompletionOrTransferOnEveryBranchProvesTheObligationDischarged()
    {
        var result = AnalyzeBlocks(Block(0, [1, 2], New(), Begin()), Block(1, [3], Complete()),
            Block(2, [3], Op(InstructionKind.Return, 3, "completion")), Block(3, []));
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void ConditionalObservationPreservesPendingAlternative()
    {
        var result = Analyze(New(), Begin(), Complete(flags: InstructionFlags.Conditional));
        Assert.Equal(IssueCertainty.Possible, Issue(result, "SS0017").Certainty);
    }

    [Fact]
    public void BranchLocalCompletionsObservedAfterJoinDoNotInventLeaks()
    {
        var result = AnalyzeBlocks(Block(0, [1, 2]),
            Block(1, [3], New("left"), Begin(1, "left")),
            Block(2, [3], New("right", 2), Begin(3, "right")), Block(3, [], Complete(4)));
        Assert.Empty(result.Issues);
        Assert.All(result.Blocks.Single(block => block.Id == 3).Exit.Resources.Where(resource => resource.Kind == ResourceKind.AsyncDisposalCompletion),
            resource => Assert.Equal(ResourceStatus.Absent | ResourceStatus.Completed, resource.Status));
    }

    [Fact]
    public void BranchLocalCompletionCanBeReturnedAfterJoin()
    {
        var result = AnalyzeBlocks(Block(0, [1, 2]),
            Block(1, [3], New("left"), Begin(1, "left")),
            Block(2, [3], New("right", 2), Begin(3, "right")), Block(3, [], Op(InstructionKind.Return, 4, "completion")));
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void ObservationThroughConditionalAliasDoesNotDischargeEveryPath()
    {
        var result = AnalyzeBlocks(Block(0, [1, 2], New(), Begin()),
            Block(1, [3], Op(InstructionKind.Alias, 2, "completion", "alias")), Block(2, [3], Op(InstructionKind.Forget, 3, target: "alias")),
            Block(3, [], Complete(4, "alias")));
        Assert.Equal(IssueCertainty.Possible, Issue(result, "SS0017").Certainty);
    }

    [Fact]
    public void OverwritingPendingCompletionReportsAtOverwriteWithOriginEvidence()
    {
        var result = Analyze(New(), Begin(), Op(InstructionKind.Forget, 2, target: "completion"));
        var overwrite = Assert.Single(result.Issues, issue => issue.Id == "SS0017" && issue.Site.Start == 2);
        Assert.Equal(IssueCertainty.Definite, overwrite.Certainty);
        Assert.Contains(overwrite.RelatedLocations, site => site.Start == 1);
    }

    [Fact]
    public void DeadSyntheticAliasCannotHideLostCompletionAtOverwrite()
    {
        var result = Analyze(New(), Begin(target: "$temporary"), Op(InstructionKind.Alias, 2, "$temporary", "completion"),
            Op(InstructionKind.Forget, 3, target: "completion"));
        Assert.Contains(result.Issues, issue => issue.Id == "SS0017" && issue.Site.Start == 3);
    }

    [Fact]
    public void LiveAliasPermitsOverwritingOriginalCompletionVariable()
    {
        var result = Analyze(New(), Begin(), Op(InstructionKind.Alias, 2, "completion", "alias"),
            Op(InstructionKind.Forget, 3, target: "completion"), Complete(4, "alias"));
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void OverwritingAlreadyObservedCompletionIsSafe()
    {
        Assert.Empty(Analyze(New(), Begin(), Complete(), Op(InstructionKind.Forget, 3, target: "completion")).Issues);
    }

    [Fact]
    public void StartingAnotherDisposalIntoSameVariablePreservesTheOldUnobservedObligation()
    {
        var result = Analyze(New("first"), New("second", 1), Begin(2, "first"), Begin(3, "second"), Complete(4));
        Assert.Contains(result.Issues, issue => issue.Id == "SS0017" && issue.Site.Start == 3 && issue.ResourceId == "completion:2");
        Assert.Contains(result.Issues, issue => issue.Id == "SS0017" && issue.Site.Start == 2);
        Assert.DoesNotContain(result.Issues, issue => issue.Id == "SS0003");
    }

    [Fact]
    public void LoopWithObservationEveryIterationReachesSafeFixedPoint()
    {
        var result = AnalyzeBlocks(Block(0, [1]), Block(1, [1, 2], New(), Begin(), Complete()), Block(2, []));
        Assert.Empty(result.Issues);
        Assert.False(result.BudgetExceeded);
        Assert.InRange(result.Iterations, 3, 20);
    }

    [Fact]
    public void LoopCannotHideUnobservedEarlierCompletionByObservingOnlyTheLastOne()
    {
        var result = AnalyzeBlocks(Block(0, [1]), Block(1, [1, 2], New(), Begin()), Block(2, [], Complete()));
        Assert.Contains(result.Issues, issue => issue.Id == "SS0017" && issue.Site.Start == 1);
        Assert.False(result.BudgetExceeded);
    }

    [Fact]
    public void CompletionCanRemainPendingAcrossSuspensionUntilAwaited()
    {
        var result = Analyze(New(), Begin(), Op(InstructionKind.Suspend, 2), Complete(3));
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void UnreachableInitiationCreatesNoObligation()
    {
        var result = AnalyzeBlocks(Block(0, []), Block(1, [], New(), Begin()));
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void SynchronousThrowFromDisposalHasNoPhantomCompletionObligation()
    {
        var result = AnalyzeBlocks(Block(0, [1, 2], New(), Op(InstructionKind.Dispose, 1, "owner", flags: InstructionFlags.AsyncDispose),
                Op(InstructionKind.Nop, 1, flags: InstructionFlags.MayThrow)),
            Block(1, [3], Begin(flags: InstructionFlags.DisposalAlreadyInvoked), Complete()),
            Block(2, [3]), Block(3, []));
        Assert.Empty(result.Issues);
        Assert.DoesNotContain(result.Blocks.Single(block => block.Id == 2).Exit.Resources,
            resource => resource.Kind == ResourceKind.AsyncDisposalCompletion);
    }

    [Fact]
    public void SplitInvocationRetainsDefiniteDoubleDisposalAtSharedSourceSite()
    {
        var result = Analyze(New(), Op(InstructionKind.Dispose, 1, "owner"),
            Op(InstructionKind.Dispose, 2, "owner", flags: InstructionFlags.AsyncDispose),
            Begin(2, flags: InstructionFlags.DisposalAlreadyInvoked), Complete(3));
        Assert.Equal(IssueCertainty.Definite, Issue(result, "SS0002").Certainty);
        Assert.DoesNotContain(result.Issues, issue => issue.Id == "SS0017");
    }

    [Fact]
    public void AwaitFaultAfterObservationDoesNotLeavePendingCompletion()
    {
        var result = AnalyzeBlocks(Block(0, [1, 2], New(), Begin(), Op(InstructionKind.Suspend, 2), Complete(3),
            Op(InstructionKind.Nop, 3, flags: InstructionFlags.MayThrow)), Block(1, []), Block(2, []));
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void OrdinarySynchronousDisposalRemainsUnchanged()
    {
        var result = Analyze(New(), Op(InstructionKind.Dispose, 1, "owner"));
        Assert.Empty(result.Issues);
        Assert.DoesNotContain(result.Blocks[0].Exit.Resources, resource => resource.Kind == ResourceKind.AsyncDisposalCompletion);
    }

    [Fact]
    public void CompletionInstructionDoesNotDisposeAnOrdinaryResource()
    {
        var result = Analyze(New(), Complete(source: "owner"));
        Issue(result, "SS0003");
    }

    [Fact]
    public void CompletionAllocationCountsTowardAnalysisBudget()
    {
        var graph = new AnalysisGraph("Budget", [Block(0, [], New(), Begin(), Complete())], 0, Site(100));
        var result = new OwnershipAnalysisEngine().Analyze(graph, new AnalysisOptions(maxTrackedResources: 1));
        Assert.True(result.BudgetExceeded);
        Issue(result, "SS0015");
    }

    [Theory]
    [InlineData(InstructionKind.BeginAsyncDispose, null, "completion")]
    [InlineData(InstructionKind.BeginAsyncDispose, "owner", null)]
    [InlineData(InstructionKind.CompleteAsyncDispose, null, null)]
    public void ProtocolInstructionsRequireTheirOperands(InstructionKind kind, string? source, string? target)
    {
        Assert.Throws<ArgumentException>(() => Op(kind, 0, source, target));
    }
}
