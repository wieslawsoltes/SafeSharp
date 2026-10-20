using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using SafeSharp.Analyzers.Analysis;

namespace SafeSharp.Analyzers.Semantics;

/// <summary>Checks ownership contracts that span members, callbacks, and service registrations.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SafeSharpSemanticAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        SemanticDiagnostics.MissingDisposalContract,
        SemanticDiagnostics.OwnedFieldNotDisposed,
        SemanticDiagnostics.CopyableOwner,
        SemanticDiagnostics.InvalidContract,
        SemanticDiagnostics.IgnoredMustUse,
        SemanticDiagnostics.ThreadAffineTaskCapture,
        SemanticDiagnostics.CaptiveScopedService,
        SemanticDiagnostics.StoredBorrowedParameter,
        SemanticDiagnostics.EscapingUsingCapture,
        SemanticDiagnostics.IncompleteServiceAnalysis,
        ConcurrencyDiagnostics.IncompleteContractAnalysis);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var symbols = new ContractSymbols(start.Compilation);
            var services = new ServiceLifetimeAnalysis(symbols);
            var summaries = new MethodSummaryProvider(start.Compilation);
            start.RegisterSymbolAction(c =>
            {
                c.CancellationToken.ThrowIfCancellationRequested();
                ContractValidationAnalysis.Analyze(c, symbols);
                if (c.Symbol is INamedTypeSymbol type)
                {
                    AggregateOwnershipAnalysis.Analyze(c, type, symbols);
                    services.AddType(type);
                }
            }, SymbolKind.NamedType, SymbolKind.Method, SymbolKind.Property, SymbolKind.Field);
            start.RegisterOperationAction(c => CallbackOwnershipAnalysis.AnalyzeInvocation(c, symbols, services), OperationKind.Invocation);
            start.RegisterOperationAction(c => CallbackOwnershipAnalysis.AnalyzeExpressionStatement(c, symbols), OperationKind.ExpressionStatement);
            start.RegisterOperationAction(c => CallbackOwnershipAnalysis.AnalyzeAnonymousFunction(c, symbols), OperationKind.AnonymousFunction);
            start.RegisterOperationBlockAction(c =>
            {
                if (c.OwningSymbol is not IMethodSymbol method || !BorrowedEscapeAnalysis.IsRelevant(c.OperationBlocks, c.CancellationToken)) return;
                var graph = c.OperationBlocks.Select(operation => ControlFlowLowerer.TryCreate(operation, c.CancellationToken))
                    .FirstOrDefault(candidate => candidate is not null);
                if (graph is not null) BorrowedEscapeAnalysis.Analyze(graph, method, start.Compilation, summaries,
                    c.Options.AnalyzerConfigOptionsProvider, c.ReportDiagnostic, c.CancellationToken);
            });
            start.RegisterCompilationEndAction(services.Analyze);
        });
    }
}
