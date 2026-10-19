using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SafeSharp.Tests;

/// <summary>Runs the real compiler and analyzer pipeline; malformed fixtures fail before analysis.</summary>
public static class AnalyzerTestHost
{
    private static readonly ImmutableArray<MetadataReference> References =
        ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? throw new InvalidOperationException("Framework references unavailable."))
        .Split(Path.PathSeparator)
        .Append(typeof(SafeSharp.Ownership).Assembly.Location)
        .Distinct(StringComparer.Ordinal)
        .Select(path => MetadataReference.CreateFromFile(path))
        .ToImmutableArray<MetadataReference>();

    public static CSharpCompilation CreateCompilation(string source, params string[] additionalSources) =>
        CSharpCompilation.Create("AnalyzerFixture",
            new[] { source }.Concat(additionalSources).Select((text, index) =>
                CSharpSyntaxTree.ParseText(text, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview), $"Fixture{index}.cs")),
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable));

    public static async Task<ImmutableArray<Diagnostic>> GetDiagnosticsAsync(string source, params DiagnosticAnalyzer[] analyzers)
    {
        var compilation = CreateCompilation(source);
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(d => d.ToString())));
        if (analyzers.Length == 0)
            analyzers = [new SafeSharp.Analyzers.OwnershipAnalyzer()];
        var result = await compilation.WithAnalyzers(ImmutableArray.Create(analyzers)).GetAnalyzerDiagnosticsAsync();
        Assert.DoesNotContain(result, d => d.Id == "AD0001");
        return result.OrderBy(d => d.Location.SourceSpan.Start).ThenBy(d => d.Id, StringComparer.Ordinal).ToImmutableArray();
    }
}
