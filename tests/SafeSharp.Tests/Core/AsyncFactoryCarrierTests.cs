using SafeSharp.Core;

namespace SafeSharp.Tests.Core;

public sealed class AsyncFactoryCarrierTests
{
    private static SourceSite Site(int offset) => new("async-factory.cs", offset, 1, offset, 0);
    private static FlowInstruction Op(InstructionKind kind, int offset, string? source = null, string? target = null,
        InstructionFlags flags = InstructionFlags.None) => new(kind, Site(offset), target, source, flags: flags);
    private static FlowInstruction Factory(string place = "task", int offset = 0, InstructionFlags flags = InstructionFlags.None) =>
        new(InstructionKind.BeginAsyncFactory, Site(offset), place, resourceId: "factory:" + offset, flags: flags);
    private static FlowInstruction Receive(string source = "task", string target = "result", int offset = 2) =>
        new(InstructionKind.ReceiveAsyncResult, Site(offset), target, source, resourceId: "opaque:" + offset);
    private static FlowBlock Block(int id, int[] successors, params FlowInstruction[] instructions) => new(id, instructions, successors);
    private static AnalysisResult Analyze(params FlowInstruction[] instructions) => AnalyzeBlocks(Block(0, [], instructions));
    private static AnalysisResult AnalyzeBlocks(params FlowBlock[] blocks) => new OwnershipAnalysisEngine().Analyze(
        new AnalysisGraph("AsyncFactory", blocks, 0, Site(100)), new AnalysisOptions(captureSnapshots: true));

    [Fact]
    public void PendingFactoryWarnsAboutPossibleProductWithoutAllocatingIt()
    {
        var result = Analyze(Factory());
        var issue = Assert.Single(result.Issues);
        Assert.Equal("SS0003", issue.Id);
        Assert.Equal(IssueCertainty.Possible, issue.Certainty);
        Assert.Contains("may produce", issue.Message);
        Assert.Equal(0, issue.Site.Start);
        var carrier = Assert.Single(result.Blocks[0].Exit.Resources);
        Assert.Equal(ResourceKind.AsyncFactoryCarrier, carrier.Kind);
        Assert.Equal(ResourceStatus.Pending, carrier.Status);
        Assert.Equal(AsyncResultState.Deferred, carrier.ResultState);
        Assert.Null(carrier.ProducedResourceId);
    }

    [Fact]
    public void FaultObservationDoesNotInventSuccessfulPayloadProduction()
    {
        var result = Analyze(Factory(flags: InstructionFlags.NoSuspend), Op(InstructionKind.Suspend, 1),
            Op(InstructionKind.ObserveAsyncFactory, 2, "task"));
        Assert.Empty(result.Issues);
        var carrier = Assert.Single(result.Blocks[0].Exit.Resources);
        Assert.Equal(ResourceStatus.Completed, carrier.Status);
        Assert.Equal(AsyncResultState.Deferred, carrier.ResultState);
    }

    [Fact]
    public void SuccessfulReceptionCreatesASeparateOwnedResult()
    {
        var result = Analyze(Factory(), Op(InstructionKind.ObserveAsyncFactory, 1, "task"), Receive());
        var issue = Assert.Single(result.Issues);
        Assert.Equal("SS0003", issue.Id);
        Assert.Equal(IssueCertainty.Definite, issue.Certainty);
        Assert.Equal(2, issue.Site.Start);
        Assert.Contains(issue.Evidence, evidence => evidence.Kind == "factory-result" && evidence.Site.Start == 2);
        var resources = result.Blocks[0].Exit.Resources;
        var carrier = resources.Single(resource => resource.Kind == ResourceKind.AsyncFactoryCarrier);
        Assert.Equal(AsyncResultState.Materialized, carrier.ResultState);
        var payload = resources.Single(resource => resource.Kind == ResourceKind.Resource);
        Assert.Equal(payload.Id, carrier.ProducedResourceId);
        Assert.Equal(ResourceStatus.Alive, payload.Status);
        Assert.Empty(payload.OwnerResourceIds);
    }

    [Theory]
    [InlineData(InstructionKind.Return)]
    [InlineData(InstructionKind.Escape)]
    [InlineData(InstructionKind.Move)]
    public void TransferringPendingCarrierPassesTheFutureResultObligation(InstructionKind kind)
    {
        Assert.Empty(Analyze(Factory(), Op(kind, 1, "task")).Issues);
    }

    [Fact]
    public void MovedCarrierCanBeReceivedAtItsNewPlace()
    {
        Assert.Empty(Analyze(Factory(), Op(InstructionKind.Move, 1, "task", "moved"), Receive("moved"),
            Op(InstructionKind.Dispose, 3, "result")).Issues);
    }

    [Theory]
    [InlineData(InstructionKind.Return, ResourceStatus.Transferred)]
    [InlineData(InstructionKind.Escape, ResourceStatus.Escaped)]
    [InlineData(InstructionKind.Move, ResourceStatus.Transferred)]
    public void TransferringMaterializedCarrierAlsoTransfersItsOwnedResult(InstructionKind kind, ResourceStatus status)
    {
        var result = Analyze(Factory(), Receive(), Op(kind, 3, "task"));
        Assert.Empty(result.Issues);
        Assert.Equal(status, result.Blocks[0].Exit.Resources.Single(resource => resource.Kind == ResourceKind.Resource).Status);
    }

    [Fact]
    public void MovingMaterializedCarrierLocallyPreservesDestinationButInvalidatesOlderPayloadAliases()
    {
        var result = Analyze(Factory(), Receive(), Op(InstructionKind.Move, 3, "task", "destination"),
            Receive("destination", "received", 4), Op(InstructionKind.Read, 5, "result"), Op(InstructionKind.Dispose, 6, "received"));
        var issue = Assert.Single(result.Issues);
        Assert.Equal("SS0004", issue.Id);
        Assert.Equal(5, issue.Site.Start);
    }

    [Fact]
    public void ConsumingMaterializedCarrierInvalidatesPreviousPayloadAliases()
    {
        var result = Analyze(Factory(), Receive(), Op(InstructionKind.Move, 3, "task"), Op(InstructionKind.Read, 4, "result"));
        Assert.Equal("SS0004", Assert.Single(result.Issues).Id);
    }

    [Fact]
    public void ReturningTaskWithDisposedResultRetainsDisposedResultDiagnostic()
    {
        var result = Analyze(Factory(), Receive(), Op(InstructionKind.Dispose, 3, "result"), Op(InstructionKind.Return, 4, "task"));
        var issue = Assert.Single(result.Issues);
        Assert.Equal("SS0001", issue.Id);
        Assert.Equal(4, issue.Site.Start);
    }

    [Fact]
    public void TaskResultLoansPreventCarrierOwnershipTransfer()
    {
        var result = Analyze(Factory(), Receive(), Op(InstructionKind.BorrowShared, 3, "result", "loan"),
            Op(InstructionKind.Move, 4, "task"), Op(InstructionKind.Read, 5, "loan"));
        Assert.Contains(result.Issues, issue => issue.Id == "SS0005" && issue.Site.Start == 4);
    }

    [Fact]
    public void ACarrierAliasCannotBeUsedAfterItsOwnershipWasConsumed()
    {
        var result = Analyze(Factory(), Op(InstructionKind.Alias, 1, "task", "alias"), Op(InstructionKind.Move, 2, "task"),
            Receive("alias", offset: 3), Op(InstructionKind.Dispose, 4, "result"));
        Assert.Contains(result.Issues, issue => issue.Id == "SS0004" && issue.Site.Start == 3);
    }

    [Fact]
    public void RepeatedReceptionAliasesOneResultRatherThanCreatingFreshOwnership()
    {
        var result = Analyze(Factory(), Receive(), Receive(target: "again", offset: 3), Op(InstructionKind.Read, 4, "result"),
            Op(InstructionKind.Read, 5, "again"), Op(InstructionKind.Dispose, 6, "again"));
        Assert.Empty(result.Issues);
        var resources = result.Blocks[0].Exit.Resources;
        Assert.Single(resources, resource => resource.Kind == ResourceKind.Resource);
        var places = result.Blocks[0].Exit.Places;
        Assert.Equal(places.Single(place => place.Place == "result").References.Single().ResourceId,
            places.Single(place => place.Place == "again").References.Single().ResourceId);
    }

    [Theory]
    [InlineData(InstructionKind.Read, "SS0001")]
    [InlineData(InstructionKind.Dispose, "SS0002")]
    public void RepeatedReceptionDoesNotResetDisposedResultState(InstructionKind use, string id)
    {
        var result = Analyze(Factory(), Receive(), Op(InstructionKind.Dispose, 3, "result"), Receive(target: "again", offset: 4), Op(use, 5, "again"));
        Assert.Contains(result.Issues, issue => issue.Id == id && issue.Site.Start == 5);
        Assert.DoesNotContain(result.Issues, issue => issue.Id == "SS0003");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedReceptionDoesNotResetMovedResultAvailability(bool localMove)
    {
        var instructions = new List<FlowInstruction> { Factory(), Receive(), Op(InstructionKind.Move, 3, "result", localMove ? "owner" : null),
            Receive(target: "again", offset: 4), Op(InstructionKind.Read, 5, "again") };
        if (localMove) instructions.Add(Op(InstructionKind.Dispose, 6, "owner"));
        var result = Analyze(instructions.ToArray());
        Assert.Contains(result.Issues, issue => issue.Id == "SS0004" && issue.Site.Start == 4);
        Assert.Contains(result.Issues, issue => issue.Id == "SS0004" && issue.Site.Start == 5);
    }

    [Fact]
    public void FactoryCarriesNoSuspendOnlyAfterResultProduction()
    {
        var result = Analyze(Factory(flags: InstructionFlags.NoSuspend), Op(InstructionKind.Suspend, 1), Receive(),
            Op(InstructionKind.Suspend, 3), Receive(target: "again", offset: 4), Op(InstructionKind.Dispose, 5, "again"));
        Assert.DoesNotContain(result.Issues, issue => issue.Id == "SS0016" && issue.Site.Start == 1);
        Assert.Contains(result.Issues, issue => issue.Id == "SS0016" && issue.Site.Start == 3);
    }

    [Fact]
    public void DisposingTaskCarrierDoesNotDischargeTheResultObligation()
    {
        var result = Analyze(Factory(), Op(InstructionKind.Dispose, 1, "task"));
        Assert.Equal("SS0003", Assert.Single(result.Issues).Id);
    }

    [Fact]
    public void DisposalCompletionObservationDoesNotClaimAFactoryResult()
    {
        var result = Analyze(Factory(), Op(InstructionKind.CompleteAsyncDispose, 1, "task"));
        Assert.Equal("SS0003", Assert.Single(result.Issues).Id);
    }

    [Fact]
    public void CarrierOverwriteRetainsThePossibleUnreceivedResultEvidence()
    {
        var result = Analyze(Factory(), Op(InstructionKind.Forget, 1, target: "task"));
        var issue = Assert.Single(result.Issues, issue => issue.Id == "SS0011");
        Assert.Equal(IssueCertainty.Possible, issue.Certainty);
        Assert.Equal(1, issue.Site.Start);
        Assert.Contains(issue.RelatedLocations, location => location.Start == 0);
    }

    [Fact]
    public void AUsableCarrierAliasAllowsOverwritingAnotherCarrierPlace()
    {
        Assert.Empty(Analyze(Factory(), Op(InstructionKind.Alias, 1, "task", "alias"), Op(InstructionKind.Forget, 2, target: "task"),
            Receive("alias", offset: 3), Op(InstructionKind.Dispose, 4, "result")).Issues);
    }

    [Fact]
    public void ConditionalCarrierOriginProducesOnlyItsCorrespondingConditionalPayload()
    {
        var result = AnalyzeBlocks(Block(0, [1, 2]), Block(1, [3], Factory("choice", 1)), Block(2, [3], Factory("choice", 2)),
            Block(3, [], Op(InstructionKind.ObserveAsyncFactory, 3, "choice"), Receive("choice", offset: 4), Op(InstructionKind.Dispose, 5, "result")));
        Assert.Empty(result.Issues);
        var payloads = result.Blocks.Single(block => block.Id == 3).Exit.Resources.Where(resource => resource.Kind == ResourceKind.Resource);
        Assert.Equal(2, payloads.Count());
        Assert.All(payloads, payload => Assert.Equal(ResourceStatus.Disposed | ResourceStatus.Absent, payload.Status));
    }

    [Fact]
    public void ConditionalObservationRetainsPendingAlternative()
    {
        var result = AnalyzeBlocks(Block(0, [1, 2], Factory()),
            Block(1, [3], Op(InstructionKind.ObserveAsyncFactory, 1, "task"), Receive(), Op(InstructionKind.Dispose, 3, "result")),
            Block(2, [3]), Block(3, []));
        var issue = Assert.Single(result.Issues);
        Assert.Equal("SS0003", issue.Id);
        Assert.Equal("factory:0", issue.ResourceId);
        Assert.Equal(IssueCertainty.Possible, issue.Certainty);
    }

    [Fact]
    public void FaultBranchContainsNoMaterializedResult()
    {
        var result = AnalyzeBlocks(Block(0, [1, 2], Factory(), Op(InstructionKind.ObserveAsyncFactory, 1, "task")),
            Block(1, [3], Receive(), Op(InstructionKind.Dispose, 3, "result")), Block(2, [3]), Block(3, []));
        Assert.Empty(result.Issues);
        Assert.DoesNotContain(result.Blocks.Single(block => block.Id == 2).Exit.Resources, resource => resource.Kind == ResourceKind.Resource);
    }

    [Fact]
    public void EveryIterationCanReceiveAndDisposeItsOwnFactoryResult()
    {
        var result = AnalyzeBlocks(Block(0, [1]), Block(1, [1, 2], Factory(), Receive(), Op(InstructionKind.Dispose, 3, "result")), Block(2, []));
        Assert.False(result.BudgetExceeded);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void RepeatedFactoryWithoutReceptionDiagnosesPendingGeneration()
    {
        var result = AnalyzeBlocks(Block(0, [1]), Block(1, [1, 2], Factory()), Block(2, []));
        Assert.False(result.BudgetExceeded);
        Assert.Contains(result.Issues, issue => issue.Id == "SS0011" && issue.Certainty == IssueCertainty.Possible);
    }

    [Fact]
    public void OpaqueTaskPayloadDoesNotAliasItsCarrierOrAssumeOwnership()
    {
        var external = new FlowInstruction(InstructionKind.Allocate, Site(0), "task", resourceId: "external-task", ownership: ResourceOwnership.External);
        var result = Analyze(external, Receive(), Op(InstructionKind.Dispose, 3, "result"), Op(InstructionKind.Read, 4, "task"));
        Assert.Empty(result.Issues);
        var payload = result.Blocks[0].Exit.Resources.Single(resource => resource.Id == "opaque:2");
        Assert.Equal(ResourceOwnership.External, payload.Ownership);
        Assert.Equal(ResourceStatus.Disposed, payload.Status);
        Assert.Equal(ResourceStatus.Alive, result.Blocks[0].Exit.Resources.Single(resource => resource.Id == "external-task").Status);
    }

    [Theory]
    [InlineData(InstructionKind.BeginAsyncFactory, null, null)]
    [InlineData(InstructionKind.ObserveAsyncFactory, null, null)]
    [InlineData(InstructionKind.ReceiveAsyncResult, "task", null)]
    [InlineData(InstructionKind.ReceiveAsyncResult, null, "value")]
    public void FactoryInstructionsValidateRequiredOperands(InstructionKind kind, string? source, string? target)
    {
        Assert.Throws<ArgumentException>(() => Op(kind, 0, source, target));
    }
}
