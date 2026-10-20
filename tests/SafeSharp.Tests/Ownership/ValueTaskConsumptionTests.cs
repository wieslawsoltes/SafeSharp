namespace SafeSharp.Tests.Flow;

public sealed class ValueTaskConsumptionTests
{
    private const string Prelude = """
        using System;
        using System.Threading.Tasks;
        using SafeSharp;
        abstract class Example {
            protected abstract ValueTask Work();
            protected abstract ValueTask<int> Number();
            protected abstract Task<int> Reusable();
        """;

    [Theory]
    [InlineData("var task = Work(); await task; await task;")]
    [InlineData("var task = Number(); var first = await task; var second = await task;")]
    [InlineData("var task = Work(); var alias = task; await task; await alias;")]
    [InlineData("var task = Number(); var alias = task.ConfigureAwait(false); await task; await alias;")]
    [InlineData("var task = Number(); var awaiter = task.GetAwaiter(); await task; _ = awaiter.GetResult();")]
    [InlineData("var task = Number(); _ = task.Result; _ = task.Result;")]
    [InlineData("var task = Work(); var first = task.AsTask(); var second = task.AsTask(); await first; await second;")]
    [InlineData("var task = Work(); var first = task.AsTask(); await task; await first;")]
    [InlineData("var task = Work(); var first = task.Preserve(); await task; await first;")]
    public async Task RepeatedConsumptionRetainsCapabilityIdentity(string body)
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + "async Task M() { " + body + " } }");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0048");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "AD0001");
    }

    [Theory]
    [InlineData("await Work(); await Work();")]
    [InlineData("var task = Work().AsTask(); await task; await task;")]
    [InlineData("var task = Number().AsTask(); _ = await task; _ = await task;")]
    [InlineData("var task = Work().Preserve(); await task; await task;")]
    [InlineData("var task = Number().Preserve(); _ = await task; _ = await task;")]
    [InlineData("var task = Reusable(); _ = await task; _ = await task;")]
    [InlineData("var task = Work(); if (flag) await task; else await task;")]
    [InlineData("for (int i = 0; i < 5; ++i) { var task = Work(); await task; }")]
    [InlineData("var task = Work(); await task; task = Work(); await task;")]
    public async Task FreshAndReusableCapabilitiesAreAccepted(string body)
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + "async Task M(bool flag) { " + body + " } }");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id is "SS0048" or "AD0001");
    }

    [Fact]
    public async Task AConditionalConsumptionRemainsPossibleAfterTheJoin()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude +
            "async Task M(bool flag) { var task = Work(); if (flag) await task; await task; } }");
        var issue = Assert.Single(diagnostics, diagnostic => diagnostic.Id == "SS0048");
        Assert.Equal("Possible", issue.Properties["SafeSharp.Certainty"]);
        Assert.NotEmpty(issue.AdditionalLocations);
    }

    [Fact]
    public async Task ARepeatedLoopConsumptionIsNotResetAtTheLoopHeader()
    {
        Assert.Contains(await AnalyzerTestHost.GetDiagnosticsAsync(Prelude +
            "async Task M() { var task = Work(); for (int i = 0; i < 5; ++i) await task; } }"), diagnostic => diagnostic.Id == "SS0048");
    }

    [Fact]
    public async Task AFailedAwaitStillConsumesTheCapability()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude +
            "async Task M() { var task = Work(); try { await task; } catch { await task; } } }");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0048");
    }

    [Fact]
    public async Task InputParametersCarrySingleConsumptionCapabilities()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude +
            "async Task M(ValueTask<int> task) { _ = await task; _ = await task; } }");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0048");
    }

    [Fact]
    public async Task ValueTaskMovePreservesTheDestinationAndInvalidatesCopies()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude +
            "async Task M() { var task = Work(); var alias = task; var moved = Ownership.Move(task); await moved; await alias; } }");
        Assert.Single(diagnostics, diagnostic => diagnostic.Id == "SS0048");
    }

    [Fact]
    public async Task ValueTaskDisposalCompletionIsNotReusableUntilConverted()
    {
        const string source = """
            using System;
            using System.Threading.Tasks;
            sealed class Resource : IAsyncDisposable { public ValueTask DisposeAsync() => default; }
            class Example {
                async Task M() { var resource = new Resource(); var completion = resource.DisposeAsync(); await completion; await completion; }
            }
            """;
        Assert.Contains(await AnalyzerTestHost.GetDiagnosticsAsync(source), diagnostic => diagnostic.Id == "SS0048");
    }

    [Fact]
    public async Task PreservingDoesNotInventAFreshDisposablePayload()
    {
        const string source = """
            using System;
            using System.Threading.Tasks;
            using SafeSharp;
            sealed class Resource : IDisposable { public void Dispose() { } public void Read() { } }
            abstract class Example {
                [ReturnsOwned] protected abstract ValueTask<Resource> Create();
                async Task M() { var task = Create().Preserve(); var first = await task; first.Dispose(); var second = await task; second.Read(); }
            }
            """;
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0001");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0048");
    }

    [Fact]
    public async Task ATaskOfValueTaskRetainsItsSingleConsumptionPayload()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude +
            "async Task M() { var outer = Task.FromResult(Number()); var first = await outer; _ = await first; var second = await outer; _ = await second; } }");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0048");
    }
}
