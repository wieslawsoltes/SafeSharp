using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using SafeSharp.Analyzers.Analysis;
using SafeSharp.Core;

namespace SafeSharp.Analyzers;

/// <summary>Concurrent, cancellation-aware compiler analyzer. It performs no I/O and retains no global compilation state.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class OwnershipAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => OwnershipDiagnostics.All;

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(start =>
        {
            var summaries = new MethodSummaryProvider(start.Compilation);
            var analyzed = new ConcurrentDictionary<IMethodSymbol, byte>(SymbolEqualityComparer.Default);
            start.RegisterOperationBlockAction(blockContext =>
            {
                if (blockContext.OwningSymbol is not IMethodSymbol method || blockContext.OperationBlocks.IsEmpty) return;
                var cancellationToken = blockContext.CancellationToken;
                // Attribute initializers may precede the executable method body in OperationBlocks.
                // Selecting index zero silently skipped precisely the methods carrying our contracts.
                var graph = blockContext.OperationBlocks.Select(operation => ControlFlowLowerer.TryCreate(operation, cancellationToken)).FirstOrDefault(candidate => candidate is not null);
                if (graph is null) return;
                var provider = blockContext.Options.AnalyzerConfigOptionsProvider;
                var options = ReadOptions(provider.GetOptions(graph.OriginalOperation.Syntax.SyntaxTree), provider.GlobalOptions);
                foreach (var item in ControlFlowLowerer.EnumerateGraphs(graph, method, cancellationToken))
                {
                    if (!analyzed.TryAdd(item.Method, 0)) continue;
                    try
                    {
                        var lowered = ControlFlowLowerer.Lower(item.Graph, item.Method, summaries, cancellationToken);
                        var result = new OwnershipAnalysisEngine().Analyze(lowered, options, cancellationToken);
                        foreach (var issue in result.Issues)
                            blockContext.ReportDiagnostic(ToDiagnostic(issue, start.Compilation));
                    }
                    catch (AnalysisBudgetException exception)
                    {
                        blockContext.ReportDiagnostic(Diagnostic.Create(OwnershipDiagnostics.Get("SS0015"), item.Graph.OriginalOperation.Syntax.GetLocation(), exception.Message));
                    }
                }
            });
        });
    }

    internal static AnalysisOptions ReadOptions(AnalyzerConfigOptions options, AnalyzerConfigOptions? globalOptions = null)
    {
        static bool Boolean(AnalyzerConfigOptions values, string key, bool fallback) =>
            values.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) ? parsed : fallback;
        var iterations = 10000;
        if (globalOptions?.TryGetValue("build_property.SafeSharpMaxIterations", out var globalIterations) == true && int.TryParse(globalIterations, out var globalLimit) && globalLimit > 0)
            iterations = Math.Min(globalLimit, 1000000);
        if (options.TryGetValue("safesharp_max_iterations", out var value) && int.TryParse(value, out var configured) && configured > 0)
            iterations = Math.Min(configured, 1000000);
        var resources = 2048;
        if (globalOptions?.TryGetValue("build_property.SafeSharpMaxTrackedResources", out var globalResources) == true && int.TryParse(globalResources, out var globalResourceLimit) && globalResourceLimit > 0)
            resources = Math.Min(globalResourceLimit, 100000);
        if (options.TryGetValue("safesharp_max_tracked_resources", out value) && int.TryParse(value, out configured) && configured > 0)
            resources = Math.Min(configured, 100000);
        return new AnalysisOptions(maxIterations: iterations, maxTrackedResources: resources,
            reportPossibleIssues: Boolean(options, "safesharp_report_possible_issues", true),
            checkSuspension: Boolean(options, "safesharp_check_suspension", true));
    }

    private static Diagnostic ToDiagnostic(AnalysisIssue issue, Compilation compilation)
    {
        var properties = ImmutableDictionary<string, string?>.Empty
            .Add("SafeSharp.Certainty", issue.Certainty.ToString())
            .Add("SafeSharp.ResourceId", issue.ResourceId)
            .Add("SafeSharp.Evidence", string.Join(" | ", issue.Evidence.Select(e => e.Message)));
        return Diagnostic.Create(OwnershipDiagnostics.Get(issue.Id), LocationFor(issue.Site, compilation),
            issue.RelatedLocations.Select(site => LocationFor(site, compilation)), properties, issue.Message);
    }

    internal static Location LocationFor(SourceSite site, Compilation compilation)
    {
        var tree = compilation.SyntaxTrees.FirstOrDefault(t => t.FilePath == site.Path);
        if (tree is null) return Location.None;
        var span = new TextSpan(Math.Min(site.Start, tree.Length), Math.Min(site.Length, Math.Max(0, tree.Length - site.Start)));
        return Location.Create(tree, span);
    }
}
