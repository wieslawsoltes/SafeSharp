using SafeSharp.Core;

namespace SafeSharp.Tests.Core;

public sealed class CompletedAsyncResultTests
{
    private static SourceSite Site(int offset) => new("completed-result.cs", offset, 1, offset, 0);
    private static FlowInstruction Op(InstructionKind kind, int offset, string? source = null, string? target = null) => new(kind, Site(offset), target, source);
    private static FlowInstruction New(InstructionFlags flags = InstructionFlags.None) => new(InstructionKind.Allocate, Site(0), "value", resourceId: "original", flags: flags);
    private static FlowInstruction Bind(string source = "value", bool pending = false) => new(InstructionKind.BindAsyncResult, Site(2), "task", source,
        resourceId: "carrier", flags: pending ? InstructionFlags.DeferredAsyncResult : InstructionFlags.None);
    private static FlowInstruction Receive() => Op(InstructionKind.ReceiveAsyncResult, 4, "task", "received");
    private static AnalysisResult Analyze(params FlowInstruction[] instructions) => new OwnershipAnalysisEngine().Analyze(
        new AnalysisGraph("CompletedResult", [new FlowBlock(0, instructions, [])], 0, Site(100)), new AnalysisOptions(captureSnapshots: true));

    [Fact]
    public void BindingAnExistingOutputDoesNotCreateAnotherResource()
    {
        var result = Analyze(New(), Bind(), Receive(), Op(InstructionKind.Dispose, 5, "received"));
        Assert.Empty(result.Issues);
        var resources = result.Blocks[0].Exit.Resources;
        Assert.Equal(2, resources.Length);
        Assert.Equal("original", resources.Single(resource => resource.Kind == ResourceKind.AsyncFactoryCarrier).ProducedResourceId);
        Assert.Equal(ResourceStatus.Disposed, resources.Single(resource => resource.Id == "original").Status);
    }

    [Fact]
    public void ACompletedProducerDoesNotHideAnIgnoredExistingOwnedOutput()
    {
        var result = Analyze(New(), Bind());
        var issue = Assert.Single(result.Issues);
        Assert.Equal("SS0003", issue.Id);
        Assert.Equal(IssueCertainty.Definite, issue.Certainty);
        Assert.Equal("original", issue.ResourceId);
    }

    [Fact]
    public void TheOriginalAndReceivedAliasesShareDisposalState()
    {
        var result = Analyze(New(), Bind(), Op(InstructionKind.Dispose, 3, "value"), Receive(), Op(InstructionKind.Read, 5, "received"));
        Assert.Equal("SS0001", Assert.Single(result.Issues).Id);
    }

    [Fact]
    public void MovingTheOriginalInvalidatesTheBoundResult()
    {
        var result = Analyze(New(), Bind(), Op(InstructionKind.Move, 3, "value", "owner"), Receive(), Op(InstructionKind.Dispose, 5, "owner"));
        Assert.Contains(result.Issues, issue => issue.Id == "SS0004" && issue.Site.Start == 4);
        Assert.DoesNotContain(result.Issues, issue => issue.Id == "SS0003");
    }

    [Theory]
    [InlineData(InstructionKind.Return)]
    [InlineData(InstructionKind.Move)]
    [InlineData(InstructionKind.Escape)]
    public void TransferringACompletedProducerTransfersTheExistingResource(InstructionKind transfer)
    {
        Assert.Empty(Analyze(New(), Bind(), Op(transfer, 3, "task")).Issues);
    }

    [Fact]
    public void BorrowedPayloadLoanRemainsLiveThroughItsCarrier()
    {
        var result = Analyze(New(), Op(InstructionKind.BorrowShared, 1, "value", "loan"), Bind("loan"),
            Op(InstructionKind.Dispose, 3, "value"), Receive(), Op(InstructionKind.Read, 5, "received"));
        Assert.Contains(result.Issues, issue => issue.Id == "SS0014" && issue.Site.Start == 3);
        Assert.Contains(result.Issues, issue => issue.Id == "SS0001" && issue.Site.Start == 5);
    }

    [Fact]
    public void BorrowedPayloadCannotBecomeOwnedByBeingWrappedAndAwaited()
    {
        var result = Analyze(New(), Op(InstructionKind.BorrowShared, 1, "value", "loan"), Bind("loan"), Receive(),
            Op(InstructionKind.Dispose, 5, "received"));
        Assert.Contains(result.Issues, issue => issue.Id == "SS0009" && issue.Site.Start == 5);
    }

    [Fact]
    public void BorrowedPayloadCannotEscapeInsideACompletedTask()
    {
        var result = Analyze(New(), Op(InstructionKind.BorrowShared, 1, "value", "loan"), Bind("loan"), Op(InstructionKind.Return, 3, "task"));
        Assert.Contains(result.Issues, issue => issue.Id == "SS0008" && issue.Site.Start == 3);
    }

    [Fact]
    public void BorrowedPayloadStaysLiveAcrossSuspensionThroughTheTask()
    {
        var result = Analyze(New(), Op(InstructionKind.BorrowShared, 1, "value", "loan"), Bind("loan"),
            Op(InstructionKind.Suspend, 3), Receive(), Op(InstructionKind.Read, 5, "received"), Op(InstructionKind.Dispose, 6, "value"));
        Assert.Contains(result.Issues, issue => issue.Id == "SS0010" && issue.Site.Start == 3);
    }

    [Fact]
    public void AnAlreadyAvailableNoSuspendPayloadIsLiveBeforeTheFirstReceive()
    {
        var result = Analyze(New(InstructionFlags.NoSuspend), Bind(), Op(InstructionKind.Suspend, 3), Receive(), Op(InstructionKind.Dispose, 5, "received"));
        Assert.Contains(result.Issues, issue => issue.Id == "SS0016" && issue.Site.Start == 3);
    }

    [Fact]
    public void AnExplicitNullResultCreatesNoOwnedResource()
    {
        var result = Analyze(Op(InstructionKind.Forget, 0, target: "null"), Bind("null"), Receive());
        Assert.Empty(result.Issues);
        Assert.Empty(Assert.Single(result.Blocks[0].Exit.Resources).ProducedResourceIds);
        var received = result.Blocks[0].Exit.Places.Single(place => place.Place == "received");
        Assert.True(received.MayBeNull);
        Assert.Empty(received.References);
    }

    [Fact]
    public void AResultPromiseDoesNotClaimThatExecutionHasCompleted()
    {
        var external = new FlowInstruction(InstructionKind.Allocate, Site(0), "value", resourceId: "external", ownership: ResourceOwnership.External);
        var result = Analyze(external, Op(InstructionKind.BorrowShared, 1, "value", "loan"), Bind("loan", pending: true));
        Assert.Empty(result.Issues);
        var carrier = result.Blocks[0].Exit.Resources.Single(resource => resource.Kind == ResourceKind.AsyncFactoryCarrier);
        Assert.Equal(ResourceStatus.Pending, carrier.Status);
        Assert.Equal(AsyncResultState.Materialized, carrier.ResultState);
        Assert.Equal("external", carrier.ProducedResourceId);
    }

    [Fact]
    public void AFailedBorrowedResultPromiseDoesNotCreateAnOwnedOutput()
    {
        var external = new FlowInstruction(InstructionKind.Allocate, Site(0), "value", resourceId: "external", ownership: ResourceOwnership.External);
        var result = Analyze(external, Op(InstructionKind.BorrowShared, 1, "value", "loan"), Bind("loan", pending: true),
            Op(InstructionKind.ObserveAsyncFactory, 3, "task"));
        Assert.Empty(result.Issues);
        Assert.Equal(ResourceOwnership.External, Assert.Single(result.Blocks[0].Exit.Resources, resource => resource.Kind == ResourceKind.Resource).Ownership);
        Assert.Equal(ResourceStatus.Completed, Assert.Single(result.Blocks[0].Exit.Resources, resource => resource.Kind == ResourceKind.AsyncFactoryCarrier).Status);
    }

    [Fact]
    public void ALoanRemainsLiveWhileAnAsynchronousResultPromiseCanRecoverIt()
    {
        var result = Analyze(New(), Op(InstructionKind.BorrowShared, 1, "value", "loan"), Bind("loan", pending: true),
            Op(InstructionKind.Dispose, 3, "value"), Receive(), Op(InstructionKind.Read, 5, "received"));
        Assert.Contains(result.Issues, issue => issue.Id == "SS0014" && issue.Site.Start == 3);
        Assert.Contains(result.Issues, issue => issue.Id == "SS0001" && issue.Site.Start == 5);
    }

    [Fact]
    public void BorrowingAnOpaqueTaskDoesNotMakeItsOutputAliasTheTask()
    {
        var external = new FlowInstruction(InstructionKind.Allocate, Site(0), "opaque", resourceId: "external-task", ownership: ResourceOwnership.External);
        var result = Analyze(external, Op(InstructionKind.BorrowShared, 1, "opaque", "task"), Receive(),
            Op(InstructionKind.Dispose, 5, "received"), Op(InstructionKind.Read, 6, "opaque"));
        Assert.Empty(result.Issues);
        var received = result.Blocks[0].Exit.Places.Single(place => place.Place == "received");
        Assert.Null(Assert.Single(received.References).LoanId);
        Assert.NotEqual("external-task", Assert.Single(received.References).ResourceId);
        Assert.Equal(ResourceOwnership.External, result.Blocks[0].Exit.Resources.Single(resource => resource.Id == received.References[0].ResourceId).Ownership);
    }

    [Fact]
    public void FinalBorrowedReturnValidationReadsTheCachedResultAfterCleanup()
    {
        var returned = new FlowInstruction(InstructionKind.Read, Site(4), source: "task", flags: InstructionFlags.ValidateReturnedResult);
        var result = Analyze(New(), Bind(), Op(InstructionKind.Dispose, 3, "value"), returned);
        Assert.Equal("SS0001", Assert.Single(result.Issues).Id);
    }

    [Fact]
    public void ReadingTaskMetadataDoesNotReadAnAlreadyDisposedOutput()
    {
        Assert.Empty(Analyze(New(), Bind(), Op(InstructionKind.Dispose, 3, "value"), Op(InstructionKind.Read, 4, "task")).Issues);
    }
}
