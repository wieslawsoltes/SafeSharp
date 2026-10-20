using SafeSharp.Core;

namespace SafeSharp.Tests.Core;

public sealed class ValueTaskProtocolTests
{
    private static SourceSite Site(int start) => new("value-task.cs", start, 1);
    private static FlowInstruction Track(string place = "task", int start = 0) => new(InstructionKind.TrackValueTask, Site(start), target: place, source: place, resourceId: "capability");
    private static FlowInstruction Consume(string place = "task", int start = 1) => new(InstructionKind.ConsumeValueTask, Site(start), source: place);
    private static AnalysisResult Analyze(params FlowInstruction[] instructions) => new OwnershipAnalysisEngine().Analyze(
        new AnalysisGraph("ValueTask", [new FlowBlock(0, instructions, [])], 0, Site(100)), new AnalysisOptions(captureSnapshots: true));

    [Fact]
    public void ScalarCapabilitiesRequireNoResourceAllocation()
    {
        var result = Analyze(Track(), Consume(), Consume(start: 2));
        Assert.Equal("SS0048", Assert.Single(result.Issues).Id);
        Assert.Empty(result.Blocks[0].Exit.Resources);
        Assert.Equal(ValueTaskConsumption.Consumed, Assert.Single(result.Blocks[0].Exit.ValueTasks).State);
    }

    [Fact]
    public void ReferenceAliasesCopyTheCapabilityIdentity()
    {
        var result = Analyze(Track(), new FlowInstruction(InstructionKind.Alias, Site(1), "copy", "task"), Consume(start: 2), Consume("copy", 3));
        Assert.Equal(IssueCertainty.Definite, Assert.Single(result.Issues).Certainty);
    }

    [Fact]
    public void PreservedAwaitablesHaveNoSingleConsumptionCapability()
    {
        var result = Analyze(Track(), Consume(), new FlowInstruction(InstructionKind.PreserveAwaitable, Site(2), "reusable", "task"),
            Consume("reusable", 3), Consume("reusable", 4));
        Assert.Empty(result.Issues);
        Assert.DoesNotContain("reusable", Assert.Single(result.Blocks[0].Exit.ValueTasks).Places);
    }

    [Fact]
    public void MovingACapabilityDoesNotConsumeItsDestination()
    {
        var result = Analyze(Track(), new FlowInstruction(InstructionKind.Move, Site(1), "moved", "task"), Consume("moved", 2), Consume("task", 3));
        var issue = Assert.Single(result.Issues);
        Assert.Equal("SS0048", issue.Id);
        Assert.Equal(3, issue.Site.Start);
    }

    [Fact]
    public void ReturningAnAlreadyConsumedCapabilityIsInvalid()
    {
        var result = Analyze(Track(), Consume(), new FlowInstruction(InstructionKind.Return, Site(2), source: "task"));
        Assert.Equal("SS0048", Assert.Single(result.Issues).Id);
    }

    [Fact]
    public void AConditionalConsumeIsJoinedWithoutRevivingTheCapability()
    {
        var result = Analyze(Track(), new FlowInstruction(InstructionKind.ConsumeValueTask, Site(1), source: "task", flags: InstructionFlags.Conditional), Consume(start: 2));
        Assert.Equal(IssueCertainty.Possible, Assert.Single(result.Issues).Certainty);
    }

    [Fact]
    public void NestedResultCarriersRetainValueTaskConsumptionIdentity()
    {
        var result = Analyze(Track(), new FlowInstruction(InstructionKind.BindAsyncResult, Site(1), "outer", "task", resourceId: "outer"),
            new FlowInstruction(InstructionKind.ReceiveAsyncResult, Site(2), "first", "outer"), Consume("first", 3),
            new FlowInstruction(InstructionKind.ReceiveAsyncResult, Site(4), "second", "outer"), Consume("second", 5));
        Assert.Equal("SS0048", Assert.Single(result.Issues).Id);
    }
}
