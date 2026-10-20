namespace SafeSharp.Tests.Flow;

public sealed class OpaqueAsyncCapabilityTests
{
    private const string Prelude = "using System; using System.Threading.Tasks; using SafeSharp; class Example {";

    [Fact]
    public async Task RepeatedOpaqueTaskAwaitsReturnTheSameValueTaskCapability()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + """
            async Task M(Task<ValueTask<int>> input) {
                var first = await input; await first; var second = await input; await second;
            } }
            """);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0048");
    }

    [Fact]
    public async Task ConfiguredAwaitAndResultPreserveTheSameOpaqueCapability()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + """
            async Task M(Task<ValueTask<int>> input) {
                var first = await input.ConfigureAwait(false); await first; var second = input.Result; await second;
            } }
            """);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0048");
    }

    [Fact]
    public async Task ReturningAnOpaqueTaskWithAConsumedResultIsInvalid()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + """
            async Task<Task<ValueTask<int>>> M(Task<ValueTask<int>> input) {
                var value = await input; await value; return input;
            } }
            """);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0048");
    }

    [Fact]
    public async Task ATaskAliasRetainsItsCachedCapability()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + """
            async Task M(Task<ValueTask<int>> input) {
                var alias = input; var first = await input; await first; var second = await alias; await second;
            } }
            """);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0048");
    }

    [Fact]
    public async Task FreshOpaqueTaskCallsInsideALoopDoNotRetainADeadCapability()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + """
            Task<ValueTask<int>> Get() => throw new Exception();
            async Task M(bool again) {
                while (again) { var input = Get(); var result = await input; await result; }
            } }
            """);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0048");
    }

    [Fact]
    public async Task RepeatedBindingSitesInsideALoopDoNotReviveAnOlderCarrierAlias()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + """
            ValueTask<int> Get() => default;
            async Task M(bool again) {
                Task<ValueTask<int>>? earlier = null;
                while (again) {
                    var value = Get(); var current = Task.FromResult(value);
                    if (earlier is not null) { var previous = await earlier; await previous; }
                    earlier = current; await value;
                }
            } }
            """);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0048");
    }

    [Fact]
    public async Task ACancelableProxyCannotPromiseAnAlreadyConsumedCapability()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + """
            async Task<Task<ValueTask<int>>> M(Task<ValueTask<int>> input, System.Threading.CancellationToken token) {
                var value = await input; await value; return input.WaitAsync(token);
            } }
            """);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0048");
    }

}
