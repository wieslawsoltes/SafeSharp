using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using SafeSharp.Analyzers;

namespace SafeSharp.Tests.Flow;

/// <summary>The analyzer host and the analyzed compilation can use different framework assemblies.</summary>
public sealed class NetStandardAsyncResultTests
{
    private const string Prelude = """
        using System;
        using System.Threading.Tasks;
        using SafeSharp;
        [NoSuspend] sealed class Resource : IDisposable {
            public void Dispose() { }
            public void Read() { }
        }
        class Example {
        """;

    [Fact]
    public async Task OwnedPayloadIsAllocatedAtReceiptWithNetStandardReferenceSymbols()
    {
        var source = Prelude + "[ReturnsOwned] Task<Resource> Create() => Task.FromResult(new Resource());" +
            " async Task Caller() { var result = await Create(); result.Read(); } }";
        var diagnostics = await AnalyzeAsync(source);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0003" &&
            diagnostic.Location.SourceSpan.Start == source.IndexOf("await Create()", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0010");
    }

    [Fact]
    public async Task AnInferredPayloadCanBeConsumedAfterTheConfiguredAwait()
    {
        var source = Prelude + "Task<Resource> Create() => Task.FromResult(new Resource());" +
            " async Task Caller() { var task = Create(); using var result = await task.ConfigureAwait(false); result.Read(); } }";
        Assert.Empty(await AnalyzeAsync(source));
    }

    [Fact]
    public async Task BorrowedSourceSummariesKeepPayloadOwnershipSeparateFromTheCarrier()
    {
        var source = Prelude + "Task<Resource> Echo(Resource input) => Task.FromResult(input);" +
            " void Caller(Resource input) { var task = Echo(input); var result = task.GetAwaiter().GetResult(); result.Dispose(); } }";
        var diagnostics = await AnalyzeAsync(source);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0009" &&
            diagnostic.Location.SourceSpan.Start == source.IndexOf("result.Dispose()", StringComparison.Ordinal));
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source)
    {
        var reference = Path.Combine(AppContext.BaseDirectory, "ReferenceAssemblies", "netstandard2.0", "netstandard.dll");
        Assert.True(File.Exists(reference), "The NETStandard.Library reference assembly must be copied by the test project.");
        var compilation = CSharpCompilation.Create("NetStandardAsyncFixture",
            [CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview), "NetStandardFixture.cs")],
            [MetadataReference.CreateFromFile(reference), MetadataReference.CreateFromFile(typeof(SafeSharp.Ownership).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(diagnostic => diagnostic.ToString())));
        Assert.Equal("netstandard", compilation.GetTypeByMetadataName("System.Threading.Tasks.Task`1")!.ContainingAssembly.Identity.Name);
        var diagnostics = await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new OwnershipAnalyzer())).GetAnalyzerDiagnosticsAsync();
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "AD0001");
        return diagnostics;
    }
}
