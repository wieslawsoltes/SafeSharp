using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace SafeSharp.Analyzers.Analysis;

/// <summary>
/// Compilation-scoped boundary summaries. Explicit contracts take precedence; source inference is
/// intentionally limited to unambiguous direct return expressions and unconditional parameter disposal.
/// Rust likewise separates signature constraints from body checking: https://rustc-dev-guide.rust-lang.org/borrow-check/region-inference.html.
/// </summary>
internal sealed class MethodSummaryProvider
{
    private readonly Compilation _compilation;
    private readonly ConcurrentDictionary<IMethodSymbol, MethodSummary> _cache = new(SymbolEqualityComparer.Default);

    public MethodSummaryProvider(Compilation compilation) => _compilation = compilation;

    public MethodSummary Get(IMethodSymbol method, CancellationToken cancellationToken) =>
        _cache.GetOrAdd(method.OriginalDefinition, m => Compute(m, cancellationToken));

    private MethodSummary Compute(IMethodSymbol method, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var owned = SymbolFacts.HasAttribute(method, "ReturnsOwned") || SymbolFacts.HasReturnAttribute(method, "Owned") || SymbolFacts.IsKnownOwnedFactory(method);
        var borrowed = SymbolFacts.Attribute(method, "ReturnsBorrowed");
        var from = borrowed?.ConstructorArguments.FirstOrDefault().Value as string;
        if (from is not null && from != "this")
        {
            // Parameter names need not match between an interface/override and its implementation.
            var declaration = SymbolFacts.ContractSources(method).OfType<IMethodSymbol>().FirstOrDefault(source => SymbolFacts.DirectAttribute(source, "ReturnsBorrowed") is not null);
            var ordinal = declaration?.Parameters.FirstOrDefault(parameter => parameter.Name == from)?.Ordinal;
            if (ordinal is { } index && index < method.Parameters.Length) from = method.Parameters[index].Name;
        }
        var mutable = borrowed is not null && borrowed.ConstructorArguments.Length > 1 && borrowed.ConstructorArguments[1].Value is true;
        var disposed = ImmutableHashSet.CreateBuilder<int>();
        var source = method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken);
        if (source is not null)
        {
            var model = _compilation.GetSemanticModel(source.SyntaxTree);
            var expressions = source.DescendantNodes(n => n == source || n is not (LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax))
                .OfType<ReturnStatementSyntax>().Where(r => r.Expression is not null).Select(r => r.Expression!).ToList();
            var expressionBody = source switch
            {
                MethodDeclarationSyntax m => m.ExpressionBody?.Expression,
                LocalFunctionStatementSyntax l => l.ExpressionBody?.Expression,
                AccessorDeclarationSyntax a => a.ExpressionBody?.Expression,
                _ => null
            };
            if (expressionBody is not null) expressions.Add(expressionBody);
            if (expressions.Count > 0 && expressions.All(e => model.GetOperation(e, cancellationToken) is { } op && SymbolFacts.Unwrap(op) is IObjectCreationOperation creation && SymbolFacts.IsTracked(creation.Type)))
                owned = true;
            if (from is null && expressions.Count > 0)
            {
                var parameters = expressions.Select(e => model.GetOperation(e, cancellationToken)).Select(op => op is null ? null : SymbolFacts.Unwrap(op) as IParameterReferenceOperation).ToArray();
                if (parameters.All(p => p is not null) && parameters.Select(p => p!.Parameter.Ordinal).Distinct().Count() == 1)
                    from = parameters[0]!.Parameter.Name;
            }
            // Only straight-line direct disposal is inferred. A call hidden in a conditional, nested
            // closure, virtual override or finally with multiple exits requires an explicit contract.
            var body = source switch
            {
                MethodDeclarationSyntax m => m.Body,
                LocalFunctionStatementSyntax l => l.Body,
                _ => null
            };
            if (!method.IsVirtual && !method.IsAbstract && !method.IsOverride && body is not null)
            {
                foreach (var statement in body.Statements)
                {
                    if (statement is not ExpressionStatementSyntax expression || model.GetOperation(expression.Expression, cancellationToken) is not IInvocationOperation invocation)
                        break;
                    if (SymbolFacts.IsDispose(invocation.TargetMethod) && invocation.Instance is { } instance && SymbolFacts.Unwrap(instance) is IParameterReferenceOperation parameter)
                        disposed.Add(parameter.Parameter.Ordinal);
                    else if (statement.DescendantNodes().Any(n => n is AwaitExpressionSyntax)) break;
                }
            }
        }
        return new MethodSummary(owned, from, mutable, disposed.ToImmutable());
    }
}

internal sealed class MethodSummary
{
    public MethodSummary(bool returnsOwned, string? borrowedFrom, bool mutableBorrow, ImmutableHashSet<int> disposedParameters)
    { ReturnsOwned = returnsOwned; BorrowedFrom = borrowedFrom; MutableBorrow = mutableBorrow; DisposedParameters = disposedParameters; }
    public bool ReturnsOwned { get; }
    public string? BorrowedFrom { get; }
    public bool MutableBorrow { get; }
    public ImmutableHashSet<int> DisposedParameters { get; }
}
