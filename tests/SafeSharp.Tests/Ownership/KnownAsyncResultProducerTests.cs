namespace SafeSharp.Tests.Flow;

public sealed class KnownAsyncResultProducerTests
{
    private const string Prelude = """
        using System;
        using System.Threading.Tasks;
        using SafeSharp;
        sealed class Resource : IDisposable { public void Dispose() { } public void Read() { } }
        class Example {
            [ReturnsOwned] async Task<Resource> Create() { await Task.Yield(); return new Resource(); }
        """;

    [Theory]
    [InlineData("Task.FromResult(original)")]
    [InlineData("ValueTask.FromResult(original)")]
    [InlineData("new ValueTask<Resource>(original)")]
    public async Task CompletedFrameworkProducersPreserveExistingOwnedOutput(string producer)
    {
        Assert.Empty(await AnalyzerTestHost.GetDiagnosticsAsync(Prelude +
            "async Task M() { var original = new Resource(); var pending = " + producer + "; using var received = await pending; original.Read(); received.Read(); } }"));
    }

    [Theory]
    [InlineData("Task.FromResult(new Resource())")]
    [InlineData("ValueTask.FromResult(new Resource())")]
    [InlineData("new ValueTask<Resource>(new Resource())")]
    public async Task TemporaryResultOwnershipRemainsReachableThroughTheProducer(string producer)
    {
        Assert.Empty(await AnalyzerTestHost.GetDiagnosticsAsync(Prelude +
            "async Task M() { var pending = " + producer + "; using var value = await pending; } }"));
    }

    [Theory]
    [InlineData("Task.FromResult(original)")]
    [InlineData("ValueTask.FromResult(original)")]
    [InlineData("new ValueTask<Resource>(original)")]
    public async Task ReleasingTheOriginalReleasesTheSameAwaitedResult(string producer)
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude +
            "async Task M() { var original = new Resource(); var pending = " + producer + "; original.Dispose(); var value = await pending; value.Read(); } }");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0001");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0003");
    }

    [Theory]
    [InlineData("Task.FromResult(new Resource())", "Task<Resource>")]
    [InlineData("ValueTask.FromResult(new Resource())", "ValueTask<Resource>")]
    [InlineData("new ValueTask<Resource>(new Resource())", "ValueTask<Resource>")]
    public async Task ADeclaredFactoryCanReturnACompletedOwnedProducer(string producer, string type)
    {
        Assert.Empty(await AnalyzerTestHost.GetDiagnosticsAsync(Prelude +
            "[ReturnsOwned] " + type + " Completed() => " + producer + "; async Task M() { using var value = await Completed(); } }"));
    }

    [Fact]
    public async Task AValueTaskConstructorCanWrapAnExistingDeferredTaskCarrier()
    {
        Assert.Empty(await AnalyzerTestHost.GetDiagnosticsAsync(Prelude +
            "async Task M() { var pending = Create(); var wrapped = new ValueTask<Resource>(pending); using var value = await wrapped.ConfigureAwait(false); } }"));
    }

    [Fact]
    public async Task TheTaskConstructorWrapperPreservesPreviouslyDisposedPayloads()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude +
            "async Task M() { var pending = Create(); var value = await pending; value.Dispose(); var wrapped = new ValueTask<Resource>(pending); var stale = await wrapped; stale.Read(); } }");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0001");
    }

    [Fact]
    public async Task APreservedValueTaskRetainsItsDeferredOwnedResult()
    {
        Assert.Empty(await AnalyzerTestHost.GetDiagnosticsAsync(Prelude +
            "async Task M() { var task = new ValueTask<Resource>(Create()).Preserve(); using var value = await task; } }"));
    }

    [Fact]
    public async Task APreservedValueTaskRecoversTheSameResultOnRepeatedAwait()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude +
            "async Task M() { var task = new ValueTask<Resource>(Create()).Preserve(); var first = await task; first.Dispose(); var second = await task; second.Read(); } }");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0001");
    }

    [Theory]
    [InlineData("Task.FromResult<Resource?>(null)")]
    [InlineData("new ValueTask<Resource?>((Resource?)null)")]
    public async Task ExplicitNullProducesNoResourceObligation(string producer)
    {
        Assert.Empty(await AnalyzerTestHost.GetDiagnosticsAsync(Prelude +
            "async Task M() { var pending = " + producer + "; var value = await pending; if (value is not null) value.Dispose(); } }"));
    }

    [Fact]
    public async Task WrappingABorrowedParameterDoesNotGrantOwnership()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude +
            "Task<Resource>? stored; void M([Borrowed] Resource borrowed) { stored = Task.FromResult(borrowed); } }");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0008");
    }

    [Fact]
    public async Task ALoanRetainedByACompletedTaskConflictsWithPrematureOwnerCleanup()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude +
            "async Task M() { var owner = new Resource(); var task = Task.FromResult(Ownership.Borrow(owner)); owner.Dispose(); var loan = await task; loan.Read(); } }");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0014");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0010");
    }

    [Fact]
    public async Task AnAlreadyCreatedNoSuspendResultIsLiveBeforeItsFirstAwait()
    {
        var source = Prelude.Replace("sealed class Resource", "[NoSuspend] sealed class Resource", StringComparison.Ordinal) +
            "async Task M() { var task = Task.FromResult(new Resource()); using var value = await task; } }";
        Assert.Contains(await AnalyzerTestHost.GetDiagnosticsAsync(source), diagnostic => diagnostic.Id == "SS0016");
    }

    [Fact]
    public async Task UserAwaitersDoNotAcquireFrameworkFactorySemantics()
    {
        const string source = """
            using System;
            using System.Runtime.CompilerServices;
            using System.Threading.Tasks;
            using SafeSharp;
            sealed class Resource : IDisposable { public void Dispose() { } }
            sealed class UserAwaitable {
                public Awaiter GetAwaiter() => new();
                public readonly struct Awaiter : INotifyCompletion {
                    public bool IsCompleted => true;
                    public void OnCompleted(Action continuation) { }
                    public Resource GetResult() => new();
                }
            }
            abstract class Example {
                [ReturnsOwned] protected abstract Task<Resource> Create();
                [ReturnsBorrowed("task")] static UserAwaitable Wrap(Task<Resource> task) => new();
                async Task M() { var task = Create(); using var value = await Wrap(task); }
            }
            """;
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0003" && diagnostic.GetMessage().Contains("may produce an owned result", StringComparison.Ordinal));
    }
}
