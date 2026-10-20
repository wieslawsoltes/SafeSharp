namespace SafeSharp.Tests.Flow;

public sealed class TaskCompositionOwnershipTests
{
    private const string Prelude = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using SafeSharp;
        sealed class Resource : IDisposable { public void Dispose() { } public void Read() { } }
        sealed class AsyncResource : IAsyncDisposable { public ValueTask DisposeAsync() => default; }
        class Example {
        [return: Owned] static Task<Resource> Create() => throw new Exception();
        """;

    [Fact]
    public async Task WhenAllResultElementsRetainTheirIndividualOwners()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + """
            async Task M() {
                var results = await Task.WhenAll(Create(), Create());
                results[0].Dispose(); results[1].Dispose();
            }
            }
            """);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task IgnoringOneWhenAllResultKeepsItsLeakObligation()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + """
            async Task M() { var results = await Task.WhenAll(Create(), Create()); results[0].Dispose(); }
            }
            """);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0003");
    }

    [Fact]
    public async Task ReturningWhenAllTransfersTheRepresentedProducerResponsibilities()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + """
            Task<Resource[]> M() => Task.WhenAll(Create(), Create());
            }
            """);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id is "SS0003" or "SS0011" or "AD0001");
    }

    [Fact]
    public async Task WhenAllDoesNotCreateFreshCopiesOfExistingResults()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + """
            async Task M() {
                var input = Task.FromResult(new Resource());
                var results = await Task.WhenAll(input); results[0].Dispose();
                var original = await input; original.Read();
            }
            }
            """);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0001");
    }

    [Fact]
    public async Task RepeatedWhenAllAwaitsShareOneResultArrayAndPayloadSet()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + """
            async Task M() {
                var combined = Task.WhenAll(Create());
                var first = await combined; first[0].Dispose();
                var second = await combined; second[0].Read();
            }
            }
            """);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0001");
    }

    [Fact]
    public async Task WhenAllCapturesInputsBeforeArrayAliasesAreMutated()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + """
            async Task M() {
                var first = Task.FromResult(new Resource()); var second = Task.FromResult(new Resource());
                var inputs = new[] { first }; var alias = inputs; var combined = Task.WhenAll(inputs);
                alias[0] = second; var results = await combined; results[0].Dispose();
                var firstValue = await first; firstValue.Read(); using var secondValue = await second;
            }
            }
            """);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0001");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id is "SS0003" or "SS0011");
    }

    [Fact]
    public async Task WhenAllFailureKeepsUnreceivedProducerObligations()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + """
            async Task M() {
                var input = Create();
                try { var results = await Task.WhenAll(input); results[0].Dispose(); }
                catch (Exception) { }
            }
            }
            """);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0003");
    }

    [Fact]
    public async Task NonGenericWhenAllDoesNotHideAResultDiscardedByTaskConversion()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + """
            async Task M() { var input = Create(); await Task.WhenAll(new Task[] { input }); }
            }
            """);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0003");
    }

    [Theory]
    [InlineData("await Task.WhenAll(first, second);")]
    [InlineData("Task.WhenAll(first, second).GetAwaiter().GetResult();")]
    [InlineData("Task.WhenAll(first, second).Wait();")]
    public async Task WhenAllObservesAllAsynchronousCleanupCompletions(string observation)
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + """
            async Task M() {
                var one = new AsyncResource(); var two = new AsyncResource();
                var first = one.DisposeAsync().AsTask(); var second = two.DisposeAsync().AsTask();
            """ + observation + " } }");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id is "SS0003" or "SS0017" or "SS0048" or "AD0001");
    }

    [Fact]
    public async Task FailedWhenAllStillObservesAllCleanupCompletions()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + """
            async Task M() {
                var owner = new AsyncResource(); var completion = owner.DisposeAsync().AsTask();
                var combined = Task.WhenAll(completion);
                try { await combined; } catch (Exception) { }
            }
            }
            """);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id is "SS0003" or "SS0017" or "SS0048" or "AD0001");
    }

    [Fact]
    public async Task WhenAnySingleInputReturnsThatInputWithoutReceivingItsPayload()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + """
            async Task M() { var winner = await Task.WhenAny(Create()); using var result = await winner; }
            }
            """);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task AwaitingWhenAnyWithoutAwaitingTheWinnerKeepsTheInputObligation()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + """
            async Task M() { var winner = await Task.WhenAny(Create()); }
            }
            """);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0003");
    }

    [Fact]
    public async Task AWhenAnyWinnerDoesNotDischargeItsLosingProducer()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + """
            async Task M() { var winner = await Task.WhenAny(Create(), Create()); using var result = await winner; }
            }
            """);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id is "SS0003" or "SS0011");
    }

    [Fact]
    public async Task ReturningWhenAnyKeepsPossibleLoserResponsibilities()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + """
            Task<Task<Resource>> M() => Task.WhenAny(Create(), Create());
            }
            """);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0003");
    }

    [Fact]
    public async Task UnwrapPreservesNestedExistingResourceIdentity()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + """
            async Task M() {
                var owner = new Resource(); var inner = Task.FromResult(owner); var outer = Task.FromResult(inner);
                var result = await outer.Unwrap(); result.Dispose(); owner.Read();
            }
            }
            """);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0001");
    }

    [Fact]
    public async Task UnwrapReceivesSourceProvenNestedFreshOwnership()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + """
            Task<Task<Resource>> Nested() => Task.FromResult(Task.FromResult(new Resource()));
            async Task M() { using var result = await Nested().Unwrap(); }
            }
            """);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task UnwrapCanObserveAnInnerDisposalCompletion()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + """
            async Task M() { var owner = new AsyncResource(); var completion = owner.DisposeAsync().AsTask();
                await Task.FromResult(completion).Unwrap(); }
            }
            """);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id is "SS0003" or "SS0017" or "SS0048" or "AD0001");
    }

    [Fact]
    public async Task WaitAsyncSuccessPreservesItsInputResultIdentity()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + """
            async Task M(CancellationToken token) {
                var input = Task.FromResult(new Resource()); var result = await input.WaitAsync(token);
                result.Dispose(); var again = await input; again.Read();
            }
            }
            """);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0001");
    }

    [Theory]
    [InlineData("token", "OperationCanceledException")]
    [InlineData("TimeSpan.FromSeconds(1)", "TimeoutException")]
    public async Task StoppingWaitAsyncDoesNotDischargeTheUnderlyingProducer(string argument, string failure)
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + """
            async Task M(CancellationToken token) { var input = Create();
            """ + "try { using var result = await input.WaitAsync(" + argument + "); } catch (" + failure + ") { } } }");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0003");
    }

    [Fact]
    public async Task ReturningWaitAsyncLeavesTheCancelableSourceObligationVisible()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + """
            Task<Resource> M(CancellationToken token) => Create().WaitAsync(token);
            }
            """);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0003");
    }

    [Fact]
    public async Task CancelingAWaitDoesNotObserveAsyncCleanup()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + """
            async Task M(CancellationToken token) { var owner = new AsyncResource(); var completion = owner.DisposeAsync().AsTask();
                try { await completion.WaitAsync(token); } catch (OperationCanceledException) { } }
            }
            """);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0017");
    }

    [Fact]
    public async Task ArrayAliasesPreserveValueTaskCapabilities()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + """
            ValueTask<int> Get() => default;
            async Task M() { var values = new[] { Get() }; var alias = values; await alias[0]; await values[0]; }
            }
            """);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0048");
    }

    [Fact]
    public async Task ConsumedValueTaskCannotBeReturnedInsideAnAggregate()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + """
            ValueTask<int> Get() => default;
            async Task<ValueTask<int>[]> M() { var value = Get(); await value; return new[] { value }; }
            }
            """);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0048");
    }

    [Fact]
    public async Task AnUnknownWaitAsyncOutputRetainsItsNoSuspendTypeContract()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync("""
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using SafeSharp;
            [NoSuspend] sealed class Guard : IDisposable { public void Dispose() { } public void Read() { } }
            class Example {
                async Task M(Task<Guard> input, CancellationToken token) {
                    var result = await input.WaitAsync(token); await Task.Yield(); result.Read();
                }
            }
            """);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0016");
    }

    [Fact]
    public async Task AggregatePayloadsRemainVisibleAcrossSuspension()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync("""
            using System;
            using System.Threading.Tasks;
            using SafeSharp;
            [NoSuspend] sealed class Guard : IDisposable { public void Dispose() { } }
            class Example {
                async Task M() {
                    var values = new[] { new Guard() }; await Task.Yield(); values[0].Dispose();
                }
            }
            """);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0016");
    }

    [Fact]
    public async Task ALocalCarrierMovePreservesItsNestedValueTaskForTheDestination()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + """
            ValueTask<int> Get() => default;
            async Task M() {
                var value = Get(); var carrier = Task.FromResult(value); var moved = Ownership.Move(carrier);
                var received = await moved; await received;
            }
            }
            """);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id is "SS0004" or "SS0048");
    }

    [Fact]
    public async Task ALocalCarrierMoveInvalidatesPriorNestedValueTaskCopies()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + """
            ValueTask<int> Get() => default;
            async Task M() {
                var value = Get(); var carrier = Task.FromResult(value); var moved = Ownership.Move(carrier);
                await value; var received = await moved; await received;
            }
            }
            """);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0048");
    }

    [Fact]
    public async Task ReturningAConsumedValueTaskInsideACarrierIsInvalid()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + """
            ValueTask<int> Get() => default;
            async Task<Task<ValueTask<int>>> M() {
                var value = Get(); await value; return Task.FromResult(value);
            }
            }
            """);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0048");
    }

}
