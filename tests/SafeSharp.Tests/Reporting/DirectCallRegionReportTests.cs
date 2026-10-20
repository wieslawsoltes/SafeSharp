using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using SafeSharp.Analyzers.Semantics;
using SafeSharp.Core;
using SafeSharp.Reporting;
using ReportDiagnostic = SafeSharp.Reporting.ReportDiagnostic;

namespace SafeSharp.Tests.Reporting;

public sealed class DirectCallRegionReportTests
{
    private static readonly Lazy<Task<Diagnostic>> RegionDiagnostic = new(() => AnalyzeAsync(typeFailure: false));
    private static readonly Lazy<Task<Diagnostic>> TypeDiagnostic = new(() => AnalyzeAsync(typeFailure: true));
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    [Theory]
    [InlineData(ReportFormat.Html, false)]
    [InlineData(ReportFormat.Dot, false)]
    [InlineData(ReportFormat.Mermaid, false)]
    [InlineData(ReportFormat.Json, false)]
    [InlineData(ReportFormat.Sarif, false)]
    [InlineData(ReportFormat.Html, true)]
    [InlineData(ReportFormat.Dot, true)]
    [InlineData(ReportFormat.Mermaid, true)]
    [InlineData(ReportFormat.Json, true)]
    [InlineData(ReportFormat.Sarif, true)]
    public async Task FailedCallContractsRetainExactGraphsAndCallerEvidence(ReportFormat format, bool typeFailure)
    {
        var diagnostic = await (typeFailure ? TypeDiagnostic : RegionDiagnostic).Value;
        var properties = diagnostic.Properties;
        Assert.False(properties.ContainsKey("SafeSharp.DelegateType"));
        Assert.NotEmpty(properties["SafeSharp.CallTarget"]!);
        Assert.NotEmpty(properties["SafeSharp.CallReason"]!);
        Assert.NotEmpty(properties["SafeSharp.CallEvidence"]!);
        var output = AnalysisReportRenderer.Render(new AnalysisReport([], [
            new ReportDiagnostic(diagnostic.Id, diagnostic.GetMessage(),
                new SourceSite("/src/DirectCall.cs", 0, 1), properties: properties)
        ]), format);

        if (format is ReportFormat.Json or ReportFormat.Sarif)
        {
            using var json = JsonDocument.Parse(output);
            var metadata = format == ReportFormat.Json
                ? json.RootElement.GetProperty("diagnostics")[0].GetProperty("properties")
                : json.RootElement.GetProperty("runs")[0].GetProperty("results")[0].GetProperty("properties").GetProperty("diagnosticProperties");
            foreach (var property in properties) Assert.Equal(property.Value, metadata.GetProperty(property.Key).GetString());
            Assert.Contains("SS0050", output);
            return;
        }

        Assert.DoesNotContain("Region graph omitted", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SafeSharp.CallTarget", output);
        Assert.Contains("SafeSharp.CallReason", output);
        Assert.Contains("SafeSharp.CallEvidence", output);
        Assert.Contains("SafeSharp.TypeOutlivesRequirements", output);
        Assert.Equal(properties["SafeSharp.RegionNames"]!.Split(',').Length, VertexCount(output, format));
        var assumptions = Edges(properties["SafeSharp.RegionConstraints"]!);
        var requirements = Edges(properties["SafeSharp.RegionRequirements"]!);
        Assert.Equal(assumptions.Length, EdgeCount(output, format, requirement: false));
        Assert.Equal(requirements.Length, EdgeCount(output, format, requirement: true));
        if (format == ReportFormat.Html)
        {
            var graph = Graph(output);
            var edges = graph.Descendants(Svg + "path").Where(edge => (string?)edge.Attribute("class") == "region-edge");
            Assert.Equal(assumptions.Concat(requirements), edges.Select(edge => edge.Attribute("data-longer")!.Value + " : " + edge.Attribute("data-shorter")!.Value));
        }
    }

    private static async Task<Diagnostic> AnalyzeAsync(bool typeFailure)
    {
        const string prelude = "using System.Collections.Generic; using SafeSharp; sealed class Resource { }";
        var source = prelude + (typeFailure ? """
            class C
            {
                [TypeOutlives("T", "scope")]
                static void Use<T>([Lifetime("value")] T value, [Lifetime("scope")] ref Resource owner) { }
                void M<T>([Lifetime("a")] T value, [Lifetime("b")] ref Resource owner) { Use(value, ref owner); }
            }
            """ : """
            class C
            {
                static void Use([TypeLifetime("item", "arg0")] List<Resource> first,
                    [TypeLifetime("item", "arg0")] List<Resource> second) { }
                void M([TypeLifetime("a", "arg0")] List<Resource> first,
                    [TypeLifetime("b", "arg0")] List<Resource> second) { Use(first, second); }
            }
            """);
        var compilation = AnalyzerTestHost.CreateCompilation(source);
        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var diagnostics = await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new LifetimeContractAnalyzer())).GetAnalyzerDiagnosticsAsync();
        var result = Assert.Single(diagnostics);
        Assert.Equal("SS0050", result.Id);
        Assert.NotEmpty(result.AdditionalLocations);
        return result;
    }

    private static string[] Edges(string value) => value.Split(';', StringSplitOptions.RemoveEmptyEntries)
        .Select(edge => edge.Trim()).Distinct(StringComparer.Ordinal).OrderBy(edge => edge, StringComparer.Ordinal).ToArray();

    private static XDocument Graph(string html)
    {
        var start = html.IndexOf("<svg class=\"region-graph\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "Expected a complete direct-call region SVG.");
        var end = html.IndexOf("</svg>", start, StringComparison.Ordinal);
        return XDocument.Parse(html.Substring(start, end + "</svg>".Length - start));
    }

    private static int VertexCount(string output, ReportFormat format) => format switch
    {
        ReportFormat.Html => Regex.Matches(output, "<g class=\"region-node\"").Count,
        ReportFormat.Dot => Regex.Matches(output, "^\\s+r[0-9]+_n[0-9]+ \\[label=", RegexOptions.Multiline).Count,
        _ => Regex.Matches(output, "^\\s+r[0-9]+_n[0-9]+\\[", RegexOptions.Multiline).Count,
    };

    private static int EdgeCount(string output, ReportFormat format, bool requirement) => format switch
    {
        ReportFormat.Html => Graph(output).Descendants(Svg + "path").Count(edge => (string?)edge.Attribute("class") == "region-edge" &&
            ((string?)edge.Attribute("data-kind") == "requirement") == requirement),
        ReportFormat.Dot => Regex.Matches(output, requirement
            ? "^\\s+r[0-9]+_n[0-9]+ -> r[0-9]+_n[0-9]+ \\[label=\"requires outlives\", style=dashed"
            : "^\\s+r[0-9]+_n[0-9]+ -> r[0-9]+_n[0-9]+ \\[label=\"outlives\"\\];", RegexOptions.Multiline).Count,
        _ => Regex.Matches(output, requirement
            ? "^\\s+r[0-9]+_n[0-9]+ -\\.->\\|requires outlives\\| r[0-9]+_n[0-9]+$"
            : "^\\s+r[0-9]+_n[0-9]+ -->\\|outlives\\| r[0-9]+_n[0-9]+$", RegexOptions.Multiline).Count,
    };
}
