using System.Text.Json;
using SafeSharp.Core;
using SafeSharp.Reporting;

namespace SafeSharp.Tests.Reporting;

public sealed class SarifReportTests
{
    [Fact]
    public void SarifPreservesExactUtf16CoordinatesRelatedLocationsEvidenceAndMetadata()
    {
        var origin = new SourceSite("/src/😀 owner.cs", 5, 12, 1, 2);
        var access = new SourceSite("/src/😀 owner.cs", 137, 29, 8, 17);
        var diagnostic = new ReportDiagnostic("SS0001", "Resource disposed earlier", access, ReportSeverity.Error,
            title: "Use after disposal", method: "Example.Use()", resourceId: "R", certainty: IssueCertainty.Possible,
            relatedLocations: [origin], evidence: [new AnalysisEvidence("dispose", "Disposed here", origin)],
            properties: new Dictionary<string, string?> { ["SafeSharp.DependencyPath"] = "Root -> Scoped" });
        using var json = JsonDocument.Parse(AnalysisReportRenderer.Render(new AnalysisReport([], [diagnostic], toolVersion: "0.1.0"), ReportFormat.Sarif));
        Assert.Equal("2.1.0", json.RootElement.GetProperty("version").GetString());
        Assert.Contains("sarif-schema-2.1.0.json", json.RootElement.GetProperty("$schema").GetString());
        var run = json.RootElement.GetProperty("runs")[0];
        Assert.Equal("utf16CodeUnits", run.GetProperty("columnKind").GetString());
        var rule = Assert.Single(run.GetProperty("tool").GetProperty("driver").GetProperty("rules").EnumerateArray());
        Assert.Equal("SS0001", rule.GetProperty("id").GetString());
        var result = Assert.Single(run.GetProperty("results").EnumerateArray());
        Assert.Equal(0, result.GetProperty("ruleIndex").GetInt32());
        Assert.Equal("error", result.GetProperty("level").GetString());
        var physical = result.GetProperty("locations")[0].GetProperty("physicalLocation");
        Assert.Equal("file:///src/%F0%9F%98%80%20owner.cs", physical.GetProperty("artifactLocation").GetProperty("uri").GetString());
        var region = physical.GetProperty("region");
        Assert.Equal(9, region.GetProperty("startLine").GetInt32());
        Assert.Equal(18, region.GetProperty("startColumn").GetInt32());
        Assert.Equal(137, region.GetProperty("charOffset").GetInt32());
        Assert.Equal(29, region.GetProperty("charLength").GetInt32());
        Assert.False(region.TryGetProperty("endLine", out _));
        var related = result.GetProperty("relatedLocations")[0];
        Assert.Equal(1, related.GetProperty("id").GetInt32());
        Assert.Equal("Disposed here", related.GetProperty("message").GetProperty("text").GetString());
        var provenance = result.GetProperty("codeFlows")[0].GetProperty("threadFlows")[0].GetProperty("locations")[0];
        Assert.Equal("abstractProvenance", provenance.GetProperty("properties").GetProperty("evidenceSemantics").GetString());
        Assert.False(provenance.TryGetProperty("executionOrder", out _));
        Assert.Equal("Root -> Scoped", result.GetProperty("properties").GetProperty("diagnosticProperties").GetProperty("SafeSharp.DependencyPath").GetString());
        Assert.Matches("^[a-f0-9]{64}$", result.GetProperty("partialFingerprints").GetProperty("safesharp/v1").GetString()!);
    }

    [Theory]
    [InlineData("/src/a #%.cs", "file:///src/a%20%23%25.cs")]
    [InlineData(@"C:\src\a #%.cs", "file:///C:/src/a%20%23%25.cs")]
    [InlineData(@"\\server\share\a b.cs", "file://server/share/a%20b.cs")]
    [InlineData("src/a b.cs", "src/a%20b.cs")]
    [InlineData("javascript:alert(1).cs", "javascript%3Aalert%281%29.cs")]
    public void PathsAreEncodedIndependentlyOfHostOperatingSystem(string path, string expected)
    {
        var diagnostic = new ReportDiagnostic("SS0001", "message", new SourceSite(path, 0, 1));
        using var json = JsonDocument.Parse(AnalysisReportRenderer.Render(new AnalysisReport([], [diagnostic]), ReportFormat.Sarif));
        var location = json.RootElement.GetProperty("runs")[0].GetProperty("results")[0].GetProperty("locations")[0];
        Assert.Equal(expected, location.GetProperty("physicalLocation").GetProperty("artifactLocation").GetProperty("uri").GetString());
    }

    [Fact]
    public void RelativeSourcePathsUseExplicitSourceRoot()
    {
        var diagnostic = new ReportDiagnostic("SS0001", "message", new SourceSite("src/File.cs", 0, 1));
        using var json = JsonDocument.Parse(AnalysisReportRenderer.Render(new AnalysisReport([], [diagnostic], sourceRoot: "/checkout/app"), ReportFormat.Sarif));
        var run = json.RootElement.GetProperty("runs")[0];
        Assert.Equal("file:///checkout/app/", run.GetProperty("originalUriBaseIds").GetProperty("%SRCROOT%").GetProperty("uri").GetString());
        var artifact = run.GetProperty("results")[0].GetProperty("locations")[0].GetProperty("physicalLocation").GetProperty("artifactLocation");
        Assert.Equal("%SRCROOT%", artifact.GetProperty("uriBaseId").GetString());
    }

    [Fact]
    public void SourceUnavailableDoesNotInventFileCoordinates()
    {
        var diagnostic = new ReportDiagnostic("SS0029", "Compilation analysis incomplete", new SourceSite(string.Empty, 0, 0), ReportSeverity.Note);
        using var json = JsonDocument.Parse(AnalysisReportRenderer.Render(new AnalysisReport([], [diagnostic], analysisComplete: false), ReportFormat.Sarif));
        var location = json.RootElement.GetProperty("runs")[0].GetProperty("results")[0].GetProperty("locations")[0];
        Assert.False(location.TryGetProperty("physicalLocation", out _));
        Assert.Equal("Source location unavailable", location.GetProperty("message").GetProperty("text").GetString());
    }
}
