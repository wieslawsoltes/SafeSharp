using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using SafeSharp.Analyzers.Analysis;
using SafeSharp.Core.Regions;

namespace SafeSharp.Analyzers.Semantics;

/// <summary>Checks explicit parametric lifetime signatures and the provenance of borrowed returns.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class LifetimeContractAnalyzer : DiagnosticAnalyzer
{
    public static readonly DiagnosticDescriptor InvalidDeclaration = Rule("SS0040", "Lifetime declaration is invalid",
        "Invalid lifetime contract on '{0}': {1}");
    public static readonly DiagnosticDescriptor ReturnViolation = Rule("SS0041", "Returned borrow does not satisfy its lifetime",
        "Returned value does not satisfy the lifetime contract of '{0}': {1}");
    public static readonly DiagnosticDescriptor InheritedContract = Rule("SS0042", "Implementation changes a lifetime or return promise",
        "Implementation '{0}' is incompatible with inherited contract '{1}': {2}");
    public static readonly DiagnosticDescriptor BudgetExceeded = Rule("SS0043", "Lifetime region analysis reached its budget",
        "Lifetime analysis for '{0}' stopped at the {1} budget; its return promise is not established");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(InvalidDeclaration, ReturnViolation, InheritedContract, BudgetExceeded);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(start =>
        {
            var contracts = new LifetimeContractProvider(start.Compilation);
            if (!contracts.Available) return;
            var summaries = new MethodSummaryProvider(start.Compilation);
            var declarations = new ConcurrentDictionary<IMethodSymbol, byte>(SymbolEqualityComparer.Default);
            var bodies = new ConcurrentDictionary<IMethodSymbol, byte>(SymbolEqualityComparer.Default);
            start.RegisterSymbolAction(symbolContext =>
            {
                var method = (IMethodSymbol)symbolContext.Symbol;
                var tree = method.Locations.FirstOrDefault(location => location.IsInSource)?.SourceTree;
                if (tree is null || !declarations.TryAdd(method, 0)) return;
                AnalyzeDeclaration(method, contracts, LifetimeContractProvider.Options(symbolContext.Options.AnalyzerConfigOptionsProvider.GetOptions(tree)),
                    symbolContext.ReportDiagnostic, symbolContext.CancellationToken);
            }, SymbolKind.Method);
            start.RegisterOperationBlockAction(blockContext =>
            {
                if (blockContext.OwningSymbol is not IMethodSymbol method) return;
                var cancellationToken = blockContext.CancellationToken;
                var outerContract = contracts.Get(method, cancellationToken);
                if (!outerContract.HasRegions && outerContract.BorrowedSource is null && !HasNestedAnnotatedFunction(blockContext.OperationBlocks, cancellationToken)) return;
                var graph = blockContext.OperationBlocks.Select(operation => ControlFlowLowerer.TryCreate(operation, cancellationToken)).FirstOrDefault(candidate => candidate is not null);
                if (graph is null) return;
                var options = LifetimeContractProvider.Options(blockContext.Options.AnalyzerConfigOptionsProvider.GetOptions(graph.OriginalOperation.Syntax.SyntaxTree));
                foreach (var item in ControlFlowLowerer.EnumerateGraphs(graph, method, cancellationToken))
                {
                    if (!bodies.TryAdd(item.Method, 0)) continue;
                    if (declarations.TryAdd(item.Method, 0)) AnalyzeDeclaration(item.Method, contracts, options, blockContext.ReportDiagnostic, cancellationToken);
                    var contract = contracts.Get(item.Method, cancellationToken);
                    if ((contract.ReturnRegion is null && contract.BorrowedSource is null) || !contract.DeclarationIssues.IsEmpty) continue;
                    var solution = contract.Solve(options, cancellationToken);
                    if (!solution.IsComplete) continue; // The declaration check already reports the incomplete solve.
                    foreach (var diagnostic in LifetimeReturnFlow.Analyze(item.Graph, contract, solution, contracts, summaries, options, cancellationToken))
                        blockContext.ReportDiagnostic(diagnostic);
                }
            });
        });
    }

    private static void AnalyzeDeclaration(IMethodSymbol method, LifetimeContractProvider provider, RegionSolverOptions options,
        Action<Diagnostic> report, CancellationToken cancellationToken)
    {
        var contract = provider.Get(method, cancellationToken);
        foreach (var issue in contract.DeclarationIssues)
            report(Diagnostic.Create(InvalidDeclaration, issue.Location, method.Name, issue.Message));
        if (!contract.DeclarationIssues.IsEmpty) return;
        if (!contract.HasRegions && contract.BorrowedSource is null && !contract.ReturnsOwned) return;
        var solved = contract.Solve(options, cancellationToken);
        if (!solved.IsComplete)
        {
            report(Diagnostic.Create(BudgetExceeded, method.Locations.FirstOrDefault(), method.Name, solved.BudgetReason));
            return;
        }
        var comparisonSteps = options.MaxSteps;
        foreach (var inherited in provider.Bases(method))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var promised = provider.Get(inherited, cancellationToken);
            if (!promised.DeclarationIssues.IsEmpty) continue;
            var inheritedSolution = promised.Solve(options, cancellationToken);
            if (!inheritedSolution.IsComplete)
            {
                report(Diagnostic.Create(BudgetExceeded, method.Locations.FirstOrDefault(), method.Name, "inherited region-solving"));
                return;
            }
            string? incompatibility;
            try { incompatibility = Incompatibility(contract, solved, promised, inheritedSolution, ref comparisonSteps, cancellationToken); }
            catch (CompatibilityBudgetException)
            {
                report(Diagnostic.Create(BudgetExceeded, method.Locations.FirstOrDefault(), method.Name, "inherited-contract comparison steps"));
                return;
            }
            if (incompatibility is null) continue;
            report(Diagnostic.Create(InheritedContract, method.Locations.FirstOrDefault(), inherited.Locations.Where(location => location.IsInSource),
                properties: null, messageArgs: new object[] { method.Name, inherited.ToDisplayString(), incompatibility }));
        }
    }

    private static string? Incompatibility(LifetimeContractModel actual, RegionSolution actualSolution, LifetimeContractModel promised,
        RegionSolution promisedSolution, ref int remainingSteps, CancellationToken cancellationToken)
    {
        ComparisonStep(ref remainingSteps, cancellationToken);
        if (promised.ReturnsOwned && (actual.BorrowedSource is not null || actual.ReturnRegion is not null)) return "an owned result was replaced by a borrowed result";
        if ((promised.BorrowedSource is not null || promised.ReturnRegion is not null) && actual.ReturnsOwned) return "a borrowed result was replaced by an owned result";
        if (promised.BorrowedSource is { } inheritedSource)
        {
            if (SourceOrdinal(actual, actual.BorrowedSource) != SourceOrdinal(promised, inheritedSource)) return "the returned borrow is tied to a different source parameter or receiver";
        }
        if ((promised.BorrowedSource is not null || promised.ReturnRegion is not null) && actual.MutableBorrow != promised.MutableBorrow)
            return "the returned loan's mutability changed";
        var count = Math.Min(actual.ParameterRegions.Length, promised.ParameterRegions.Length);
        for (var longer = 0; longer < count; longer++)
        for (var shorter = 0; shorter < count; shorter++)
        {
            ComparisonStep(ref remainingSteps, cancellationToken);
            if (longer == shorter || actual.ParameterRegions[longer] is not { } a || actual.ParameterRegions[shorter] is not { } b || !actualSolution.Outlives(a, b)) continue;
            if (promised.ParameterRegions[longer] is not { } x || promised.ParameterRegions[shorter] is not { } y || !promisedSolution.Outlives(x, y))
                return "an additional outlives requirement was imposed on input parameters";
        }
        if (promised.ReturnRegion is not { } inheritedReturn) return null;
        if (actual.ReturnRegion is not { } actualReturn) return "the returned lifetime promise was removed";
        for (var parameter = 0; parameter < count; parameter++)
        {
            ComparisonStep(ref remainingSteps, cancellationToken);
            if (actual.ParameterRegions[parameter] is not { } source || !actualSolution.Outlives(source, actualReturn)) continue;
            if (promised.ParameterRegions[parameter] is not { } inheritedInput || !promisedSolution.Outlives(inheritedInput, inheritedReturn))
                return "the result may borrow from an input which does not satisfy the inherited return region";
        }
        if (!actual.Method.IsStatic && actualSolution.Outlives("this", actualReturn) && !promisedSolution.Outlives("this", inheritedReturn))
            return "the result may borrow from the receiver beyond its inherited promise";
        return null;
    }

    private static void ComparisonStep(ref int remainingSteps, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (remainingSteps-- <= 0) throw new CompatibilityBudgetException();
    }

    private static bool HasNestedAnnotatedFunction(ImmutableArray<IOperation> blocks, CancellationToken cancellationToken)
    {
        // Preserve nested declaration checks while avoiding Roslyn CFG construction for ordinary bodies.
        foreach (var block in blocks)
        foreach (var syntax in block.Syntax.DescendantNodesAndSelf())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (syntax is LocalFunctionStatementSyntax { AttributeLists.Count: > 0 } or
                LambdaExpressionSyntax { AttributeLists.Count: > 0 } or ParameterSyntax { AttributeLists.Count: > 0 }) return true;
        }
        return false;
    }

    private sealed class CompatibilityBudgetException : Exception { }

    private static int SourceOrdinal(LifetimeContractModel contract, string? source) => source == "this" ? -1 :
        contract.Method.Parameters.FirstOrDefault(parameter => parameter.Name == source)?.Ordinal ?? -2;

    private static DiagnosticDescriptor Rule(string id, string title, string message) => new(id, title, message,
        "SafeSharp.Lifetimes", DiagnosticSeverity.Warning, isEnabledByDefault: true,
        description: "Checks explicit named lifetime regions, outlives implications, and borrowed return provenance. This does not implement higher-ranked lifetime polymorphism or a full Rust type system.",
        helpLinkUri: "https://github.com/wieslawsoltes/SafeSharp/blob/main/docs/lifetime-regions.md#" + id.ToLowerInvariant());
}
