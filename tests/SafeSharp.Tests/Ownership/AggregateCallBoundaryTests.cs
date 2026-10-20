namespace SafeSharp.Tests.Flow;

public sealed class AggregateCallBoundaryTests
{
    private const string Prelude = """
        using System;
        using System.Threading.Tasks;
        using SafeSharp;
        sealed class Resource : IDisposable { public void Dispose() { } public void Read() { } }
        interface IUnknown { void Change(Resource[] values); }
        sealed class Constructor { public Constructor(Resource[] values) { values[0] = null!; } }
        class Example {
            static void Change(Resource[] values) { values[0] = null!; }
            static void ChangeObject(object values) { ((Resource[])values)[0] = null!; }
            static void ChangeThenThrow(Resource[] values) { values[0] = null!; throw new Exception(); }
            static void Inspect([Borrowed] Resource[] values) { _ = values.Length; }
        """;

    [Theory]
    [InlineData("Change(values);")]
    [InlineData("unknown.Change(values);")]
    [InlineData("ChangeObject(values);")]
    [InlineData("object boxed = values; ChangeObject(boxed);")]
    [InlineData("_ = new Constructor(values);")]
    [InlineData("values.SetValue(null, 0);")]
    public async Task UnknownArrayChangesCannotProveCleanupOfTheOriginalElement(string change)
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude +
            "void M(IUnknown unknown) { var values = new[] { new Resource() }; " + change + " values[0].Dispose(); } }");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0003");
    }

    [Fact]
    public async Task AThrowingCallMayHaveAlreadyReplacedTheArrayElement()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + """
            void M() { var values = new[] { new Resource() };
                try { ChangeThenThrow(values); } catch (Exception) { }
                values[0].Dispose();
            } }
            """);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0003");
    }

    [Fact]
    public async Task AnIndependentValueSnapshotCanStillCleanTheOriginalElement()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + """
            void M() { var values = new[] { new Resource() }; var original = values[0]; Change(values); original.Dispose(); }
            }
            """);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id is "SS0003" or "SS0011");
    }

    [Fact]
    public async Task AnOpaqueCallDoesNotChangeTheTasksAlreadyCapturedByWhenAll()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + """
            static void ReplaceTask(Task<Resource>[] values) { values[0] = null!; }
            async Task M() {
                var inputs = new[] { Task.FromResult(new Resource()) };
                var combined = Task.WhenAll(inputs);
                ReplaceTask(inputs);
                var results = await combined; results[0].Dispose();
            } }
            """);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id is "SS0003" or "SS0011");
    }

    [Fact]
    public async Task SharedBorrowContractsPreserveRepresentedElementContents()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + """
            void M() { var values = new[] { new Resource() }; Inspect(values); values[0].Dispose(); }
            }
            """);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id is "SS0003" or "SS0011");
    }

    [Theory]
    [InlineData("_ = values.GetLength(0);")]
    [InlineData("_ = values.GetUpperBound(0);")]
    [InlineData("_ = values.Length;")]
    public async Task TrustedArrayQueriesPreserveElementIdentities(string query)
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude +
            "void M() { var values = new[] { new Resource() }; " + query + " values[0].Dispose(); } }");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id is "SS0003" or "SS0011");
    }

    [Fact]
    public async Task ASourceHelperWithoutAnElementEffectSummaryIsConservative()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + """
            static void OpaqueRead(Resource[] values) { _ = values.Length; }
            void M() { var values = new[] { new Resource() }; OpaqueRead(values); values[0].Dispose(); }
            }
            """);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0003");
    }
}
