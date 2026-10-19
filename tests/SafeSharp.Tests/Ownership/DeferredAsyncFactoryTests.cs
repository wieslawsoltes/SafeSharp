namespace SafeSharp.Tests.Flow;

public sealed class DeferredAsyncFactoryTests
{
    private const string Prelude = """
        using System;
        using System.Threading.Tasks;
        using SafeSharp;
        sealed class Resource : IDisposable { public void Dispose() { } public void Read() { } }
        abstract class Example {
            [ReturnsOwned] async Task<Resource> Create() { await Task.Yield(); return new Resource(); }
            [ReturnsOwned] async ValueTask<Resource> CreateValue() { await Task.Yield(); return new Resource(); }
            async Task<Resource> Inferred() { await Task.Yield(); return new Resource(); }
            async ValueTask<Resource> InferredValue() { await Task.Yield(); return new Resource(); }
            protected abstract void Consume([Consumes] Task<Resource> task);
        """;

    [Theory]
    [InlineData("Create()")]
    [InlineData("CreateValue()")]
    [InlineData("Inferred()")]
    [InlineData("InferredValue()")]
    public async Task StoredOwnedFactoryResultsAreAcquiredOnlyWhenReceived(string factory)
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude +
            "async Task M() { var pending = " + factory + "; var alias = pending; using var value = await alias; value.Read(); } }");
        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData("Create().ConfigureAwait(false)", "await pending")]
    [InlineData("CreateValue().ConfigureAwait(false)", "await pending")]
    [InlineData("CreateValue().AsTask()", "await pending")]
    [InlineData("CreateValue().AsTask().ConfigureAwait(false)", "await pending")]
    [InlineData("Create().GetAwaiter()", "pending.GetResult()")]
    [InlineData("CreateValue().GetAwaiter()", "pending.GetResult()")]
    [InlineData("Create().ConfigureAwait(false).GetAwaiter()", "pending.GetResult()")]
    public async Task StoredFrameworkWrappersRetainTheFactoryCarrier(string expression, string receive)
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude +
            "async Task M() { var pending = " + expression + "; using var value = " + receive + "; value.Read(); } }");
        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData("Create();")]
    [InlineData("_ = Create();")]
    [InlineData("var pending = Create();")]
    [InlineData("var pending = CreateValue();")]
    [InlineData("var pending = Create().ConfigureAwait(false);")]
    [InlineData("var pending = CreateValue().AsTask();")]
    [InlineData("var pending = Create(); pending.Wait();")]
    [InlineData("var pending = Create(); pending.Dispose();")]
    [InlineData("var pending = Create(); _ = pending.IsCompleted;")]
    public async Task DroppedFactoryCarrierRetainsPossibleOwnedResultObligation(string body)
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + "void M() { " + body + " } }");
        var issue = Assert.Single(diagnostics, diagnostic => diagnostic.Id == "SS0003");
        Assert.Contains("may produce an owned result", issue.GetMessage());
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0017");
    }

    [Theory]
    [InlineData("await pending;")]
    [InlineData("_ = await pending;")]
    [InlineData("_ = pending.Result;")]
    [InlineData("pending.GetAwaiter().GetResult();")]
    [InlineData("pending.ConfigureAwait(false).GetAwaiter().GetResult();")]
    public async Task ObservingSuccessfulCompletionStillRequiresCleanupOfDiscardedResult(string receive)
    {
        var source = Prelude + "async Task M() { var pending = Create(); " + receive + " } }";
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        var issue = Assert.Single(diagnostics, diagnostic => diagnostic.Id == "SS0003");
        Assert.DoesNotContain("may produce an owned result", issue.GetMessage());
        Assert.True(issue.Location.SourceSpan.Start >= source.IndexOf(receive, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Create()")]
    [InlineData("CreateValue().AsTask()")]
    public async Task RepeatedAwaitReturnsTheSamePayloadIdentity(string factory)
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude +
            "async Task M() { var pending = " + factory + "; var first = await pending; var second = await pending; first.Read(); second.Read(); first.Dispose(); } }");
        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData("await pending")]
    [InlineData("pending.Result")]
    [InlineData("pending.GetAwaiter().GetResult()")]
    [InlineData("await pending.ConfigureAwait(false)")]
    public async Task DisposedResultStaysDisposedThroughLaterReception(string receive)
    {
        var source = Prelude + "async Task M() { var pending = Create(); var first = await pending; first.Dispose(); var again = " + receive + "; again.Read(); } }";
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        var issue = Assert.Single(diagnostics, diagnostic => diagnostic.Id == "SS0001");
        Assert.Equal("again.Read()", source.Substring(issue.Location.SourceSpan.Start, issue.Location.SourceSpan.Length));
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0003");
    }

    [Fact]
    public async Task MovingThePayloadDoesNotPermitReceivingFreshOwnershipFromItsOldTask()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude +
            "async Task M() { var pending = Create(); var first = await pending; using var owner = Ownership.Move(first); var stale = await pending; stale.Read(); } }");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0004");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0003");
    }

    [Theory]
    [InlineData("Task<Resource> M() { var pending = Create(); return pending; }")]
    [InlineData("Task<Resource> M() { var pending = CreateValue(); return pending.AsTask(); }")]
    [InlineData("ValueTask<Resource> M() { var pending = CreateValue(); return pending; }")]
    [InlineData("void M() { var pending = Create(); Consume(pending); }")]
    [InlineData("async Task M() { var pending = Create(); var moved = Ownership.Move(pending); using var value = await moved; }")]
    public async Task TransferringTheCarrierTransfersTheUnproducedResultObligation(string member)
    {
        Assert.Empty(await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + member + " }"));
    }

    [Fact]
    public async Task ConsumedCarrierAliasesRemainUnavailable()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude +
            "async Task M() { var pending = Create(); var alias = pending; Consume(pending); using var value = await alias; } }");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0004");
    }

    [Theory]
    [InlineData("async Task<Task<Resource>> M() { var pending = Create(); var value = await pending; return pending; }")]
    [InlineData("async Task M() { var pending = Create(); var value = await pending; Consume(pending); }")]
    [InlineData("async Task M() { var pending = Create(); var first = await pending; var moved = Ownership.Move(pending); using var value = await moved; }")]
    public async Task MaterializedCarrierTransfersItsPayloadResponsibility(string member)
    {
        Assert.Empty(await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + member + " }"));
    }

    [Fact]
    public async Task ReturningACarrierAfterCleanupRejectsItsDisposedPayload()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude +
            "async Task<Task<Resource>> M() { var pending = Create(); using var value = await pending; return pending; } }");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0001");
    }

    [Fact]
    public async Task ConsumingAMaterializedCarrierInvalidatesThePreviouslyReceivedPayload()
    {
        var source = Prelude + "async Task M() { var pending = Create(); var value = await pending; Consume(pending); value.Read(); } }";
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0004" &&
            source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length) == "value.Read()");
    }

    [Fact]
    public async Task BorrowingAPayloadPreventsConsumingItsCarrierUntilTheLoanEnds()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude +
            "async Task M() { var pending = Create(); var value = await pending; var loan = Ownership.Borrow(value); Consume(pending); loan.Read(); } }");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0005");
    }

    [Theory]
    [InlineData("var pending = choose ? Create() : Inferred();")]
    [InlineData("Task<Resource> pending; if (choose) pending = Create(); else pending = Inferred();")]
    public async Task ExclusiveBranchFactoriesDoNotInventUnreceivedOrUndisposedAlternatives(string setup)
    {
        Assert.Empty(await AnalyzerTestHost.GetDiagnosticsAsync(Prelude +
            "async Task M(bool choose) { " + setup + " using var value = await pending; value.Read(); } }"));
    }

    [Fact]
    public async Task ConditionalReceptionRetainsTheUnreceivedCarrierPath()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude +
            "async Task M(bool receive) { var pending = Create(); if (receive) { using var value = await pending; } } }");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0003" && diagnostic.GetMessage().Contains("may produce an owned result", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFinallyObservingTheCarrierCanDischargeItsResultAfterAStoredCall()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude +
            "async Task M() { var pending = Create(); try { await Task.Yield(); } finally { using var value = await pending; } } }");
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task CaughtAwaitFailureDoesNotInventTheSuccessfulResultObligation()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude +
            "async Task M() { var pending = Create(); try { using var value = await pending; } catch (Exception) { } } }");
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task ReplacingPendingCarrierKeepsEarlierFactoryEvidence()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude +
            "async Task M() { var pending = Create(); pending = Inferred(); using var value = await pending; } }");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0011");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0003");
    }

    [Fact]
    public async Task ReplacingOneAliasDoesNotLoseAStillUsedCarrier()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude +
            "async Task M() { var pending = Create(); var original = pending; pending = Inferred(); using var first = await original; using var second = await pending; } }");
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task EveryLoopIterationReceivesAndDisposesItsOwnFactoryResult()
    {
        Assert.Empty(await AnalyzerTestHost.GetDiagnosticsAsync(Prelude +
            "async Task M(int count) { while (count-- > 0) { var pending = Create(); using var value = await pending; } } }"));
    }

    [Fact]
    public async Task ResultNoSuspendContractIsInactiveDuringTheCarrierWait()
    {
        var source = Prelude.Replace("sealed class Resource", "[NoSuspend] sealed class Resource", StringComparison.Ordinal) +
            "async Task M() { var pending = Create(); await Task.Yield(); using var value = await pending; value.Read(); } }";
        Assert.Empty(await AnalyzerTestHost.GetDiagnosticsAsync(source));
    }

    [Fact]
    public async Task ResultNoSuspendContractRemainsActiveBehindAReusedCarrier()
    {
        var source = Prelude.Replace("sealed class Resource", "[NoSuspend] sealed class Resource", StringComparison.Ordinal) +
            "async Task M() { var pending = Create(); var value = await pending; await Task.Yield(); using var alias = await pending; } }";
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0016");
    }

    [Fact]
    public async Task OpaqueTaskResultIsNotAnAliasOfTheTaskObject()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude +
            "async Task M(Task<Resource> external) { using var value = await external; value.Dispose(); _ = external.IsCompleted; } }");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0001");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0002");
    }
}
