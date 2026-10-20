using SafeSharp.Core;

namespace SafeSharp.Tests.Core;

public sealed class AsyncCapabilityCacheTests
{
    private static SourceSite Site(int value) => new("capability-cache.cs", value, 1);
    private static AnalysisResult Analyze(params FlowInstruction[] instructions) => new OwnershipAnalysisEngine().Analyze(
        new AnalysisGraph("Capabilities", [new FlowBlock(0, instructions, [])], 0, Site(100)), new AnalysisOptions(captureSnapshots: true));
    private static FlowInstruction TaskInput(int site = 0) => new(InstructionKind.Allocate, Site(site), "task", resourceId: "task", ownership: ResourceOwnership.External);
    private static FlowInstruction Receive(string target, int site) => new(InstructionKind.ReceiveAsyncResult, Site(site), target, "task");
    private static FlowInstruction TrackResult(string target, int site) => new(InstructionKind.TrackValueTask, Site(site), target, target,
        resourceId: "receive:" + site, owner: "task", flags: InstructionFlags.PreserveExistingCapability);
    private static FlowInstruction Consume(string source, int site) => new(InstructionKind.ConsumeValueTask, Site(site), source: source);

    [Fact]
    public void AnOpaqueTaskHasOneConsumptionCapabilityAcrossReceiveSites()
    {
        var result = Analyze(TaskInput(), Receive("first", 1), TrackResult("first", 2), Consume("first", 3),
            Receive("second", 4), TrackResult("second", 5), Consume("second", 6));
        var issue = Assert.Single(result.Issues, issue => issue.Id == "SS0048");
        Assert.Equal(IssueCertainty.Definite, issue.Certainty);
        Assert.Single(result.Blocks[0].Exit.ValueTasks);
        Assert.All(result.Blocks[0].Exit.Resources, resource => Assert.Equal(ResourceOwnership.External, resource.Ownership));
    }

    [Fact]
    public void ReturningTheOpaqueCarrierRetainsItsConsumedCapabilityCheck()
    {
        var result = Analyze(TaskInput(), Receive("first", 1), TrackResult("first", 2), Consume("first", 3),
            new FlowInstruction(InstructionKind.Return, Site(4), source: "task"));
        Assert.Contains(result.Issues, issue => issue.Id == "SS0048" && issue.Site.Start == 4);
    }

    [Fact]
    public void AFreshCarrierGenerationDoesNotReuseTheOldConsumedCache()
    {
        var result = Analyze(TaskInput(), Receive("first", 1), TrackResult("first", 2), Consume("first", 3),
            TaskInput(4), Receive("second", 5), TrackResult("second", 6), Consume("second", 7));
        Assert.DoesNotContain(result.Issues, issue => issue.Id == "SS0048");
    }

    [Fact]
    public void ARetainedCarrierGenerationKeepsPossibleOldConsumption()
    {
        var result = Analyze(TaskInput(), new FlowInstruction(InstructionKind.Alias, Site(1), "older", "task"),
            Receive("first", 2), TrackResult("first", 3), Consume("first", 4), TaskInput(5),
            new FlowInstruction(InstructionKind.Read, Site(6), source: "older"), Receive("second", 7), TrackResult("second", 8), Consume("second", 9));
        Assert.Contains(result.Issues, issue => issue.Id == "SS0048" && issue.Certainty == IssueCertainty.Possible);
    }

    [Fact]
    public void RebindingARetainedCompletedTaskSitePreservesBothCapabilities()
    {
        var result = Analyze(new FlowInstruction(InstructionKind.TrackValueTask, Site(0), "first", resourceId: "first"),
            new FlowInstruction(InstructionKind.BindAsyncResult, Site(1), "task", "first", resourceId: "carrier"),
            new FlowInstruction(InstructionKind.Alias, Site(2), "older", "task"), Consume("first", 3),
            new FlowInstruction(InstructionKind.TrackValueTask, Site(4), "second", resourceId: "second"),
            new FlowInstruction(InstructionKind.BindAsyncResult, Site(5), "task", "second", resourceId: "carrier"),
            new FlowInstruction(InstructionKind.ReceiveAsyncResult, Site(6), "received", "older"), Consume("received", 7));
        Assert.Contains(result.Issues, issue => issue.Id == "SS0048" && issue.Certainty == IssueCertainty.Possible);
        Assert.Equal(2, result.Blocks[0].Exit.ValueTasks.Length);
    }

    [Fact]
    public void RebindingAnUnretainedCompletedTaskSiteKeepsItsNewCapabilityFresh()
    {
        var result = Analyze(new FlowInstruction(InstructionKind.TrackValueTask, Site(0), "value", resourceId: "value"),
            new FlowInstruction(InstructionKind.BindAsyncResult, Site(1), "task", "value", resourceId: "carrier"), Consume("value", 2),
            new FlowInstruction(InstructionKind.TrackValueTask, Site(3), "value", resourceId: "value"),
            new FlowInstruction(InstructionKind.BindAsyncResult, Site(4), "task", "value", resourceId: "carrier"),
            new FlowInstruction(InstructionKind.ReceiveAsyncResult, Site(5), "received", "task"), Consume("received", 6));
        Assert.DoesNotContain(result.Issues, issue => issue.Id == "SS0048");
    }

    [Fact]
    public void BorrowedReturnValidationDoesNotConsumeAnAvailableCapability()
    {
        var result = Analyze(TaskInput(), Receive("value", 1), TrackResult("value", 2),
            new FlowInstruction(InstructionKind.Read, Site(3), source: "task", flags: InstructionFlags.ValidateReturnedResult), Consume("value", 4));
        Assert.DoesNotContain(result.Issues, issue => issue.Id == "SS0048");
    }

}
