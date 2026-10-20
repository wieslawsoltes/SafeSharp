using SafeSharp.Analyzers;
using SafeSharp.Core;

namespace SafeSharp.Tests.Flow;

public sealed class CompilationAnalysisTests
{
    [Fact]
    public void ReportsExpressionBodiedPropertyAndIndexerGraphs()
    {
        const string source = """
            using System.IO;
            class Example
            {
                public MemoryStream Value => new MemoryStream();
                public MemoryStream this[int index] => new MemoryStream();
            }
            """;
        var compilation = AnalyzerTestHost.CreateCompilation(source);
        Assert.DoesNotContain(compilation.GetDiagnostics(), d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        var results = OwnershipCompilationAnalysis.Analyze(compilation, new AnalysisOptions(captureSnapshots: true)).ToArray();
        Assert.Equal(2, results.Length);
        Assert.All(results, result =>
        {
            Assert.Empty(result.Issues);
            Assert.Contains(result.Graph.Blocks.SelectMany(block => block.Instructions), instruction => instruction.Kind == InstructionKind.Allocate);
            Assert.Contains(result.Graph.Blocks.SelectMany(block => block.Instructions), instruction => instruction.Kind == InstructionKind.Return);
            Assert.NotEmpty(result.Blocks);
        });
    }

    [Theory]
    [InlineData("Value")]
    [InlineData("this[0]")]
    public async Task InfersFreshOwnershipFromExpressionBodiedGetters(string expression)
    {
        var source = """
            using System.IO;
            class Example
            {
                public MemoryStream Value => new MemoryStream();
                public MemoryStream this[int index] => new MemoryStream();
                public void Leak() { var resource = EXPRESSION; }
            }
            """.Replace("EXPRESSION", expression, StringComparison.Ordinal);
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        Assert.Single(diagnostics, diagnostic => diagnostic.Id == "SS0003");
    }
}
