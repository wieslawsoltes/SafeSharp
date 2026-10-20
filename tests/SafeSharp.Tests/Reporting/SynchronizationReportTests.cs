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

public sealed class SynchronizationReportTests
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
    private const string Prefix = "SafeSharp.Synchronization";
    private static readonly ReportFormat[] GraphFormats = [ReportFormat.Html, ReportFormat.Dot, ReportFormat.Mermaid];

    [Theory]
    [InlineData(ReportFormat.Html)]
    [InlineData(ReportFormat.Dot)]
    [InlineData(ReportFormat.Mermaid)]
    public void GraphsRetainExactRequirementAndRecordedIntervals(ReportFormat format)
    {
        var properties = Properties();
        var output = Render(format, properties);
        Assert.DoesNotContain("Synchronization graph omitted", output);
        Assert.Equal(5, NodeCount(output, format));
        Assert.Equal(3, EdgeCount(output, format));
        Assert.Contains("0..many", output);
        if (format == ReportFormat.Html)
        {
            var graph = Graph(output);
            Assert.Single(Nodes(graph, "required"));
            Assert.Equal(2, Nodes(graph, "held").Count());
            Assert.Single(graph.Descendants(Svg + "path"), edge => (string?)edge.Attribute("data-kind") == "requirement");
            Assert.Equal(2, graph.Descendants(Svg + "path").Count(edge => (string?)edge.Attribute("data-kind") == "observed"));
            Assert.Contains("this.gate", Nodes(graph, "required").Single().Value);
        }
        else
        {
            // A matching display name is not a producer-supplied identity edge.
            Assert.DoesNotContain("s0_required -> s0_held", output);
            Assert.DoesNotContain("s0_required -->", output);
        }
    }

    [Theory]
    [InlineData(ReportFormat.Html)]
    [InlineData(ReportFormat.Dot)]
    [InlineData(ReportFormat.Mermaid)]
    public void EmptyObservedStateDoesNotParseHumanReadableHeldText(ReportFormat format)
    {
        var properties = Properties(0);
        properties[Prefix + "Held"] = "invented.guard [Monitor: 9..9]";
        var output = Render(format, properties);
        Assert.Equal(3, NodeCount(output, format));
        Assert.Equal(1, EdgeCount(output, format));
        Assert.Contains("No capability records", output);
    }

    [Theory]
    [InlineData(ReportFormat.Html)]
    [InlineData(ReportFormat.Dot)]
    [InlineData(ReportFormat.Mermaid)]
    public void MaximumRecordCountIsCompleteAndDuplicateLabelsRemainDistinct(ReportFormat format)
    {
        var properties = Properties(64);
        for (var index = 0; index < 64; index++) properties[Prefix + "Held." + index + ".Name"] = "same display label";
        var output = Render(format, properties);
        Assert.DoesNotContain("Synchronization graph omitted", output);
        Assert.Equal(67, NodeCount(output, format));
        Assert.Equal(65, EdgeCount(output, format));
    }

    [Theory]
    [InlineData("missing-count")]
    [InlineData("invalid-count")]
    [InlineData("negative-count")]
    [InlineData("too-many-records")]
    [InlineData("missing-record")]
    [InlineData("out-of-range-record")]
    [InlineData("negative-minimum")]
    [InlineData("inverted-interval")]
    [InlineData("unknown-protocol")]
    [InlineData("unknown-record-field")]
    [InlineData("leading-zero-record-index")]
    [InlineData("control-character")]
    [InlineData("unpaired-surrogate")]
    [InlineData("xml-noncharacter-fffe")]
    [InlineData("xml-noncharacter-ffff")]
    [InlineData("oversized-name")]
    [InlineData("missing-required-protocol")]
    public void MalformedOrOversizedEvidenceNeverProducesAPartialGraph(string scenario)
    {
        var properties = Properties();
        switch (scenario)
        {
            case "missing-count": properties.Remove(Prefix + "HeldCount"); break;
            case "invalid-count": properties[Prefix + "HeldCount"] = "2.0"; break;
            case "negative-count": properties[Prefix + "HeldCount"] = "-1"; break;
            case "too-many-records": properties[Prefix + "HeldCount"] = "65"; break;
            case "missing-record": properties.Remove(Prefix + "Held.1.Name"); break;
            case "out-of-range-record": properties[Prefix + "Held.2.Name"] = "other"; break;
            case "negative-minimum": properties[Prefix + "Held.0.Minimum"] = "-1"; break;
            case "inverted-interval": properties[Prefix + "Held.0.Minimum"] = "2"; break;
            case "unknown-protocol": properties[Prefix + "Held.0.Protocol"] = "Custom"; break;
            case "unknown-record-field": properties[Prefix + "Held.0.Invented"] = "1"; break;
            case "leading-zero-record-index": properties[Prefix + "Held.01.Name"] = "other"; break;
            case "control-character": properties[Prefix + "Held.0.Name"] = "gate\u0001"; break;
            case "unpaired-surrogate": properties[Prefix + "Required"] = "gate\ud800"; break;
            case "xml-noncharacter-fffe": properties[Prefix + "Held.0.Name"] = "gate\ufffe"; break;
            case "xml-noncharacter-ffff": properties[Prefix + "Held.0.Name"] = "gate\uffff"; break;
            case "oversized-name": properties[Prefix + "Held.0.Name"] = new string('x', 65537); break;
            case "missing-required-protocol": properties.Remove(Prefix + "Protocol"); break;
            default: throw new ArgumentException(scenario);
        }
        foreach (var format in GraphFormats)
        {
            var output = Render(format, properties);
            Assert.Contains("Synchronization graph omitted", output);
            Assert.Equal(0, NodeCount(output, format));
            Assert.Equal(0, EdgeCount(output, format));
            Assert.Contains("SafeSharp.SynchronizationReason", output);
        }
    }

    [Theory]
    [InlineData(ReportFormat.Json)]
    [InlineData(ReportFormat.Sarif)]
    public void StructuredAndUnknownMetadataRoundTripExactly(ReportFormat format)
    {
        var properties = Properties();
        properties[Prefix + "FutureField"] = "Unicode Ω, newline\n, quote\" and control \u0001";
        properties[Prefix + "RequiredIdentity"] = null;
        using var json = JsonDocument.Parse(Render(format, properties));
        var metadata = format == ReportFormat.Json
            ? json.RootElement.GetProperty("diagnostics")[0].GetProperty("properties")
            : json.RootElement.GetProperty("runs")[0].GetProperty("results")[0].GetProperty("properties").GetProperty("diagnosticProperties");
        foreach (var pair in properties) Assert.Equal(pair.Value, metadata.GetProperty(pair.Key).GetString());
    }

    [Theory]
    [InlineData(ReportFormat.Html)]
    [InlineData(ReportFormat.Dot)]
    [InlineData(ReportFormat.Mermaid)]
    [InlineData(ReportFormat.Json)]
    [InlineData(ReportFormat.Sarif)]
    public void OutputIsCultureIndependentAndHostileLabelsCannotBecomeDirectives(ReportFormat format)
    {
        const string hostile = "gate\"]; click injected <script>alert(1)</script> & Ω😀";
        var properties = Properties();
        properties[Prefix + "Required"] = hostile;
        properties[Prefix + "RequiredIdentity"] = hostile;
        properties[Prefix + "Held.0.Name"] = hostile;
        var output = Render(format, properties);
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            Assert.Equal(output, Render(format, properties));
        }
        finally { CultureInfo.CurrentCulture = previous; }
        if (format == ReportFormat.Html)
        {
            var graph = Graph(output);
            Assert.DoesNotContain("<script>alert(1)</script>", output);
            Assert.Contains(hostile, Nodes(graph, "required").Single().Value);
        }
        else if (format == ReportFormat.Dot)
            Assert.Contains("gate\\\"]; click injected <script>", output);
        else if (format == ReportFormat.Mermaid)
        {
            Assert.DoesNotContain(hostile, output);
            Assert.Contains("#34;#93;#59; click injected #60;script#62;", output);
        }
    }

    [Theory]
    [InlineData(ReportFormat.Html)]
    [InlineData(ReportFormat.Dot)]
    [InlineData(ReportFormat.Mermaid)]
    public void MultilineSourceLabelsRemainValidWithoutChangingTopology(ReportFormat format)
    {
        var properties = Properties();
        properties[Prefix + "Access"] = "Use(\r\n\tgate)";
        properties[Prefix + "Held.0.Name"] = "shared\nstate";
        var output = Render(format, properties);
        Assert.DoesNotContain("Synchronization graph omitted", output);
        Assert.Equal(5, NodeCount(output, format));
        Assert.Equal(3, EdgeCount(output, format));
        if (format == ReportFormat.Html)
        {
            var graph = Graph(output);
            Assert.Equal("Use(   gate)", Nodes(graph, "access").Single().Elements(Svg + "text").Last().Value);
            Assert.Contains("shared\nstate", Nodes(graph, "held").First().Element(Svg + "title")!.Value);
        }
    }

    [Fact]
    public async Task ActualAnalyzerWitnessPreservesItsRequirementAndHeldReceiverAcrossAllFormats()
    {
        var compilation = AnalyzerTestHost.CreateCompilation("""
            using SafeSharp;
            sealed class Counter
            {
                readonly object gate = new();
                readonly object other = new();
                [GuardedBy(nameof(gate))] int value;
                void Invalid() { lock (other) value++; }
            }
            """);
        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var diagnostics = await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new ConcurrencyContractAnalyzer())).GetAnalyzerDiagnosticsAsync();
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("SS0049", diagnostic.Id);
        Assert.Equal("this.gate", diagnostic.Properties[Prefix + "RequiredIdentity"]);
        Assert.Equal("value", diagnostic.Properties[Prefix + "Access"]);
        Assert.Equal("Write", diagnostic.Properties[Prefix + "AccessMode"]);
        Assert.Equal("1", diagnostic.Properties[Prefix + "HeldCount"]);
        Assert.Equal("this.other", diagnostic.Properties[Prefix + "Held.0.Name"]);
        Assert.NotEmpty(diagnostic.AdditionalLocations);
        foreach (var format in GraphFormats)
        {
            var output = Render(format, diagnostic.Properties);
            Assert.DoesNotContain("Synchronization graph omitted", output);
            Assert.Equal(4, NodeCount(output, format));
            Assert.Equal(2, EdgeCount(output, format));
        }
        foreach (var format in new[] { ReportFormat.Json, ReportFormat.Sarif })
        {
            using var json = JsonDocument.Parse(Render(format, diagnostic.Properties));
            var metadata = format == ReportFormat.Json ? json.RootElement.GetProperty("diagnostics")[0].GetProperty("properties") :
                json.RootElement.GetProperty("runs")[0].GetProperty("results")[0].GetProperty("properties").GetProperty("diagnosticProperties");
            foreach (var pair in diagnostic.Properties) Assert.Equal(pair.Value, metadata.GetProperty(pair.Key).GetString());
        }
    }

    private static Dictionary<string, string?> Properties(int count = 2)
    {
        var properties = new Dictionary<string, string?>
        {
            [Prefix + "Required"] = "gate", [Prefix + "RequiredIdentity"] = "this.gate",
            [Prefix + "Protocol"] = "Monitor", [Prefix + "Access"] = "value", [Prefix + "AccessMode"] = "Write",
            [Prefix + "Reason"] = "A matching guard is not established on every path.",
            [Prefix + "Held"] = "This text is retained independently of structured records.",
            [Prefix + "HeldCount"] = count.ToString(CultureInfo.InvariantCulture),
        };
        for (var index = 0; index < count; index++)
        {
            var prefix = Prefix + "Held." + index.ToString(CultureInfo.InvariantCulture) + ".";
            properties[prefix + "Name"] = index == 1 ? "this.gate" : "this.other";
            properties[prefix + "Protocol"] = index == 1 ? "Read" : "Monitor";
            properties[prefix + "Minimum"] = index == 1 ? "0" : "1";
            properties[prefix + "Maximum"] = index == 1 ? "many" : "1";
        }
        return properties;
    }
    private static string Render(ReportFormat format, IEnumerable<KeyValuePair<string, string?>> properties) =>
        AnalysisReportRenderer.Render(new AnalysisReport([], [new ReportDiagnostic("SS0049", "Required guard is not established.",
            new SourceSite("/src/Counter.cs", 0, 1), properties: properties)]), format);
    private static XDocument Graph(string output)
    {
        var start = output.IndexOf("<svg class=\"synchronization-graph\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "Expected a complete synchronization SVG.");
        var end = output.IndexOf("</svg>", start, StringComparison.Ordinal);
        return XDocument.Parse(output.Substring(start, end + "</svg>".Length - start));
    }
    private static IEnumerable<XElement> Nodes(XDocument graph, string role) => graph.Descendants(Svg + "g")
        .Where(node => (string?)node.Attribute("data-role") == role);
    private static int NodeCount(string output, ReportFormat format) => format switch
    {
        ReportFormat.Html => Regex.Matches(output, "<g class=\"synchronization-node\"").Count,
        ReportFormat.Dot => Regex.Matches(output, "^\\s+s0_(?:access|required|state|held[0-9]+) \\[label=", RegexOptions.Multiline).Count,
        _ => Regex.Matches(output, "^\\s+s0_(?:access|required|state|held[0-9]+)\\[", RegexOptions.Multiline).Count,
    };
    private static int EdgeCount(string output, ReportFormat format) => format switch
    {
        ReportFormat.Html => Regex.Matches(output, "<path class=\"synchronization-edge\"").Count,
        ReportFormat.Dot => Regex.Matches(output, "^\\s+s0_(?:access|state) -> s0_", RegexOptions.Multiline).Count,
        _ => Regex.Matches(output, "^\\s+s0_(?:access|state) (?:-\\.->|-->)\\|[^|]+\\| s0_", RegexOptions.Multiline).Count,
    };
}
