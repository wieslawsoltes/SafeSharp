using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using SafeSharp.Analyzers.Analysis;

namespace SafeSharp.Analyzers.Semantics;

internal sealed partial class SynchronizationContracts
{
    private readonly ConcurrentDictionary<IFieldSymbol, bool> semaphoreMutexFields = new(SymbolEqualityComparer.Default);

    internal bool IsSemaphoreMutexCreation(IOperation? operation)
    {
        if (operation is null) return false;
        operation = SymbolFacts.Unwrap(operation);
        return operation is IObjectCreationOperation { Constructor: { } constructor } creation && IsSemaphore(constructor) &&
            constructor.Parameters.Length == 2 && creation.Arguments.Any(argument => argument.Parameter?.Ordinal == 1 &&
                argument.Value.ConstantValue is { HasValue: true, Value: 1 });
    }

    internal bool IsSemaphoreMutexField(IFieldSymbol field, CancellationToken cancellationToken)
    {
        if (!field.IsReadOnly || !Same(field.Type, SemaphoreSlim)) return false;
        return semaphoreMutexFields.GetOrAdd(field.OriginalDefinition, candidate => ProveField(candidate, cancellationToken));
    }

    private bool ProveField(IFieldSymbol field, CancellationToken cancellationToken)
    {
        var initialized = false;
        var steps = 0;
        foreach (var reference in field.DeclaringSyntaxReferences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reference.GetSyntax(cancellationToken) is not VariableDeclaratorSyntax { Initializer.Value: { } initializer }) return false;
            if (!IsSemaphoreMutexCreation(Compilation.GetSemanticModel(initializer.SyntaxTree).GetOperation(initializer, cancellationToken))) return false;
            initialized = true;
        }
        if (!initialized) return false; // Metadata and constructor-only initialization need an explicit caller precondition.
        // Readonly does not imply that the initializer wins: a constructor may replace the field.
        // Every visible assignment must preserve maxCount=1, and a writable ref escape defeats it.
        foreach (var declaration in field.ContainingType.DeclaringSyntaxReferences)
        {
            if (declaration.GetSyntax(cancellationToken) is not TypeDeclarationSyntax type) return false;
            var model = Compilation.GetSemanticModel(type.SyntaxTree);
            foreach (var constructor in type.Members.OfType<ConstructorDeclarationSyntax>())
            foreach (var node in constructor.DescendantNodes())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++steps > 20000) throw new AnalysisBudgetException("Semaphore field-construction proof exceeded 20000 syntax steps.");
                if (node is AssignmentExpressionSyntax { Left: ThisExpressionSyntax }) return false;
                // A readonly field may still be exposed as writable ref storage inside its
                // constructor. Subsequent writes through the alias need not mention the field.
                if (node is RefExpressionSyntax reference &&
                    reference.Expression.DescendantNodesAndSelf().Any(target => target is IdentifierNameSyntax && Same(model.GetSymbolInfo(target, cancellationToken).Symbol, field))) return false;
                if (node is AssignmentExpressionSyntax assignment &&
                    assignment.Left.DescendantNodesAndSelf().Any(target => target is IdentifierNameSyntax && Same(model.GetSymbolInfo(target, cancellationToken).Symbol, field)))
                {
                    if (!Same(model.GetSymbolInfo(assignment.Left, cancellationToken).Symbol, field) ||
                        !IsSemaphoreMutexCreation(model.GetOperation(assignment.Right, cancellationToken))) return false;
                }
                if (node is ArgumentSyntax argument && argument.RefKindKeyword.ValueText is "ref" or "out" &&
                    argument.Expression.DescendantNodesAndSelf().Any(target => target is IdentifierNameSyntax && Same(model.GetSymbolInfo(target, cancellationToken).Symbol, field))) return false;
            }
        }
        return true;
    }

    internal bool IsSemaphoreAwaitableMember(IMethodSymbol method)
    {
        var type = method.ContainingType.OriginalDefinition;
        if (type.Locations.Any(location => location.IsInSource) || Same(type.ContainingAssembly, Compilation.Assembly)) return false;
        if (type.ContainingAssembly.Identity.Name is not ("mscorlib" or "System.Private.CoreLib" or "System.Runtime" or
            "netstandard" or "System.Threading.Tasks" or "System.Threading.Tasks.Extensions")) return false;
        return type.ToDisplayString() is "System.Threading.Tasks.Task" or "System.Threading.Tasks.Task<TResult>" or
            "System.Runtime.CompilerServices.TaskAwaiter" or "System.Runtime.CompilerServices.TaskAwaiter<TResult>" or
            "System.Runtime.CompilerServices.ConfiguredTaskAwaitable" or "System.Runtime.CompilerServices.ConfiguredTaskAwaitable<TResult>" or
            "System.Runtime.CompilerServices.ConfiguredTaskAwaitable.ConfiguredTaskAwaiter" or
            "System.Runtime.CompilerServices.ConfiguredTaskAwaitable<TResult>.ConfiguredTaskAwaiter";
    }
}
