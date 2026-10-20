using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using SafeSharp.Analyzers.Semantics;

namespace SafeSharp.Analyzers.Analysis;

/// <summary>
/// Connects explicit thread-transfer boundaries to ownership inputs and effects. Capture discovery
/// uses original semantic operations because lowered CFG callbacks are IFlowAnonymousFunctionOperation.
/// Each source callback is resolved independently; alternative closures never inherit one another's captures.
/// </summary>
internal sealed class CallbackOwnershipModel
{
    private const int MaximumCaptureSteps = 20000;
    private const int MaximumScopeOperations = 20000;
    private const int MaximumBoundaryCalls = 256;
    private const int MaximumCallbackTargets = 128;
    private readonly Compilation compilation;
    private readonly ConcurrencyContracts contracts;
    private readonly ConcurrentDictionary<(SyntaxTree Tree, int Start, int Length), CallbackInvocation> invocations = new();
    private readonly ConcurrentDictionary<(SyntaxTree Tree, int Start, int Length), ImmutableDictionary<IMethodSymbol, ImmutableArray<ConcurrencyCapture>>> scopes = new();

    internal CallbackOwnershipModel(Compilation compilation)
    { this.compilation = compilation; contracts = new ConcurrencyContracts(compilation); }

    internal ImmutableArray<CallbackTransfer> Transfers(IInvocationOperation lowered, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!lowered.Arguments.Any(argument => IsTransfer(argument.Parameter))) return ImmutableArray<CallbackTransfer>.Empty;
        var original = compilation.GetSemanticModel(lowered.Syntax.SyntaxTree).GetOperation(lowered.Syntax, cancellationToken) as IInvocationOperation;
        return original is null ? ImmutableArray<CallbackTransfer>.Empty : Invocation(original, cancellationToken).Transfers;
    }

    internal ImmutableArray<ConcurrencyCapture> Inputs(IMethodSymbol method, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (method.MethodKind is not (MethodKind.AnonymousFunction or MethodKind.LocalFunction)) return ImmutableArray<ConcurrencyCapture>.Empty;
        var syntax = method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken);
        if (syntax is null || compilation.GetSemanticModel(syntax.SyntaxTree).GetOperation(syntax, cancellationToken) is not { } root)
            return ImmutableArray<ConcurrencyCapture>.Empty;
        while (root.Parent is { } parent) root = parent;
        var scope = scopes.GetOrAdd(Key(root.Syntax), _ => BuildScope(root, cancellationToken));
        return scope.TryGetValue(method.OriginalDefinition, out var captures) ? captures : ImmutableArray<ConcurrencyCapture>.Empty;
    }

    internal ImmutableArray<ImmutableArray<ConcurrencyCapture>> AliasGroups(ImmutableArray<ConcurrencyCapture> captures,
        CancellationToken cancellationToken)
    {
        // Only immutable captured reference cells and direct reference-preserving initializers
        // establish this relationship. Reassigned cells, conversions that execute user code,
        // nullable `as` conversions, and uncaptured intermediate storage remain independent.
        var values = captures.ToDictionary(capture => capture.Symbol, SymbolEqualityComparer.Default);
        var parents = new Dictionary<ISymbol, ISymbol>(SymbolEqualityComparer.Default);
        foreach (var capture in captures) parents.Add(capture.Symbol, capture.Symbol);
        ISymbol Root(ISymbol symbol)
        {
            while (!SymbolEqualityComparer.Default.Equals(parents[symbol], symbol)) symbol = parents[symbol];
            return symbol;
        }
        foreach (var capture in captures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (capture.MutableCell || !capture.Type.IsReferenceType || capture.Symbol is not ILocalSymbol local) continue;
            var syntax = local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken);
            if (syntax is null || compilation.GetSemanticModel(syntax.SyntaxTree).GetOperation(syntax, cancellationToken) is not
                IVariableDeclaratorOperation { Initializer.Value: { } initializer }) continue;
            var source = ReferenceSource(initializer);
            if (source is null || !values.TryGetValue(source, out var original) || original.MutableCell || !original.Type.IsReferenceType) continue;
            var targetRoot = Root(capture.Symbol);
            var sourceRoot = Root(source);
            if (!SymbolEqualityComparer.Default.Equals(targetRoot, sourceRoot)) parents[targetRoot] = sourceRoot;
        }
        var groups = new Dictionary<ISymbol, List<ConcurrencyCapture>>(SymbolEqualityComparer.Default);
        foreach (var capture in captures)
        {
            var root = Root(capture.Symbol);
            if (!groups.TryGetValue(root, out var group)) groups.Add(root, group = new List<ConcurrencyCapture>());
            group.Add(capture);
        }
        return groups.Values.OrderBy(group => group[0].Location.SourceSpan.Start).Select(group => group.ToImmutableArray()).ToImmutableArray();
    }

    private static ISymbol? ReferenceSource(IOperation operation)
    {
        while (true)
        {
            if (operation is IParenthesizedOperation parenthesized) operation = parenthesized.Operand;
            else if (operation is IConversionOperation conversion && !conversion.IsTryCast && conversion.OperatorMethod is null &&
                     (conversion.Conversion.IsIdentity || conversion.Conversion.IsReference)) operation = conversion.Operand;
            else return operation switch
            {
                ILocalReferenceOperation local => local.Local,
                IParameterReferenceOperation parameter => parameter.Parameter,
                IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance } instance => instance.Type,
                _ => null
            };
        }
    }

    private ImmutableDictionary<IMethodSymbol, ImmutableArray<ConcurrencyCapture>> BuildScope(IOperation root, CancellationToken cancellationToken)
    {
        var inputs = new Dictionary<IMethodSymbol, Dictionary<ISymbol, ConcurrencyCapture>>(SymbolEqualityComparer.Default);
        var operations = 0;
        var boundaries = 0;
        foreach (var operation in ContractSymbols.Walk(root, descendFunctions: true))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++operations > MaximumScopeOperations)
                throw new AnalysisBudgetException("Callback ownership discovery exceeded 20,000 enclosing-scope operations; capture ownership has not been established.");
            if (operation is not IInvocationOperation invocation || !invocation.Arguments.Any(argument => IsTransfer(argument.Parameter))) continue;
            if (++boundaries > MaximumBoundaryCalls)
                throw new AnalysisBudgetException("Callback ownership discovery exceeded 256 transfer boundaries in one enclosing scope; capture ownership has not been established.");
            foreach (var target in Invocation(invocation, cancellationToken).Targets)
            {
                if (!inputs.TryGetValue(target.Method.OriginalDefinition, out var captures))
                    inputs.Add(target.Method.OriginalDefinition, captures = new Dictionary<ISymbol, ConcurrencyCapture>(SymbolEqualityComparer.Default));
                foreach (var capture in target.Captures.Where(IsTransferableStorage)) captures[capture.Symbol] = capture;
            }
        }
        var result = ImmutableDictionary.CreateBuilder<IMethodSymbol, ImmutableArray<ConcurrencyCapture>>(SymbolEqualityComparer.Default);
        foreach (var pair in inputs)
            result.Add(pair.Key, pair.Value.Values.OrderBy(capture => capture.Location.SourceSpan.Start).ThenBy(capture => capture.Name, StringComparer.Ordinal).ToImmutableArray());
        return result.ToImmutable();
    }

    private CallbackInvocation Invocation(IInvocationOperation invocation, CancellationToken cancellationToken) =>
        invocations.GetOrAdd(Key(invocation.Syntax), _ => Resolve(invocation, cancellationToken));

    private CallbackInvocation Resolve(IInvocationOperation invocation, CancellationToken cancellationToken)
    {
        var transfers = ImmutableArray.CreateBuilder<CallbackTransfer>();
        var targets = ImmutableArray.CreateBuilder<CallbackTarget>();
        foreach (var argument in invocation.Arguments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsTransfer(argument.Parameter)) continue;
            var analysis = new ConcurrencyTypeAnalysis(contracts, MaximumCaptureSteps, cancellationToken);
            var resolver = new ConcurrencyCaptureAnalysis(contracts, analysis, invocation, includeAnnotatedFields: false, ConcurrencyCapability.Send);
            resolver.Resolve(argument.Value);
            if (analysis.BudgetExceeded)
                throw new AnalysisBudgetException("Callback ownership capture resolution exceeded 20,000 steps; ownership transfer has not been established.");
            var argumentTargets = new List<CallbackTarget>();
            var callbackMethods = resolver.CallbackMethods.ToArray();
            if (callbackMethods.Length > MaximumCallbackTargets)
                throw new AnalysisBudgetException("Callback ownership resolution exceeded 128 visible targets; captured cleanup obligations have not been established.");
            foreach (var method in callbackMethods)
            {
                var syntax = method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken);
                if (syntax is null || compilation.GetSemanticModel(syntax.SyntaxTree).GetOperation(syntax, cancellationToken) is not { } operation) continue;
                var targetAnalysis = new ConcurrencyTypeAnalysis(contracts, MaximumCaptureSteps, cancellationToken);
                var targetResolver = new ConcurrencyCaptureAnalysis(contracts, targetAnalysis, invocation, includeAnnotatedFields: false, ConcurrencyCapability.Send);
                targetResolver.Resolve(operation);
                if (targetAnalysis.BudgetExceeded)
                    throw new AnalysisBudgetException("Callback ownership input resolution exceeded 20,000 steps; captured cleanup obligations have not been established.");
                if (targetResolver.Unresolved) continue;
                var target = new CallbackTarget(method, targetResolver.Captures.Where(IsTransferableStorage).ToImmutableArray());
                argumentTargets.Add(target);
                targets.Add(target);
            }
            foreach (var group in AliasGroups(resolver.Captures.Where(IsTransferableStorage).ToImmutableArray(), cancellationToken))
            {
                // Mixed/opaque targets cannot establish that a particular capture is always sent.
                // A visible target choice sends a capture definitely only when every target contains it.
                var definite = !resolver.Unresolved && (argumentTargets.Count == 0
                    ? IsDirectOrdinaryMethodGroup(argument.Value)
                    : !resolver.HasOpaqueMethodTargets && argumentTargets.Count == callbackMethods.Length &&
                      argumentTargets.All(target => target.Captures.Any(value => group.Any(capture => SymbolEqualityComparer.Default.Equals(value.Symbol, capture.Symbol)))));
                transfers.Add(new CallbackTransfer(group, argument.Value.Syntax.GetLocation(), !definite));
            }
        }
        return new CallbackInvocation(transfers.ToImmutable(), targets.ToImmutable());
    }

    private bool IsTransfer(IParameterSymbol? parameter) => parameter?.Type.TypeKind == TypeKind.Delegate &&
        contracts.HasBoundary(parameter, "TransfersToThread") && !contracts.HasBoundary(parameter, "SharesAcrossThreads");

    private static bool IsTransferableStorage(ConcurrencyCapture capture) => !capture.SharedStorage &&
        capture.Symbol is not IFieldSymbol { IsStatic: true } && capture.Symbol is not IPropertySymbol { IsStatic: true };

    private static bool IsDirectOrdinaryMethodGroup(IOperation operation)
    {
        operation = ContractSymbols.Unwrap(operation);
        if (operation is IDelegateCreationOperation creation) operation = ContractSymbols.Unwrap(creation.Target);
        return operation is IMethodReferenceOperation { Method.MethodKind: not MethodKind.LocalFunction };
    }

    private static (SyntaxTree, int, int) Key(SyntaxNode syntax) => (syntax.SyntaxTree, syntax.SpanStart, syntax.Span.Length);

    private sealed class CallbackInvocation
    {
        internal CallbackInvocation(ImmutableArray<CallbackTransfer> transfers, ImmutableArray<CallbackTarget> targets)
        { Transfers = transfers; Targets = targets; }
        internal ImmutableArray<CallbackTransfer> Transfers { get; }
        internal ImmutableArray<CallbackTarget> Targets { get; }
    }

    private sealed class CallbackTarget
    {
        internal CallbackTarget(IMethodSymbol method, ImmutableArray<ConcurrencyCapture> captures) { Method = method; Captures = captures; }
        internal IMethodSymbol Method { get; }
        internal ImmutableArray<ConcurrencyCapture> Captures { get; }
    }
}

internal sealed class CallbackTransfer
{
    internal CallbackTransfer(ImmutableArray<ConcurrencyCapture> captures, Location boundary, bool conditional)
    { Captures = captures; Boundary = boundary; Conditional = conditional; }
    internal ImmutableArray<ConcurrencyCapture> Captures { get; }
    internal Location Boundary { get; }
    internal bool Conditional { get; }
}
