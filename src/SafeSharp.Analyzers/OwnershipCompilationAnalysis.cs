using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using SafeSharp.Analyzers.Analysis;
using SafeSharp.Core;
using AnalysisResult = SafeSharp.Core.AnalysisResult;

namespace SafeSharp.Analyzers;

/// <summary>Reusable analysis entry point for tools. Compilation diagnostics must be checked by the caller.</summary>
public static class OwnershipCompilationAnalysis
{
    public static IEnumerable<AnalysisResult> Analyze(Compilation compilation, AnalysisOptions? options = null, CancellationToken cancellationToken = default,
        AnalyzerConfigOptionsProvider? configOptions = null)
    {
        if (compilation is null) throw new ArgumentNullException(nameof(compilation));
        var summaries = new MethodSummaryProvider(compilation);
        var analyzed = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        foreach (var tree in compilation.SyntaxTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SymbolFacts.IsGenerated(tree, cancellationToken)) continue;
            var model = compilation.GetSemanticModel(tree);
            var configured = configOptions is null ? options : OwnershipAnalyzer.ReadOptions(configOptions.GetOptions(tree), configOptions.GlobalOptions);
            var effectiveOptions = configOptions is null ? options : new AnalysisOptions(
                captureSnapshots: options?.CaptureSnapshots ?? false,
                maxIterations: configured!.MaxIterations, maxTrackedResources: configured.MaxTrackedResources,
                reportPossibleIssues: configured.ReportPossibleIssues, checkSuspension: configured.CheckSuspension);
            var root = tree.GetRoot(cancellationToken);
            foreach (var node in root.DescendantNodes().Where(n => n is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or LocalFunctionStatementSyntax ||
                n is PropertyDeclarationSyntax { ExpressionBody: not null } or IndexerDeclarationSyntax { ExpressionBody: not null }))
            {
                var symbol = model.GetDeclaredSymbol(node, cancellationToken);
                var method = symbol as IMethodSymbol ?? (symbol as IPropertySymbol)?.GetMethod;
                if (method is null || analyzed.Contains(method)) continue;
                var operationNode = node switch
                {
                    PropertyDeclarationSyntax property => (SyntaxNode)property.ExpressionBody!.Expression,
                    IndexerDeclarationSyntax indexer => indexer.ExpressionBody!.Expression,
                    _ => node
                };
                var operation = model.GetOperation(operationNode, cancellationToken);
                if (operation is null) continue;
                var graph = ControlFlowLowerer.TryCreate(operation, cancellationToken);
                if (graph is null) continue;
                foreach (var item in ControlFlowLowerer.EnumerateGraphs(graph, method, cancellationToken))
                {
                    if (!analyzed.Add(item.Method)) continue;
                    yield return new OwnershipAnalysisEngine().Analyze(ControlFlowLowerer.Lower(item.Graph, item.Method, summaries, cancellationToken), effectiveOptions, cancellationToken);
                }
            }
            if (root is CompilationUnitSyntax unit && unit.Members.OfType<GlobalStatementSyntax>().Any() &&
                compilation.GetEntryPoint(cancellationToken) is { } entryPoint && analyzed.Add(entryPoint))
            {
                var graph = Microsoft.CodeAnalysis.FlowAnalysis.ControlFlowGraph.Create(root, model, cancellationToken);
                if (graph is not null)
                    yield return new OwnershipAnalysisEngine().Analyze(ControlFlowLowerer.Lower(graph, entryPoint, summaries, cancellationToken), effectiveOptions, cancellationToken);
            }
        }
    }
}
