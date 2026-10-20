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

public sealed class HigherRankedRegionReportTests
{
    private static readonly Lazy<Task<Diagnostic>> UniverseDiagnostic = new(() => AnalyzeAsync(typeFailure: false));
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
    public async Task ActualHigherRankedDiagnosticsRetainExactGraphsAndEvidence(ReportFormat format, bool typeFailure)
    {
        var diagnostic = await (typeFailure ? TypeDiagnostic : UniverseDiagnostic).Value;
        var properties = diagnostic.Properties;
        var output = Render(format, properties);
        if (format is ReportFormat.Json or ReportFormat.Sarif)
        {
            using var json = JsonDocument.Parse(output);
            var metadata = format == ReportFormat.Json
                ? json.RootElement.GetProperty("diagnostics")[0].GetProperty("properties")
                : json.RootElement.GetProperty("runs")[0].GetProperty("results")[0].GetProperty("properties").GetProperty("diagnosticProperties");
            foreach (var property in properties) Assert.Equal(property.Value, metadata.GetProperty(property.Key).GetString());
            return;
        }
        Assert.DoesNotContain("Region graph omitted", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SafeSharp.RegionUniverses", output);
        Assert.Contains("SafeSharp.RegionVisibilityProofs", output);
        Assert.Contains("SafeSharp.TypeOutlivesRequirements", output);
        Assert.Equal(properties["SafeSharp.RegionNames"]!.Split(',').Length, VertexCount(output, format));
        var assumptions = Edges(properties["SafeSharp.RegionConstraints"]!);
        var requirements = Edges(properties["SafeSharp.RegionRequirements"]!);
        Assert.Equal(assumptions.Length, EdgeCount(output, format, requirement: false));
        Assert.Equal(requirements.Length, EdgeCount(output, format, requirement: true));
        if (format == ReportFormat.Html)
        {
            var graph = Graph(output);
            Assert.Contains(graph.Descendants(Svg + "g"), node => (string?)node.Attribute("data-region") == "$static");
            var edges = graph.Descendants(Svg + "path").Where(edge => (string?)edge.Attribute("class") == "region-edge");
            Assert.Equal(assumptions.Concat(requirements), edges.Select(edge => edge.Attribute("data-longer")!.Value + " : " + edge.Attribute("data-shorter")!.Value));
        }
    }

    [Theory]
    [InlineData(ReportFormat.Html)]
    [InlineData(ReportFormat.Dot)]
    [InlineData(ReportFormat.Mermaid)]
    public void UniverseAndTypeMetadataDoNotInventAdditionalEdges(ReportFormat format)
    {
        var properties = new Dictionary<string, string?>
        {
            ["SafeSharp.RegionNames"] = "$static, u0, u1",
            ["SafeSharp.RegionConstraints"] = "$static : u0; $static : u1",
            ["SafeSharp.RegionRequirements"] = string.Empty,
            ["SafeSharp.RegionUniversePromotions"] = "u0 : $static",
            ["SafeSharp.RegionVisibilityProofs"] = "u0 -> u1",
            ["SafeSharp.TypeOutlivesRequirements"] = "t0 : u1",
        };
        var output = Render(format, properties);
        Assert.Equal(3, VertexCount(output, format));
        Assert.Equal(2, EdgeCount(output, format, requirement: false));
        Assert.Equal(0, EdgeCount(output, format, requirement: true));
    }

    [Theory]
    [InlineData("$other")]
    [InlineData("$static_extra")]
    [InlineData("$static; injected")]
    public void OnlyTheExactReservedStaticNameIsAccepted(string name)
    {
        var properties = new Dictionary<string, string?>
        { ["SafeSharp.RegionNames"] = name + ", u0", ["SafeSharp.RegionConstraints"] = name + " : u0" };
        var output = Render(ReportFormat.Html, properties);
        Assert.Equal(0, VertexCount(output, ReportFormat.Html));
        Assert.Contains("malformed", output);
    }

    private static async Task<Diagnostic> AnalyzeAsync(bool typeFailure)
    {
        const string prelude = "using System; using SafeSharp; sealed class Resource { }";
        var source = prelude + (typeFailure ? """
            [return: TypeLifetime("a", "param0"), TypeLifetime("b", "param1"), TypeLifetime("b", "return")]
            delegate Func<T, Resource, Resource> D<T>();
            class C
            {
                [TypeOutlives("T", "y", Scope = "result")]
                [return: TypeLifetime("x", "param0"), TypeLifetime("y", "param1"), TypeLifetime("y", "return")]
                static Func<T, Resource, Resource> M<T>() => (first, second) => second;
                D<Resource> F() => M<Resource>;
            }
            """ : """
            [return: TypeLifetime("fresh", "return")]
            delegate Func<Resource> D([Lifetime("owner")] Resource owner);
            class C
            {
                [return: TypeLifetime("^input", "return")]
                static Func<Resource> M([Lifetime("input")] Resource input) => () => input;
                D F() => M;
            }
            """);
        var compilation = AnalyzerTestHost.CreateCompilation(source);
        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var diagnostics = await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new LifetimeContractAnalyzer())).GetAnalyzerDiagnosticsAsync();
        var result = Assert.Single(diagnostics);
        Assert.Equal("SS0047", result.Id);
        return result;
    }

    private static string Render(ReportFormat format, IReadOnlyDictionary<string, string?> properties) =>
        AnalysisReportRenderer.Render(new AnalysisReport([], [new ReportDiagnostic("SS0047", "Higher-ranked lifetime obligation failed.",
            new SourceSite("/src/HigherRanked.cs", 0, 1), properties: properties)]), format);
    private static string[] Edges(string value) => value.Split(';', StringSplitOptions.RemoveEmptyEntries)
        .Select(edge => edge.Trim()).Distinct(StringComparer.Ordinal).OrderBy(edge => edge, StringComparer.Ordinal).ToArray();
    private static XDocument Graph(string html)
    {
        var start = html.IndexOf("<svg class=\"region-graph\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "Expected a complete higher-ranked region SVG.");
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
