namespace SafeSharp.Tests.Flow;

/// <summary>Storage identity checks through real Roslyn CFGs and the ownership engine.</summary>
public sealed class ReferenceStorageOwnershipTests
{
    private const string Prelude = """
        using System;
        using System.Threading.Tasks;
        using SafeSharp;
        sealed class Resource : IDisposable {
            public void Dispose() { }
            public void Read() { }
        }
        class Example {
            static ref Resource Identity(ref Resource input) => ref input;
            static ref readonly Resource ReadOnly(in Resource input) => ref input;
            static ref Resource First(ref Resource input, Resource ignored) => ref input;
            static ref Resource Select(bool choose, ref Resource first, ref Resource second) => ref (choose ? ref first : ref second);
            static void Fill([Owned] out Resource output, Resource ignored) => output = new Resource();
            static ref ValueTask<int> TaskIdentity(ref ValueTask<int> input) => ref input;
            static void Replace(ref Resource input, Resource replacement) => input = replacement;
            static void Reset(ref Resource input) => input = new Resource();
            static ref Resource ResetAndReturn(ref Resource input) { input = new Resource(); return ref input; }
            static ref Resource Cleanup(ref Resource input, Resource replacement) { try { return ref input; } finally { input = replacement; } }
            static Resource Snapshot(ref Resource input, Resource replacement) { var result = input; input = replacement; return result; }
        """;

    [Theory]
    [InlineData("ref Resource alias = ref value;")]
    [InlineData("ref Resource alias = ref Identity(ref value);")]
    [InlineData("ref Resource first = ref value; ref Resource alias = ref first;")]
    [InlineData("ref Resource first = ref value; ref Resource alias = ref Identity(ref first);")]
    public async Task AssigningThroughARefUpdatesTheOriginalOwner(string declaration)
    {
        var source = Prelude + "void Caller(Resource external) { var value = external; " + declaration +
            " alias = new Resource(); value.Dispose(); } }";
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id is "SS0001" or "SS0002" or "SS0003" or "SS0011");
    }

    [Theory]
    [InlineData("ref Resource alias = ref value;")]
    [InlineData("ref Resource alias = ref Identity(ref value);")]
    [InlineData("ref readonly Resource alias = ref ReadOnly(in value);")]
    public async Task RefReadsObserveTheCurrentDisposedValue(string declaration)
    {
        var source = Prelude + "void Caller() { var value = new Resource(); " + declaration + " value.Dispose(); alias.Read(); } }";
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        var read = source.IndexOf("alias.Read()", StringComparison.Ordinal);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0001" && diagnostic.Location.SourceSpan.Start == read);
    }

    [Theory]
    [InlineData("ref Resource alias = ref value;")]
    [InlineData("ref Resource alias = ref Identity(ref value);")]
    public async Task ReplacingARefCellDoesNotChangeAnEvaluatedByValueCopy(string declaration)
    {
        var source = Prelude + "void Caller() { var value = new Resource(); " + declaration +
            " var before = alias; alias = new Resource(); value.Dispose(); before.Read(); before.Dispose(); } }";
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id is "SS0001" or "SS0002" or "SS0003" or "SS0011");
    }

    [Fact]
    public async Task RebindingDoesNotRedirectAnOlderRefAlias()
    {
        var source = Prelude + """
            void Caller() {
                var first = new Resource(); var second = new Resource();
                ref Resource alias = ref first; ref Resource older = ref alias;
                alias = ref second; older.Dispose(); first.Read(); second.Read(); second.Dispose();
            } }
            """;
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0001" && diagnostic.Location.SourceSpan.Start == source.IndexOf("first.Read()", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0001" && diagnostic.Location.SourceSpan.Start == source.IndexOf("second.Read()", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Identity(ref value) = new Resource();")]
    [InlineData("ref Resource alias = ref Identity(ref value); alias = new Resource();")]
    public async Task ReturnedStorageWritesAreVisibleThroughTheOriginalLocal(string write)
    {
        var source = Prelude + "void Caller(Resource external) { var value = external; " + write + " value.Dispose(); value.Read(); } }";
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0001" && diagnostic.Location.SourceSpan.Start == source.IndexOf("value.Read()", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0003");
    }

    [Theory]
    [InlineData("ref Resource chosen = ref (choose ? ref first : ref second);")]
    [InlineData("ref Resource chosen = ref Select(choose, ref first, ref second);")]
    public async Task ConditionalReturnedReferencesPreserveBothPossibleTargets(string declaration)
    {
        var source = Prelude + "void Caller(bool choose) { var first = new Resource(); var second = new Resource(); " + declaration +
            " first.Dispose(); second.Dispose(); chosen.Read(); } }";
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0001" && diagnostic.Location.SourceSpan.Start == source.IndexOf("chosen.Read()", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("First(ref alias, (alias = ref second))")]
    [InlineData("First(ref alias, choose ? (alias = ref second) : (alias = ref second))")]
    public async Task EarlierRefArgumentsKeepTheirAddressAfterALaterArgumentRebindsTheAlias(string call)
    {
        var source = Prelude + "void Caller(Resource external, bool choose) { var first = external; var second = external; ref Resource alias = ref first; " +
            "ref Resource result = ref " + call + "; result = new Resource(); first.Dispose(); result.Read(); second.Read(); } }";
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0001" && diagnostic.Location.SourceSpan.Start == source.IndexOf("result.Read()", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0001" && diagnostic.Location.SourceSpan.Start == source.IndexOf("second.Read()", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0003");
    }

    [Theory]
    [InlineData("Fill(out alias, (alias = ref second));")]
    [InlineData("Fill(out alias, choose ? (alias = ref second) : (alias = ref second));")]
    public async Task OutArgumentsWriteToTheAddressCapturedBeforeLaterArgumentEffects(string call)
    {
        var source = Prelude + "void Caller(Resource external, bool choose) { var first = external; var second = external; ref Resource alias = ref first; " +
            call + " first.Dispose(); first.Read(); second.Read(); } }";
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0001" && diagnostic.Location.SourceSpan.Start == source.IndexOf("first.Read()", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0001" && diagnostic.Location.SourceSpan.Start == source.IndexOf("second.Read()", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0003");
    }

    [Theory]
    [InlineData("ref ValueTask<int> alias = ref task;")]
    [InlineData("ref ValueTask<int> alias = ref TaskIdentity(ref task);")]
    public async Task ValueTaskReadsThroughReferencesRetainSingleConsumption(string declaration)
    {
        var source = Prelude + "void Caller(ValueTask<int> task) { " + declaration +
            " _ = alias.GetAwaiter().GetResult(); _ = task.GetAwaiter().GetResult(); } }";
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0048" && diagnostic.Location.SourceSpan.Start == source.IndexOf("task.GetAwaiter().GetResult()", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Replace(ref value, replacement);")]
    [InlineData("ref Resource result = ref Cleanup(ref value, replacement);")]
    public async Task SourceProvenRefReplacementsCarryTheActualIncomingObject(string call)
    {
        var source = Prelude + "void Caller(Resource external) { var value = external; var replacement = new Resource(); " + call +
            " value.Dispose(); replacement.Read(); } }";
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0001" && diagnostic.Location.SourceSpan.Start == source.IndexOf("replacement.Read()", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0003");
    }

    [Theory]
    [InlineData("Reset(ref value);")]
    [InlineData("ref Resource result = ref ResetAndReturn(ref value);")]
    public async Task AProvenSingleFreshRefOutputCreatesOneCallerOwner(string call)
    {
        var source = Prelude + "void Caller(Resource external) { var value = external; " + call + " value.Dispose(); value.Read(); } }";
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0001" && diagnostic.Location.SourceSpan.Start == source.IndexOf("value.Read()", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0003");
    }

    [Fact]
    public async Task AnUndisposedFreshRefOutputStillReportsTheOwnershipObligation()
    {
        var source = Prelude + "void Caller(Resource external) { var value = external; Reset(ref value); } }";
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0003" && diagnostic.Location.SourceSpan.Start == source.IndexOf("ref value", source.IndexOf("void Caller", StringComparison.Ordinal), StringComparison.Ordinal));
    }

    [Fact]
    public async Task AByValueResultRetainsTheIncomingRefValueBeforeTheCalleeReplacesTheCell()
    {
        var source = Prelude + "void Caller(Resource external) { var value = new Resource(); var original = value; var snapshot = Snapshot(ref value, external); " +
            "original.Dispose(); snapshot.Read(); value.Read(); } }";
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0001" && diagnostic.Location.SourceSpan.Start == source.IndexOf("snapshot.Read()", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0001" && diagnostic.Location.SourceSpan.Start == source.IndexOf("value.Read()", StringComparison.Ordinal));
    }

}
