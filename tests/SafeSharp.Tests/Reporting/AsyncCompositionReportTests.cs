using System.Text.Json;
using SafeSharp.Core;
using SafeSharp.Reporting;

namespace SafeSharp.Tests.Reporting;

public sealed class AsyncCompositionReportTests
{
    private static AnalysisReport Report()
    {
        var site = new SourceSite("composition.cs", 10, 1);
        var graph = new AnalysisGraph("Composition", [new FlowBlock(0, [
            new FlowInstruction(InstructionKind.BeginAsyncFactory, site, "input", resourceId: "input"),
            new FlowInstruction(InstructionKind.CreateAggregate, site, "inputs", resourceId: "inputs"),
            new FlowInstruction(InstructionKind.SetAggregateElement, site, source: "input", owner: "inputs", projection: "[0]"),
            new FlowInstruction(InstructionKind.BeginAsyncComposition, site, "proxy", "inputs", resourceId: "proxy",
                asyncComposition: AsyncCompositionKind.WhenAll, flags: InstructionFlags.HasAsyncResult),
            new FlowInstruction(InstructionKind.CompleteAsyncComposition, site, source: "proxy"),
            new FlowInstruction(InstructionKind.ReceiveAsyncResult, site, "results", "proxy")], [])], 0, site);
        return new AnalysisReport([new OwnershipAnalysisEngine().Analyze(graph, new AnalysisOptions(captureSnapshots: true))]);
    }

    [Fact]
    public void JsonLinksTheProxyCapturedInputsAndIndividualResults()
    {
        using var writer = new StringWriter();
        new JsonReportWriter().Write(Report(), writer);
        using var document = JsonDocument.Parse(writer.ToString());
        var resources = document.RootElement.GetProperty("methods")[0].GetProperty("blocks")[0]
            .GetProperty("exitState").GetProperty("resources").EnumerateArray().ToArray();
        var proxy = Assert.Single(resources, resource => resource.GetProperty("id").GetString() == "proxy");
        Assert.Equal("WhenAll", proxy.GetProperty("asyncComposition").GetString());
        Assert.Equal("async-inputs:proxy", Assert.Single(proxy.GetProperty("dependencyResourceIds").EnumerateArray()).GetString());
        var array = Assert.Single(resources, resource => resource.GetProperty("id").GetString() == "async-results:proxy");
        var element = Assert.Single(array.GetProperty("elements").EnumerateArray());
        Assert.Equal("[0]", element.GetProperty("index").GetString());
        Assert.Equal("async-result:input", Assert.Single(element.GetProperty("resourceIds").EnumerateArray()).GetString());
    }

    [Fact]
    public void HtmlShowsCompositionAndAggregateTables()
    {
        using var writer = new StringWriter();
        new HtmlReportWriter().Write(Report(), writer);
        Assert.Contains("Asynchronous compositions", writer.ToString());
        Assert.Contains("Aggregate element identities", writer.ToString());
        Assert.Contains("WhenAll", writer.ToString());
        Assert.Contains("async-result:input", writer.ToString());
    }
}
