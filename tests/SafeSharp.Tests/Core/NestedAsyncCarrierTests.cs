using SafeSharp.Core;

namespace SafeSharp.Tests.Core;

public sealed class NestedAsyncCarrierTests
{
    private static SourceSite Site(int offset) => new("nested.cs", offset, 1);
    private static AnalysisResult Analyze(params FlowInstruction[] instructions) => new OwnershipAnalysisEngine().Analyze(
        new AnalysisGraph("Nested", [new FlowBlock(0, instructions, [])], 0, Site(100)), new AnalysisOptions(captureSnapshots: true));

    [Fact]
    public void FailedOuterCompletionNeverMaterializesAnInnerCarrierOrTerminalResult()
    {
        var result = Analyze(new FlowInstruction(InstructionKind.BeginAsyncFactory, Site(0), "outer", resourceId: "factory", asyncResultDepth: 3),
            new FlowInstruction(InstructionKind.ObserveAsyncFactory, Site(1), source: "outer"));
        var resource = Assert.Single(result.Blocks[0].Exit.Resources);
        Assert.Equal(ResourceStatus.Completed, resource.Status);
        Assert.Equal(3, resource.AsyncResultDepth);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void EachSuccessfulReceiveMaterializesOnlyOneLayer()
    {
        var result = Analyze(new FlowInstruction(InstructionKind.BeginAsyncFactory, Site(0), "outer", resourceId: "factory", asyncResultDepth: 2),
            new FlowInstruction(InstructionKind.ReceiveAsyncResult, Site(1), "inner", "outer"),
            new FlowInstruction(InstructionKind.ReceiveAsyncResult, Site(2), "value", "inner"),
            new FlowInstruction(InstructionKind.Dispose, Site(3), source: "value"));
        Assert.Empty(result.Issues);
        Assert.Equal(2, result.Blocks[0].Steps[1].After.Resources.Length);
        Assert.All(result.Blocks[0].Steps[1].After.Resources, resource => Assert.Equal(ResourceKind.AsyncFactoryCarrier, resource.Kind));
        Assert.Equal(ResourceStatus.Disposed, Assert.Single(result.Blocks[0].Exit.Resources, resource => resource.Kind == ResourceKind.Resource).Status);
    }

    [Fact]
    public void ReturningNestedMaterializedCarriersTransfersTheTerminalOwner()
    {
        var result = Analyze(new FlowInstruction(InstructionKind.Allocate, Site(0), "resource", resourceId: "owner"),
            new FlowInstruction(InstructionKind.BindAsyncResult, Site(1), "inner", "resource", resourceId: "inner"),
            new FlowInstruction(InstructionKind.BindAsyncResult, Site(2), "outer", "inner", resourceId: "outer"),
            new FlowInstruction(InstructionKind.Return, Site(3), source: "outer"));
        Assert.Empty(result.Issues);
        Assert.All(result.Blocks[0].Exit.Resources, resource => Assert.Equal(ResourceStatus.Transferred, resource.Status));
    }

    [Fact]
    public void CarrierCyclesAtAllocationSiteJoinsHaveFiniteTraversal()
    {
        var result = Analyze(new FlowInstruction(InstructionKind.BindAsyncResult, Site(0), "task", "unknown", resourceId: "cycle"),
            new FlowInstruction(InstructionKind.BindAsyncResult, Site(1), "task", "task", resourceId: "cycle"),
            new FlowInstruction(InstructionKind.Return, Site(2), source: "task"));
        Assert.False(result.BudgetExceeded);
        Assert.Empty(result.Issues);
    }
}
