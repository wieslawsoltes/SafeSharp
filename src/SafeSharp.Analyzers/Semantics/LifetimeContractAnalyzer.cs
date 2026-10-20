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
    public static readonly DiagnosticDescriptor DelegateContract = Rule("SS0047", "Callback does not satisfy the delegate lifetime contract",
        "Callback '{0}' is incompatible with delegate '{1}': {2}");

    public static readonly DiagnosticDescriptor CallContract = Rule("SS0050", "Call does not satisfy its lifetime contract",
        "Call to '{0}' does not establish its lifetime contract: {1}");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(InvalidDeclaration, ReturnViolation, InheritedContract, BudgetExceeded, DelegateContract, CallContract);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(start =>
        {
            var contracts = new LifetimeContractProvider(start.Compilation);
            if (!contracts.Available) return;
            var summaries = new MethodSummaryProvider(start.Compilation);
            var callEffects = new LifetimeDelegateAnalysis.CallEffectsProvider();
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
            start.RegisterSymbolAction(symbolContext =>
            {
                if (symbolContext.Symbol is not INamedTypeSymbol { TypeKind: TypeKind.Delegate, DelegateInvokeMethod: { } method }) return;
                var tree = method.Locations.FirstOrDefault(location => location.IsInSource)?.SourceTree;
                if (tree is null || !declarations.TryAdd(method, 0)) return;
                AnalyzeDeclaration(method, contracts, LifetimeContractProvider.Options(symbolContext.Options.AnalyzerConfigOptionsProvider.GetOptions(tree)),
                    symbolContext.ReportDiagnostic, symbolContext.CancellationToken);
            }, SymbolKind.NamedType);
            start.RegisterOperationAction(operationContext => LifetimeDelegateAnalysis.Analyze(operationContext, contracts, summaries), OperationKind.DelegateCreation);
            start.RegisterOperationAction(operationContext => LifetimeDelegateAnalysis.AnalyzeCall(operationContext, contracts, callEffects), OperationKind.Invocation);
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
            string? typeBudget = null;
            ImmutableDictionary<string, string?>? typeProperties = null;
            try
            {
                incompatibility = Incompatibility(contract, solved, promised, inheritedSolution, ref comparisonSteps, cancellationToken);
                if (incompatibility is null)
                    incompatibility = LifetimeDelegateAnalysis.InheritedTypeMismatch(contract, promised, method, inherited, options, provider.Compilation,
                        ref comparisonSteps, cancellationToken, out typeBudget, out typeProperties);
            }
            catch (CompatibilityBudgetException)
            {
                report(Diagnostic.Create(BudgetExceeded, method.Locations.FirstOrDefault(), method.Name, "inherited-contract comparison steps"));
                return;
            }
            if (typeBudget is not null)
            {
                report(Diagnostic.Create(BudgetExceeded, method.Locations.FirstOrDefault(), method.Name, typeBudget));
                return;
            }
            if (incompatibility is null) continue;
            report(Diagnostic.Create(InheritedContract, method.Locations.FirstOrDefault(), inherited.Locations.Where(location => location.IsInSource),
                properties: typeProperties, messageArgs: new object[] { method.Name, inherited.ToDisplayString(), incompatibility }));
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
        // The receiver is a universally supplied input as well. Ignoring its edges would allow
        // overrides to strengthen a : this or this : a without checking the base precondition.
        var inputCount = count + (!actual.Method.IsStatic && !promised.Method.IsStatic ? 1 : 0);
        string? ActualInput(int ordinal) => ordinal == count ? "this" : actual.ParameterRegions[ordinal];
        string? PromisedInput(int ordinal) => ordinal == count ? "this" : promised.ParameterRegions[ordinal];
        for (var longer = 0; longer < inputCount; longer++)
        for (var shorter = 0; shorter < inputCount; shorter++)
        {
            ComparisonStep(ref remainingSteps, cancellationToken);
            if (longer == shorter || ActualInput(longer) is not { } a || ActualInput(shorter) is not { } b || !actualSolution.Outlives(a, b)) continue;
            if (PromisedInput(longer) is not { } x || PromisedInput(shorter) is not { } y || !promisedSolution.Outlives(x, y))
                return "an additional outlives requirement was imposed on input parameters or the receiver";
        }
        if (promised.ReturnRegion is not { } inheritedReturn) return null;
        if (actual.ReturnRegion is not { } actualReturn) return "the returned lifetime promise was removed";
        for (var parameter = 0; parameter < count; parameter++)
        {
            ComparisonStep(ref remainingSteps, cancellationToken);
            if (actual.BorrowedSource is { } explicitSource && SourceOrdinal(actual, explicitSource) != parameter) continue;
            if (actual.ParameterRegions[parameter] is not { } source || !actualSolution.Outlives(source, actualReturn)) continue;
            if (promised.ParameterRegions[parameter] is not { } inheritedInput || !promisedSolution.Outlives(inheritedInput, inheritedReturn))
                return "the result may borrow from an input which does not satisfy the inherited return region";
        }
        if (!actual.Method.IsStatic && (actual.BorrowedSource is null or "this") &&
            actualSolution.Outlives("this", actualReturn) && !promisedSolution.Outlives("this", inheritedReturn))
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
        description: "Checks explicit lifetime regions, borrowed provenance, nested callable binders, declared type variance and type-outlives obligations at inherited contracts and delegate conversions. This is not a full Rust type or trait system.",
        helpLinkUri: "https://github.com/wieslawsoltes/SafeSharp/blob/main/docs/lifetime-regions.md#" + id.ToLowerInvariant());
}
