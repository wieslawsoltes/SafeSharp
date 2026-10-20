namespace SafeSharp.Tests.Flow;

public sealed class BorrowedAsyncResultTests
{
    private const string Prelude = """
        using System;
        using System.Threading.Tasks;
        using SafeSharp;
        sealed class Resource : IDisposable { public void Dispose() { } public void Read() { } }
        abstract class Example {
        """;

    [Theory]
    [InlineData("Task<Resource> Echo(Resource value) => Task.FromResult(value);")]
    [InlineData("ValueTask<Resource> Echo(Resource value) => ValueTask.FromResult(value);")]
    [InlineData("ValueTask<Resource> Echo(Resource value) => new ValueTask<Resource>(value);")]
    [InlineData("ValueTask<Resource> Echo(Resource value) => new ValueTask<Resource>(Task.FromResult(value));")]
    [InlineData("async Task<Resource> Echo(Resource value) { await Task.Yield(); return value; }")]
    [InlineData("async ValueTask<Resource> Echo(Resource value) { await Task.Yield(); return value; }")]
    [InlineData("[ReturnsBorrowed(\"value\")] protected abstract Task<Resource> Echo(Resource value);")]
    [InlineData("[ReturnsBorrowed(\"value\")] protected abstract ValueTask<Resource> Echo(Resource value);")]
    [InlineData("[return: Lifetime(\"a\")] Task<Resource> Echo([Lifetime(\"a\")] Resource value) => Task.FromResult(value);")]
    public async Task AnAsynchronousPayloadBorrowDoesNotGrantDisposalOwnership(string factory)
    {
        var source = Prelude + factory + " void M(Resource owner) { var task = Echo(owner); var result = task.Result; result.Dispose(); } }";
        var disposed = source.IndexOf("result.Dispose()", StringComparison.Ordinal);
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0009" && diagnostic.Location.SourceSpan.Start == disposed);
    }

    [Theory]
    [InlineData("task.Result")]
    [InlineData("task.GetAwaiter().GetResult()")]
    [InlineData("task.ConfigureAwait(false).GetAwaiter().GetResult()")]
    public async Task AStoredPayloadPromiseRetainsItsOwnerLoan(string receive)
    {
        var source = Prelude + "[ReturnsBorrowed(\"value\")] protected abstract Task<Resource> Echo(Resource value);" +
            " void M() { var owner = new Resource(); var task = Echo(owner); owner.Dispose(); var result = " + receive + "; result.Read(); } }";
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0014");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0001" &&
            diagnostic.Location.SourceSpan.Start == source.IndexOf("result.Read()", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AResultLoanRemainsLiveAcrossItsAwait()
    {
        var source = Prelude + "[ReturnsBorrowed(\"value\")] protected abstract Task<Resource> Echo(Resource value);" +
            " async Task M(Resource owner) { var task = Echo(owner); var result = await task; result.Read(); } }";
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0010");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0003");
    }

    [Theory]
    [InlineData("Task<Resource> Echo(Task<Resource> value) => value;")]
    [InlineData("T Echo<T>(T value) => value;")]
    public async Task AnInferredCarrierAliasDoesNotImplyItsPayloadAliasesTheTask(string identity)
    {
        var source = Prelude + identity + " void M(Task<Resource> opaque) { var task = Echo(opaque); var result = task.Result; result.Dispose(); _ = opaque.IsCompleted; } }";
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id is "SS0001" or "SS0009" or "SS0014");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0003");
    }

    [Fact]
    public async Task ACarrierIdentityCallRetainsAKnownOwnedFactory()
    {
        var source = Prelude + "[ReturnsOwned] protected abstract Task<Resource> Create(); T Echo<T>(T value) => value;" +
            " void M() { var pending = Create(); var task = Echo(pending); var result = task.Result; result.Read(); } }";
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0003" &&
            diagnostic.Location.SourceSpan.Start == source.IndexOf("task.Result", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0009");
    }

    [Theory]
    [InlineData("Task.FromResult(new Resource())", "Task<Resource>")]
    [InlineData("ValueTask.FromResult(new Resource())", "ValueTask<Resource>")]
    [InlineData("new ValueTask<Resource>(new Resource())", "ValueTask<Resource>")]
    public async Task TransparentSourceProducersCreateOwnedPayloadSummaries(string producer, string returnType)
    {
        var source = Prelude + returnType + " Create() => " + producer + ";" +
            " async Task M() { var pending = Create(); var alias = pending.ConfigureAwait(false); using var result = await alias; result.Read(); } }";
        Assert.Empty(await AnalyzerTestHost.GetDiagnosticsAsync(source));
    }

    [Theory]
    [InlineData("Task<Resource> Echo(Resource value) { try { return Task.FromResult(value); } finally { value.Dispose(); } }")]
    [InlineData("Task<Resource> Echo(Resource value) { using (value) { return Task.FromResult(value); } }")]
    [InlineData("[ReturnsBorrowed(\"value\")] Task<Resource> Echo(Resource value) { try { return Task.FromResult(value); } finally { value.Dispose(); } }")]
    public async Task AReturnedBorrowedTaskIsValidatedAfterFinallyCleanup(string factory)
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + factory + " }");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0001");
    }

    [Fact]
    public async Task APropertyPayloadContractUsesItsReceiverAsThePayloadOrigin()
    {
        const string source = """
            using System;
            using System.Threading.Tasks;
            using SafeSharp;
            sealed class Resource : IDisposable {
                public void Dispose() { }
                [ReturnsBorrowed("this")] public Task<Resource> Pending => Task.FromResult(this);
            }
            class Example { void M(Resource owner) { var task = owner.Pending; var result = task.Result; result.Dispose(); } }
            """;
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0009");
    }

    [Fact]
    public async Task AConversionPayloadContractRetainsItsParameterOrigin()
    {
        const string source = """
            using System;
            using System.Threading.Tasks;
            using SafeSharp;
            sealed class Resource : IDisposable {
                public void Dispose() { }
                [ReturnsBorrowed("value")] public static implicit operator Task<Resource>(Resource value) => Task.FromResult(value);
            }
            class Example { void M(Resource owner) { Task<Resource> task = owner; var result = task.Result; result.Dispose(); } }
            """;
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0009");
    }

    [Fact]
    public async Task AnOwnedConversionProducesItsPayloadOnlyAfterReceipt()
    {
        const string source = """
            using System;
            using System.Threading.Tasks;
            using SafeSharp;
            [NoSuspend] sealed class Resource : IDisposable { public void Dispose() { } }
            sealed class Factory {
                [ReturnsOwned] public static implicit operator Task<Resource>(Factory value) => Task.FromResult(new Resource());
            }
            class Example { async Task M(Factory source) { Task<Resource> task = source; using var result = await task; } }
            """;
        Assert.Empty(await AnalyzerTestHost.GetDiagnosticsAsync(source));
    }
}
