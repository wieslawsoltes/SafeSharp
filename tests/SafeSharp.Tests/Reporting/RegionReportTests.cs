using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SafeSharp.Core;
using SafeSharp.Reporting;

namespace SafeSharp.Tests.Reporting;

public sealed class RegionReportTests
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    private static Dictionary<string, string?> Properties(string? names = "short, middle, long, isolated",
        string? constraints = "long : middle; middle : short", string? required = "short", string? sources = "long, <Unknown>") => new()
    {
        ["SafeSharp.RegionNames"] = names,
        ["SafeSharp.RegionConstraints"] = constraints,
        ["SafeSharp.RequiredRegion"] = required,
        ["SafeSharp.SourceRegions"] = sources,
        ["SafeSharp.RegionProof"] = "long -> middle -> short; short : short (reflexive)",
        ["SafeSharp.RegionComponents"] = "isolated; long; middle; short",
    };

    private static ReportDiagnostic Diagnostic(Dictionary<string, string?> properties, int offset = 0) => new("SS0041",
        "The returned origin does not establish the required lifetime.", new SourceSite("/src/Regions.cs", offset, 4), properties: properties);
    private static string Render(ReportFormat format, Dictionary<string, string?> properties) =>
        AnalysisReportRenderer.Render(new AnalysisReport([], [Diagnostic(properties)]), format);

    [Theory]
    [InlineData(ReportFormat.Html)]
    [InlineData(ReportFormat.Dot)]
    [InlineData(ReportFormat.Mermaid)]
    public void RendersOnlyDeclaredEdgesWithoutProofOrReflexiveExpansion(ReportFormat format)
    {
        var output = Render(format, Properties());
        Assert.Equal(4, VertexCount(output, format));
        Assert.Equal(2, EdgeCount(output, format));
        Assert.Contains("source", output);
        Assert.Contains("required", output);
        Assert.Contains("SafeSharp.RegionProof", output);
        switch (format)
        {
            case ReportFormat.Html:
                var graph = Graph(output);
                Assert.Equal(new[] { "long : middle", "middle : short" }, graph.Descendants(Svg + "path")
                    .Where(element => (string?)element.Attribute("class") == "region-edge")
                    .Select(element => element.Attribute("data-longer")!.Value + " : " + element.Attribute("data-shorter")!.Value));
                Assert.Contains("Declared outlives constraints", output);
                Assert.Contains("Only declared edges are drawn", output);
                break;
            case ReportFormat.Dot:
                Assert.Contains("subgraph cluster_r0", output);
                Assert.Contains("r0_n1 -> r0_n2 [label=\"outlives\"];", output);
                Assert.Contains("r0_n2 -> r0_n3 [label=\"outlives\"];", output);
                Assert.DoesNotContain("r0_n1 -> r0_n3", output);
                break;
            case ReportFormat.Mermaid:
                Assert.Contains("subgraph r0", output);
                Assert.Contains("r0_n1 -->|outlives| r0_n2", output);
                Assert.Contains("r0_n2 -->|outlives| r0_n3", output);
                Assert.DoesNotContain("r0_n1 -->|outlives| r0_n3", output);
                break;
        }
    }

    [Fact]
    public void HtmlLabelsBothRolesAndKeepsOriginKindsSeparateFromUserRegionNames()
    {
        var output = Render(ReportFormat.Html, Properties("Unknown, target", "", "target", "target, <Unknown>"));
        var graph = Graph(output);
        var nodes = graph.Descendants(Svg + "g").ToDictionary(element => element.Attribute("data-region")!.Value);
        Assert.Equal(2, nodes.Count);
        Assert.Equal("declared", nodes["Unknown"].Elements(Svg + "text").Last().Value);
        Assert.Equal("source + required", nodes["target"].Elements(Svg + "text").Last().Value);
        Assert.Equal("#ffb578", nodes["target"].Element(Svg + "rect")!.Attribute("stroke")!.Value);
        Assert.Contains("Other reported origins, not declared vertices: <code>&lt;Unknown&gt;</code>", output);
        Assert.Contains("No outlives constraints were declared", output);
    }

    [Theory]
    [InlineData(ReportFormat.Html)]
    [InlineData(ReportFormat.Dot)]
    [InlineData(ReportFormat.Mermaid)]
    public void PreservesDisconnectedVerticesAndDoesNotInferMissingConstraints(ReportFormat format)
    {
        var output = Render(format, Properties("a, b, c", "", "b", "a"));
        Assert.Equal(3, VertexCount(output, format));
        Assert.Equal(0, EdgeCount(output, format));
    }

    [Theory]
    [InlineData(ReportFormat.Html)]
    [InlineData(ReportFormat.Dot)]
    [InlineData(ReportFormat.Mermaid)]
    public void CyclesSelfEdgesAndDuplicateDeclarationsRetainOnlyTheirActualTopology(ReportFormat format)
    {
        var properties = Properties("a, b, c", "a : b; b : a; c : c; a : b", "c", "a");
        // Component membership is metadata, not a license to add all pairs within a component.
        properties["SafeSharp.RegionComponents"] = "a, b, c";
        var output = Render(format, properties);
        Assert.Equal(3, VertexCount(output, format));
        Assert.Equal(3, EdgeCount(output, format));
        if (format == ReportFormat.Html)
        {
            var graph = Graph(output);
            Assert.InRange(int.Parse(graph.Root!.Attribute("width")!.Value, CultureInfo.InvariantCulture), 1, 1500);
            Assert.InRange(int.Parse(graph.Root!.Attribute("height")!.Value, CultureInfo.InvariantCulture), 1, 1500);
            Assert.DoesNotContain("NaN", output);
        }
    }

    public static IEnumerable<object[]> MalformedMetadata()
    {
        foreach (var format in new[] { ReportFormat.Html, ReportFormat.Dot, ReportFormat.Mermaid })
        foreach (var (key, value) in new (string, string?)[]
        {
            ("SafeSharp.RegionNames", null),
            ("SafeSharp.RegionConstraints", null),
            ("SafeSharp.RegionNames", "a,,b"),
            ("SafeSharp.RegionNames", "a, bad-name"),
            ("SafeSharp.RegionNames", "a, \ud800"),
            ("SafeSharp.RegionConstraints", "long -> short"),
            ("SafeSharp.RegionConstraints", "long : absent"),
            ("SafeSharp.RegionConstraints", "long : middle : short"),
            ("SafeSharp.RegionConstraints", "long : short;"),
            ("SafeSharp.RequiredRegion", "absent"),
            ("SafeSharp.SourceRegions", "long,,short"),
        }) yield return [format, key, value!];
    }

    [Theory]
    [MemberData(nameof(MalformedMetadata))]
    public void MalformedMetadataRemainsVisibleWithoutFabricatedGraph(ReportFormat format, string key, string? value)
    {
        var properties = Properties();
        properties[key] = value;
        var output = Render(format, properties);
        Assert.Equal(0, VertexCount(output, format));
        Assert.Equal(0, EdgeCount(output, format));
        Assert.Contains("diagnostic", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(key, output);
    }

    [Theory]
    [InlineData(ReportFormat.Html)]
    [InlineData(ReportFormat.Dot)]
    [InlineData(ReportFormat.Mermaid)]
    public void EmptyNamedRegionSetDoesNotCreateOriginKindVertices(ReportFormat format)
    {
        var output = Render(format, Properties("", "", null, "<Local>, <Unknown>"));
        Assert.Equal(0, VertexCount(output, format));
        Assert.Equal(0, EdgeCount(output, format));
        Assert.Contains("No named regions were supplied", output);
        Assert.Contains("SafeSharp.SourceRegions", output);
    }

    [Theory]
    [InlineData(ReportFormat.Html)]
    [InlineData(ReportFormat.Dot)]
    [InlineData(ReportFormat.Mermaid)]
    public void HostileProofTextIsEscapedWithoutBeingInterpretedAsGraphSyntax(ReportFormat format)
    {
        const string hostile = "\"]\nend\nclick injected \"javascript:alert(1)\"\n</script><script>alert('region')</script>";
        var properties = Properties();
        properties["SafeSharp.RegionProof"] = hostile;
        properties["SafeSharp.RegionComponents"] = hostile;
        var output = Render(format, properties);
        Assert.Equal(2, EdgeCount(output, format));
        Assert.DoesNotContain(hostile, output);
        if (format == ReportFormat.Html)
        {
            Assert.DoesNotContain("<script>alert('region')", output);
            Assert.Contains("&lt;/script&gt;&lt;script&gt;", output);
            Assert.Single(Regex.Matches(output, "<script>"));
            Assert.NotNull(Graph(output).Root);
        }
        else
        {
            Assert.DoesNotContain("\nclick injected", output);
            Assert.DoesNotContain("\nend\n", output);
        }
    }

    [Theory]
    [InlineData(ReportFormat.Html)]
    [InlineData(ReportFormat.Dot)]
    [InlineData(ReportFormat.Mermaid)]
    public void PreviewBoundsAreExplicitAndPreserveCompleteMetadata(ReportFormat format)
    {
        var names = string.Join(", ", Enumerable.Range(0, 65).Select(index => "r" + index.ToString("D2", CultureInfo.InvariantCulture)));
        var output = Render(format, Properties(names, "", "r00", "r64"));
        Assert.Equal(0, VertexCount(output, format));
        Assert.Contains("64 named regions and 256 declared edges", output);
        Assert.Contains("r64", output);
        Assert.Contains("SafeSharp.RegionNames", output);
    }

    [Fact]
    public void EdgePreviewBoundDoesNotPartiallyDisplayARegionGraph()
    {
        var names = Enumerable.Range(0, 17).Select(index => "r" + index.ToString("D2", CultureInfo.InvariantCulture)).ToArray();
        var edges = string.Join("; ", names.SelectMany(longer => names.Select(shorter => longer + " : " + shorter)));
        var output = Render(ReportFormat.Html, Properties(string.Join(", ", names), edges, "r00", "r16"));
        Assert.Equal(0, VertexCount(output, ReportFormat.Html));
        Assert.Equal(0, EdgeCount(output, ReportFormat.Html));
        Assert.Contains("64 named regions and 256 declared edges", output);
        Assert.Contains("r16 : r16", output);
    }

    [Fact]
    public void ParsingBoundRetainsMetadataWithoutAttemptingLayout()
    {
        var properties = Properties();
        properties["SafeSharp.RegionConstraints"] = new string('x', 65537);
        var output = Render(ReportFormat.Html, properties);
        Assert.Equal(0, VertexCount(output, ReportFormat.Html));
        Assert.Contains("parsing limit", output);
        Assert.Contains(properties["SafeSharp.RegionConstraints"]!, output);
    }

    [Theory]
    [InlineData(ReportFormat.Json)]
    [InlineData(ReportFormat.Sarif)]
    public void MachineReportsPreserveEveryRegionPropertyExactly(ReportFormat format)
    {
        var properties = Properties();
        properties["SafeSharp.RegionProof"] = "\"<proof>\n\t\\";
        properties["SafeSharp.RegionComponents"] = null;
        using var json = JsonDocument.Parse(Render(format, properties));
        var metadata = format == ReportFormat.Json
            ? json.RootElement.GetProperty("diagnostics")[0].GetProperty("properties")
            : json.RootElement.GetProperty("runs")[0].GetProperty("results")[0].GetProperty("properties").GetProperty("diagnosticProperties");
        foreach (var property in properties)
            Assert.Equal(property.Value, metadata.GetProperty(property.Key).GetString());
    }

    [Theory]
    [InlineData(ReportFormat.Html)]
    [InlineData(ReportFormat.Dot)]
    [InlineData(ReportFormat.Mermaid)]
    public void RegionReportsUseDeterministicOrderingAndCultureIndependentCoordinates(ReportFormat format)
    {
        var properties = Properties("İ, I, résultat, this", "this : résultat; I : İ", "résultat", "I");
        var expected = Render(format, properties);
        var previous = CultureInfo.CurrentCulture;
        var previousUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            var reordered = properties.Reverse().ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            Assert.Equal(expected, Render(format, reordered));
        }
        finally { CultureInfo.CurrentCulture = previous; CultureInfo.CurrentUICulture = previousUi; }
    }

    [Fact]
    public void MultipleHtmlRegionGraphsHaveUniqueMarkerAndAccessibleLabelIdentifiers()
    {
        var report = new AnalysisReport([], [Diagnostic(Properties(), 2), Diagnostic(Properties(), 10)]);
        var html = AnalysisReportRenderer.Render(report, ReportFormat.Html);
        Assert.Contains("id=\"region-0-arrow\"", html);
        Assert.Contains("id=\"region-1-arrow\"", html);
        Assert.Equal(2, Regex.Matches(html, "<svg class=\"region-graph\"").Count);
        Assert.Contains("aria-labelledby=\"region-0-title region-0-description\"", html);
        Assert.Contains("aria-labelledby=\"region-1-title region-1-description\"", html);
    }

    private static XDocument Graph(string html)
    {
        var start = html.IndexOf("<svg class=\"region-graph\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "Expected a region SVG.");
        var end = html.IndexOf("</svg>", start, StringComparison.Ordinal);
        return XDocument.Parse(html.Substring(start, end + "</svg>".Length - start));
    }

    private static int VertexCount(string output, ReportFormat format) => format switch
    {
        ReportFormat.Html => Regex.Matches(output, "<g class=\"region-node\"").Count,
        ReportFormat.Dot => Regex.Matches(output, "^\\s+r[0-9]+_n[0-9]+ \\[label=", RegexOptions.Multiline).Count,
        _ => Regex.Matches(output, "^\\s+r[0-9]+_n[0-9]+\\[", RegexOptions.Multiline).Count,
    };

    private static int EdgeCount(string output, ReportFormat format) => format switch
    {
        ReportFormat.Html => Regex.Matches(output, "<path class=\"region-edge\"").Count,
        ReportFormat.Dot => Regex.Matches(output, "^\\s+r[0-9]+_n[0-9]+ -> r[0-9]+_n[0-9]+ \\[label=\"outlives\"\\];", RegexOptions.Multiline).Count,
        _ => Regex.Matches(output, "^\\s+r[0-9]+_n[0-9]+ -->\\|outlives\\| r[0-9]+_n[0-9]+$", RegexOptions.Multiline).Count,
    };
}
