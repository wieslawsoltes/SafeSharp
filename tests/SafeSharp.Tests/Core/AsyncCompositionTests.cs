using SafeSharp.Core;

namespace SafeSharp.Tests.Core;

public sealed class AsyncCompositionTests
{
    private static SourceSite Site(int value) => new("composition.cs", value, 1);
    private static AnalysisResult Analyze(params FlowInstruction[] instructions) => new OwnershipAnalysisEngine().Analyze(
        new AnalysisGraph("Composition", [new FlowBlock(0, instructions, [])], 0, Site(999)), new AnalysisOptions(captureSnapshots: true));
    private static FlowInstruction Factory(string name, int value, int depth = 1) => new(InstructionKind.BeginAsyncFactory, Site(value), name, resourceId: name, asyncResultDepth: depth);
    private static FlowInstruction Compose(string target, string source, AsyncCompositionKind kind, int value = 10, bool result = true) =>
        new(InstructionKind.BeginAsyncComposition, Site(value), target, source, resourceId: target, asyncComposition: kind,
            flags: result ? InstructionFlags.HasAsyncResult : InstructionFlags.None);
    private static FlowInstruction Aggregate(string target, int value = 3) => new(InstructionKind.CreateAggregate, Site(value), target, resourceId: target);
    private static FlowInstruction Set(string array, string source, int element, int value = 4) =>
        new(InstructionKind.SetAggregateElement, Site(value), source: source, owner: array, projection: "[" + element + "]");
    private static FlowInstruction Complete(string source, int value = 20) => new(InstructionKind.CompleteAsyncComposition, Site(value), source: source);
    private static FlowInstruction Receive(string target, string source, int value = 21) => new(InstructionKind.ReceiveAsyncResult, Site(value), target, source);
    private static FlowInstruction Element(string target, string source, int index, int value = 22) =>
        new(InstructionKind.ReadAggregateElement, Site(value), target, source, projection: "[" + index + "]");
    private static FlowInstruction Dispose(string source, int value = 23) => new(InstructionKind.Dispose, Site(value), source: source);

    [Fact]
    public void WaitAsyncFaultDoesNotMaterializeOrDischargeItsInput()
    {
        var result = Analyze(Factory("input", 0), Compose("proxy", "input", AsyncCompositionKind.WaitAsync),
            new FlowInstruction(InstructionKind.ObserveAsyncFactory, Site(11), source: "proxy"));
        Assert.Contains(result.Issues, issue => issue.Id == "SS0003");
        Assert.All(result.Blocks[0].Exit.Resources, resource => Assert.Equal(ResourceKind.AsyncFactoryCarrier, resource.Kind));
        Assert.Equal(ResourceStatus.Pending, Assert.Single(result.Blocks[0].Exit.Resources, resource => resource.Id == "input").Status);
    }

    [Fact]
    public void WaitAsyncSuccessSharesTheOriginalPayloadIdentity()
    {
        var result = Analyze(Factory("input", 0), Compose("proxy", "input", AsyncCompositionKind.WaitAsync), Complete("proxy"),
            Receive("first", "proxy"), Dispose("first"), Receive("second", "input", 24), new FlowInstruction(InstructionKind.Read, Site(25), source: "second"));
        Assert.Contains(result.Issues, issue => issue.Id == "SS0001");
        Assert.Single(result.Blocks[0].Exit.Resources, resource => resource.Kind == ResourceKind.Resource);
    }

    [Fact]
    public void ReturningWaitAsyncDoesNotTransferThePossiblyAbandonedInput()
    {
        var result = Analyze(Factory("input", 0), Compose("proxy", "input", AsyncCompositionKind.WaitAsync),
            new FlowInstruction(InstructionKind.Return, Site(12), source: "proxy"));
        Assert.Contains(result.Issues, issue => issue.Id == "SS0003");
        Assert.Equal(ResourceStatus.Pending, Assert.Single(result.Blocks[0].Exit.Resources, resource => resource.Id == "input").Status);
    }

    [Fact]
    public void UnwrapReceivesEachDeferredLayerOnce()
    {
        var result = Analyze(Factory("outer", 0, depth: 2), Compose("proxy", "outer", AsyncCompositionKind.Unwrap), Complete("proxy"),
            Receive("value", "proxy"), Dispose("value"));
        Assert.Empty(result.Issues);
        Assert.Equal(ResourceStatus.Disposed, Assert.Single(result.Blocks[0].Exit.Resources, resource => resource.Kind == ResourceKind.Resource).Status);
    }

    [Fact]
    public void ReturningUnwrapTransfersItsNestedProducerResponsibility()
    {
        var result = Analyze(Factory("outer", 0, depth: 2), Compose("proxy", "outer", AsyncCompositionKind.Unwrap),
            new FlowInstruction(InstructionKind.Return, Site(12), source: "proxy"));
        Assert.Empty(result.Issues);
        Assert.All(result.Blocks[0].Exit.Resources, resource => Assert.Equal(ResourceStatus.Transferred, resource.Status));
    }

    [Fact]
    public void WhenAnyDoesNotObserveEitherInput()
    {
        var result = Analyze(Factory("first", 0), Factory("second", 1), Aggregate("inputs"), Set("inputs", "first", 0), Set("inputs", "second", 1),
            Compose("proxy", "inputs", AsyncCompositionKind.WhenAny), Complete("proxy"), Receive("winner", "proxy"));
        Assert.All(result.Blocks[0].Exit.Resources.Where(resource => resource.Id is "first" or "second"), resource => Assert.Equal(ResourceStatus.Pending, resource.Status));
        var winner = Assert.Single(result.Blocks[0].Exit.Places, place => place.Place == "winner");
        Assert.Equal(2, winner.References.Length);
        Assert.All(winner.References, reference => Assert.False(reference.CoversResource));
    }

    [Fact]
    public void ReturningOneCandidateWhenAnyTransfersOnlyThatCandidate()
    {
        var result = Analyze(Factory("input", 0), Aggregate("inputs"), Set("inputs", "input", 0), Compose("proxy", "inputs", AsyncCompositionKind.WhenAny),
            new FlowInstruction(InstructionKind.Return, Site(12), source: "proxy"));
        Assert.Empty(result.Issues);
        Assert.Equal(ResourceStatus.Transferred, Assert.Single(result.Blocks[0].Exit.Resources, resource => resource.Id == "input").Status);
    }

    [Fact]
    public void ReturningWhenAnyRetainsPossibleLoserObligations()
    {
        var result = Analyze(Factory("first", 0), Factory("second", 1), Aggregate("inputs"), Set("inputs", "first", 0), Set("inputs", "second", 1),
            Compose("proxy", "inputs", AsyncCompositionKind.WhenAny), new FlowInstruction(InstructionKind.Return, Site(12), source: "proxy"));
        Assert.Contains(result.Issues, issue => issue.Id == "SS0003");
        Assert.All(result.Blocks[0].Exit.Resources.Where(resource => resource.Id is "first" or "second"),
            resource => Assert.Equal(ResourceStatus.Pending | ResourceStatus.Transferred, resource.Status));
    }

    [Fact]
    public void WhenAllExposesEachInputPayloadAtItsOriginalIndex()
    {
        var result = Analyze(Factory("first", 0), Factory("second", 1), Aggregate("inputs"), Set("inputs", "first", 0), Set("inputs", "second", 1),
            Compose("proxy", "inputs", AsyncCompositionKind.WhenAll), Complete("proxy"), Receive("values", "proxy"),
            Element("one", "values", 0), Dispose("one"), Element("two", "values", 1, 24), Dispose("two", 25));
        Assert.Empty(result.Issues);
        Assert.Equal(2, result.Blocks[0].Exit.Resources.Count(resource => resource.Kind == ResourceKind.Resource && resource.Status == ResourceStatus.Disposed));
    }

    [Fact]
    public void WhenAllInputsAreCapturedBeforeTheInputArrayChanges()
    {
        var result = Analyze(Factory("first", 0), Factory("second", 1), Aggregate("inputs"), Set("inputs", "first", 0),
            Compose("proxy", "inputs", AsyncCompositionKind.WhenAll), Set("inputs", "second", 0, 12), Complete("proxy"), Receive("values", "proxy"),
            Element("one", "values", 0), Dispose("one"));
        Assert.Equal(ResourceStatus.Completed, Assert.Single(result.Blocks[0].Exit.Resources, resource => resource.Id == "first").Status);
        Assert.Equal(ResourceStatus.Pending, Assert.Single(result.Blocks[0].Exit.Resources, resource => resource.Id == "second").Status);
        Assert.Contains(result.Issues, issue => issue.Id == "SS0003" && issue.ResourceId == "second");
    }

    [Fact]
    public void FailedWhenAllDoesNotClaimThatOwnedResultsWereProduced()
    {
        var result = Analyze(Factory("input", 0), Aggregate("inputs"), Set("inputs", "input", 0), Compose("proxy", "inputs", AsyncCompositionKind.WhenAll),
            new FlowInstruction(InstructionKind.ObserveAsyncFactory, Site(12), source: "proxy"));
        Assert.DoesNotContain(result.Blocks[0].Exit.Resources, resource => resource.Kind == ResourceKind.Resource);
        Assert.Equal(ResourceStatus.Pending, Assert.Single(result.Blocks[0].Exit.Resources, resource => resource.Id == "input").Status);
    }

    [Fact]
    public void WhenAllObservesEveryDisposalCompletionEvenWhenOneFaults()
    {
        var result = Analyze(new FlowInstruction(InstructionKind.Allocate, Site(0), "owner", resourceId: "owner"),
            new FlowInstruction(InstructionKind.BeginAsyncDispose, Site(1), "cleanup", "owner", resourceId: "cleanup"),
            Aggregate("inputs"), Set("inputs", "cleanup", 0), Compose("proxy", "inputs", AsyncCompositionKind.WhenAll, result: false),
            new FlowInstruction(InstructionKind.ObserveAsyncFactory, Site(12), source: "proxy"));
        Assert.Empty(result.Issues);
        Assert.Equal(ResourceStatus.Completed, Assert.Single(result.Blocks[0].Exit.Resources, resource => resource.Id == "cleanup").Status);
    }

    [Fact]
    public void NonGenericWhenAllDoesNotDiscardAGenericOwnedPayloadObligation()
    {
        var result = Analyze(Factory("input", 0), Aggregate("inputs"), Set("inputs", "input", 0),
            Compose("proxy", "inputs", AsyncCompositionKind.WhenAll, result: false), Complete("proxy"));
        Assert.Contains(result.Issues, issue => issue.Id == "SS0003");
        Assert.DoesNotContain(result.Blocks[0].Exit.Resources, resource => resource.Kind == ResourceKind.Resource);
    }
}
