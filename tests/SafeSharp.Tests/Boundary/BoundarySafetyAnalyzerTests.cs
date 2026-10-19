using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using SafeSharp.Analyzers;

namespace SafeSharp.Tests.Boundary;

public sealed class BoundarySafetyAnalyzerTests
{
    private const string Prelude = """
        using System;
        using System.IO;
        using System.Threading.Tasks;
        using SafeSharp;
        sealed class Resource : IDisposable
        {
            public void Read() { }
            public void Dispose() { }
        }
        sealed class AsyncResource : IAsyncDisposable
        {
            public void Read() { }
            public ValueTask DisposeAsync() => default;
        }
        """;

    public static TheoryData<string, string> ReportedBoundaries => new()
    {
        { "class Example { void Work() { } void M() { var [|r = new Resource()|]; Work(); r.Dispose(); } }", "SS0030" },
        { "class Example { [ReturnsOwned] Resource Create() => new Resource(); void Work() { } void M() { var [|r = Create()|]; Work(); r.Dispose(); } }", "SS0030" },
        { "class Example { async Task M() { var [|r = new AsyncResource()|]; await Task.Yield(); await r.DisposeAsync(); } }", "SS0030" },
        { "class Example { void Work() { } void M() { var [|r = new Resource()|]; Work(); try { r.Read(); } finally { r.Dispose(); } } }", "SS0030" },
        { "class Example { void M(int[] values) { var [|r = new Resource()|]; _ = values[0]; r.Dispose(); } }", "SS0030" },
        { "class Example { void M(AsyncResource r) { [|r.DisposeAsync()|]; } }", "SS0031" },
        { "class Example { void M(IAsyncDisposable r) { _ = [|r.DisposeAsync()|]; } }", "SS0031" },
        { "class Example { void M(AsyncResource r) { [|r.DisposeAsync()|].ConfigureAwait(false); } }", "SS0031" },
        { "class Example { void M(AsyncResource r) { _ = [|r.DisposeAsync()|].ConfigureAwait(false); } }", "SS0031" },
        { "class Example { void M(AsyncResource r) { [|((IAsyncDisposable)r).DisposeAsync()|]; } }", "SS0031" },
        { "class Example { void M(dynamic value) { [|value.Run()|]; } }", "SS0032" },
        { "class Example { object M(dynamic value) => [|value[0]|]; }", "SS0032" },
        { "class Example { unsafe int M(int* pointer) => [|*pointer|]; }", "SS0032" },
        { "class Example { unsafe void M() { int value = 0; int* pointer = [|&value|]; } }", "SS0032" },
        { "class Example { unsafe void M() { int* pointer = [|stackalloc int[1]|]; } }", "SS0032" },
        { "interface IFactory { Resource Create(); } class Example { void M(IFactory factory) { _ = [|factory.Create()|]; } }", "SS0033" },
        { "namespace Foreign { sealed class ReturnsOwnedAttribute : Attribute { } } interface IFactory { [Foreign.ReturnsOwned] Resource Create(); } class Example { void M(IFactory factory) { _ = [|factory.Create()|]; } }", "SS0033" },
        { "interface IConsumer { void Consume([Borrowed] Resource source); } class Example : IConsumer { public void Consume([BorrowedMut] Resource [|value|]) { } }", "SS0034" },
        { "abstract class Base { public abstract void Consume([Consumes] Resource value); } class Example : Base { public override void Consume([Borrowed] Resource [|value|]) { } }", "SS0034" },
        { "interface IConsumer { void Consume([Borrowed] Resource value); } class Example : IConsumer { void IConsumer.Consume([Consumes] Resource [|value|]) { } }", "SS0034" },
        { "interface IConsumer<T> { void Consume(int first, [Borrowed] T source); } class Example : IConsumer<Resource> { public void Consume(int renamed, [BorrowedMut] Resource [|value|]) { } }", "SS0034" },
    };

    [Theory]
    [MemberData(nameof(ReportedBoundaries))]
    public async Task ReportsExactOperationOrContractSpan(string body, string expectedId)
    {
        var (source, expectedSpan) = ExtractMarker(Prelude + body);
        var diagnostics = await AnalyzeAsync(source, enableAudits: true);
        var matches = diagnostics.Where(diagnostic => diagnostic.Id == expectedId).ToArray();
        var diagnostic = Assert.Single(matches);
        Assert.Equal(expectedSpan, diagnostic.Location.SourceSpan);
        Assert.Equal("Fixture0.cs", diagnostic.Location.SourceTree?.FilePath);
        Assert.EndsWith("#" + expectedId.ToLowerInvariant(), diagnostic.Descriptor.HelpLinkUri, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.GetMessage()));
        if (expectedId == "SS0030") Assert.Equal(2, diagnostic.AdditionalLocations.Count);
        if (expectedId == "SS0034") Assert.Single(diagnostic.AdditionalLocations);
    }

    public static TheoryData<string, string> AcceptedBoundaries => new()
    {
        { "class Example { void M() { var r = new Resource(); r.Dispose(); } }", "SS0030" },
        { "class Example { void Work() { } void M() { using var r = new Resource(); Work(); } }", "SS0030" },
        { "class Example { void Work() { } void M() { var r = new Resource(); try { Work(); } finally { r.Dispose(); } } }", "SS0030" },
        { "class Example { void Work() { } void M() { var r = new Resource(); Action later = () => Work(); r.Dispose(); } }", "SS0030" },
        { "class Example { void Work() { } void M() { var r = new Resource(); void Later() { Work(); } r.Dispose(); } }", "SS0030" },
        { "class Example { async Task M(AsyncResource r) { await r.DisposeAsync(); } }", "SS0031" },
        { "class Example { async Task M(AsyncResource r) { await r.DisposeAsync().ConfigureAwait(false); } }", "SS0031" },
        { "class Example { ValueTask M(AsyncResource r) => r.DisposeAsync(); }", "SS0031" },
        { "class Example { async Task M(AsyncResource r) { var cleanup = r.DisposeAsync(); await cleanup; } }", "SS0031" },
        { "class Example { void M(AsyncResource r) { r.DisposeAsync().GetAwaiter().GetResult(); } }", "SS0031" },
        { "class Fake { public ValueTask DisposeAsync() => default; } class Example { void M(Fake value) { value.DisposeAsync(); } }", "SS0031" },
        { "class Mixed : IAsyncDisposable { ValueTask IAsyncDisposable.DisposeAsync() => default; public int DisposeAsync() => 42; } class Example { void M(Mixed value) { value.DisposeAsync(); } }", "SS0031" },
        { "class Example { void M() { Console.WriteLine(42); } }", "SS0032" },
        { "interface IFactory { [ReturnsOwned] Resource Create(); } class Example { void M(IFactory factory) { _ = factory.Create(); } }", "SS0033" },
        { "interface IFactory { [ReturnsBorrowed(\"source\")] Resource Get(Resource source); } class Example { void M(IFactory factory, Resource value) { _ = factory.Get(value); } }", "SS0033" },
        { "class Example { void M() { _ = File.OpenRead(\"data\"); } }", "SS0033" },
        { "class Example { Resource Create() => new Resource(); void M() { _ = Create(); } }", "SS0033" },
        { "interface IConsumer { void Consume([Borrowed] Resource source); } class Example : IConsumer { public void Consume(Resource value) { } }", "SS0034" },
        { "abstract class Base { public abstract void Consume([Borrowed] Resource value); } class Example : Base { public override void Consume([Borrowed] Resource value) { } }", "SS0034" },
        { "interface IConsumer { void Consume(Resource source); } class Example : IConsumer { public void Consume([Borrowed] Resource value) { } }", "SS0034" },
        { "abstract class Base { public abstract void Consume([Consumes] Resource value); } class Example : Base { public override void Consume([Owned] Resource value) { } }", "SS0034" },
    };

    [Theory]
    [MemberData(nameof(AcceptedBoundaries))]
    public async Task AcceptsRecognizedBoundariesWithoutTheTargetDiagnostic(string body, string excludedId)
    {
        var diagnostics = await AnalyzeAsync(Prelude + body, enableAudits: true);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == excludedId);
    }

    [Theory]
    [InlineData("class Example { void M(dynamic value) { value.Run(); } }", "SS0032")]
    [InlineData("interface IFactory { Resource Create(); } class Example { void M(IFactory factory) { _ = factory.Create(); } }", "SS0033")]
    public async Task AuditRulesAreDisabledByDefault(string body, string rule)
    {
        var diagnostics = await AnalyzeAsync(Prelude + body);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == rule);
    }

    [Theory]
    [InlineData("SS0030", "class Example { void M() { var r = new Resource(); r.Read(); r.Dispose(); } }")]
    [InlineData("SS0031", "class Example { void M(AsyncResource r) { r.DisposeAsync(); } }")]
    [InlineData("SS0032", "class Example { void M(dynamic value) { value.Run(); } }")]
    [InlineData("SS0033", "interface IFactory { Resource Create(); } class Example { void M(IFactory factory) { _ = factory.Create(); } }")]
    [InlineData("SS0034", "interface IConsumer { void M([Borrowed] Resource source); } class Example : IConsumer { public void M([Consumes] Resource value) { } }")]
    public async Task RuleCanBeSuppressedByCompilationOptions(string rule, string body)
    {
        var diagnostics = await AnalyzeAsync(Prelude + body, enableAudits: true, suppressedRule: rule);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == rule);
    }

    [Fact]
    public async Task PragmasSuppressAnEnabledAuditRule()
    {
        var diagnostics = await AnalyzeAsync(Prelude + """

            #pragma warning disable SS0032
            class Example { void M(dynamic value) { value.Run(); } }
            #pragma warning restore SS0032
            """, enableAudits: true);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0032");
    }

    [Fact]
    public async Task SeverityOverrideChangesTheReportedSeverity()
    {
        var diagnostics = await AnalyzeAsync(Prelude + "class Example { void M(AsyncResource r) { r.DisposeAsync(); } }", errorRule: "SS0031");
        Assert.Equal(DiagnosticSeverity.Error, Assert.Single(diagnostics, diagnostic => diagnostic.Id == "SS0031").Severity);
    }

    [Fact]
    public async Task GeneratedSourceIsSkipped()
    {
        var diagnostics = await AnalyzeAsync("// <auto-generated/>\n" + Prelude + "class Example { void M(AsyncResource r) { r.DisposeAsync(); } }", enableAudits: true);
        Assert.Empty(diagnostics);
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source, bool enableAudits = false, string? suppressedRule = null, string? errorRule = null)
    {
        var compilation = AnalyzerTestHost.CreateCompilation(source);
        var options = compilation.Options.SpecificDiagnosticOptions;
        if (enableAudits) options = options.SetItem("SS0032", ReportDiagnostic.Info).SetItem("SS0033", ReportDiagnostic.Info);
        if (suppressedRule is not null) options = options.SetItem(suppressedRule, ReportDiagnostic.Suppress);
        if (errorRule is not null) options = options.SetItem(errorRule, ReportDiagnostic.Error);
        compilation = compilation.WithOptions(compilation.Options.WithSpecificDiagnosticOptions(options));
        var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(diagnostic => diagnostic.ToString())));
        var diagnostics = await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new BoundarySafetyAnalyzer())).GetAnalyzerDiagnosticsAsync();
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "AD0001");
        return diagnostics.OrderBy(diagnostic => diagnostic.Location.SourceSpan.Start).ThenBy(diagnostic => diagnostic.Id, StringComparer.Ordinal).ToImmutableArray();
    }

    private static (string Source, TextSpan Span) ExtractMarker(string marked)
    {
        var start = marked.IndexOf("[|", StringComparison.Ordinal);
        var end = marked.IndexOf("|]", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "A fixture must contain one nonempty diagnostic span marker.");
        return (marked.Remove(end, 2).Remove(start, 2), new TextSpan(start, end - start - 2));
    }
}
