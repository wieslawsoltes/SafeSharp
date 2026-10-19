using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using SafeSharp.Analyzers.Semantics;
using SafeSharp.Core.Regions;

namespace SafeSharp.Analyzers.Analysis;

/// <summary>
/// Compilation-scoped boundary summaries. Explicit contracts take precedence; source inference is
/// a finite may-origin dataflow analysis proves returns/out slots and limited parameter disposal effects.
/// Rust likewise separates signature constraints from body checking: https://rustc-dev-guide.rust-lang.org/borrow-check/region-inference.html.
/// </summary>
internal sealed class MethodSummaryProvider
{
    private readonly Compilation _compilation;
    private readonly LifetimeContractProvider _lifetimes;
    private readonly ConcurrentDictionary<IMethodSymbol, MethodSummary> _cache = new(SymbolEqualityComparer.Default);

    public MethodSummaryProvider(Compilation compilation)
    { _compilation = compilation; _lifetimes = new LifetimeContractProvider(compilation); Callbacks = new CallbackOwnershipModel(compilation); }

    internal CallbackOwnershipModel Callbacks { get; }

    public MethodSummary Get(IMethodSymbol method, CancellationToken cancellationToken) =>
        _cache.GetOrAdd(method.OriginalDefinition, m => Compute(m, cancellationToken, new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default)));

    private MethodSummary Compute(IMethodSymbol method, CancellationToken cancellationToken, HashSet<IMethodSymbol> path)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Recursion has an opaque summary, never a guessed ownership postcondition. Nested
        // inference does not populate the cache with a context-dependent incomplete result.
        if (path.Count >= 24 || !path.Add(method.OriginalDefinition)) return MethodSummary.Unknown;
        try
        {
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
            var sources = ImmutableArray.CreateBuilder<string>();
            if (from is not null) sources.Add(from);
            var lifetime = _lifetimes.Get(method, cancellationToken);
            if (from is null && !owned && lifetime.ReturnRegion is { } returnedRegion && lifetime.DeclarationIssues.IsEmpty)
            {
                var regions = lifetime.Solve(new RegionSolverOptions(), cancellationToken);
                if (regions.IsComplete)
                {
                    for (var i = 0; i < lifetime.ParameterRegions.Length; i++)
                        if (lifetime.ParameterRegions[i] is { } inputRegion && regions.Outlives(inputRegion, returnedRegion))
                            sources.Add(method.Parameters[i].Name);
                    if (!method.IsStatic && regions.Outlives("this", returnedRegion)) sources.Add("this");
                    mutable = lifetime.MutableBorrow;
                }
            }
            var disposed = ImmutableArray.CreateBuilder<int>();
            var outputs = ImmutableDictionary.CreateBuilder<int, bool>();
            foreach (var parameter in method.Parameters.Where(parameter => parameter.RefKind == RefKind.Out && SymbolFacts.HasAttribute(parameter, "Owned")))
                outputs[parameter.Ordinal] = parameter.Type.NullableAnnotation == NullableAnnotation.Annotated;
            var source = method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken);
            if (source is not null && (!method.IsVirtual && !method.IsAbstract && !method.IsOverride || method.IsSealed || method.ContainingType.IsSealed))
            {
                var model = _compilation.GetSemanticModel(source.SyntaxTree);
                var operationNode = source switch
                {
                    PropertyDeclarationSyntax { ExpressionBody: { } propertyBody } => (SyntaxNode)propertyBody.Expression,
                    IndexerDeclarationSyntax { ExpressionBody: { } indexerBody } => indexerBody.Expression,
                    ArrowExpressionClauseSyntax arrow => arrow.Expression,
                    _ => source
                };
                MethodSummary Callee(IMethodSymbol callee) => _cache.TryGetValue(callee.OriginalDefinition, out var cached) ? cached :
                    Compute(callee.OriginalDefinition, cancellationToken, path);
                var origins = MethodOriginAnalysis.Analyze(method, model.GetOperation(operationNode, cancellationToken), Callee, cancellationToken);
                static bool IsFresh(ImmutableHashSet<string> values) => values.Contains(MethodOriginAnalysis.Fresh) &&
                    values.All(value => value is MethodOriginAnalysis.Fresh or MethodOriginAnalysis.Null);
                if (sources.Count == 0 && lifetime.ReturnRegion is null && IsFresh(origins.Returns))
                    owned = true;
                if (sources.Count == 0 && !owned && lifetime.ReturnRegion is null)
                {
                    var originsWithoutNull = origins.Returns.Remove(MethodOriginAnalysis.Null);
                    var candidates = originsWithoutNull.Select(origin => origin == MethodOriginAnalysis.Receiver ? "this" :
                        method.Parameters.FirstOrDefault(parameter => MethodOriginAnalysis.Parameter(parameter.Ordinal) == origin)?.Name).ToArray();
                    if (candidates.Length > 0 && candidates.All(candidate => candidate is not null))
                        sources.AddRange(candidates.Select(candidate => candidate!).OrderBy(candidate => candidate, StringComparer.Ordinal));
                }
                foreach (var output in origins.Outputs.Where(output => IsFresh(output.Value)))
                    outputs[output.Key] = output.Value.Contains(MethodOriginAnalysis.Null);
                // Only straight-line direct disposal is inferred. A call hidden in a conditional, nested
                // closure, virtual override or finally with multiple exits requires an explicit contract.
                var body = source switch
                {
                    MethodDeclarationSyntax m => m.Body,
                    LocalFunctionStatementSyntax l => l.Body,
                    _ => null
                };
                if (!method.IsVirtual && !method.IsAbstract && !method.IsOverride && body is not null &&
                    !body.DescendantNodes().Any(node => node is YieldStatementSyntax))
                {
                    foreach (var statement in body.Statements)
                    {
                        if (statement is not ExpressionStatementSyntax expression || model.GetOperation(expression.Expression, cancellationToken) is not IInvocationOperation invocation)
                            break;
                        if (SymbolFacts.IsDispose(invocation.TargetMethod) && invocation.Instance is { } instance && DirectParameter(instance) is { } parameter &&
                            SymbolEqualityComparer.Default.Equals(parameter.Parameter.ContainingSymbol.OriginalDefinition, method.OriginalDefinition))
                            disposed.Add(parameter.Parameter.Ordinal);
                        else break;
                    }
                }
            }
            return new MethodSummary(owned, sources.ToImmutable(), mutable, disposed.ToImmutable(), outputs.ToImmutable());
        }
        finally { path.Remove(method.OriginalDefinition); }
    }

    private static IParameterReferenceOperation? DirectParameter(IOperation operation)
    {
        while (true)
        {
            if (operation is IParenthesizedOperation parenthesized) operation = parenthesized.Operand;
            else if (operation is IConversionOperation { OperatorMethod: null, IsTryCast: false } conversion &&
                     (conversion.Conversion.IsIdentity || conversion.IsImplicit && conversion.Conversion.IsReference)) operation = conversion.Operand;
            else return operation as IParameterReferenceOperation;
        }
    }
}

internal sealed class MethodSummary
{
    internal static readonly MethodSummary Unknown = new(false, ImmutableArray<string>.Empty, false,
        ImmutableArray<int>.Empty, ImmutableDictionary<int, bool>.Empty);

    public MethodSummary(bool returnsOwned, ImmutableArray<string> borrowedSources, bool mutableBorrow,
        ImmutableArray<int> parameterDisposalOrder, ImmutableDictionary<int, bool> ownedOutputParameters)
    {
        ReturnsOwned = returnsOwned;
        BorrowedSources = borrowedSources;
        MutableBorrow = mutableBorrow;
        ParameterDisposalOrder = parameterDisposalOrder;
        DisposedParameters = parameterDisposalOrder.ToImmutableHashSet();
        OwnedOutputParameters = ownedOutputParameters;
    }

    public bool ReturnsOwned { get; }
    public ImmutableArray<string> BorrowedSources { get; }
    public string? BorrowedFrom => BorrowedSources.Length == 1 ? BorrowedSources[0] : null;
    public bool HasBorrowedReturn => !BorrowedSources.IsEmpty;
    public bool MutableBorrow { get; }
    public ImmutableHashSet<int> DisposedParameters { get; }
    public ImmutableArray<int> ParameterDisposalOrder { get; }
    public ImmutableDictionary<int, bool> OwnedOutputParameters { get; }
}
