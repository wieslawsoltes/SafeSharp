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
}
