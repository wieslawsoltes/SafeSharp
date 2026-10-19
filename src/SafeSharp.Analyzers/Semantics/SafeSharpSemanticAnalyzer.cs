using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

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
        SemanticDiagnostics.IncompleteServiceAnalysis);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var symbols = new ContractSymbols(start.Compilation);
            var services = new ServiceLifetimeAnalysis(symbols);
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
            start.RegisterOperationAction(c => CallbackOwnershipAnalysis.AnalyzeAssignment(c, symbols), OperationKind.SimpleAssignment);
            start.RegisterOperationAction(c => CallbackOwnershipAnalysis.AnalyzeAnonymousFunction(c, symbols), OperationKind.AnonymousFunction);
            start.RegisterCompilationEndAction(services.Analyze);
        });
    }
}
