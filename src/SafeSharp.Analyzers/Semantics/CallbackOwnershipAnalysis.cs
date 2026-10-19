using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace SafeSharp.Analyzers.Semantics;

internal static class CallbackOwnershipAnalysis
{
    public static void AnalyzeExpressionStatement(OperationAnalysisContext context, ContractSymbols symbols)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        var operation = ContractSymbols.Unwrap(((IExpressionStatementOperation)context.Operation).Operation);
        if (operation.Type?.SpecialType == SpecialType.System_Void) return;
        if (operation is IAwaitOperation awaited)
        {
            // Awaiting a non-generic Task consumes its completion. A value produced by await can still be ignored.
            if (awaited.Type is null || awaited.Type.SpecialType == SpecialType.System_Void) return;
            operation = ContractSymbols.Unwrap(awaited.Operation);
        }
        ISymbol? source = operation switch
        {
            IInvocationOperation invocation when symbols.Has(invocation.TargetMethod, "MustUse") || symbols.HasOnType(invocation.Type, "MustUse") => invocation.TargetMethod,
            IObjectCreationOperation creation when symbols.HasOnType(creation.Type, "MustUse") => creation.Type,
            _ => null,
        };
        if (source is not null)
            context.ReportDiagnostic(Diagnostic.Create(SemanticDiagnostics.IgnoredMustUse, operation.Syntax.GetLocation(), source.Name));
    }

    public static void AnalyzeAssignment(OperationAnalysisContext context, ContractSymbols symbols)
    {
        var assignment = (ISimpleAssignmentOperation)context.Operation;
        var storage = ContractSymbols.Unwrap(assignment.Target) switch
        {
            IFieldReferenceOperation field => (ISymbol)field.Field,
            IPropertyReferenceOperation property => property.Property,
            _ => null,
        };
        if (storage is null) return;
        var reported = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var reference in BorrowedOrigins(assignment.Value, symbols))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var parameter = reference.Parameter;
            if (!reported.Add(parameter)) continue;
            if (!symbols.Has(parameter, "Borrowed") && !symbols.Has(parameter, "BorrowedMut") && !symbols.Has(parameter, "DoesNotEscape")) continue;
            context.ReportDiagnostic(Diagnostic.Create(SemanticDiagnostics.StoredBorrowedParameter, reference.Syntax.GetLocation(),
                additionalLocations: storage.Locations.Where(l => l.IsInSource), properties: null,
                messageArgs: new object[] { parameter.Name, storage.Name }));
        }
    }

    public static void AnalyzeInvocation(OperationAnalysisContext context, ContractSymbols symbols, ServiceLifetimeAnalysis services)
    {
        var invocation = (IInvocationOperation)context.Operation;
        context.CancellationToken.ThrowIfCancellationRequested();
        services.AddRegistration(invocation, context.CancellationToken);
        if (!symbols.IsTaskRun(invocation.TargetMethod)) return;
        foreach (var argument in invocation.Arguments)
        {
            var value = ContractSymbols.Unwrap(argument.Value);
            if (value is IDelegateCreationOperation creation) value = ContractSymbols.Unwrap(creation.Target);
            if (value is IAnonymousFunctionOperation lambda)
            {
                foreach (var capture in Captures(lambda, context.CancellationToken))
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    if (symbols.HasOnType(capture.Type, "ThreadAffine"))
                        context.ReportDiagnostic(Diagnostic.Create(SemanticDiagnostics.ThreadAffineTaskCapture, capture.Location,
                            additionalLocations: new[] { invocation.Syntax.GetLocation() }, properties: null, messageArgs: new object[] { capture.Name }));
                    if (FindUsingScope(capture.Symbol, invocation.Syntax, symbols, context.CancellationToken) is { } scope
                        && !IsTaskJoined(invocation, scope, symbols, context.CancellationToken))
                        context.ReportDiagnostic(Diagnostic.Create(SemanticDiagnostics.EscapingUsingCapture, capture.Location,
                            additionalLocations: capture.Symbol.Locations.Where(l => l.IsInSource), properties: null, messageArgs: new object[] { capture.Name }));
                }
            }
            else if (value is IMethodReferenceOperation methodReference && methodReference.Instance is { } instance)
            {
                instance = ContractSymbols.Unwrap(instance);
                if (symbols.HasOnType(instance.Type, "ThreadAffine"))
                    context.ReportDiagnostic(Diagnostic.Create(SemanticDiagnostics.ThreadAffineTaskCapture, instance.Syntax.GetLocation(), instance.Syntax.ToString()));
                var receiver = ReferencedSymbol(instance);
                if (receiver is not null && FindUsingScope(receiver, invocation.Syntax, symbols, context.CancellationToken) is { } scope
                    && !IsTaskJoined(invocation, scope, symbols, context.CancellationToken))
                    context.ReportDiagnostic(Diagnostic.Create(SemanticDiagnostics.EscapingUsingCapture, instance.Syntax.GetLocation(), receiver.Name));
            }
        }
    }

    public static void AnalyzeAnonymousFunction(OperationAnalysisContext context, ContractSymbols symbols)
    {
        var lambda = (IAnonymousFunctionOperation)context.Operation;
        IOperation current = lambda;
        while (current.Parent is IDelegateCreationOperation || current.Parent is IConversionOperation || current.Parent is IParenthesizedOperation)
            current = current.Parent;
        var escaping = current.Parent is IReturnOperation
            || current.Parent is IEventAssignmentOperation
            || (current.Parent is ISimpleAssignmentOperation assignment
                && (ContractSymbols.Unwrap(assignment.Target) is IFieldReferenceOperation || ContractSymbols.Unwrap(assignment.Target) is IPropertyReferenceOperation));
        if (!escaping) return;
        foreach (var capture in Captures(lambda, context.CancellationToken))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (FindUsingScope(capture.Symbol, lambda.Syntax, symbols, context.CancellationToken) is not null)
                context.ReportDiagnostic(Diagnostic.Create(SemanticDiagnostics.EscapingUsingCapture, capture.Location,
                    additionalLocations: capture.Symbol.Locations.Where(l => l.IsInSource), properties: null, messageArgs: new object[] { capture.Name }));
        }
    }

    private static IEnumerable<IParameterReferenceOperation> BorrowedOrigins(IOperation value, ContractSymbols symbols)
    {
        value = ContractSymbols.Unwrap(value);
        if (value is IParameterReferenceOperation parameter)
        {
            yield return parameter;
            yield break;
        }
        IEnumerable<IOperation>? children = value switch
        {
            IConditionalOperation conditional => conditional.WhenFalse is null ? new[] { conditional.WhenTrue } : new[] { conditional.WhenTrue, conditional.WhenFalse },
            ICoalesceOperation coalesce => new[] { coalesce.Value, coalesce.WhenNull },
            ITupleOperation tuple => tuple.Elements,
            IArrayCreationOperation array when array.Initializer is not null => array.Initializer.ElementValues,
            _ => null,
        };
        if (value is IInvocationOperation invocation)
        {
            var attribute = symbols.Find(invocation.TargetMethod.GetAttributes(), "ReturnsBorrowed");
            var source = attribute?.ConstructorArguments.FirstOrDefault().Value as string;
            if (source == "this" && invocation.Instance is not null) children = new[] { invocation.Instance };
            else if (source is not null)
            {
                var sourceArgument = invocation.Arguments.FirstOrDefault(a => a.Parameter?.Name == source);
                if (sourceArgument is not null) children = new[] { sourceArgument.Value };
            }
        }
        if (children is null) yield break;
        foreach (var child in children)
            foreach (var origin in BorrowedOrigins(child, symbols)) yield return origin;
    }

    private static IEnumerable<CapturedValue> Captures(IAnonymousFunctionOperation lambda, System.Threading.CancellationToken cancellationToken)
    {
        var captures = new Dictionary<ISymbol, CapturedValue>(SymbolEqualityComparer.Default);
        foreach (var operation in ContractSymbols.Walk(lambda.Body, descendFunctions: true))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ISymbol? symbol = null;
            ITypeSymbol? type = null;
            string? name = null;
            switch (operation)
            {
                case ILocalReferenceOperation local when !DeclaredWithin(local.Local, lambda.Syntax):
                    symbol = local.Local; type = local.Type; name = local.Local.Name;
                    break;
                case IParameterReferenceOperation parameter when !DeclaredWithin(parameter.Parameter, lambda.Syntax):
                    symbol = parameter.Parameter; type = parameter.Type; name = parameter.Parameter.Name;
                    break;
                case IFieldReferenceOperation field when field.Instance is null || UsesOuterValue(field.Instance, lambda.Syntax):
                    symbol = field.Field; type = field.Type; name = field.Field.Name;
                    break;
                case IInstanceReferenceOperation instance when instance.ReferenceKind == InstanceReferenceKind.ContainingTypeInstance:
                    symbol = instance.Type; type = instance.Type; name = "this";
                    break;
            }
            if (symbol is not null && type is not null && name is not null)
            {
                var capture = new CapturedValue(symbol, type, name, operation.Syntax.GetLocation());
                if (!captures.TryGetValue(symbol, out var previous) || capture.Location.SourceSpan.Start < previous.Location.SourceSpan.Start)
                    captures[symbol] = capture;
            }
        }
        return captures.Values.OrderBy(c => c.Location.SourceSpan.Start).ThenBy(c => c.Name, System.StringComparer.Ordinal);
    }

    private static bool UsesOuterValue(IOperation instance, SyntaxNode lambda)
    {
        foreach (var operation in ContractSymbols.Walk(instance))
        {
            if (operation is IInstanceReferenceOperation reference && reference.ReferenceKind == InstanceReferenceKind.ContainingTypeInstance) return true;
            if (operation is ILocalReferenceOperation local && !DeclaredWithin(local.Local, lambda)) return true;
            if (operation is IParameterReferenceOperation parameter && !DeclaredWithin(parameter.Parameter, lambda)) return true;
        }
        return false;
    }

    private static bool DeclaredWithin(ISymbol symbol, SyntaxNode scope) => symbol.DeclaringSyntaxReferences.Any(r => r.SyntaxTree == scope.SyntaxTree && scope.Span.Contains(r.Span));

    private static SyntaxNode? FindUsingScope(ISymbol symbol, SyntaxNode capture, ContractSymbols symbols, System.Threading.CancellationToken cancellationToken)
    {
        if (symbol is ILocalSymbol)
        {
            foreach (var reference in symbol.DeclaringSyntaxReferences)
            {
                var declaration = reference.GetSyntax(cancellationToken);
                if (declaration.Parent?.Parent is LocalDeclarationStatementSyntax local && local.UsingKeyword.RawKind != 0)
                {
                    var scope = local.Ancestors().OfType<BlockSyntax>().FirstOrDefault() as SyntaxNode ?? local.SyntaxTree.GetRoot(cancellationToken);
                    if (scope.SyntaxTree == capture.SyntaxTree && scope.Span.Contains(capture.Span)) return scope;
                }
                if (declaration.Parent?.Parent is UsingStatementSyntax statement && statement.Span.Contains(capture.Span)) return statement;
            }
        }
        foreach (var statement in capture.Ancestors().OfType<UsingStatementSyntax>())
        {
            if (statement.Expression is null) continue;
            var operation = symbols.Compilation.GetSemanticModel(statement.SyntaxTree).GetOperation(statement.Expression, cancellationToken);
            if (operation is not null && ContractSymbols.Equal(ReferencedSymbol(ContractSymbols.Unwrap(operation)), symbol)) return statement;
        }
        return null;
    }

    private static bool IsTaskJoined(IInvocationOperation invocation, SyntaxNode usingScope, ContractSymbols symbols, System.Threading.CancellationToken cancellationToken)
    {
        IOperation current = invocation;
        while (current.Parent is { } parent)
        {
            if (parent is IAwaitOperation) return true;
            if (parent is IConversionOperation || parent is IParenthesizedOperation)
            {
                current = parent; continue;
            }
            if (parent is IInvocationOperation wrapper && ReferenceEquals(wrapper.Instance, current))
            {
                var ns = wrapper.TargetMethod.ContainingNamespace.ToDisplayString();
                if ((ns == "System.Threading.Tasks" || ns == "System.Runtime.CompilerServices") && wrapper.Arguments.Length == 0
                    && (wrapper.TargetMethod.Name == "Wait" || wrapper.TargetMethod.Name == "GetResult")) return true;
                if ((ns == "System.Threading.Tasks" || ns == "System.Runtime.CompilerServices") && (wrapper.TargetMethod.Name == "ConfigureAwait" || wrapper.TargetMethod.Name == "GetAwaiter"))
                {
                    current = parent; continue;
                }
            }
            break;
        }
        ILocalSymbol? taskLocal = current.Parent switch
        {
            IVariableInitializerOperation initializer when initializer.Parent is IVariableDeclaratorOperation declarator => declarator.Symbol,
            ISimpleAssignmentOperation assignment when assignment.Target is ILocalReferenceOperation local => local.Local,
            _ => null,
        };
        if (taskLocal is null) return false;
        var containingStatement = invocation.Syntax.AncestorsAndSelf().OfType<StatementSyntax>().FirstOrDefault();
        if (containingStatement?.Parent is not BlockSyntax block) return false;
        var index = block.Statements.IndexOf(containingStatement);
        for (var i = index + 1; i < block.Statements.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var next = block.Statements[i];
            if (!usingScope.Span.Contains(next.Span)) return false;
            if (next is EmptyStatementSyntax) continue;
            if (next is not ExpressionStatementSyntax expression) return false;
            var nextOperation = symbols.Compilation.GetSemanticModel(next.SyntaxTree).GetOperation(expression.Expression, cancellationToken);
            if (nextOperation is not IAwaitOperation awaited) return false;
            var awaitedValue = ContractSymbols.Unwrap(awaited.Operation);
            if (awaitedValue is IInvocationOperation configured && configured.TargetMethod.Name == "ConfigureAwait"
                && configured.TargetMethod.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks" && configured.Instance is not null)
                awaitedValue = ContractSymbols.Unwrap(configured.Instance);
            return awaitedValue is ILocalReferenceOperation reference && ContractSymbols.Equal(reference.Local, taskLocal);
        }
        return false;
    }

    private static ISymbol? ReferencedSymbol(IOperation operation) => operation switch
    {
        ILocalReferenceOperation local => local.Local,
        IParameterReferenceOperation parameter => parameter.Parameter,
        IFieldReferenceOperation field => field.Field,
        IPropertyReferenceOperation property => property.Property,
        _ => null,
    };

    private sealed class CapturedValue
    {
        public CapturedValue(ISymbol symbol, ITypeSymbol type, string name, Location location) { Symbol = symbol; Type = type; Name = name; Location = location; }
        public ISymbol Symbol { get; }
        public ITypeSymbol Type { get; }
        public string Name { get; }
        public Location Location { get; }
    }
}
