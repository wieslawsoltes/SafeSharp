using System.Text.Json;
using SafeSharp.Core;
using SafeSharp.Reporting;

namespace SafeSharp.Tests.Reporting;

public sealed class AsyncFactoryReportTests
{
    private static SourceSite Site(int offset) => new("factory.cs", offset, 1, offset, 0);

    private static AnalysisReport Report(bool receive)
    {
        var operations = new List<FlowInstruction> { new(InstructionKind.BeginAsyncFactory, Site(0), "task", resourceId: "factory") };
        if (receive)
        {
            operations.Add(new FlowInstruction(InstructionKind.ObserveAsyncFactory, Site(1), source: "task"));
            operations.Add(new FlowInstruction(InstructionKind.ReceiveAsyncResult, Site(2), "result", "task"));
        }
        var graph = new AnalysisGraph("Factory", [new FlowBlock(0, operations, [])], 0, Site(10));
        return new AnalysisReport([new OwnershipAnalysisEngine().Analyze(graph, new AnalysisOptions(captureSnapshots: true))]);
    }

    [Theory]
    [InlineData(false, "Deferred", null)]
    [InlineData(true, "Materialized", "async-result:factory")]
    public void JsonKeepsCarrierPhaseAndPayloadIdentityDistinct(bool receive, string phase, string? payload)
    {
        using var writer = new StringWriter();
        new JsonReportWriter().Write(Report(receive), writer);
        using var json = JsonDocument.Parse(writer.ToString());
        var carrier = json.RootElement.GetProperty("methods")[0].GetProperty("blocks")[0].GetProperty("exitState")
            .GetProperty("resources").EnumerateArray().Single(resource => resource.GetProperty("id").GetString() == "factory");
        Assert.Equal("AsyncFactoryCarrier", carrier.GetProperty("kind").GetString());
        Assert.Equal(phase, carrier.GetProperty("resultState").GetString());
        Assert.Equal(payload, carrier.GetProperty("producedResourceId").GetString());
        Assert.Empty(carrier.GetProperty("ownerResourceIds").EnumerateArray());
    }

    [Fact]
    public void HtmlExplainsTheCarrierAndItsSeparateOwnedPayload()
    {
        using var writer = new StringWriter();
        new HtmlReportWriter().Write(Report(true), writer);
        var html = writer.ToString();
        Assert.Contains("AsyncFactoryCarrier / Owned", html);
        Assert.Contains("result: Materialized (async-result:factory)", html);
        Assert.Contains("factory-result", html);
    }

    [Fact]
    public void SarifDoesNotClaimDefiniteAllocationForAnUnreceivedFactory()
    {
        using var writer = new StringWriter();
        new SarifReportWriter().Write(Report(false), writer);
        using var json = JsonDocument.Parse(writer.ToString());
        var issue = Assert.Single(json.RootElement.GetProperty("runs")[0].GetProperty("results").EnumerateArray());
        Assert.Equal("SS0003", issue.GetProperty("ruleId").GetString());
        Assert.Contains("may produce an owned result", issue.GetProperty("message").GetProperty("text").GetString());
        Assert.Equal("Possible", issue.GetProperty("properties").GetProperty("certainty").GetString());
    }
}
