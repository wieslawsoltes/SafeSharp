using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;
using SafeSharp.Analyzers.Analysis;
using SafeSharp.Core;
using SafeSharp.Core.Regions;

namespace SafeSharp.Analyzers.Semantics;

/// <summary>Checks a callback against all region assignments permitted by its annotated delegate signature.</summary>
/// <remarks>
/// Original C# adaptation of Rust's universal-region checks:
/// https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_borrowck/src/region_infer/mod.rs.
/// Target regions are arbitrary universally supplied regions; the callback's regions are fresh
/// inference variables. Input shortening and output covariance must not imply a new relation
/// between the target's independent regions. Writable C# ref/out slots require invariance.
/// Explicit TypeLifetime paths additionally introduce nested callable binders and Roslyn variance.
/// Higher-ranked trait resolution and arbitrary CLR type inference remain outside this contract domain.
/// </remarks>
internal static partial class LifetimeDelegateAnalysis
{
    internal static void Analyze(OperationAnalysisContext context, LifetimeContractProvider contracts, MethodSummaryProvider summaries)
    {
        if (context.Operation is not IDelegateCreationOperation { Type: INamedTypeSymbol { DelegateInvokeMethod: { } invoke } } creation) return;
        var cancellationToken = context.CancellationToken;
        var promised = contracts.Get(invoke, cancellationToken);
        if ((!promised.HasRegions && !promised.ReturnsOwned) || !promised.DeclarationIssues.IsEmpty) return;
        var options = LifetimeContractProvider.Options(context.Options.AnalyzerConfigOptionsProvider.GetOptions(creation.Syntax.SyntaxTree));
        var promisedSolution = promised.Solve(options, cancellationToken);
        if (!promisedSolution.IsComplete)
        {
            ReportBudget(context, creation, invoke, promisedSolution.BudgetReason ?? "delegate region-solving");
            return;
        }

        switch (creation.Target)
        {
            case IMethodReferenceOperation reference:
                Compare(context, creation, reference.Method.ReducedFrom ?? reference.Method, promised, contracts, summaries,
                    reference.Method.ReducedFrom is not null || reference.Method.IsExtensionMethod && reference.Instance is not null ? 1 : 0, options);
                break;
            case IAnonymousFunctionOperation lambda:
            {
                var actual = contracts.Get(lambda.Symbol, cancellationToken);
                if (!actual.DeclarationIssues.IsEmpty) return;
                if (actual.ReturnRegion is not null || actual.BorrowedSource is not null || actual.ReturnsOwned || lambda.Symbol.ReturnsVoid ||
                    HasTypeContracts(actual) || HasTypeContracts(promised))
                {
                    Compare(context, creation, lambda.Symbol, promised, contracts, summaries, 0, options);
                    return;
                }
                if (ParameterMismatch(lambda.Symbol, invoke, 0, cancellationToken) is { } mismatch)
                {
                    Report(context, creation.Target.Syntax.GetLocation(), lambda.Symbol, promised, mismatch);
                    return;
                }
                if (promised.ReturnsOwned)
                {
                    Report(context, creation.Target.Syntax.GetLocation(), lambda.Symbol, promised,
                        "an owned-result delegate requires an explicit owned-result contract on this anonymous function");
                    return;
                }
                AnalyzeLambdaBody(context, creation, lambda, promised, promisedSolution, contracts, summaries, options);
                break;
            }
            default:
                // A clone of an already typed delegate retains the contract which was checked at
                // its creation, or supplied by its external producer. No invocation list is guessed.
                if (creation.Target.Type is INamedTypeSymbol { DelegateInvokeMethod: { } sourceInvoke } &&
                    !SymbolEqualityComparer.Default.Equals(sourceInvoke.OriginalDefinition, invoke.OriginalDefinition))
                    Compare(context, creation, sourceInvoke, promised, contracts, summaries, 0, options);
                break;
        }
    }

    private static void Compare(OperationAnalysisContext context, IDelegateCreationOperation creation, IMethodSymbol method,
        LifetimeContractModel promised, LifetimeContractProvider contracts, MethodSummaryProvider summaries, int boundParameters,
        RegionSolverOptions options)
    {
        var cancellationToken = context.CancellationToken;
        var actual = contracts.Get(method, cancellationToken);
        if (!actual.DeclarationIssues.IsEmpty) return;
        if (method.Parameters.Length - boundParameters != promised.Method.Parameters.Length)
        {
            Report(context, creation.Target.Syntax.GetLocation(), method, promised, "the bound callback parameter layout is not supported");
            return;
        }
        if (ParameterMismatch(method, promised.Method, boundParameters, cancellationToken) is { } mismatch)
        {
            Report(context, creation.Target.Syntax.GetLocation(), method, promised, mismatch);
            return;
        }
        // Anonymous functions without an explicit result contract are checked against the target
        // body contract above. Do not ask an enclosing-method origin summary to stand for a lambda.
        var summary = method.MethodKind == MethodKind.AnonymousFunction ? MethodSummary.Unknown : summaries.Get(method, cancellationToken);
        var returnsOwned = actual.ReturnsOwned || summary.ReturnsOwned;
        if (promised.ReturnsOwned != returnsOwned && (promised.ReturnsOwned || promised.ReturnRegion is not null))
        {
            Report(context, creation.Target.Syntax.GetLocation(), method, promised,
                promised.ReturnsOwned ? "the callback does not establish ownership of its returned value" : "an owned value cannot replace a borrowed-result promise");
            return;
        }
        if (promised.ReturnRegion is not null && actual.MutableBorrow != promised.MutableBorrow &&
            (actual.ReturnRegion is not null || actual.BorrowedSource is not null || !summary.BorrowedSources.IsEmpty))
        {
            Report(context, creation.Target.Syntax.GetLocation(), method, promised, "the returned loan's mutability differs from the delegate contract");
            return;
        }

        Binding binding;
        UniversalRegionSolution? result = null;
        HigherRankedRegionSolution? ranked = null;
        try
        {
            binding = new Binding(promised, actual, boundParameters, creation.Target.Syntax.GetLocation(), options, cancellationToken, context.Compilation);
            var promisesAsyncPayload = summaries.AsyncResults.IsCarrier(promised.Method.ReturnType);
            if (promised.ReturnRegion is not null && !binding.AddReturn(actual, summary, promisesAsyncPayload))
            {
                Report(context, creation.Target.Syntax.GetLocation(), method, promised,
                    promisesAsyncPayload ? "the callback's async result payload has no proven input or receiver provenance" :
                        "the callback's returned borrow has no proven input or receiver provenance");
                return;
            }
            if (HasTypeContracts(actual) || HasTypeContracts(promised))
            {
                var targetMethod = ((INamedTypeSymbol)creation.Type!).DelegateInvokeMethod!;
                if (binding.AddTypes(actual, method, targetMethod, boundParameters) is { } typeMismatch)
                {
                    Report(context, creation.Target.Syntax.GetLocation(), method, promised, typeMismatch);
                    return;
                }
                ranked = new HigherRankedRegionSolver().Solve(binding.RankedProblem(), binding.RemainingOptions(), cancellationToken);
            }
            else result = new UniversalRegionSolver().Solve(binding.Problem(), options, cancellationToken);
        }
        catch (BindingBudgetException exhausted)
        {
            ReportBudget(context, creation, method, exhausted.Reason);
            return;
        }
        if (!(ranked?.IsComplete ?? result!.IsComplete))
        {
            ReportBudget(context, creation, method, ranked?.BudgetReason ?? result?.BudgetReason ?? "universal delegate obligations");
            return;
        }
        if (ranked?.IsCompatible ?? result!.IsCompatible) return;
        var related = promised.DeclarationLocations.Concat(actual.DeclarationLocations)
            .Concat(method.Locations).Where(location => location.IsInSource && location != creation.Target.Syntax.GetLocation()).Distinct();
        var failures = ranked?.Failures ?? result!.Failures;
        if (failures.IsEmpty && ranked is not null)
        {
            var typeFailure = ranked.TypeFailures[0];
            context.ReportDiagnostic(Diagnostic.Create(LifetimeContractAnalyzer.DelegateContract, creation.Target.Syntax.GetLocation(), related,
                binding.TypeProperties(typeFailure, ranked), method.Name, promised.Method.ContainingType.ToDisplayString(),
                "the borrowed contents of type '" + binding.DescribeType(typeFailure.Requirement.Type) + "' are not known to outlive " + binding.Describe(typeFailure.Region)));
            return;
        }
        var failure = failures[0];
        var properties = binding.Properties(failure, ranked?.Closure ?? result!.Closure!, ranked);
        context.ReportDiagnostic(Diagnostic.Create(LifetimeContractAnalyzer.DelegateContract, creation.Target.Syntax.GetLocation(), related, properties,
            method.Name, promised.Method.ContainingType.ToDisplayString(),
            "the callback requires " + binding.Describe(failure.Longer) + " to outlive " + binding.Describe(failure.Shorter) +
            (ranked is { Promotions.IsEmpty: false } ? "; a nested lifetime cannot escape its visible binder" : ", but the delegate permits these lifetimes to be independent")));
    }

    private static bool HasTypeContracts(LifetimeContractModel contract) => !contract.TypePositions.IsEmpty || contract.TypeBounds.Any(bound => !bound.IsImplied);

    private static string? ParameterMismatch(IMethodSymbol actual, IMethodSymbol promised, int boundParameters, CancellationToken cancellationToken)
    {
        for (var index = 0; index < promised.Parameters.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index + boundParameters >= actual.Parameters.Length) return "the callback parameter layout differs";
            var target = promised.Parameters[index];
            var source = actual.Parameters[index + boundParameters];
            var consumes = SymbolFacts.HasAttribute(source, "Consumes") || SymbolFacts.HasAttribute(source, "Owned");
            var targetConsumes = SymbolFacts.HasAttribute(target, "Consumes") || SymbolFacts.HasAttribute(target, "Owned");
            if (consumes != targetConsumes)
                return "parameter '" + source.Name + "' changes the delegate's ownership-transfer contract";
            if (SymbolFacts.HasAttribute(source, "BorrowedMut") && !SymbolFacts.HasAttribute(target, "BorrowedMut"))
                return "parameter '" + source.Name + "' requires an exclusive borrow which the delegate does not supply";
            if (SymbolFacts.HasAttribute(target, "Borrowed") && !SymbolFacts.HasAttribute(source, "Borrowed") && !SymbolFacts.HasAttribute(source, "BorrowedMut"))
                return "parameter '" + source.Name + "' needs an explicit Borrowed contract to preserve the delegate's input restriction";
            if (SymbolFacts.HasAttribute(target, "BorrowedMut") && !SymbolFacts.HasAttribute(source, "Borrowed") && !SymbolFacts.HasAttribute(source, "BorrowedMut"))
                return "parameter '" + source.Name + "' needs an explicit borrowing contract to preserve the delegate's input restriction";
            if (SymbolFacts.HasAttribute(target, "DoesNotEscape") && !SymbolFacts.HasAttribute(source, "DoesNotEscape"))
                return "parameter '" + source.Name + "' removes the delegate's DoesNotEscape guarantee";
        }
        return null;
    }

    private static void AnalyzeLambdaBody(OperationAnalysisContext context, IDelegateCreationOperation creation, IAnonymousFunctionOperation lambda,
        LifetimeContractModel promised, RegionSolution solution, LifetimeContractProvider contracts, MethodSummaryProvider summaries, RegionSolverOptions options)
    {
        var cancellationToken = context.CancellationToken;
        IOperation enclosing = lambda;
        while (enclosing.Parent is not null) enclosing = enclosing.Parent;
        var root = enclosing switch
        {
            IFieldInitializerOperation field => ControlFlowGraph.Create(field, cancellationToken),
            IPropertyInitializerOperation property => ControlFlowGraph.Create(property, cancellationToken),
            _ => ControlFlowLowerer.TryCreate(enclosing, cancellationToken)
        };
        var graph = root is null ? null : ControlFlowLowerer.EnumerateGraphs(root, lambda.Symbol, cancellationToken).Skip(1)
            .FirstOrDefault(item => SymbolEqualityComparer.Default.Equals(item.Method, lambda.Symbol)).Graph;
        if (graph is null)
        {
            ReportBudget(context, creation, lambda.Symbol, "unavailable anonymous-function control flow");
            return;
        }
        var bodyContract = new LifetimeContractModel(lambda.Symbol, promised.ParameterRegions, promised.ReturnRegion,
            promised.Constraints, ImmutableArray<LifetimeDeclarationIssue>.Empty, promised.DeclarationLocations,
            null, promised.MutableBorrow, false);
        foreach (var diagnostic in LifetimeReturnFlow.Analyze(graph, bodyContract, solution, contracts, summaries, options, cancellationToken))
        {
            if (diagnostic.Id == LifetimeContractAnalyzer.BudgetExceeded.Id) { context.ReportDiagnostic(diagnostic); continue; }
            context.ReportDiagnostic(Diagnostic.Create(LifetimeContractAnalyzer.DelegateContract, diagnostic.Location,
                diagnostic.AdditionalLocations, diagnostic.Properties.Add("SafeSharp.DelegateType", promised.Method.ContainingType.ToDisplayString()),
                "anonymous function", promised.Method.ContainingType.ToDisplayString(), diagnostic.GetMessage()));
        }
    }

    private static void Report(OperationAnalysisContext context, Location location, IMethodSymbol method, LifetimeContractModel promised, string message) =>
        context.ReportDiagnostic(Diagnostic.Create(LifetimeContractAnalyzer.DelegateContract, location,
            promised.DeclarationLocations.Concat(promised.Method.ContainingType.Locations).Concat(method.Locations)
                .Where(related => related.IsInSource && related != location).Distinct(),
            ImmutableDictionary<string, string?>.Empty.Add("SafeSharp.DelegateType", promised.Method.ContainingType.ToDisplayString())
                .Add("SafeSharp.Callback", method.ToDisplayString()), method.Name, promised.Method.ContainingType.ToDisplayString(), message));

    private static void ReportBudget(OperationAnalysisContext context, IDelegateCreationOperation creation, IMethodSymbol method, string reason) =>
        context.ReportDiagnostic(Diagnostic.Create(LifetimeContractAnalyzer.BudgetExceeded, creation.Syntax.GetLocation(), method.Name, reason));

    private sealed class BindingBudgetException : Exception
    {
        internal BindingBudgetException(string reason) => Reason = reason;
        internal string Reason { get; }
    }

    private sealed partial class Binding
    {
        private readonly LifetimeContractModel promised;
        private readonly Dictionary<string, string> universals = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> inference = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> descriptions = new(StringComparer.Ordinal);
        private readonly List<RegionConstraint> assumptions = new();
        private readonly List<RegionConstraint> requirements = new();
        private readonly string[] actualInputs;
        private readonly SourceSite? site;
        private readonly CancellationToken cancellationToken;
        private readonly RegionSolverOptions options;
        private readonly Compilation compilation;

        internal Binding(LifetimeContractModel promised, LifetimeContractModel actual, int boundParameters, Location location,
            RegionSolverOptions options, CancellationToken cancellationToken, Compilation compilation, bool receiverSuppliedByCaller = false,
            string? suppliedReceiverRegion = null, bool directCall = false)
        {
            this.promised = promised;
            this.cancellationToken = cancellationToken;
            this.options = options;
            this.compilation = compilation;
            site = LifetimeContractProvider.Source(location);
            // Every input needs at least one edge. Reject this lower bound before allocating
            // the input map or copying signatures larger than the configured graph budget.
            if ((long)promised.Constraints.Length + actual.Constraints.Length + actual.Method.Parameters.Length > options.MaxConstraints)
                throw new BindingBudgetException("outlives-constraint count");
            foreach (var name in promised.RootRegionNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Universal(name, (directCall ? "caller region '" : "delegate region '") + (directCall && name.StartsWith("$caller:", StringComparison.Ordinal) ? name.Substring(8) : name) + "'");
            }
            foreach (var edge in promised.Constraints)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!universals.ContainsKey(edge.Longer) || !universals.ContainsKey(edge.Shorter)) continue;
                AddEdge(assumptions, universals[edge.Longer], universals[edge.Shorter], edge.Site, "Delegate assumption: " + edge.Reason);
            }
            foreach (var name in actual.RootRegionNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Inference(name, (directCall ? "callee region '" : "callback region '") + name + "'");
            }
            foreach (var edge in actual.Constraints)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!inference.ContainsKey(edge.Longer) || !inference.ContainsKey(edge.Shorter)) continue;
                AddEdge(requirements, inference[edge.Longer], inference[edge.Shorter], edge.Site, "Callback requirement: " + edge.Reason);
            }
            actualInputs = new string[actual.Method.Parameters.Length];
            for (var index = 0; index < actualInputs.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var parameter = actual.Method.Parameters[index];
                var required = Inference(actual.ParameterRegions[index] ?? "$parameter:" + index, (directCall ? "callee input '" : "callback input '") + parameter.Name + "'");
                actualInputs[index] = required;
                var supplied = index < boundParameters
                    ? Universal("$bound:" + index, "captured argument '" + parameter.Name + "'")
                    : Universal(promised.ParameterRegions[index - boundParameters] ?? "$parameter:" + (index - boundParameters),
                        (directCall ? "argument '" : "delegate input '") + promised.Method.Parameters[index - boundParameters].Name + "'");
                AddEdge(requirements, supplied, required, site, "Input may be borrowed for a shorter region.");
                if (parameter.RefKind is RefKind.Ref or RefKind.Out)
                    AddEdge(requirements, required, supplied, site, "Writable ref/out storage is invariant.");
            }
            if (LifetimeContractModel.HasReceiver(actual.Method))
                AddEdge(requirements, receiverSuppliedByCaller && universals.TryGetValue(suppliedReceiverRegion ?? "this", out var suppliedReceiver) ? suppliedReceiver : Universal("$receiver", "captured receiver"), inference["this"], site,
                    receiverSuppliedByCaller ? "The inherited receiver is supplied by the caller." : "The captured receiver has a lifetime independent of later delegate inputs.");
            if (directCall)
            {
                // A direct invocation has a nonempty evaluation extent. Without this lower bound,
                // every type requirement could be satisfied by an empty inferred region and unknown
                // type provenance would accidentally count as a proof.
                var call = Universal("$call", "the current invocation extent");
                foreach (var region in promised.ParameterRegions.OfType<string>().Concat(promised.TypePositions.Where(position => position.RegionScope.Length == 0).Select(position => position.Region)).Concat(suppliedReceiverRegion is null ? Array.Empty<string>() : new[] { suppliedReceiverRegion }).Distinct(StringComparer.Ordinal))
                    if (universals.TryGetValue(region, out var supplied))
                        AddEdge(assumptions, supplied, call, site, "Supplied input regions cover the current invocation.");
                foreach (var required in inference.Values.ToArray())
                    AddEdge(requirements, required, call, site, "A callee input lifetime covers the current invocation.");
            }
        }

        internal bool AddReturn(LifetimeContractModel actual, MethodSummary summary, bool promisesAsyncPayload)
        {
            var required = universals[promised.ReturnRegion!];
            if (actual.ReturnRegion is { } region)
            {
                AddEdge(requirements, inference[region], required, site, "Callback result must outlive the promised delegate result.");
                return true;
            }
            // A source summary for `Task<T> Identity(Task<T> carrier)` describes the carrier,
            // not T. Only explicit result contracts or a proven payload summary can establish
            // the lifetime promised by a delegate's asynchronous result.
            if (actual.BorrowedSource is null && promisesAsyncPayload && !summary.ReturnsAsyncPayload) return false;
            var sources = actual.BorrowedSource is { } source ? ImmutableArray.Create(source) : summary.BorrowedSources;
            if (sources.IsEmpty) return false;
            var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var parameter in actual.Method.Parameters)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ordinals.Add(parameter.Name, parameter.Ordinal);
            }
            foreach (var name in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var origin = name == "this" && inference.TryGetValue("this", out var receiver) ? receiver
                    : ordinals.TryGetValue(name, out var index) ? actualInputs[index] : null;
                if (origin is null) return false;
                AddEdge(requirements, origin, required, site, "Every possible returned source must satisfy the delegate result.");
            }
            return true;
        }

        internal UniversalRegionProblem Problem() => new(new RegionProblem(universals.Values, assumptions), inference.Values, requirements);
        internal string Describe(string name) => descriptions.TryGetValue(name, out var description) ? description : "the static lifetime";

        internal ImmutableDictionary<string, string?> Properties(UniversalRegionFailure failure, RegionSolution closure, HigherRankedRegionSolution? ranked = null) =>
            AdditionalProperties(Properties(failure.Longer, failure.Shorter, failure.Proof, closure), ranked);

        private ImmutableDictionary<string, string?> Properties(string longer, string shorter, RegionProof proof, RegionSolution closure) =>
            ImmutableDictionary<string, string?>.Empty
                .Add("SafeSharp.DelegateType", promised.Method.ContainingType.ToDisplayString())
                .Add("SafeSharp.RegionNames", string.Join(", ", universals.Values.Concat(inference.Values).OrderBy(name => name, StringComparer.Ordinal)))
                .Add("SafeSharp.RegionConstraints", string.Join("; ", assumptions.Select(edge => edge.Longer + " : " + edge.Shorter)))
                .Add("SafeSharp.RequiredRegion", shorter)
                .Add("SafeSharp.SourceRegions", longer)
                .Add("SafeSharp.RegionProof", string.Join(" -> ", proof.Steps.Select(edge => edge.Longer).Concat(new[] { shorter })))
                .Add("SafeSharp.RegionBindings", string.Join("; ", descriptions.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + " = " + pair.Value)))
                .Add("SafeSharp.RegionRequirements", string.Join("; ", requirements.Select(edge => edge.Longer + " : " + edge.Shorter)))
                .Add("SafeSharp.RegionRequirementReasons", string.Join("; ", requirements.Select(edge => edge.Longer + " : " + edge.Shorter + " (" + edge.Reason + ")")))
                .Add("SafeSharp.RegionComponents", string.Join("; ", closure.Components.Select(component => string.Join(", ", component.Members))))
                .Add("SafeSharp.RegionQuantification", "one universal delegate binder");

        private string Universal(string key, string description)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (universals.TryGetValue(key, out var name)) return name;
            CheckNameBudget();
            name = "u" + universals.Count;
            universals.Add(key, name); descriptions.Add(name, description); return name;
        }

        private string Inference(string key, string description)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (inference.TryGetValue(key, out var name)) return name;
            CheckNameBudget();
            name = "i" + inference.Count;
            inference.Add(key, name); descriptions.Add(name, description); return name;
        }

        private void CheckNameBudget()
        {
            if (universals.Count + inference.Count >= options.MaxRegions)
                throw new BindingBudgetException("named-region count");
        }

        private void AddEdge(List<RegionConstraint> edges, string longer, string shorter, SourceSite? edgeSite, string reason)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (assumptions.Count + requirements.Count >= options.MaxConstraints)
                throw new BindingBudgetException("outlives-constraint count");
            edges.Add(new RegionConstraint(longer, shorter, edgeSite, reason));
        }
    }
}
