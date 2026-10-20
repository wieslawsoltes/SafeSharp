using SafeSharp.Core;

namespace SafeSharp.Tests.Core;

public sealed class CallValueSnapshotTests
{
    private const string Snapshot = "$t:call-argument:0:1:Argument";
    private static SourceSite Site(int offset) => new("call-values.cs", offset, 1, offset, 0);
    private static FlowInstruction New(int offset, string id) => new(InstructionKind.Allocate, Site(offset), "owner", resourceId: id);
    private static FlowInstruction Op(InstructionKind kind, int offset, string? source = null, string? target = null) =>
        new(kind, Site(offset), target, source);
    private static AnalysisResult Analyze(params FlowInstruction[] instructions) => new OwnershipAnalysisEngine().Analyze(
        new AnalysisGraph("CallValues", [new FlowBlock(0, instructions, [])], 0, Site(100)), new AnalysisOptions(captureSnapshots: true));

    [Fact]
    public void ADeadCallSnapshotDoesNotConcealTheLastOwnerOverwrite()
    {
        var result = Analyze(New(0, "original"), Op(InstructionKind.Alias, 1, "owner", Snapshot),
            New(2, "replacement"), Op(InstructionKind.Dispose, 3, "owner"));
        var overwrite = Assert.Single(result.Issues, issue => issue.Id == "SS0011");
        Assert.Equal(2, overwrite.Site.Start);
        Assert.Equal(IssueCertainty.Definite, overwrite.Certainty);
        Assert.Contains(result.Issues, issue => issue.Id == "SS0003" && issue.ResourceId == "original");
    }

    [Fact]
    public void ALiveCallSnapshotRetainsTheEvaluatedOwnerUntilItsEffect()
    {
        var result = Analyze(New(0, "original"), Op(InstructionKind.Alias, 1, "owner", Snapshot),
            New(2, "replacement"), Op(InstructionKind.Dispose, 3, Snapshot),
            Op(InstructionKind.ReleaseLoan, 4, Snapshot, Snapshot), Op(InstructionKind.Dispose, 5, "owner"));
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void AReturnedAliasRetainsTheOwnerAfterItsCallSnapshotEnds()
    {
        var result = Analyze(New(0, "original"), Op(InstructionKind.Alias, 1, "owner", Snapshot),
            New(2, "replacement"), Op(InstructionKind.Alias, 3, Snapshot, "result"),
            Op(InstructionKind.ReleaseLoan, 4, Snapshot, Snapshot), Op(InstructionKind.Dispose, 5, "result"),
            Op(InstructionKind.Dispose, 6, "owner"));
        Assert.Empty(result.Issues);
        var afterRelease = result.Blocks[0].Steps.Single(step => step.Instruction.Site.Start == 4).After;
        Assert.Empty(afterRelease.Places.Single(place => place.Place == Snapshot).References);
        Assert.Contains(afterRelease.Places.Single(place => place.Place == "result").References, reference => reference.ResourceId == "original");
    }
}
