namespace SafeSharp.Tests.Flow;

public sealed class NestedAsyncOwnershipTests
{
    private const string Prelude = """
        using System;
        using System.Threading.Tasks;
        using SafeSharp;
        sealed class Resource : IDisposable { public void Dispose() { } public void Read() { } }
        class Example {
        """;

    [Theory]
    [InlineData("Task.FromResult(Task.FromResult(new Resource()))", 2)]
    [InlineData("Task.FromResult(Task.FromResult(Task.FromResult(new Resource())))", 3)]
    public async Task NestedCompletedCarriersTransferTheTerminalResource(string expression, int layers)
    {
        var type = "Resource";
        for (var i = 0; i < layers; ++i) type = "Task<" + type + ">";
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + type + " M() { return " + expression + "; } }");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id is "SS0003" or "SS0011" or "AD0001");
    }

    [Fact]
    public async Task ASourceSummaryRetainsNestedFreshOwnership()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + """
            Task<Task<Resource>> Create() => Task.FromResult(Task.FromResult(new Resource()));
            async Task M() { var outer = Create(); var inner = await outer; using var result = await inner; }
            }
            """);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task IgnoringANestedTerminalResultRetainsItsOwnershipObligation()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + """
            Task<Task<Resource>> Create() => Task.FromResult(Task.FromResult(new Resource()));
            async Task M() { var outer = Create(); var inner = await outer; var result = await inner; }
            }
            """);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0003");
    }

    [Fact]
    public async Task ASourceWrapperRetainsAnExistingInnerCarrierIdentity()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + """
            Task<Task<Resource>> Wrap(Task<Resource> input) => Task.FromResult(input);
            async Task M() { var original = Task.FromResult(new Resource()); var wrapper = Wrap(original); var inner = await wrapper;
                var first = await inner; first.Dispose(); var second = await original; second.Read(); }
            }
            """);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0001");
    }

    [Fact]
    public async Task NestedBorrowPromisesKeepTheOriginalOwnerAlive()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + """
            Task<Task<Resource>> Wrap(Resource input) => Task.FromResult(Task.FromResult(input));
            async Task M() { var owner = new Resource(); var wrapper = Wrap(owner); owner.Dispose();
                var inner = await wrapper; var result = await inner; result.Read(); }
            }
            """);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0014");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0001");
    }

    [Fact]
    public async Task ADeepBorrowCannotEscapeInsideCompletedCarriers()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + """
            Task<Task<Resource>> M([Borrowed] Resource borrowed) => Task.FromResult(Task.FromResult(borrowed));
            }
            """);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0008");
    }
}
