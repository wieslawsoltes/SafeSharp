namespace SafeSharp.Tests.Flow;

public sealed class AsyncFactoryTests
{
    private const string Prelude = """
        using System;
        using System.Threading.Tasks;
        using SafeSharp;
        [NoSuspend] sealed class Resource : IDisposable { public void Dispose() { } public void Read() { } }
        class Example {
            [ReturnsOwned] async Task<Resource> Create() { await Task.Yield(); return new Resource(); }
        """;

    [Theory]
    [InlineData("Create()")]
    [InlineData("Create().ConfigureAwait(false)")]
    public async Task OwnedResultIsAcquiredAfterItsAwaitSuspension(string factory)
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + "async Task M() { using var value = await " + factory + "; value.Read(); } }");
        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData("Create()")]
    [InlineData("Create().ConfigureAwait(false)")]
    public async Task AwaitedOwnedResultRetainsItsTypeContracts(string factory)
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + "async Task M() { using var value = await " + factory + "; await Task.Yield(); value.Read(); } }");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0016");
    }

    [Fact]
    public async Task AnUnreleasedAwaitedResultCarriesTheDisposalObligation()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + "async Task M() { var value = await Create(); value.Read(); } }");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0003");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0016");
    }
}
