using System.Text.Json;
using SafeSharp.Core;
using SafeSharp.Reporting;

namespace SafeSharp.Tests.Reporting;

public sealed class ValueTaskReportTests
{
    private static AnalysisReport Report()
    {
        var site = new SourceSite("value-task.cs", 10, 1);
        var graph = new AnalysisGraph("Consume", [new FlowBlock(0, [
            new FlowInstruction(InstructionKind.TrackValueTask, site, "pending", resourceId: "single-use"),
            new FlowInstruction(InstructionKind.ConsumeValueTask, site, source: "pending")], [])], 0, site);
        return new AnalysisReport([new OwnershipAnalysisEngine().Analyze(graph, new AnalysisOptions(captureSnapshots: true))]);
    }

    [Fact]
    public void JsonIncludesProtocolStateWithoutInventingAResource()
    {
        using var writer = new StringWriter();
        new JsonReportWriter().Write(Report(), writer);
        using var json = JsonDocument.Parse(writer.ToString());
        var state = json.RootElement.GetProperty("methods")[0].GetProperty("blocks")[0].GetProperty("exitState");
        Assert.Empty(state.GetProperty("resources").EnumerateArray());
        var capability = Assert.Single(state.GetProperty("valueTasks").EnumerateArray());
        Assert.Equal("single-use", capability.GetProperty("id").GetString());
        Assert.Equal("Consumed", capability.GetProperty("state").GetString());
        Assert.Equal("pending", Assert.Single(capability.GetProperty("places").EnumerateArray()).GetString());
        Assert.NotEmpty(capability.GetProperty("evidenceSites").EnumerateArray());
    }

    [Fact]
    public void HtmlShowsCapabilityAliasesAndConsumption()
    {
        using var writer = new StringWriter();
        new HtmlReportWriter().Write(Report(), writer);
        Assert.Contains("ValueTask consumption capabilities", writer.ToString());
        Assert.Contains("single-use", writer.ToString());
        Assert.Contains("Consumed", writer.ToString());
    }
}
