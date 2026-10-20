using System.Text.Json;
using SafeSharp.Core;
using SafeSharp.Reporting;

namespace SafeSharp.Tests.Reporting;

public sealed class ProtocolReportTests
{
    private static SourceSite Site(int offset) => new("protocol.cs", offset, 1, offset, 0);

    private static AnalysisReport ReservationReport()
    {
        var graph = new AnalysisGraph("TwoPhase", [new FlowBlock(0,
        [
            new FlowInstruction(InstructionKind.Allocate, Site(0), "owner", resourceId: "resource"),
            new FlowInstruction(InstructionKind.ReserveExclusiveBorrow, Site(1), "reservation", "owner"),
            new FlowInstruction(InstructionKind.ActivateBorrow, Site(2), source: "reservation"),
            new FlowInstruction(InstructionKind.Write, Site(3), source: "reservation"),
            new FlowInstruction(InstructionKind.Dispose, Site(4), source: "owner")
        ], [])], 0, Site(10));
        return new AnalysisReport([new OwnershipAnalysisEngine().Analyze(graph, new AnalysisOptions(captureSnapshots: true))]);
    }

    [Fact]
    public void JsonPreservesReservationAndActivationStateAndSource()
    {
        using var writer = new StringWriter();
        new JsonReportWriter().Write(ReservationReport(), writer);
        using var json = JsonDocument.Parse(writer.ToString());
        var steps = json.RootElement.GetProperty("methods")[0].GetProperty("blocks")[0].GetProperty("steps");
        var reserved = Assert.Single(steps[1].GetProperty("after").GetProperty("loans").EnumerateArray());
        Assert.Equal("Reserved", reserved.GetProperty("phase").GetString());
        Assert.Empty(reserved.GetProperty("activationSites").EnumerateArray());
        var active = Assert.Single(steps[2].GetProperty("after").GetProperty("loans").EnumerateArray());
        Assert.Equal("Active", active.GetProperty("phase").GetString());
        Assert.Equal(2, Assert.Single(active.GetProperty("activationSites").EnumerateArray()).GetProperty("start").GetInt32());
    }

    [Fact]
    public void HtmlExplainsPhaseAndActivationLocation()
    {
        using var writer = new StringWriter();
        new HtmlReportWriter().Write(ReservationReport(), writer);
        var html = writer.ToString();
        Assert.Contains("Kind / phase / parent", html);
        Assert.Contains("Exclusive / Reserved", html);
        Assert.Contains("Exclusive / Active", html);
        Assert.Contains("activation: protocol.cs:3:1", html);
    }

    private static AnalysisReport CompletionReport(bool observe)
    {
        var instructions = new List<FlowInstruction>
        {
            new(InstructionKind.Allocate, Site(0), "owner", resourceId: "resource"),
            new(InstructionKind.BeginAsyncDispose, Site(1), "completion", "owner", resourceId: "completion")
        };
        if (observe) instructions.Add(new FlowInstruction(InstructionKind.CompleteAsyncDispose, Site(2), source: "completion"));
        var graph = new AnalysisGraph("AsyncDisposal", [new FlowBlock(0, instructions, [])], 0, Site(10));
        return new AnalysisReport([new OwnershipAnalysisEngine().Analyze(graph, new AnalysisOptions(captureSnapshots: true))]);
    }

    [Theory]
    [InlineData(false, "Pending")]
    [InlineData(true, "Completed")]
    public void JsonPreservesCompletionKindStatusAndOwnerProvenance(bool observe, string expected)
    {
        using var writer = new StringWriter();
        new JsonReportWriter().Write(CompletionReport(observe), writer);
        using var json = JsonDocument.Parse(writer.ToString());
        var resources = json.RootElement.GetProperty("methods")[0].GetProperty("blocks")[0].GetProperty("exitState").GetProperty("resources");
        var completion = resources.EnumerateArray().Single(resource => resource.GetProperty("id").GetString() == "completion");
        Assert.Equal("AsyncDisposalCompletion", completion.GetProperty("kind").GetString());
        Assert.Equal(expected, completion.GetProperty("status").GetString());
        Assert.Equal("resource", Assert.Single(completion.GetProperty("disposedResourceIds").EnumerateArray()).GetString());
        Assert.Empty(completion.GetProperty("ownerResourceIds").EnumerateArray());
    }

    [Fact]
    public void HtmlDistinguishesDisposedOwnerFromPendingCompletion()
    {
        using var writer = new StringWriter();
        new HtmlReportWriter().Write(CompletionReport(false), writer);
        var html = writer.ToString();
        Assert.Contains("AsyncDisposalCompletion / Owned", html);
        Assert.Contains("Pending", html);
        Assert.Contains("completion for: resource", html);
        Assert.Contains("SS0017", html);
    }

    [Fact]
    public void SarifCarriesTheUnobservedCompletionAndItsSourceEvidence()
    {
        using var writer = new StringWriter();
        new SarifReportWriter().Write(CompletionReport(false), writer);
        using var json = JsonDocument.Parse(writer.ToString());
        var issue = Assert.Single(json.RootElement.GetProperty("runs")[0].GetProperty("results").EnumerateArray());
        Assert.Equal("SS0017", issue.GetProperty("ruleId").GetString());
        Assert.NotEmpty(issue.GetProperty("relatedLocations").EnumerateArray());
        Assert.Contains("completion", issue.GetProperty("message").GetProperty("text").GetString());
    }
}
