using System.Text.Json;
using SafeSharp.Core;
using SafeSharp.Reporting;

namespace SafeSharp.Tests.Reporting;

public sealed class ReferenceStorageReportTests
{
    private static AnalysisReport Report()
    {
        var site = new SourceSite("ref-storage.cs", 0, 1);
        var graph = new AnalysisGraph("References", [new FlowBlock(0, [
            new FlowInstruction(InstructionKind.BindReference, site, "alias", "first"),
            new FlowInstruction(InstructionKind.MergeReference, site, "alias", "second")], [])], 0, site);
        return new AnalysisReport([new OwnershipAnalysisEngine().Analyze(graph, new AnalysisOptions(captureSnapshots: true))]);
    }

    [Fact]
    public void JsonSeparatesAddressTargetsFromOrdinaryValueAliases()
    {
        using var writer = new StringWriter();
        new JsonReportWriter().Write(Report(), writer);
        using var json = JsonDocument.Parse(writer.ToString());
        var state = json.RootElement.GetProperty("methods")[0].GetProperty("blocks")[0].GetProperty("exitState");
        var binding = Assert.Single(state.GetProperty("referenceStorage").EnumerateArray());
        Assert.Equal("alias", binding.GetProperty("reference").GetString());
        Assert.Equal(new[] { "first", "second" }, binding.GetProperty("targets").EnumerateArray().Select(target => target.GetString()));
        Assert.Empty(state.GetProperty("places").EnumerateArray());
    }

    [Fact]
    public void HtmlDisplaysTheReferencesPossibleStorageTargets()
    {
        using var writer = new StringWriter();
        new HtmlReportWriter().Write(Report(), writer);
        Assert.Contains("Reference storage", writer.ToString());
        Assert.Contains("Possible target locations", writer.ToString());
        Assert.Contains("first, second", writer.ToString());
    }
}
