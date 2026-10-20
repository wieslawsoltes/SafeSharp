namespace SafeSharp.Tests.Flow;

public sealed class RefArrayBoundaryTests
{
    private const string Prelude = """
        using System;
        using System.Threading.Tasks;
        using SafeSharp;
        sealed class Resource : IDisposable { public void Dispose() { } }
        class Example {
            static ref Resource Identity(ref Resource value) => ref value;
            static void Replace(ref Resource value, Resource replacement) => value = replacement;
            static void Fill(out Resource value, Resource replacement) => value = replacement;
        """;

    [Theory]
    [InlineData("ref Resource alias = ref values[0]; alias = external;")]
    [InlineData("ref Resource alias = ref (values[0]); alias = external;")]
    [InlineData("Identity(ref values[0]) = external;")]
    [InlineData("Replace(ref values[0], external);")]
    [InlineData("Fill(out values[0], external);")]
    [InlineData("ref Resource alias = ref (choose ? ref values[0] : ref external); alias = external;")]
    public async Task AnExposedElementAddressCannotLeaveAStaleExactCleanupProof(string mutation)
    {
        var source = Prelude + "void M(Resource external, bool choose) { var values = new[] { new Resource() }; " +
            mutation + " values[0].Dispose(); } }";
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0003" &&
            diagnostic.Location.SourceSpan.Start == source.IndexOf("new Resource()", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AValueCopiedBeforeExposingTheAddressStillOwnsTheOriginalElement()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + """
            void M(Resource external) {
                var values = new[] { new Resource() }; var original = values[0];
                ref Resource alias = ref values[0]; alias = external; original.Dispose();
            } }
            """);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id is "SS0003" or "SS0011");
    }

    [Fact]
    public async Task AConditionalByValueCaptureKeepsItsExactIdentity()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + """
            void M(bool choose) {
                var values = new[] { new Resource() };
                var original = choose ? values[0] : values[0]; original.Dispose();
            } }
            """);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id is "SS0003" or "SS0011");
    }

    [Fact]
    public async Task TaskInputsCapturedBeforeTheAddressExposureRemainFrozen()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + """
            async Task M(Resource external) {
                var inputs = new[] { Task.FromResult(new Resource()) };
                var combined = Task.WhenAll(inputs);
                ref Task<Resource> alias = ref inputs[0]; alias = Task.FromResult(external);
                var results = await combined; results[0].Dispose();
            } }
            """);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id is "SS0003" or "SS0011");
    }
}
