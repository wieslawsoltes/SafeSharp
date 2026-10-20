using SafeSharp.Core;

namespace SafeSharp.Tests.Core;

public sealed class ReferenceStorageTests
{
    private static SourceSite Site(int start) => new("reference-storage.cs", start, 1);
    private static FlowInstruction Allocate(string place, string id, int start = 0) => new(InstructionKind.Allocate, Site(start), target: place, resourceId: id);
    private static FlowInstruction Bind(string target, string source, int start = 0) => new(InstructionKind.BindReference, Site(start), target, source);
    private static FlowInstruction Alias(string target, string source, int start = 0) => new(InstructionKind.Alias, Site(start), target, source);
    private static FlowInstruction Dispose(string place, int start = 0) => new(InstructionKind.Dispose, Site(start), source: place);
    private static FlowInstruction Read(string place, int start = 0) => new(InstructionKind.Read, Site(start), source: place);
    private static AnalysisResult Analyze(params FlowInstruction[] instructions) => new OwnershipAnalysisEngine().Analyze(
        new AnalysisGraph("References", [new FlowBlock(0, instructions, [])], 0, Site(1000)), new AnalysisOptions(captureSnapshots: true));

    [Fact]
    public void WritesThroughAReferenceUpdateItsStorage()
    {
        var result = Analyze(Allocate("owner", "old"), Bind("alias", "owner"), Dispose("owner"),
            Allocate("replacement", "new"), Alias("alias", "replacement"), Dispose("owner"), Read("replacement", 20));
        var issue = Assert.Single(result.Issues);
        Assert.Equal("SS0001", issue.Id);
        Assert.Equal(20, issue.Site.Start);
        Assert.Equal("owner", Assert.Single(Assert.Single(result.Blocks[0].Exit.ReferenceStorage).Targets));
    }

    [Fact]
    public void ByValueCopiesKeepTheirOldValueAfterStorageReplacement()
    {
        var result = Analyze(Allocate("owner", "old"), Bind("alias", "owner"), Alias("snapshot", "alias"),
            Allocate("replacement", "new"), Alias("alias", "replacement"), Dispose("owner"), Read("snapshot"), Dispose("snapshot"));
        Assert.Empty(result.Issues);
        var owner = Assert.Single(result.Blocks[0].Exit.Places.Where(place => place.Place == "owner"));
        Assert.Equal("new", Assert.Single(owner.References).ResourceId);
    }

    [Fact]
    public void RebindingAReferenceDoesNotRedirectOlderAliases()
    {
        var result = Analyze(Allocate("first", "one"), Allocate("second", "two"), Bind("alias", "first"), Bind("older", "alias"),
            Bind("alias", "second"), Dispose("older"), Read("first", 30), Read("second"), Dispose("second"));
        var issue = Assert.Single(result.Issues);
        Assert.Equal("SS0001", issue.Id);
        Assert.Equal(30, issue.Site.Start);
        Assert.Equal("first", Assert.Single(result.Blocks[0].Exit.ReferenceStorage.Single(binding => binding.Reference == "older").Targets));
        Assert.Equal("second", Assert.Single(result.Blocks[0].Exit.ReferenceStorage.Single(binding => binding.Reference == "alias").Targets));
    }

    [Fact]
    public void ReleasingAnAddressSnapshotDoesNotForgetItsTarget()
    {
        var result = Analyze(Allocate("owner", "resource"), Bind("argument", "owner"),
            new FlowInstruction(InstructionKind.ReleaseReference, Site(10), target: "argument"), Dispose("owner"));
        Assert.Empty(result.Issues);
        Assert.Empty(result.Blocks[0].Exit.ReferenceStorage);
    }

    [Fact]
    public void OverwritingThroughAReferenceDoesNotCountTheReplacedCellAsAnotherOwner()
    {
        var result = Analyze(Allocate("owner", "old"), Bind("alias", "owner"), Allocate("alias", "new", 10), Dispose("owner"));
        Assert.Contains(result.Issues, issue => issue.Id == "SS0011" && issue.Site.Start == 10);
        Assert.Contains(result.Issues, issue => issue.Id == "SS0003" && issue.ResourceId == "old");
        Assert.DoesNotContain(result.Issues, issue => issue.Id == "SS0003" && issue.ResourceId == "new");
    }

    [Fact]
    public void AConditionalReferenceReadIsNotAUniqueOwnerOfEitherAlternative()
    {
        var result = Analyze(Allocate("first", "one"), Allocate("second", "two"), Bind("alias", "first"),
            new FlowInstruction(InstructionKind.MergeReference, Site(1), "alias", "second"), Dispose("alias"), Read("first", 10), Read("second", 20));
        Assert.All(result.Issues.Where(issue => issue.Id == "SS0001"), issue => Assert.Equal(IssueCertainty.Possible, issue.Certainty));
        Assert.Equal(2, result.Issues.Count(issue => issue.Id == "SS0001"));
        Assert.Equal(2, result.Issues.Count(issue => issue.Id == "SS0003"));
    }

    [Fact]
    public void ConditionalStorageWritesRetainThePreviousValueOnTheUnselectedPath()
    {
        var result = Analyze(Allocate("first", "one"), Allocate("second", "two"), Bind("alias", "first"),
            new FlowInstruction(InstructionKind.MergeReference, Site(1), "alias", "second"), Allocate("replacement", "new"), Alias("alias", "replacement"));
        var state = result.Blocks[0].Exit;
        Assert.Equal(new[] { "new", "one" }, state.Places.Single(place => place.Place == "first").References.Select(reference => reference.ResourceId).Order());
        Assert.Equal(new[] { "new", "two" }, state.Places.Single(place => place.Place == "second").References.Select(reference => reference.ResourceId).Order());
        Assert.All(state.Places.Where(place => place.Place is "first" or "second").SelectMany(place => place.References), reference => Assert.True(reference.MayBeAbsent));
    }

    [Fact]
    public void ControlFlowJoinsUnionStorageTargets()
    {
        var graph = new AnalysisGraph("Join references", [
            new FlowBlock(0, [Allocate("first", "one"), Allocate("second", "two")], [1, 2]),
            new FlowBlock(1, [Bind("alias", "first")], [3]),
            new FlowBlock(2, [Bind("alias", "second")], [3]),
            new FlowBlock(3, [Dispose("alias"), Read("first", 10), Read("second", 20)], [])], 0, Site(1000));
        var result = new OwnershipAnalysisEngine().Analyze(graph, new AnalysisOptions(captureSnapshots: true));
        Assert.Equal(new[] { "first", "second" }, Assert.Single(result.Blocks.Single(block => block.Id == 3).Entry.ReferenceStorage).Targets);
        Assert.Equal(2, result.Issues.Count(issue => issue.Id == "SS0001" && issue.Certainty == IssueCertainty.Possible));
    }

    [Fact]
    public void AddressesAreCapturedBeforeLaterArgumentsRebindTheirSource()
    {
        var result = Analyze(Allocate("first", "one"), Allocate("second", "two"), Bind("alias", "first"), Bind("argument", "alias"),
            Bind("alias", "second"), Dispose("argument"), Read("first", 10), Read("second"), Dispose("second"));
        Assert.Equal("SS0001", Assert.Single(result.Issues).Id);
        Assert.Equal(10, Assert.Single(result.Issues).Site.Start);
    }

    [Fact]
    public void ProjectedStorageThroughARefRootDesignatesTheSameCell()
    {
        var result = Analyze(Allocate("owner.field", "one"), Bind("alias", "owner"), Bind("field", "alias.field"),
            Dispose("field"), Read("owner.field", 10));
        Assert.Equal("SS0001", Assert.Single(result.Issues).Id);
    }

    [Theory]
    [InlineData("alias")]
    [InlineData("owner")]
    public void AValueTaskStoredThroughARefRetainsItsConsumptionIdentity(string secondRead)
    {
        var result = Analyze(new FlowInstruction(InstructionKind.TrackValueTask, Site(0), "task", "task", resourceId: "capability"),
            Bind("alias", "owner"), Alias("alias", "task"),
            new FlowInstruction(InstructionKind.ConsumeValueTask, Site(1), source: "owner"),
            new FlowInstruction(InstructionKind.ConsumeValueTask, Site(2), source: secondRead));
        var issue = Assert.Single(result.Issues);
        Assert.Equal("SS0048", issue.Id);
        Assert.Equal(IssueCertainty.Definite, issue.Certainty);
    }

    [Fact]
    public void ReferenceProjectionGrowthStopsWithAnExplicitIncompleteAnalysis()
    {
        var graph = new AnalysisGraph("Growing references", [
            new FlowBlock(0, [Bind("alias", "owner")], [1]),
            new FlowBlock(1, [Bind("alias", "alias.Next")], [1, 2]),
            new FlowBlock(2, [Read("alias")], [])], 0, Site(1000));
        var result = new OwnershipAnalysisEngine().Analyze(graph);
        Assert.True(result.BudgetExceeded);
        Assert.Equal("SS0015", Assert.Single(result.Issues).Id);
        Assert.Contains("reference-storage", Assert.Single(result.Issues).Message, StringComparison.Ordinal);
        Assert.Empty(result.Blocks);
    }
}
