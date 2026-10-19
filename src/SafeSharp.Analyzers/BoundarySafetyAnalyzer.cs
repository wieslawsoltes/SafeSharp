using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using SafeSharp.Analyzers.Analysis;

namespace SafeSharp.Analyzers;

/// <summary>Reports C# lifetime boundaries which have no direct counterpart in safe Rust.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BoundarySafetyAnalyzer : DiagnosticAnalyzer
{
    public static readonly DiagnosticDescriptor ExceptionSafety = new("SS0030", "Cleanup can be bypassed by an exception",
        "Resource '{0}' has manual cleanup after a potentially throwing operation; use using or try/finally", "Ownership",
        DiagnosticSeverity.Info, true, description: "Normal-path ownership analysis alone cannot establish cleanup for an unhandled exception. This advisory identifies local acquisitions with a later manual release and an intervening throwing operation.",
        helpLinkUri: "https://github.com/wieslawsoltes/SafeSharp/blob/main/docs/diagnostics.md#ss0030");
    public static readonly DiagnosticDescriptor UnobservedDisposal = new("SS0031", "Asynchronous disposal is not observed",
        "Await or return the result of '{0}'; dropping the awaitable can leave disposal incomplete", "Ownership",
        DiagnosticSeverity.Warning, true, description: "DisposeAsync completion must be observed. Assigning its result to a local for a later await is a separate lifetime-analysis boundary.",
        helpLinkUri: "https://github.com/wieslawsoltes/SafeSharp/blob/main/docs/diagnostics.md#ss0031");
    public static readonly DiagnosticDescriptor UnsafeBoundary = new("SS0032", "Operation crosses the ownership model boundary",
        "Ownership analysis does not prove safety across this {0} operation", "Ownership",
        DiagnosticSeverity.Info, false, description: "Unsafe pointers and dynamic dispatch can introduce unmodeled aliases and lifetime effects. Enable this rule to audit these boundaries.",
        helpLinkUri: "https://github.com/wieslawsoltes/SafeSharp/blob/main/docs/diagnostics.md#ss0032");
    public static readonly DiagnosticDescriptor MissingContract = new("SS0033", "Resource result has no ownership contract",
        "The result of '{0}' has no known ownership contract; annotate the API with ReturnsOwned or ReturnsBorrowed", "Ownership",
        DiagnosticSeverity.Info, false, description: "Unknown resource-returning APIs are external boundaries. SafeSharp does not invent an ownership transfer for arbitrary library calls.",
        helpLinkUri: "https://github.com/wieslawsoltes/SafeSharp/blob/main/docs/diagnostics.md#ss0033");
    public static readonly DiagnosticDescriptor OverrideContract = new("SS0034", "Implementation changes an ownership contract",
        "Parameter '{0}' declares {1} but its inherited contract requires {2}", "Ownership",
        DiagnosticSeverity.Warning, true, description: "Callers through an interface or base declaration must observe the same ownership effects as callers through the implementation. Omitted implementation annotations inherit the declared contract.",
        helpLinkUri: "https://github.com/wieslawsoltes/SafeSharp/blob/main/docs/diagnostics.md#ss0034");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(ExceptionSafety, UnobservedDisposal, UnsafeBoundary, MissingContract, OverrideContract);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(start =>
        {
            var summaries = new MethodSummaryProvider(start.Compilation);
            start.RegisterOperationAction(operationContext =>
            {
                var invocation = (IInvocationOperation)operationContext.Operation;
                if (SymbolFacts.IsDisposeAsync(invocation.TargetMethod) && IsDiscarded(invocation))
                    operationContext.ReportDiagnostic(Diagnostic.Create(UnobservedDisposal, invocation.Syntax.GetLocation(), invocation.TargetMethod.Name));
                if (SymbolFacts.IsTracked(invocation.Type) && !SymbolFacts.IsServiceResolution(invocation.TargetMethod) && !SymbolFacts.IsScopeFactory(invocation.TargetMethod))
                {
                    var summary = summaries.Get(invocation.TargetMethod, operationContext.CancellationToken);
                    if (!summary.ReturnsOwned && summary.BorrowedFrom is null)
                        operationContext.ReportDiagnostic(Diagnostic.Create(MissingContract, invocation.Syntax.GetLocation(), invocation.TargetMethod.ToDisplayString()));
                }
            }, OperationKind.Invocation);
            start.RegisterSyntaxNodeAction(AnalyzeAcquisition, SyntaxKind.VariableDeclarator);
            start.RegisterSymbolAction(AnalyzeOverride, SymbolKind.Method);
            start.RegisterOperationAction(operationContext => operationContext.ReportDiagnostic(Diagnostic.Create(UnsafeBoundary,
                operationContext.Operation.Syntax.GetLocation(), "dynamic dispatch")), OperationKind.DynamicInvocation, OperationKind.DynamicObjectCreation, OperationKind.DynamicIndexerAccess);
            start.RegisterSyntaxNodeAction(syntaxContext => syntaxContext.ReportDiagnostic(Diagnostic.Create(UnsafeBoundary,
                syntaxContext.Node.GetLocation(), "pointer or unsafe")), SyntaxKind.PointerIndirectionExpression, SyntaxKind.AddressOfExpression, SyntaxKind.StackAllocArrayCreationExpression);
        });
    }

    private static void AnalyzeOverride(SymbolAnalysisContext context)
    {
        var method = (IMethodSymbol)context.Symbol;
        foreach (var declaration in SymbolFacts.ContractSources(method).OfType<IMethodSymbol>())
        {
            if (SymbolEqualityComparer.Default.Equals(declaration, method)) continue;
            for (var index = 0; index < Math.Min(method.Parameters.Length, declaration.Parameters.Length); index++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                var parameter = method.Parameters[index];
                var actual = DirectMode(parameter);
                var expected = DirectMode(declaration.Parameters[index]);
                if (actual is null || actual == expected || expected is null) continue;
                context.ReportDiagnostic(Diagnostic.Create(OverrideContract, parameter.Locations.FirstOrDefault(),
                    declaration.Parameters[index].Locations.Where(location => location.IsInSource), properties: null,
                    messageArgs: new object[] { parameter.Name, actual, expected }));
            }
        }
    }

    private static string? DirectMode(IParameterSymbol parameter) =>
        SymbolFacts.DirectAttribute(parameter, "Consumes") is not null || SymbolFacts.DirectAttribute(parameter, "Owned") is not null ? "consuming ownership" :
        SymbolFacts.DirectAttribute(parameter, "BorrowedMut") is not null ? "exclusive borrowing" :
        SymbolFacts.DirectAttribute(parameter, "Borrowed") is not null ? "shared borrowing" : null;

    private static bool IsDiscarded(IInvocationOperation invocation)
    {
        IOperation current = invocation;
        while (current.Parent is IConversionOperation or IParenthesizedOperation) current = current.Parent;
        if (current.Parent is IExpressionStatementOperation) return true;
        if (current.Parent is ISimpleAssignmentOperation { Target: IDiscardOperation }) return true;
        // ConfigureAwait does not observe completion on its own.
        if (current.Parent is IInvocationOperation configure && configure.TargetMethod.Name == "ConfigureAwait")
            return configure.Parent is IExpressionStatementOperation;
        return false;
    }

    private static void AnalyzeAcquisition(SyntaxNodeAnalysisContext context)
    {
        var variable = (VariableDeclaratorSyntax)context.Node;
        if (variable.Initializer is null || variable.Parent?.Parent is not LocalDeclarationStatementSyntax declaration ||
            !declaration.UsingKeyword.IsKind(SyntaxKind.None) || declaration.Parent is not BlockSyntax block ||
            context.SemanticModel.GetDeclaredSymbol(variable, context.CancellationToken) is not ILocalSymbol local || !SymbolFacts.IsDisposable(local.Type)) return;
        var initializer = context.SemanticModel.GetOperation(variable.Initializer.Value, context.CancellationToken);
        if (initializer is null) return;
        initializer = SymbolFacts.Unwrap(initializer);
        if (initializer is not IObjectCreationOperation && !(initializer is IInvocationOperation factory &&
            (SymbolFacts.HasAttribute(factory.TargetMethod, "ReturnsOwned") || SymbolFacts.IsKnownOwnedFactory(factory.TargetMethod)))) return;

        var declarationIndex = block.Statements.IndexOf(declaration);
        SyntaxNode? firstHazard = null;
        for (var index = declarationIndex + 1; index < block.Statements.Count; index++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var statement = block.Statements[index];
            if (statement is TryStatementSyntax guarded && guarded.Finally is { } cleanup &&
                cleanup.Block.Statements.Any(s => DirectlyDisposes(s, local, context))) return;
            if (DirectlyDisposes(statement, local, context))
            {
                if (firstHazard is not null)
                    context.ReportDiagnostic(Diagnostic.Create(ExceptionSafety, variable.GetLocation(), new[] { firstHazard.GetLocation(), statement.GetLocation() },
                        properties: null, messageArgs: new object[] { local.Name }));
                return;
            }
            firstHazard ??= statement.DescendantNodesAndSelf(n => n is not AnonymousFunctionExpressionSyntax && n is not LocalFunctionStatementSyntax)
                .FirstOrDefault(n => n is InvocationExpressionSyntax or ObjectCreationExpressionSyntax or AwaitExpressionSyntax or ThrowStatementSyntax or ElementAccessExpressionSyntax);
        }
    }

    private static bool DirectlyDisposes(StatementSyntax statement, ILocalSymbol local, SyntaxNodeAnalysisContext context)
    {
        if (statement is not ExpressionStatementSyntax expression) return false;
        var operation = context.SemanticModel.GetOperation(expression.Expression, context.CancellationToken);
        if (operation is IAwaitOperation awaited) operation = awaited.Operation;
        if (operation is not IInvocationOperation invocation || !(SymbolFacts.IsDispose(invocation.TargetMethod) || SymbolFacts.IsDisposeAsync(invocation.TargetMethod)) || invocation.Instance is null) return false;
        return SymbolFacts.Unwrap(invocation.Instance) is ILocalReferenceOperation reference && SymbolEqualityComparer.Default.Equals(reference.Local, local);
    }
}
