using System.Globalization;
using System.Text.Json;
using SafeSharp.Core;
using SafeSharp.Reporting;

namespace SafeSharp.Tests.Reporting;

public sealed class ReportWriterTests
{
    [Fact]
    public void JsonContainsComputedCfgStatesLoansLivenessAndEvidence()
    {
        var result = Analyze();
        using var json = JsonDocument.Parse(AnalysisReportRenderer.Render(new AnalysisReport([result]), ReportFormat.Json));
        var root = json.RootElement;
        Assert.Equal("1.0", root.GetProperty("schemaVersion").GetString());
        Assert.Equal("utf16CodeUnits", root.GetProperty("columnKind").GetString());
        Assert.Equal(0, root.GetProperty("coordinateBase").GetInt32());
        Assert.True(root.GetProperty("analysisComplete").GetBoolean());
        var method = Assert.Single(root.GetProperty("methods").EnumerateArray());
        Assert.Equal(result.Iterations, method.GetProperty("iterations").GetInt32());
        Assert.Equal([0, 1, 2, 3], method.GetProperty("blocks").EnumerateArray().Select(block => block.GetProperty("id").GetInt32()).ToArray());
        var entry = method.GetProperty("blocks")[0];
        Assert.Equal([1, 2], entry.GetProperty("successors").EnumerateArray().Select(id => id.GetInt32()).ToArray());
        var loan = Assert.Single(entry.GetProperty("exitState").GetProperty("loans").EnumerateArray());
        Assert.Equal("reader", loan.GetProperty("holder").GetString());
        Assert.Equal("Shared", loan.GetProperty("kind").GetString());
        Assert.False(loan.GetProperty("mayBeAbsent").GetBoolean());
        Assert.True(entry.GetProperty("exitState").GetProperty("isReachable").GetBoolean());
        var owner = Assert.Single(entry.GetProperty("exitState").GetProperty("places").EnumerateArray(), place => place.GetProperty("place").GetString() == "owner");
        Assert.False(owner.GetProperty("mayBeNull").GetBoolean());
        Assert.True(owner.GetProperty("references")[0].GetProperty("coversResource").GetBoolean());
        Assert.Contains(entry.GetProperty("liveOut").EnumerateArray(), place => place.GetString() == "reader");
        var released = method.GetProperty("blocks")[1].GetProperty("steps")[0];
        Assert.NotEmpty(released.GetProperty("before").GetProperty("loans").EnumerateArray());
        Assert.Empty(released.GetProperty("after").GetProperty("loans").EnumerateArray());
        var resource = Assert.Single(method.GetProperty("blocks")[3].GetProperty("exitState").GetProperty("resources").EnumerateArray());
        Assert.Equal("Disposed", resource.GetProperty("status").GetString());
        var issue = Assert.Single(root.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("SS0001", issue.GetProperty("id").GetString());
        Assert.NotEmpty(issue.GetProperty("evidence").EnumerateArray());
    }

    [Theory]
    [InlineData(ReportFormat.Json)]
    [InlineData(ReportFormat.Sarif)]
    [InlineData(ReportFormat.Dot)]
    [InlineData(ReportFormat.Mermaid)]
    [InlineData(ReportFormat.Html)]
    public void ReportsAreDeterministicAcrossMethodEnumerationAndCulture(ReportFormat format)
    {
        var first = Analyze("I.Work()", "/src/I.cs");
        var second = Analyze("İ.Work()", "/src/İ.cs");
        var expected = AnalysisReportRenderer.Render(new AnalysisReport([first, second]), format);
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            var actual = AnalysisReportRenderer.Render(new AnalysisReport([second, first]), format);
            Assert.Equal(expected, actual);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    [Fact]
    public void JsonRoundTripsControlCharactersUnicodeAndHostProperties()
    {
        const string hostile = "\"\t\b\f\r\n\\<tag>&\u2028\u2029😀";
        var source = new SourceSite("src/a # %.cs", 10, 2, 1, 4);
        var diagnostic = new ReportDiagnostic("SS0026", hostile, source,
            properties: new Dictionary<string, string?> { ["SafeSharp.DependencyPath"] = hostile, ["null"] = null });
        using var json = JsonDocument.Parse(AnalysisReportRenderer.Render(new AnalysisReport([], [diagnostic], title: hostile), ReportFormat.Json));
        Assert.Equal(hostile, json.RootElement.GetProperty("title").GetString());
        var actual = json.RootElement.GetProperty("diagnostics")[0];
        Assert.Equal(hostile, actual.GetProperty("message").GetString());
        Assert.Equal(hostile, actual.GetProperty("properties").GetProperty("SafeSharp.DependencyPath").GetString());
        Assert.Equal(JsonValueKind.Null, actual.GetProperty("properties").GetProperty("null").ValueKind);
    }

    [Fact]
    public void HostDiagnosticsControlSeverityAndSuppressionWithoutErasingEngineResults()
    {
        var result = Analyze();
        var issue = Assert.Single(result.Issues);
        var error = new ReportDiagnostic(issue.Id, issue.Message, issue.Site, ReportSeverity.Error);
        using var json = JsonDocument.Parse(AnalysisReportRenderer.Render(new AnalysisReport([result], [error]), ReportFormat.Json));
        Assert.Equal("error", json.RootElement.GetProperty("diagnostics")[0].GetProperty("severity").GetString());
        using var suppressed = JsonDocument.Parse(AnalysisReportRenderer.Render(new AnalysisReport([result], []), ReportFormat.Json));
        Assert.Empty(suppressed.RootElement.GetProperty("diagnostics").EnumerateArray());
        Assert.NotEmpty(suppressed.RootElement.GetProperty("methods")[0].GetProperty("issues").EnumerateArray());
    }

    [Fact]
    public void MissingSnapshotsAreDistinguishedFromEmptyStates()
    {
        var result = new OwnershipAnalysisEngine().Analyze(Graph());
        using var json = JsonDocument.Parse(AnalysisReportRenderer.Render(new AnalysisReport([result]), ReportFormat.Json));
        foreach (var block in json.RootElement.GetProperty("methods")[0].GetProperty("blocks").EnumerateArray())
        {
            Assert.False(block.GetProperty("hasSnapshots").GetBoolean());
            Assert.False(block.TryGetProperty("entryState", out _));
            Assert.False(block.TryGetProperty("steps", out _));
        }
    }

    [Fact]
    public void BudgetExhaustionIsReportedAsIncompleteInJsonSarifAndHtml()
    {
        var result = new OwnershipAnalysisEngine().Analyze(Graph(), new AnalysisOptions(captureSnapshots: true, maxIterations: 1));
        var report = new AnalysisReport([result]);
        Assert.True(result.BudgetExceeded);
        Assert.False(report.AnalysisComplete);
        using var json = JsonDocument.Parse(AnalysisReportRenderer.Render(report, ReportFormat.Json));
        Assert.False(json.RootElement.GetProperty("analysisComplete").GetBoolean());
        using var sarif = JsonDocument.Parse(AnalysisReportRenderer.Render(report, ReportFormat.Sarif));
        Assert.False(sarif.RootElement.GetProperty("runs")[0].GetProperty("invocations")[0].GetProperty("executionSuccessful").GetBoolean());
        Assert.Contains("Analysis incomplete.", AnalysisReportRenderer.Render(report, ReportFormat.Html));
    }

    [Theory]
    [InlineData(ReportFormat.Json)]
    [InlineData(ReportFormat.Sarif)]
    [InlineData(ReportFormat.Dot)]
    [InlineData(ReportFormat.Mermaid)]
    [InlineData(ReportFormat.Html)]
    public void CancellationIsObservedBeforeRendering(ReportFormat format)
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.Throws<OperationCanceledException>(() => AnalysisReportRenderer.Render(new AnalysisReport([Analyze()]), format, source.Token));
    }

    [Fact]
    public void HtmlEncodesAllSourceAndDiagnosticContentWithoutExecutableInterpolation()
    {
        const string attack = "\"</title></script><script>alert('owned')</script><img src=x onerror=alert(1)>";
        var result = Analyze(attack, attack);
        var issue = new ReportDiagnostic("SS0001", attack, new SourceSite(attack, 0, 3),
            properties: new Dictionary<string, string?> { [attack] = attack },
            evidence: [new AnalysisEvidence(attack, attack, new SourceSite(attack, 0, 3))]);
        var html = AnalysisReportRenderer.Render(new AnalysisReport([result], [issue], title: attack), ReportFormat.Html);
        Assert.DoesNotContain(attack, html);
        Assert.DoesNotContain("<img src=x", html);
        Assert.DoesNotContain("<script>alert", html);
        Assert.Contains("&lt;script&gt;alert(&#39;owned&#39;)&lt;/script&gt;", html);
        Assert.Equal(1, Count(html, "<script>"));
        Assert.Equal(1, Count(html, "</script>"));
        Assert.DoesNotContain("src=\"https://", html);
        Assert.DoesNotContain("innerHTML", html);
    }

    [Fact]
    public void DotUsesGeneratedNodeIdsAndEscapesSourceLabels()
    {
        const string attack = "\"; evil -> injected; //\n\\N";
        var dot = AnalysisReportRenderer.Render(new AnalysisReport([Analyze(attack)]), ReportFormat.Dot);
        Assert.Contains("label=\"\\\"; evil -> injected; //\\l\\\\N\"", dot);
        Assert.Contains("m0_b0 -> m0_b1;", dot);
        Assert.Contains("m0_b0 -> m0_b2;", dot);
        Assert.Contains("m0_b1 -> m0_b3;", dot);
        Assert.DoesNotContain("\n\\N", dot);
        Assert.DoesNotContain("label=\"\";", dot);
    }

    [Fact]
    public void MermaidEncodesQuotesDirectivesAndLineBreaksInsideLabels()
    {
        const string attack = "\"]\nend\nclick injected \"javascript:alert(1)\"\n%%{init:{}}%%";
        var mermaid = AnalysisReportRenderer.Render(new AnalysisReport([Analyze(attack)]), ReportFormat.Mermaid);
        Assert.DoesNotContain(attack, mermaid);
        Assert.DoesNotContain("\nclick", mermaid);
        Assert.DoesNotContain("%%{", mermaid);
        Assert.Contains("#34;#93;", mermaid);
        Assert.Contains("m0_b0 --> m0_b1", mermaid);
        Assert.Contains("m0_b2 --> m0_b3", mermaid);
    }

    [Fact]
    public void HtmlDisplaysComputedSnapshotsAndNllState()
    {
        var html = AnalysisReportRenderer.Render(new AnalysisReport([Analyze()]), ReportFormat.Html);
        Assert.Contains("Live before", html);
        Assert.Contains("Live after", html);
        Assert.Contains("Live loans", html);
        Assert.Contains("Disposed", html);
        Assert.Contains("B0 → B1", html);
        Assert.Contains("href=\"#m0_b1\"", html);
        Assert.Contains("reader", html);
        Assert.DoesNotContain("no captured state", html);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReportsPreserveNullRefinementAndPrunedBranchReachability(bool nullable)
    {
        var site = new SourceSite("/src/Nullable.cs", 0, 1);
        var graph = new AnalysisGraph("Nullable.Guard()",
        [
            new FlowBlock(0, [new FlowInstruction(InstructionKind.Allocate, site, target: "owner", resourceId: "R",
                flags: nullable ? InstructionFlags.MayBeNull : InstructionFlags.None)], [1, 2]),
            new FlowBlock(1, [new FlowInstruction(InstructionKind.AssumeNull, site, source: "owner")], []),
            new FlowBlock(2, [new FlowInstruction(InstructionKind.AssumeNotNull, site, source: "owner"),
                new FlowInstruction(InstructionKind.Dispose, site, source: "owner")], []),
        ], 0, site);
        var result = new OwnershipAnalysisEngine().Analyze(graph, new AnalysisOptions(captureSnapshots: true));
        Assert.Empty(result.Issues);
        var report = new AnalysisReport([result]);
        using var json = JsonDocument.Parse(AnalysisReportRenderer.Render(report, ReportFormat.Json));
        var blocks = json.RootElement.GetProperty("methods")[0].GetProperty("blocks");
        Assert.Equal(nullable, blocks[0].GetProperty("exitState").GetProperty("places")[0].GetProperty("mayBeNull").GetBoolean());
        Assert.Equal(nullable, blocks[1].GetProperty("exitState").GetProperty("isReachable").GetBoolean());
        var html = AnalysisReportRenderer.Render(report, ReportFormat.Html);
        if (nullable)
        {
            Assert.Contains("may be null", html);
            Assert.DoesNotContain("PRUNED", html);
        }
        else
        {
            Assert.Contains("PRUNED", html);
            Assert.Contains("This state is infeasible", html);
        }
    }

    [Theory]
    [InlineData(ReportFormat.Html)]
    [InlineData(ReportFormat.Dot)]
    [InlineData(ReportFormat.Mermaid)]
    public void GraphFormatsRenderRecordedServiceWitnessesWithSafeLabels(ReportFormat format)
    {
        const string hostile = "Root\"]\nclick injected \"javascript:alert(1)\"<script>";
        var diagnostic = new ReportDiagnostic("SS0026", "Scoped service captured by singleton.", new SourceSite("/src/Services.cs", 12, 8),
            properties: new Dictionary<string, string?>
            {
                ["SafeSharp.DependencyPath"] = hostile + " -> Bridge -> Scoped",
                ["SafeSharp.DependencyServices"] = "IRoot -> IBridge -> IScoped",
                ["SafeSharp.DependencyLifetimes"] = "Singleton -> Transient -> Scoped",
            });
        var output = AnalysisReportRenderer.Render(new AnalysisReport([], [diagnostic]), format);
        Assert.Contains("Singleton", output);
        Assert.Contains("Transient", output);
        Assert.Contains("IBridge", output);
        Assert.DoesNotContain(hostile, output);
        if (format != ReportFormat.Html) Assert.DoesNotContain("\nclick injected", output);
        switch (format)
        {
            case ReportFormat.Html:
                Assert.Contains("<ol class=\"dependency-witness\"", output);
                Assert.Equal(3, Count(output, "<div class=\"dependency-node\">"));
                Assert.Contains("&lt;script&gt;", output);
                break;
            case ReportFormat.Dot:
                Assert.Contains("d0_p0 -> d0_p1;", output);
                Assert.Contains("d0_p1 -> d0_p2;", output);
                Assert.DoesNotContain("label=\"Root\"]", output);
                break;
            case ReportFormat.Mermaid:
                Assert.Contains("d0_p0 --> d0_p1", output);
                Assert.Contains("d0_p1 --> d0_p2", output);
                Assert.Contains("Root#34;#93;", output);
                break;
        }
    }

    internal static AnalysisResult Analyze(string name = "Owner.Use()", string path = "/src/Owner.cs") =>
        new OwnershipAnalysisEngine().Analyze(Graph(name, path), new AnalysisOptions(captureSnapshots: true));

    private static AnalysisGraph Graph(string name = "Owner.Use()", string path = "/src/Owner.cs")
    {
        SourceSite Site(int start) => new(path, start, 6, start / 10, 2);
        return new AnalysisGraph(name,
        [
            new FlowBlock(0,
            [
                new FlowInstruction(InstructionKind.Allocate, Site(0), target: "owner", resourceId: "R"),
                new FlowInstruction(InstructionKind.BorrowShared, Site(20), target: "reader", source: "owner"),
            ], [2, 1]),
            new FlowBlock(2,
            [
                new FlowInstruction(InstructionKind.Read, Site(100), source: "reader"),
                new FlowInstruction(InstructionKind.Dispose, Site(120), source: "owner"),
            ], [3]),
            new FlowBlock(1,
            [
                new FlowInstruction(InstructionKind.Read, Site(40), source: "reader"),
                new FlowInstruction(InstructionKind.Dispose, Site(60), source: "owner"),
                new FlowInstruction(InstructionKind.Read, Site(80), source: "owner"),
            ], [3]),
            new FlowBlock(3, [], []),
        ], 0, new SourceSite(path, 0, 140));
    }

    private static int Count(string value, string token) => (value.Length - value.Replace(token, string.Empty).Length) / token.Length;
}
