namespace SafeSharp.Tests.Flow;

public sealed class CustomAwaitableOwnershipTests
{
    private const string Prelude = """
        using System;
        using System.Runtime.CompilerServices;
        using System.Threading.Tasks;
        using SafeSharp;
        sealed class Resource : IDisposable { public void Dispose() { } public void Read() { } }
        """;

    [Theory]
    [InlineData("[ReturnsOwned] public Resource GetResult() => throw new NotImplementedException();")]
    [InlineData("public Resource GetResult() => new Resource();")]
    public async Task CompilerSelectedCustomGetResultOwnershipIsRespected(string getResult)
    {
        var source = Prelude + """
            sealed class Awaitable {
                public Awaiter GetAwaiter() => new();
                public readonly struct Awaiter : INotifyCompletion {
                    public bool IsCompleted => true;
                    public void OnCompleted(Action continuation) { }
            """ + getResult + """
                }
            }
            class Example { async Task M() { var result = await new Awaitable(); } }
            """;
        Assert.Contains(await AnalyzerTestHost.GetDiagnosticsAsync(source), diagnostic => diagnostic.Id == "SS0003");
    }

    [Fact]
    public async Task DisposingTheCustomGetResultOwnedOutputCompletesItsObligation()
    {
        var source = Prelude + """
            sealed class Awaitable {
                public Awaiter GetAwaiter() => new();
                public readonly struct Awaiter : INotifyCompletion {
                    public bool IsCompleted => true;
                    public void OnCompleted(Action continuation) { }
                    public Resource GetResult() => new Resource();
                }
            }
            class Example { async Task M() { using var result = await new Awaitable(); } }
            """;
        Assert.Empty(await AnalyzerTestHost.GetDiagnosticsAsync(source));
    }

    [Fact]
    public async Task OpaqueCustomGetResultDoesNotInventOwnership()
    {
        var source = Prelude + """
            abstract class Awaitable : INotifyCompletion {
                public Awaitable GetAwaiter() => this;
                public bool IsCompleted => true;
                public void OnCompleted(Action continuation) { }
                public abstract Resource GetResult();
            }
            class Example { async Task M(Awaitable input) { var result = await input; } }
            """;
        Assert.DoesNotContain(await AnalyzerTestHost.GetDiagnosticsAsync(source), diagnostic => diagnostic.Id is "SS0003" or "AD0001");
    }

    [Fact]
    public async Task ReceiverBoundCustomAwaitPatternRetainsBorrowedOutput()
    {
        var source = Prelude + """
            abstract class Awaitable : IDisposable, INotifyCompletion {
                [ReturnsBorrowed("this")] public Awaitable GetAwaiter() => this;
                public bool IsCompleted => true;
                public void OnCompleted(Action continuation) { }
                [ReturnsBorrowed("this")] public abstract Resource GetResult();
                public void Dispose() { }
            }
            class Example { async Task M(Awaitable input) { var result = await input; result.Dispose(); } }
            """;
        Assert.Contains(await AnalyzerTestHost.GetDiagnosticsAsync(source), diagnostic => diagnostic.Id == "SS0009");
    }
}
