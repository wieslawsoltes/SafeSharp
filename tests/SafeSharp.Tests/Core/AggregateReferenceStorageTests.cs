using SafeSharp.Core;

namespace SafeSharp.Tests.Core;

public sealed class AggregateReferenceStorageTests
{
    private static SourceSite Site(int offset) => new("aggregate-reference.cs", offset, 1);
    private static FlowInstruction Allocate(string place, int offset = 0) => new(InstructionKind.Allocate, Site(offset), place, resourceId: place);
    private static FlowInstruction Array(string place, int offset = 1) => new(InstructionKind.CreateAggregate, Site(offset), place, resourceId: place);
    private static FlowInstruction Set(string array, string source, string key = "[0]", int offset = 2) => new(InstructionKind.SetAggregateElement, Site(offset), source: source, owner: array, projection: key);
    private static FlowInstruction Bind(string target, string array, string key = "[0]", int offset = 3) => new(InstructionKind.BindAggregateElementReference, Site(offset), target, array, projection: key);
    private static FlowInstruction Copy(string target, string source, int offset = 4) => new(InstructionKind.Alias, Site(offset), target, source);
    private static FlowInstruction Read(string target, string array, string key = "[0]", int offset = 5) => new(InstructionKind.ReadAggregateElement, Site(offset), target, array, projection: key);
    private static FlowInstruction Dispose(string source, int offset = 6) => new(InstructionKind.Dispose, Site(offset), source: source);
    private static AnalysisResult Analyze(params FlowInstruction[] instructions) => new OwnershipAnalysisEngine().Analyze(
        new AnalysisGraph("Aggregate references", [new FlowBlock(0, instructions, [])], 0, Site(1000)), new AnalysisOptions(captureSnapshots: true));

    [Fact]
    public void TakingAnAddressDoesNotWeakenTheCurrentElement()
    {
        var result = Analyze(Allocate("owned"), Array("array"), Set("array", "owned"), Bind("reference", "array"), Dispose("reference"));
        Assert.Empty(result.Issues);
        Assert.Equal(ResourceStatus.Disposed, result.Blocks[0].Exit.Resources.Single(resource => resource.Id == "owned").Status);
        Assert.Equal(OwnershipTransfer.AggregateElementCache("array", "[0]"), Assert.Single(Assert.Single(result.Blocks[0].Exit.ReferenceStorage).Targets));
    }

    [Fact]
    public void ARefWriteAndAnOrdinaryElementReadShareTheCanonicalCell()
    {
        var result = Analyze(Allocate("old"), Allocate("new"), Array("array"), Set("array", "old"), Bind("reference", "array"),
            Dispose("reference"), Copy("reference", "new"), Read("value", "array"), Dispose("value"));
        Assert.Empty(result.Issues);
        Assert.All(result.Blocks[0].Exit.Resources.Where(resource => resource.Kind == ResourceKind.Resource), resource => Assert.Equal(ResourceStatus.Disposed, resource.Status));
    }

    [Fact]
    public void OrdinaryWritesAreVisibleThroughAnExistingAddress()
    {
        var result = Analyze(Allocate("old"), Allocate("new"), Array("array"), Set("array", "old"), Bind("reference", "array"),
            Dispose("old"), Set("array", "new"), Dispose("reference"));
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void ReplacingTheArrayVariableDoesNotRedirectItsCapturedAddress()
    {
        var result = Analyze(Allocate("first"), Allocate("second"), Array("array"), Set("array", "first"), Bind("reference", "array"),
            Array("replacement"), Set("replacement", "second"), Copy("array", "replacement"), Dispose("reference"), Read("value", "array"), Dispose("value"));
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void ConditionalStorageWritesDoNotReplaceBothArrayElements()
    {
        var result = Analyze(Allocate("first"), Allocate("second"), Allocate("new"), Array("array"), Set("array", "first"), Set("array", "second", "[1]"),
            Bind("a", "array"), Bind("b", "array", "[1]"), new FlowInstruction(InstructionKind.MergeReference, Site(8), "a", "b"), Copy("a", "new"),
            Read("left", "array"), Read("right", "array", "[1]"));
        var state = result.Blocks[0].Exit;
        Assert.Equal(new[] { "first", "new" }, state.Places.Single(place => place.Place == "left").References.Select(reference => reference.ResourceId).Order());
        Assert.Equal(new[] { "new", "second" }, state.Places.Single(place => place.Place == "right").References.Select(reference => reference.ResourceId).Order());
        Assert.All(state.Places.Where(place => place.Place is "left" or "right").SelectMany(place => place.References), reference => Assert.False(reference.CoversResource));
    }

    [Fact]
    public void AnUnknownIndexAddressAlsoTargetsCellsMaterializedLater()
    {
        var result = Analyze(Array("array"), Bind("reference", "array", "[*]"), Allocate("later"), Set("array", "later", "[4]"),
            Dispose("reference"), Read("value", "array", "[4]"), new FlowInstruction(InstructionKind.Read, Site(20), source: "value"));
        Assert.Contains(result.Issues, issue => issue.Id == "SS0001" && issue.Site.Start == 20 && issue.Certainty == IssueCertainty.Possible);
        Assert.Contains(result.Issues, issue => issue.Id == "SS0003" && issue.ResourceId == "later");
    }

    [Fact]
    public void UnknownIndexWritesUpdatePreviouslyCapturedExactAddresses()
    {
        var result = Analyze(Allocate("old"), Allocate("new"), Array("array"), Set("array", "old"), Bind("reference", "array"),
            Set("array", "new", "[*]"), Dispose("reference"));
        Assert.Equal(ResourceStatus.Alive | ResourceStatus.Disposed, result.Blocks[0].Exit.Resources.Single(resource => resource.Id == "new").Status);
        Assert.Equal(ResourceStatus.Alive | ResourceStatus.Disposed, result.Blocks[0].Exit.Resources.Single(resource => resource.Id == "old").Status);
    }

    [Fact]
    public void ExactReplacementAfterAnUnknownWriteRemovesTheOldCellAlternative()
    {
        var result = Analyze(Allocate("old"), Allocate("new"), Array("array"), Set("array", "old", "[*]"), Bind("reference", "array"),
            Copy("reference", "new"), Read("value", "array"), Dispose("value"), Dispose("old"));
        Assert.DoesNotContain(result.Issues, issue => issue.Id is "SS0001" or "SS0002" or "SS0003");
        Assert.Equal("new", Assert.Single(result.Blocks[0].Exit.Places.Single(place => place.Place == "value").References).ResourceId);
    }

    [Fact]
    public void RefAndElementReadsShareTheValueTaskConsumptionCapability()
    {
        var result = Analyze(new FlowInstruction(InstructionKind.TrackValueTask, Site(0), "task", resourceId: "capability"),
            Array("array"), Set("array", "task"), Bind("reference", "array"),
            new FlowInstruction(InstructionKind.ConsumeValueTask, Site(7), source: "reference"), Read("value", "array"),
            new FlowInstruction(InstructionKind.ConsumeValueTask, Site(9), source: "value"));
        var issue = Assert.Single(result.Issues);
        Assert.Equal("SS0048", issue.Id);
        Assert.Equal(IssueCertainty.Definite, issue.Certainty);
    }

    [Fact]
    public void ProjectedArrayAddressesCannotDesignateTheEnclosingArraysCell()
    {
        var result = Analyze(Allocate("owned"), Array("array"), Set("array", "owned"), Bind("reference", "array.child"), Dispose("reference"));
        Assert.Contains(result.Issues, issue => issue.Id == "SS0003" && issue.ResourceId == "owned");
        Assert.Equal(ReferenceStorageState.UnknownTarget, Assert.Single(Assert.Single(result.Blocks[0].Exit.ReferenceStorage).Targets));
    }

    [Fact]
    public void AnOlderElementAddressRetainsItsAggregateGenerationAtARepeatedAllocationSite()
    {
        var result = Analyze(Allocate("old"), Array("array"), Set("array", "old"), Bind("reference", "array"),
            Array("array"), Allocate("new"), Set("array", "new"), Dispose("reference"));
        Assert.True(result.Blocks[0].Exit.Resources.Single(resource => resource.Id == "array").SummarizesMultipleAggregates);
        Assert.All(result.Blocks[0].Exit.Resources.Where(resource => resource.Kind == ResourceKind.Resource),
            resource => Assert.Equal(ResourceStatus.Alive | ResourceStatus.Disposed, resource.Status));
    }

    [Fact]
    public void RefWritesToASummarizedGenerationCannotStronglyReplaceAllInstances()
    {
        var result = Analyze(Allocate("old"), Array("array"), Set("array", "old"), Bind("reference", "array"),
            Array("array"), Allocate("new"), Copy("reference", "new"), Read("current", "array"), Dispose("reference"));
        Assert.Contains(result.Blocks[0].Exit.Places.Single(place => place.Place == "current").References, reference => reference.ResourceId == "old");
        Assert.Contains(result.Issues, issue => issue.Id == "SS0003" && issue.ResourceId == "old");
    }

    [Fact]
    public void IncompleteWildcardTargetExpansionWithholdsTheAnalysis()
    {
        var instructions = new List<FlowInstruction> { Array("array") };
        for (var index = 0; index < ReferenceStorageState.MaximumTargets + 1; index++) instructions.Add(Set("array", "empty", "[" + index + "]"));
        instructions.Add(Bind("reference", "array", "[*]"));
        instructions.Add(Dispose("reference"));
        var result = Analyze(instructions.ToArray());
        Assert.True(result.BudgetExceeded);
        Assert.Equal("SS0015", Assert.Single(result.Issues).Id);
        Assert.Empty(result.Blocks);
    }

    [Fact]
    public void AnAddressOfAnAlternativeArrayDoesNotProveReleaseOfBothOwners()
    {
        var graph = new AnalysisGraph("Alternative arrays", [
            new FlowBlock(0, [Allocate("first"), Allocate("second"), Array("left"), Array("right"), Set("left", "first"), Set("right", "second")], [1, 2]),
            new FlowBlock(1, [Copy("selected", "left")], [3]),
            new FlowBlock(2, [Copy("selected", "right")], [3]),
            new FlowBlock(3, [Bind("reference", "selected"), Dispose("reference")], [])], 0, Site(1000));
        var result = new OwnershipAnalysisEngine().Analyze(graph, new AnalysisOptions(captureSnapshots: true));
        Assert.Equal(2, result.Issues.Count(issue => issue.Id == "SS0003"));
        Assert.All(result.Blocks.Single(block => block.Id == 3).Exit.Resources.Where(resource => resource.Kind == ResourceKind.Resource),
            resource => Assert.Equal(ResourceStatus.Alive | ResourceStatus.Disposed, resource.Status));
    }

    [Fact]
    public void AnUnknownUninitializedElementIsNotAssumedNonNull()
    {
        var result = Analyze(Array("array"), Read("value", "array", "[*]"), new FlowInstruction(InstructionKind.AssumeNull, Site(8), source: "value"));
        Assert.True(result.Blocks[0].Exit.IsReachable);
    }
}
