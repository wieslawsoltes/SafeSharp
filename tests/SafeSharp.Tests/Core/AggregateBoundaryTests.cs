using SafeSharp.Core;

namespace SafeSharp.Tests.Core;

public sealed class AggregateBoundaryTests
{
    private static SourceSite Site(int offset) => new("aggregate-boundary.cs", offset, 1);
    private static AnalysisResult Analyze(params FlowInstruction[] instructions) => new OwnershipAnalysisEngine().Analyze(
        new AnalysisGraph("Boundaries", [new FlowBlock(0, instructions, [])], 0, Site(999)), new AnalysisOptions(captureSnapshots: true));
    private static FlowInstruction Allocate(string place, int offset) => new(InstructionKind.Allocate, Site(offset), place, resourceId: place);
    private static FlowInstruction Aggregate(string place, int offset) => new(InstructionKind.CreateAggregate, Site(offset), place, resourceId: place);
    private static FlowInstruction Set(string owner, string source, int offset) => new(InstructionKind.SetAggregateElement, Site(offset), source: source, owner: owner, projection: "[0]");
    private static FlowInstruction Read(string target, string owner, int offset) => new(InstructionKind.ReadAggregateElement, Site(offset), target, owner, projection: "[0]");

    [Fact]
    public void AProjectedArrayDoesNotReceiveItsEnclosingArraysElements()
    {
        var result = Analyze(Allocate("value", 0), Aggregate("array", 1), Set("array", "value", 2),
            Read("result", "array.child", 3), new FlowInstruction(InstructionKind.Dispose, Site(4), source: "result"));
        Assert.Equal(ResourceStatus.Alive, Assert.Single(result.Blocks[0].Exit.Resources, resource => resource.Id == "value").Status);
        Assert.Contains(result.Issues, issue => issue.Id == "SS0003" && issue.ResourceId == "value");
    }

    [Fact]
    public void AProjectedArrayWriteDoesNotReplaceItsEnclosingArraysElement()
    {
        var result = Analyze(Allocate("original", 0), Allocate("stored", 1), Aggregate("array", 2), Set("array", "original", 3),
            Set("array.child", "stored", 4), Read("result", "array", 5), new FlowInstruction(InstructionKind.Dispose, Site(6), source: "result"));
        Assert.Equal(ResourceStatus.Disposed, Assert.Single(result.Blocks[0].Exit.Resources, resource => resource.Id == "original").Status);
        Assert.Equal(ResourceStatus.Escaped, Assert.Single(result.Blocks[0].Exit.Resources, resource => resource.Id == "stored").Status);
    }

    [Fact]
    public void AProjectedInputCollectionDoesNotExposeItsEnclosingFactories()
    {
        var result = Analyze(new FlowInstruction(InstructionKind.BeginAsyncFactory, Site(0), "factory", resourceId: "factory"),
            Aggregate("inputs", 1), Set("inputs", "factory", 2),
            new FlowInstruction(InstructionKind.BeginAsyncComposition, Site(3), "combined", "inputs.child", resourceId: "combined",
                asyncComposition: AsyncCompositionKind.WhenAll, flags: InstructionFlags.HasAsyncResult),
            new FlowInstruction(InstructionKind.ReceiveAsyncResult, Site(4), "results", "combined"));
        Assert.Equal(ResourceStatus.Pending, Assert.Single(result.Blocks[0].Exit.Resources, resource => resource.Id == "factory").Status);
        Assert.DoesNotContain(result.Blocks[0].Exit.Resources, resource => resource.Id == "async-result:factory");
    }

    [Fact]
    public void ObservingAProjectionDoesNotCompleteTheEnclosingComposition()
    {
        var result = Analyze(Allocate("owner", 0),
            new FlowInstruction(InstructionKind.BeginAsyncDispose, Site(1), "cleanup", "owner", resourceId: "cleanup"),
            Aggregate("inputs", 2), Set("inputs", "cleanup", 3),
            new FlowInstruction(InstructionKind.BeginAsyncComposition, Site(4), "combined", "inputs", resourceId: "combined",
                asyncComposition: AsyncCompositionKind.WhenAll),
            new FlowInstruction(InstructionKind.ObserveAsyncComposition, Site(5), source: "combined.child"));
        Assert.Equal(ResourceStatus.Pending, Assert.Single(result.Blocks[0].Exit.Resources, resource => resource.Id == "cleanup").Status);
        Assert.Equal(ResourceStatus.Pending, Assert.Single(result.Blocks[0].Exit.Resources, resource => resource.Id == "combined").Status);
    }

    [Fact]
    public void ProjectedCleanupDependenciesDoNotDischargeTheEnclosingCompletion()
    {
        var result = Analyze(Allocate("owner", 0),
            new FlowInstruction(InstructionKind.BeginAsyncDispose, Site(1), "cleanup", "owner", resourceId: "cleanup"),
            new FlowInstruction(InstructionKind.BeginAsyncComposition, Site(2), "proxy", "cleanup.child", resourceId: "proxy",
                asyncComposition: AsyncCompositionKind.WaitAsync),
            new FlowInstruction(InstructionKind.CompleteAsyncComposition, Site(3), source: "proxy"));
        Assert.Equal(ResourceStatus.Pending, Assert.Single(result.Blocks[0].Exit.Resources, resource => resource.Id == "cleanup").Status);
    }

    [Fact]
    public void AProjectedTaskDoesNotReceiveTheEnclosingCachedCapability()
    {
        var result = Analyze(new FlowInstruction(InstructionKind.TrackValueTask, Site(0), "value", resourceId: "capability"),
            new FlowInstruction(InstructionKind.BindAsyncResult, Site(1), "task", "value", resourceId: "task"),
            new FlowInstruction(InstructionKind.ConsumeValueTask, Site(2), source: "value"),
            new FlowInstruction(InstructionKind.ReceiveAsyncResult, Site(3), "result", "task.child"),
            new FlowInstruction(InstructionKind.TrackValueTask, Site(4), "result", "result", resourceId: "opaque-capability",
                flags: InstructionFlags.PreserveExistingCapability),
            new FlowInstruction(InstructionKind.ConsumeValueTask, Site(5), source: "result"));
        Assert.DoesNotContain(result.Issues, issue => issue.Id == "SS0048");
        Assert.Equal(2, result.Blocks[0].Exit.ValueTasks.Length);
    }
}
