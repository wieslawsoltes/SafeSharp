namespace SafeSharp.Tests.Flow;

public sealed class AggregateReferenceStorageOwnershipTests
{
    private const string Prelude = """
        using System;
        using System.Threading.Tasks;
        using SafeSharp;
        sealed class Resource : IDisposable { public void Dispose() { } public void Read() { } }
        class Example {
            static ref Resource Identity(ref Resource value) => ref value;
            static ref readonly Resource ReadOnly(in Resource value) => ref value;
            static ref Resource Select(bool choose, ref Resource first, ref Resource second) => ref (choose ? ref first : ref second);
            static ref Resource First(ref Resource first, object ignored) => ref first;
            static void Replace(ref Resource value, Resource replacement) => value = replacement;
            static void Reset(ref Resource value) => value = new Resource();
            static void Fill([Owned] out Resource value) => value = new Resource();
            static void Preserve(ref Resource value) { }
            static ref ValueTask<int> TaskIdentity(ref ValueTask<int> value) => ref value;
        """;

    [Theory]
    [InlineData("ref Resource alias = ref values[0]; alias.Dispose();")]
    [InlineData("ref readonly Resource alias = ref values[0]; alias.Dispose();")]
    [InlineData("Identity(ref values[0]).Dispose();")]
    [InlineData("ReadOnly(in values[0]).Dispose();")]
    [InlineData("Preserve(ref values[0]); values[0].Dispose();")]
    [InlineData("ref Resource alias = ref (choose ? ref values[0] : ref values[0]); alias.Dispose();")]
    [InlineData("Select(choose, ref values[0], ref values[0]).Dispose();")]
    public async Task AProvenAddressRetainsTheExactOwner(string body)
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + "void M(bool choose) { var values = new[] { new Resource() }; " + body + " } }");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id is "SS0001" or "SS0002" or "SS0003" or "SS0011");
    }

    [Theory]
    [InlineData("ref Resource alias = ref values[0]; alias = new Resource();")]
    [InlineData("Identity(ref values[0]) = new Resource();")]
    [InlineData("Reset(ref values[0]);")]
    [InlineData("Fill(out values[0]);")]
    public async Task RefWritesAreVisibleToOrdinaryElementReads(string mutation)
    {
        var source = Prelude + "void M(Resource external) { var values = new[] { external }; " + mutation + " values[0].Dispose(); values[0].Read(); } }";
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0001" && diagnostic.Location.SourceSpan.Start == source.IndexOf("values[0].Read()", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0003");
    }

    [Theory]
    [InlineData("ref Resource alias = ref values[0];")]
    [InlineData("ref Resource alias = ref Identity(ref values[0]);")]
    public async Task OrdinaryWritesAreVisibleToExistingRefAliases(string declaration)
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + "void M(Resource external) { var values = new[] { external }; " + declaration +
            " values[0] = new Resource(); alias.Dispose(); } }");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id is "SS0001" or "SS0002" or "SS0003" or "SS0011");
    }

    [Fact]
    public async Task SourceProvenRefReplacementPreservesTheActualResourceIdentity()
    {
        var source = Prelude + """
            void M(Resource external) {
                var values = new[] { external }; var replacement = new Resource();
                Replace(ref values[0], replacement); values[0].Dispose(); replacement.Read();
            } }
            """;
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0001" && diagnostic.Location.SourceSpan.Start == source.IndexOf("replacement.Read()", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0003");
    }

    [Fact]
    public async Task AnEarlierRefArgumentSnapshotsItsArrayBeforeALaterArgumentReplacesTheVariable()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + """
            void M() {
                var values = new[] { new Resource() }; var original = values;
                ref Resource alias = ref First(ref values[0], (values = new[] { new Resource() }).Length);
                alias.Dispose(); values[0].Dispose();
            } }
            """);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id is "SS0001" or "SS0002" or "SS0003" or "SS0011");
    }

    [Fact]
    public async Task RebindingAnArrayRefDoesNotRedirectOlderRefs()
    {
        var source = Prelude + """
            void M() {
                var values = new[] { new Resource(), new Resource() };
                ref Resource alias = ref values[0]; ref Resource older = ref alias;
                alias = ref values[1]; older.Dispose(); alias.Dispose(); values[0].Read(); values[1].Read();
            } }
            """;
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0001" && diagnostic.Location.SourceSpan.Start == source.IndexOf("values[0].Read()", StringComparison.Ordinal));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0001" && diagnostic.Location.SourceSpan.Start == source.IndexOf("values[1].Read()", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0003");
    }

    [Theory]
    [InlineData("ref Resource chosen = ref (choose ? ref values[0] : ref values[1]);")]
    [InlineData("ref Resource chosen = ref Select(choose, ref values[0], ref values[1]);")]
    public async Task ConditionalRefsRetainEveryPossibleElement(string declaration)
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + "void M(bool choose) { var values = new[] { new Resource(), new Resource() }; " + declaration +
            " chosen.Dispose(); } }");
        Assert.Equal(2, diagnostics.Count(diagnostic => diagnostic.Id == "SS0003"));
    }

    [Theory]
    [InlineData("ref ValueTask<int> alias = ref tasks[0];")]
    [InlineData("ref ValueTask<int> alias = ref TaskIdentity(ref tasks[0]);")]
    public async Task ElementAndReferenceReadsPreserveOneConsumptionCapability(string declaration)
    {
        var source = Prelude + "void M(ValueTask<int> task) { var tasks = new[] { task }; " + declaration +
            " _ = alias.GetAwaiter().GetResult(); _ = tasks[0].GetAwaiter().GetResult(); } }";
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0048" && diagnostic.Location.SourceSpan.Start == source.IndexOf("tasks[0].GetAwaiter().GetResult()", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OrdinaryElementReplacementChangesTheCapabilityReadThroughTheRef()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + """
            void M(ValueTask<int> first, ValueTask<int> second) {
                var tasks = new[] { first }; ref ValueTask<int> alias = ref tasks[0];
                _ = alias.GetAwaiter().GetResult(); tasks[0] = second; _ = alias.GetAwaiter().GetResult();
            } }
            """);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0048");
    }

    [Fact]
    public async Task JaggedAndMultidimensionalArrayCellsHaveIndependentCanonicalStorage()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + """
            void M() {
                var jagged = new[] { new[] { new Resource() } };
                var rectangular = new[,] { { new Resource(), new Resource() } };
                ref Resource first = ref jagged[0][0]; ref Resource second = ref rectangular[0, 0];
                ref Resource third = ref rectangular[0, 1]; first.Dispose(); second.Dispose(); third.Dispose();
            } }
            """);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id is "SS0001" or "SS0002" or "SS0003" or "SS0011");
    }

    [Theory]
    [InlineData("catch { values[0].Dispose(); }")]
    [InlineData("finally { values[0].Dispose(); }")]
    [InlineData("catch (Exception) when (CleanupFilter(values[0])) { }")]
    public async Task AThrowingRefWriteCannotLeaveAStaleCleanupProofInHandlers(string handler)
    {
        var source = Prelude + """
            static void ReplaceAndThrow(ref Resource value, Resource replacement) { value = replacement; throw new Exception(); }
            static bool CleanupFilter(Resource value) { value.Dispose(); return true; }
            void M(Resource external) {
                var values = new[] { new Resource() };
                try { ReplaceAndThrow(ref values[0], external); }
            """ + handler + " } }";
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0003" && diagnostic.Location.SourceSpan.Start == source.LastIndexOf("new Resource()", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AByValueSnapshotBeforeAnExceptionalRefWriteStillOwnsItsOriginalObject()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + """
            static void ReplaceAndThrow(ref Resource value, Resource replacement) { value = replacement; throw new Exception(); }
            void M(Resource external) {
                var values = new[] { new Resource() }; var original = values[0];
                try { ReplaceAndThrow(ref values[0], external); }
                finally { original.Dispose(); }
            } }
            """);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0003");
    }

    [Fact]
    public async Task AValueCopiedFromARefBeforeReplacementKeepsItsOwnIdentity()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + """
            void M() {
                var values = new[] { new Resource() }; ref Resource alias = ref values[0];
                var original = alias; alias = new Resource(); original.Dispose(); values[0].Dispose();
            } }
            """);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id is "SS0001" or "SS0002" or "SS0003" or "SS0011");
    }

    [Fact]
    public async Task MovingThroughARefInvalidatesBothElementAndPriorValueAliases()
    {
        var source = Prelude + """
            void M() {
                var values = new[] { new Resource() }; ref Resource alias = ref values[0];
                var before = values[0]; var moved = Ownership.Move(alias);
                values[0].Read(); before.Read(); moved.Dispose();
            } }
            """;
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0004" && diagnostic.Location.SourceSpan.Start == source.IndexOf("values[0]", source.IndexOf("Ownership.Move", StringComparison.Ordinal), StringComparison.Ordinal));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0004" && diagnostic.Location.SourceSpan.Start == source.IndexOf("before.Read()", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0003");
    }

    [Theory]
    [InlineData("ref Resource alias = ref values[index]; alias = external;")]
    [InlineData("ref Resource alias = ref values[0]; values[index] = external;")]
    public async Task UnknownIndexMutationDoesNotProveOriginalCleanup(string mutation)
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + "void M(int index, Resource external) { var values = new[] { new Resource() }; " + mutation +
            " values[0].Dispose(); } }");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0003");
    }

    [Theory]
    [InlineData("Borrowed", true)]
    [InlineData("BorrowedMut", false)]
    public async Task UntrackedScalarReferencesKeepTheExistingReceiverPermission(string contract, bool conflicts)
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + "void M([" + contract +
            "] int[] values) { ref int element = ref values[0]; element++; } }");
        Assert.Equal(conflicts, diagnostics.Any(diagnostic => diagnostic.Id == "SS0007"));
    }
}
