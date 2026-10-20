using System.Collections.Immutable;
using System.Globalization;
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

public sealed class DelegateRegionReportTests
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
    private static readonly ReportFormat[] GraphFormats = [ReportFormat.Html, ReportFormat.Dot, ReportFormat.Mermaid];

    private static Dictionary<string, string?> Properties() => new()
    {
        ["SafeSharp.DelegateType"] = "Pick",
        ["SafeSharp.RegionNames"] = "u0, u1, i0, i1, isolated",
        ["SafeSharp.RegionConstraints"] = "u1 : u0",
        ["SafeSharp.RegionRequirements"] = "u0 : i0; u1 : i1; i1 : u0",
        ["SafeSharp.RegionRequirementReasons"] = "u0 : i0 (Input shortening); i1 : u0 (Returned source)",
        ["SafeSharp.RegionBindings"] = "u0 = delegate region 'a'; u1 = delegate region 'b'; i0 = callback region 'x'; i1 = callback region 'y'",
        ["SafeSharp.RegionQuantification"] = "one universal delegate binder",
        ["SafeSharp.RequiredRegion"] = "u0",
        ["SafeSharp.SourceRegions"] = "u1",
        ["SafeSharp.RegionProof"] = "u1 -> i1 -> u0",
        ["SafeSharp.RegionComponents"] = "u0, u1, i0, i1, isolated",
    };

    private static ReportDiagnostic Diagnostic(IReadOnlyDictionary<string, string?> properties, int offset = 0) =>
        new("SS0047", "The callback requires a relation between independent delegate regions.",
            new SourceSite("/src/Delegates.cs", offset, 4), properties: properties);

    private static string Render(ReportFormat format, IReadOnlyDictionary<string, string?> properties) =>
        AnalysisReportRenderer.Render(new AnalysisReport([], [Diagnostic(properties)]), format);

    [Theory]
    [InlineData(ReportFormat.Html)]
    [InlineData(ReportFormat.Dot)]
    [InlineData(ReportFormat.Mermaid)]
    public void AssumptionsAndRequirementsHaveDistinctExactEdges(ReportFormat format)
    {
        var output = Render(format, Properties());
        Assert.Equal(5, VertexCount(output, format));
        Assert.Equal(1, EdgeCount(output, format, requirement: false));
        Assert.Equal(3, EdgeCount(output, format, requirement: true));
        Assert.Contains("assumptions", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("requirements", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SafeSharp.RegionBindings", output);
        Assert.Contains("SafeSharp.RegionQuantification", output);
        switch (format)
        {
            case ReportFormat.Html:
                var graph = Graph(output);
                var edges = EdgePaths(graph);
                Assert.Equal(new[] { "u1 : u0", "i1 : u0", "u0 : i0", "u1 : i1" }, edges.Select(Endpoints));
                Assert.Null(edges[0].Attribute("stroke-dasharray"));
                Assert.All(edges.Skip(1), edge =>
                {
                    Assert.Equal("6 4", (string?)edge.Attribute("stroke-dasharray"));
                    Assert.Equal("#c9a5ff", (string?)edge.Attribute("stroke"));
                    Assert.Equal("url(#region-0-requirement-arrow)", (string?)edge.Attribute("marker-end"));
                });
                Assert.Contains("Declared delegate assumptions (solid)", output);
                Assert.Contains("Callback outlives requirements (dashed)", output);
                break;
            case ReportFormat.Dot:
                Assert.Contains("r0_n4 -> r0_n3 [label=\"outlives\"];", output);
                Assert.Contains("r0_n1 -> r0_n3 [label=\"requires outlives\", style=dashed", output);
                Assert.Contains("r0_n3 -> r0_n0 [label=\"requires outlives\", style=dashed", output);
                Assert.DoesNotContain("r0_n3 -> r0_n4", output);
                break;
            case ReportFormat.Mermaid:
                Assert.Contains("r0_n4 -->|outlives| r0_n3", output);
                Assert.Contains("r0_n1 -.->|requires outlives| r0_n3", output);
                Assert.Contains("r0_n3 -.->|requires outlives| r0_n0", output);
                Assert.DoesNotContain("r0_n3 -.->|requires outlives| r0_n4", output);
                break;
        }
    }

    [Theory]
    [InlineData(ReportFormat.Html)]
    [InlineData(ReportFormat.Dot)]
    [InlineData(ReportFormat.Mermaid)]
    public void DuplicateKindsAndCyclesPreserveOnlyTheirRecordedTopology(ReportFormat format)
    {
        var properties = Properties();
        properties["SafeSharp.RegionConstraints"] = "u0 : i0; u0 : i0";
        properties["SafeSharp.RegionRequirements"] = "u0 : i0; i0 : u0; i1 : i1; u0 : i0";
        var output = Render(format, properties);
        Assert.Equal(1, EdgeCount(output, format, requirement: false));
        Assert.Equal(3, EdgeCount(output, format, requirement: true));
        if (format != ReportFormat.Html) return;
        var sameEndpoints = EdgePaths(Graph(output)).Where(edge => Endpoints(edge) == "u0 : i0").ToArray();
        Assert.Equal(2, sameEndpoints.Length);
        Assert.NotEqual((string?)sameEndpoints[0].Attribute("d"), (string?)sameEndpoints[1].Attribute("d"));
        Assert.DoesNotContain("NaN", output);
    }

    [Theory]
    [InlineData(ReportFormat.Html)]
    [InlineData(ReportFormat.Dot)]
    [InlineData(ReportFormat.Mermaid)]
    public void ReasonsProofsBindingsAndComponentsCannotCreateEdges(ReportFormat format)
    {
        var properties = Properties();
        properties["SafeSharp.RegionConstraints"] = "";
        properties["SafeSharp.RegionRequirements"] = "";
        properties["SafeSharp.RegionRequirementReasons"] = "u0 : i0; u1 : i1; i1 : u0";
        properties["SafeSharp.RegionBindings"] = "u0 : i0; u1 : i1";
        var output = Render(format, properties);
        Assert.Equal(5, VertexCount(output, format));
        Assert.Equal(0, EdgeCount(output, format, requirement: false));
        Assert.Equal(0, EdgeCount(output, format, requirement: true));
        Assert.Contains("SafeSharp.RegionRequirementReasons", output);
    }

    public static IEnumerable<object?[]> MalformedRequirements()
    {
        foreach (var format in GraphFormats)
        foreach (var value in new[] { null, "u0 -> i0", "u0 : absent", "u0 : i0 : u1", "u0 : i0;",
            "u0 : i0 (Input shortening)", "u0 : i0;;u1 : i1", "u0 : \"injected\"" })
            yield return [format, value];
    }

    [Theory]
    [MemberData(nameof(MalformedRequirements))]
    public void MalformedRequirementMetadataOmitsTheWholeGraph(ReportFormat format, string? metadata)
    {
        var properties = Properties();
        properties["SafeSharp.RegionRequirements"] = metadata;
        var output = Render(format, properties);
        Assert.Equal(0, VertexCount(output, format));
        Assert.Equal(0, EdgeCount(output, format, requirement: false));
        Assert.Equal(0, EdgeCount(output, format, requirement: true));
        Assert.Contains("malformed", output);
        Assert.Contains("SafeSharp.RegionRequirements", output);
    }

    [Theory]
    [InlineData(ReportFormat.Html, 256, true)]
    [InlineData(ReportFormat.Dot, 256, true)]
    [InlineData(ReportFormat.Mermaid, 256, true)]
    [InlineData(ReportFormat.Html, 257, false)]
    [InlineData(ReportFormat.Dot, 257, false)]
    [InlineData(ReportFormat.Mermaid, 257, false)]
    public void PreviewBoundCountsAssumptionsAndRequirementsTogether(ReportFormat format, int count, bool render)
    {
        var names = Enumerable.Range(0, 17).Select(index => "r" + index.ToString("D2", CultureInfo.InvariantCulture)).ToArray();
        var pairs = names.SelectMany(longer => names.Select(shorter => longer + " : " + shorter)).Take(count).ToArray();
        var properties = Properties();
        properties["SafeSharp.RegionNames"] = string.Join(", ", names);
        properties["SafeSharp.RegionConstraints"] = string.Join("; ", pairs.Take(128));
        properties["SafeSharp.RegionRequirements"] = string.Join("; ", pairs.Skip(128));
        properties["SafeSharp.RequiredRegion"] = "r00";
        properties["SafeSharp.SourceRegions"] = "r16";
        var output = Render(format, properties);
        Assert.Equal(render ? 17 : 0, VertexCount(output, format));
        Assert.Equal(render ? 128 : 0, EdgeCount(output, format, requirement: false));
        Assert.Equal(render ? count - 128 : 0, EdgeCount(output, format, requirement: true));
        if (!render) Assert.Contains("256 combined assumption and requirement edges", output);
        Assert.Contains(format == ReportFormat.Mermaid ? pairs[^1].Replace(":", "#58;", StringComparison.Ordinal) : pairs[^1], output);
    }

    [Theory]
    [InlineData(ReportFormat.Html)]
    [InlineData(ReportFormat.Dot)]
    [InlineData(ReportFormat.Mermaid)]
    public void OversizedRequirementMetadataIsRetainedWithoutParsingOrLayout(ReportFormat format)
    {
        var properties = Properties();
        properties["SafeSharp.RegionRequirements"] = new string('x', 65_537);
        var output = Render(format, properties);
        Assert.Equal(0, VertexCount(output, format));
        Assert.Contains("parsing limit", output);
        Assert.Contains(properties["SafeSharp.RegionRequirements"]!, output);
    }

    [Theory]
    [InlineData(ReportFormat.Html)]
    [InlineData(ReportFormat.Dot)]
    [InlineData(ReportFormat.Mermaid)]
    public void ExplanationMetadataIsEscapedAndDoesNotBecomeGraphSyntax(ReportFormat format)
    {
        const string hostile = "\"]\nend\nclick injected \"javascript:alert(1)\"\n</script><script>alert('delegate')</script>";
        var properties = Properties();
        properties["SafeSharp.RegionBindings"] = hostile;
        properties["SafeSharp.RegionRequirementReasons"] = hostile;
        var output = Render(format, properties);
        Assert.Equal(1, EdgeCount(output, format, requirement: false));
        Assert.Equal(3, EdgeCount(output, format, requirement: true));
        Assert.DoesNotContain(hostile, output);
        if (format == ReportFormat.Html)
        {
            Assert.DoesNotContain("<script>alert('delegate')", output);
            Assert.NotNull(Graph(output).Root);
        }
        else Assert.DoesNotContain("\nclick injected", output);
    }

    [Theory]
    [InlineData(ReportFormat.Json)]
    [InlineData(ReportFormat.Sarif)]
    public void MachineReportsPreserveRequirementsAndExplanationsExactly(ReportFormat format)
    {
        var properties = Properties();
        properties["SafeSharp.RegionRequirementReasons"] = "u0 : i0 (\"<input>\"\n\t\\)";
        using var json = JsonDocument.Parse(Render(format, properties));
        var metadata = format == ReportFormat.Json
            ? json.RootElement.GetProperty("diagnostics")[0].GetProperty("properties")
            : json.RootElement.GetProperty("runs")[0].GetProperty("results")[0].GetProperty("properties").GetProperty("diagnosticProperties");
        foreach (var property in properties) Assert.Equal(property.Value, metadata.GetProperty(property.Key).GetString());
    }

    [Theory]
    [InlineData(ReportFormat.Html)]
    [InlineData(ReportFormat.Dot)]
    [InlineData(ReportFormat.Mermaid)]
    public async Task ActualDelegateDiagnosticsProvideTheExactRequirementGraph(ReportFormat format)
    {
        const string source = """
            using System;
            using SafeSharp;
            sealed class Resource : IDisposable { public void Dispose() { } }
            [return: Lifetime("a"), Borrowed]
            delegate Resource Pick([Borrowed, Lifetime("a")] Resource first, [Borrowed, Lifetime("b")] Resource second);
            class Example {
                [return: Lifetime("y"), Borrowed]
                static Resource Second([Borrowed, Lifetime("x")] Resource first, [Borrowed, Lifetime("y")] Resource second) => second;
                Pick M() => Second;
            }
            """;
        var compilation = AnalyzerTestHost.CreateCompilation(source);
        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var diagnostics = await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new LifetimeContractAnalyzer())).GetAnalyzerDiagnosticsAsync();
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "AD0001");
        var diagnostic = Assert.Single(diagnostics, diagnostic => diagnostic.Id == "SS0047");
        var output = Render(format, diagnostic.Properties);
        var requirements = diagnostic.Properties["SafeSharp.RegionRequirements"]!.Split(';').Select(value => value.Trim()).Distinct().ToArray();
        Assert.Equal(0, EdgeCount(output, format, requirement: false));
        Assert.Equal(requirements.Length, EdgeCount(output, format, requirement: true));
        Assert.Equal(diagnostic.Properties["SafeSharp.RegionNames"]!.Split(',').Length, VertexCount(output, format));
        if (format == ReportFormat.Html)
            Assert.Equal(requirements.OrderBy(value => value, StringComparer.Ordinal), EdgePaths(Graph(output)).Select(Endpoints));
    }

    [Fact]
    public void RequirementMarkersAreUniqueAndAccessibleAcrossDiagnostics()
    {
        var report = new AnalysisReport([], [Diagnostic(Properties(), 0), Diagnostic(Properties(), 20)]);
        var output = AnalysisReportRenderer.Render(report, ReportFormat.Html);
        Assert.Contains("id=\"region-0-requirement-arrow\"", output);
        Assert.Contains("id=\"region-1-requirement-arrow\"", output);
        Assert.Equal(2, Regex.Matches(output, "<svg class=\"region-graph\"").Count);
        Assert.Contains("aria-labelledby=\"region-1-title region-1-description\"", output);
    }

    [Theory]
    [InlineData(ReportFormat.Html)]
    [InlineData(ReportFormat.Dot)]
    [InlineData(ReportFormat.Mermaid)]
    public void RequirementOrderingIsIndependentOfCultureAndPropertyInsertion(ReportFormat format)
    {
        var properties = Properties();
        var expected = Render(format, properties);
        var previous = CultureInfo.CurrentCulture;
        var previousUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            var reversed = properties.Reverse().ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            Assert.Equal(expected, Render(format, reversed));
        }
        finally { CultureInfo.CurrentCulture = previous; CultureInfo.CurrentUICulture = previousUi; }
    }

    private static XDocument Graph(string html)
    {
        var start = html.IndexOf("<svg class=\"region-graph\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "Expected a region SVG.");
        var end = html.IndexOf("</svg>", start, StringComparison.Ordinal);
        return XDocument.Parse(html.Substring(start, end + "</svg>".Length - start));
    }

    private static XElement[] EdgePaths(XDocument graph) => graph.Descendants(Svg + "path")
        .Where(element => (string?)element.Attribute("class") == "region-edge").ToArray();
    private static string Endpoints(XElement edge) => edge.Attribute("data-longer")!.Value + " : " + edge.Attribute("data-shorter")!.Value;

    private static int VertexCount(string output, ReportFormat format) => format switch
    {
        ReportFormat.Html => Regex.Matches(output, "<g class=\"region-node\"").Count,
        ReportFormat.Dot => Regex.Matches(output, "^\\s+r[0-9]+_n[0-9]+ \\[label=", RegexOptions.Multiline).Count,
        _ => Regex.Matches(output, "^\\s+r[0-9]+_n[0-9]+\\[", RegexOptions.Multiline).Count,
    };

    private static int EdgeCount(string output, ReportFormat format, bool requirement) => format switch
    {
        ReportFormat.Html when !output.Contains("<svg class=\"region-graph\"", StringComparison.Ordinal) => 0,
        ReportFormat.Html => EdgePaths(Graph(output)).Count(edge => ((string?)edge.Attribute("data-kind") == "requirement") == requirement),
        ReportFormat.Dot => Regex.Matches(output, requirement
            ? "^\\s+r[0-9]+_n[0-9]+ -> r[0-9]+_n[0-9]+ \\[label=\"requires outlives\", style=dashed"
            : "^\\s+r[0-9]+_n[0-9]+ -> r[0-9]+_n[0-9]+ \\[label=\"outlives\"\\];", RegexOptions.Multiline).Count,
        _ => Regex.Matches(output, requirement
            ? "^\\s+r[0-9]+_n[0-9]+ -\\.->\\|requires outlives\\| r[0-9]+_n[0-9]+$"
            : "^\\s+r[0-9]+_n[0-9]+ -->\\|outlives\\| r[0-9]+_n[0-9]+$", RegexOptions.Multiline).Count,
    };
}
