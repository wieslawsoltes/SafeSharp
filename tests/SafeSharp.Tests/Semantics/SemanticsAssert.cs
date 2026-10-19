using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using SafeSharp.Analyzers.Semantics;

namespace SafeSharp.Tests.Semantics;

internal static class SemanticsAssert
{
    public static Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source) => AnalyzerTestHost.GetDiagnosticsAsync(source, new SafeSharpSemanticAnalyzer());

    public static async Task DiagnosticAsync(string source, string id)
    {
        var start = source.IndexOf("[|", StringComparison.Ordinal);
        var end = source.IndexOf("|]", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "A positive fixture must mark the exact diagnostic span.");
        var span = new TextSpan(start, end - start - 2);
        source = source.Remove(end, 2).Remove(start, 2);
        var diagnostics = await AnalyzeAsync(source);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(id, diagnostic.Id);
        Assert.Equal(span, diagnostic.Location.SourceSpan);
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.GetMessage()));
        Assert.StartsWith("https://github.com/wieslawsoltes/SafeSharp/", diagnostic.Descriptor.HelpLinkUri);
    }

    public static async Task NoDiagnosticAsync(string source) => Assert.Empty(await AnalyzeAsync(source));
}
