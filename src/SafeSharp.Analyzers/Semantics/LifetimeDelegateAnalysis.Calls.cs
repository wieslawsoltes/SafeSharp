using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using SafeSharp.Core.Regions;

namespace SafeSharp.Analyzers.Semantics;

internal static partial class LifetimeDelegateAnalysis
{
    /// <summary>
    /// Instantiates an explicit type-lifetime precondition using the caller's supplied values.
    /// The shared binder keeps higher-ranked inputs rigid on the required side and existential
    /// on the supplied side. Type requirements are checked against caller facts, never assumed
    /// from the declaration being invoked. See rustc_borrowck/type_check/relate_tys.rs and
    /// rustc_borrowck/region_infer/mod.rs at the pinned Rust revision in docs/rust-design.md.
    /// </summary>
    internal static void AnalyzeCall(OperationAnalysisContext context, LifetimeContractProvider contracts, CallEffectsProvider effects)
    {
        if (context.Operation is not IInvocationOperation invocation) return;
        var cancellationToken = context.CancellationToken;
        var method = invocation.TargetMethod;
        var reduced = method.ReducedFrom;
        if (reduced is not null)
        {
            if (reduced.TypeParameters.Length == method.TypeArguments.Length && reduced.TypeParameters.Length != 0)
                reduced = reduced.Construct(method.TypeArguments.ToArray());
            method = reduced;
        }
        var required = contracts.Get(method, cancellationToken);
        if (!required.DeclarationIssues.IsEmpty ||
            !required.TypePositions.Any(position => position.Parameter >= 0) && !required.TypeBounds.Any(bound => !bound.IsImplied && bound.Scope.Length == 0)) return;
        var options = LifetimeContractProvider.Options(context.Options.AnalyzerConfigOptionsProvider.GetOptions(invocation.Syntax.SyntaxTree));
        var location = invocation.Syntax.GetLocation();
        var callerMethod = EnclosingCallMethod(invocation, context.ContainingSymbol);
        if (callerMethod is null)
        {
            ReportCall(context, required, "there is no enclosing callable lifetime contract for these arguments");
            return;
        }
        var caller = contracts.Get(callerMethod, cancellationToken);
        if (!caller.DeclarationIssues.IsEmpty) return;
        try
        {
            var evidence = new CallEvidence(context, invocation, method, caller, options, effects);
            if (evidence.Build(required, reduced is not null, out var supplied, out var receiver) is { } missing)
            {
                ReportCall(context, required, missing, evidence.Properties());
                return;
            }
            var input = InputContract(required);
            var binding = new Binding(supplied!, input, 0, location, evidence.RemainingOptions(), cancellationToken,
                context.Compilation, receiverSuppliedByCaller: true, suppliedReceiverRegion: receiver, directCall: true);
            if (binding.AddTypes(input, method, callerMethod, 0) is { } mismatch)
            {
                ReportCall(context, required, mismatch.Replace("callback", "callee").Replace("delegate", "supplied argument"), evidence.Properties());
                return;
            }
            var result = new HigherRankedRegionSolver().Solve(binding.RankedProblem(), binding.RemainingOptions(), cancellationToken);
            if (!result.IsComplete)
            {
                context.ReportDiagnostic(Diagnostic.Create(LifetimeContractAnalyzer.BudgetExceeded, location, method.Name,
                    result.BudgetReason ?? "direct-call type obligations"));
                return;
            }
            if (result.IsCompatible) return;
            if (!result.Failures.IsEmpty)
            {
                var failure = result.Failures[0];
                ReportCall(context, required, "the arguments require " + binding.Describe(failure.Longer) + " to outlive " +
                    binding.Describe(failure.Shorter) + ", which the caller does not establish",
                    CallProperties(binding.Properties(failure, result.Closure!, result), evidence));
            }
            else
            {
                var failure = result.TypeFailures[0];
                ReportCall(context, required, "the borrowed contents of type '" + binding.DescribeType(failure.Requirement.Type) +
                    "' are not known to outlive " + binding.Describe(failure.Region),
                    CallProperties(binding.TypeProperties(failure, result), evidence));
            }
        }
        catch (BindingBudgetException exhausted)
        {
            context.ReportDiagnostic(Diagnostic.Create(LifetimeContractAnalyzer.BudgetExceeded, location, method.Name, exhausted.Reason));
        }
    }

    private static ImmutableDictionary<string, string?> CallProperties(ImmutableDictionary<string, string?> properties, CallEvidence evidence) =>
        properties.Remove("SafeSharp.DelegateType").SetItems(evidence.Properties())
            .SetItem("SafeSharp.RegionQuantification", "direct invocation with explicit nested callable binders");

    private static void ReportCall(OperationAnalysisContext context, LifetimeContractModel required, string reason,
        ImmutableDictionary<string, string?>? properties = null) =>
        context.ReportDiagnostic(Diagnostic.Create(LifetimeContractAnalyzer.CallContract, context.Operation.Syntax.GetLocation(),
            required.DeclarationLocations.Where(location => location.IsInSource).Distinct(),
            (properties ?? ImmutableDictionary<string, string?>.Empty)
                .SetItem("SafeSharp.CallTarget", required.Method.ToDisplayString()).SetItem("SafeSharp.CallReason", reason),
            required.Method.Name, reason));

    private static IMethodSymbol? EnclosingCallMethod(IOperation operation, ISymbol containing)
    {
        for (var parent = operation.Parent; parent is not null; parent = parent.Parent)
            if (parent is IAnonymousFunctionOperation lambda) return lambda.Symbol;
            else if (parent is ILocalFunctionOperation local) return local.Symbol;
        return containing as IMethodSymbol;
    }

    private static LifetimeContractModel InputContract(LifetimeContractModel model)
    {
        var positions = model.TypePositions.Where(position => position.Parameter >= 0).ToImmutableArray();
        var scopes = new HashSet<string>(positions.SelectMany(position => position.Scopes).Select(scope => scope.Key), StringComparer.Ordinal) { string.Empty };
        var regions = new HashSet<string>(model.RootRegionNames.Concat(positions.Select(position => position.Region)), StringComparer.Ordinal);
        return new LifetimeContractModel(model.Method, model.ParameterRegions, null,
            model.Constraints.Where(edge => regions.Contains(edge.Longer) && regions.Contains(edge.Shorter)).ToImmutableArray(),
            ImmutableArray<LifetimeDeclarationIssue>.Empty, model.DeclarationLocations, null, false, false, positions,
            model.TypeBounds.Where(bound => scopes.Contains(bound.Scope)).ToImmutableArray(), model.RootRegionNames);
    }

    private sealed class CallEvidence
    {
        private readonly OperationAnalysisContext context;
        private readonly IInvocationOperation invocation;
        private readonly IMethodSymbol callee;
        private readonly LifetimeContractModel caller;
        private readonly RegionSolverOptions options;
        private readonly CallEffectsProvider effects;
        private CallEffectSummary? effectSummary;
        private readonly List<RegionConstraint> assumptions = new();
        private readonly List<LifetimeTypePosition> positions = new();
        private readonly List<LifetimeTypeBound> bounds = new();
        private readonly HashSet<string> rootRegions = new(StringComparer.Ordinal);
        private readonly List<string> argumentDescriptions = new();
        private int steps;
        private RegionSolution? callerSolution;

        internal CallEvidence(OperationAnalysisContext context, IInvocationOperation invocation, IMethodSymbol callee,
            LifetimeContractModel caller, RegionSolverOptions options, CallEffectsProvider effects)
        { this.context = context; this.invocation = invocation; this.callee = callee; this.caller = caller; this.options = options; this.effects = effects; }

        internal string? Build(LifetimeContractModel required, bool reduced, out LifetimeContractModel? supplied, out string? receiver)
        {
            supplied = null;
            receiver = null;
            effectSummary = effects.Get(invocation, caller.Method, context.Compilation, options.MaxSteps, context.CancellationToken);
            steps += effectSummary.Steps;
            Step();
            foreach (var region in caller.RootRegionNames) { Step(); rootRegions.Add(Root(region)); }
            foreach (var edge in caller.Constraints)
            {
                Step();
                if (caller.RootRegionNames.Contains(edge.Longer, StringComparer.Ordinal) && caller.RootRegionNames.Contains(edge.Shorter, StringComparer.Ordinal))
                    assumptions.Add(new RegionConstraint(Root(edge.Longer), Root(edge.Shorter), edge.Site, "Caller assumption: " + edge.Reason));
            }
            foreach (var bound in caller.TypeBounds)
            {
                Step();
                if (bound.Scope.Length == 0) bounds.Add(new LifetimeTypeBound(bound.Type, Root(bound.Region), string.Empty, bound.Location, bound.IsImplied));
            }
            var values = new IOperation?[callee.Parameters.Length];
            if (reduced) values[0] = invocation.Instance;
            foreach (var argument in invocation.Arguments)
            {
                Step();
                if (argument.Parameter is null) continue;
                var ordinal = argument.Parameter.Ordinal + (reduced ? 1 : 0);
                if (ordinal >= values.Length) return "the bound argument layout is not represented";
                if (values[ordinal] is not null) return "expanded argument storage has no explicit borrowed-content contract";
                values[ordinal] = argument.Value;
            }
            var regions = ImmutableArray.CreateBuilder<string?>(values.Length);
            for (var ordinal = 0; ordinal < values.Length; ordinal++)
            {
                Step();
                var value = values[ordinal];
                var source = value is null ? null : Source(value, new HashSet<ISymbol>(SymbolEqualityComparer.Default), 0);
                var region = source is null ? "$argument:" + ordinal : source.Value.Region;
                rootRegions.Add(region);
                regions.Add(region);
                argumentDescriptions.Add(callee.Parameters[ordinal].Name + " = " + (source?.Description ?? "unknown evaluated value"));
                var needed = required.TypePositions.Where(position => position.Parameter == ordinal).ToArray();
                if (needed.Length == 0) continue;
                if (source is not { Parameter: { } parameter })
                    return "argument '" + callee.Parameters[ordinal].Name + "' has no stable declared TypeLifetime evidence";
                if (effectSummary.InvalidatesContents(parameter, invocation))
                    return "argument '" + callee.Parameters[ordinal].Name + "' has had its borrowed-content evidence invalidated by a write, reference exposure, or unmodeled call";
                if (!SameShape(parameter.Type, callee.Parameters[ordinal].Type))
                    return "argument '" + callee.Parameters[ordinal].Name + "' changes its nominal type shape; borrowed-content projection is not established";
                var sourcePositions = caller.TypePositions.Where(position => position.Parameter == parameter.Ordinal).ToDictionary(position => position.Path, StringComparer.Ordinal);
                var selected = new List<(LifetimeTypePosition Source, LifetimeTypePosition Supplied)>();
                foreach (var target in needed)
                {
                    Step();
                    if (!sourcePositions.TryGetValue(target.Path, out var original))
                        return "argument '" + callee.Parameters[ordinal].Name + "' lacks TypeLifetime evidence at path '" + target.Path + "'";
                    var name = original.RegionScope.Length == 0 ? Root(original.Name) : original.Name;
                    var scope = RemapScope(original.Scope, parameter.Ordinal, ordinal);
                    var regionScope = RemapScope(original.RegionScope, parameter.Ordinal, ordinal);
                    // Changing the outer parameter's passing mode changes outer variance. The
                    // selected nominal constructor and callable paths were verified above, so the
                    // destination path supplies exactly that re-rooted variance and scope shape.
                    var mapped = new LifetimeTypePosition(ordinal, target.Path, name, scope, regionScope, target.Variance,
                        original.Type, target.IsInput, original.Location, target.Scopes);
                    positions.Add(mapped);
                    selected.Add((original, mapped));
                    if (regionScope.Length == 0) rootRegions.Add(mapped.Region);
                }
                AddNestedEvidence(parameter.Ordinal, ordinal, selected);
            }
            if (LifetimeContractModel.HasReceiver(callee))
            {
                receiver = invocation.Instance is null ? "$receiver:unknown" : Source(invocation.Instance,
                    new HashSet<ISymbol>(SymbolEqualityComparer.Default), 0)?.Region ?? "$receiver:unknown";
                rootRegions.Add(receiver);
            }
            CheckSize();
            supplied = new LifetimeContractModel(callee, regions.ToImmutable(), null, assumptions.ToImmutableArray(),
                ImmutableArray<LifetimeDeclarationIssue>.Empty, caller.DeclarationLocations, null, false, false,
                positions.ToImmutableArray(), bounds.ToImmutableArray(), rootRegions.OrderBy(name => name, StringComparer.Ordinal).ToImmutableArray());
            return null;
        }

        private void AddNestedEvidence(int sourceOrdinal, int targetOrdinal,
            List<(LifetimeTypePosition Source, LifetimeTypePosition Supplied)> selected)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in selected) { Step(); map[pair.Source.Region] = pair.Supplied.Region; }
            var nested = selected.Where(pair => pair.Source.RegionScope.Length != 0).Select(pair => pair.Source)
                .GroupBy(position => position.Region, StringComparer.Ordinal).Select(group => group.First()).ToArray();
            if (nested.Length != 0)
            {
                if (callerSolution is null)
                {
                    callerSolution = caller.Solve(RemainingOptions(), context.CancellationToken);
                    steps += callerSolution.Steps;
                    Step();
                }
                if (!callerSolution.IsComplete) throw new BindingBudgetException(callerSolution.BudgetReason ?? "caller nested-region assumptions");
                foreach (var longer in nested)
                foreach (var shorter in nested)
                {
                    Step();
                    if (longer.Region == shorter.Region || longer.RegionScope != shorter.RegionScope || !callerSolution.Outlives(longer.Region, shorter.Region)) continue;
                    assumptions.Add(new RegionConstraint(map[longer.Region], map[shorter.Region],
                        LifetimeContractProvider.Source(longer.Location), "Caller nested contract implies this outlives relation."));
                }
            }
            foreach (var bound in caller.TypeBounds)
            {
                Step();
                if (bound.Scope.Length == 0 || !map.TryGetValue(bound.Region, out var region)) continue;
                var scope = RemapScope(bound.Scope, sourceOrdinal, targetOrdinal);
                if (scope == bound.Scope && !scope.StartsWith("parameter" + targetOrdinal, StringComparison.Ordinal)) continue;
                bounds.Add(new LifetimeTypeBound(bound.Type, region, scope, bound.Location, bound.IsImplied));
            }
        }

        private ValueEvidence? Source(IOperation operation, HashSet<ISymbol> seen, int depth)
        {
            Step();
            if (depth >= LifetimeTypePath.MaximumSegments) throw new BindingBudgetException("direct-call value provenance depth");
            switch (operation)
            {
                case IParenthesizedOperation parenthesized:
                    return Source(parenthesized.Operand, seen, depth + 1);
                case IConversionOperation conversion when !conversion.Conversion.IsUserDefined &&
                    (conversion.Conversion.IsIdentity || conversion.Conversion.IsReference && SameShape(conversion.Operand.Type, conversion.Type)):
                    return Source(conversion.Operand, seen, depth + 1);
                case IParameterReferenceOperation parameter when SymbolEqualityComparer.Default.Equals(parameter.Parameter.ContainingSymbol, caller.Method) &&
                    parameter.Parameter.RefKind != RefKind.Out && IsStable(parameter.Parameter):
                    var region = caller.ParameterRegions[parameter.Parameter.Ordinal] is { } named ? Root(named) : "$input:" + parameter.Parameter.Ordinal;
                    return new ValueEvidence(parameter.Parameter, region, "stable caller parameter '" + parameter.Parameter.Name + "'");
                case IInstanceReferenceOperation instance when instance.ReferenceKind == InstanceReferenceKind.ContainingTypeInstance && LifetimeContractModel.HasReceiver(caller.Method):
                    return new ValueEvidence(null, Root("this"), "the caller receiver");
                case ILocalReferenceOperation local when local.Local.RefKind == RefKind.None && IsStable(local.Local) && seen.Add(local.Local):
                    if (local.Local.DeclaringSyntaxReferences.Length != 1) return null;
                    var syntax = local.Local.DeclaringSyntaxReferences[0].GetSyntax(context.CancellationToken);
                    if (syntax is VariableDeclaratorSyntax { Initializer.Value: { } initializer } &&
                        context.Compilation.GetSemanticModel(syntax.SyntaxTree).GetOperation(initializer, context.CancellationToken) is { } value)
                        return Source(value, seen, depth + 1);
                    return null;
                default:
                    return null;
            }
        }

        private bool IsStable(ISymbol symbol) => !effectSummary!.InvalidatesSlot(symbol, invocation);

        private void CheckSize()
        {
            if (rootRegions.Count + positions.Count >= options.MaxRegions) throw new BindingBudgetException("direct-call region evidence count");
            if ((long)assumptions.Count + bounds.Count + positions.Count >= options.MaxConstraints) throw new BindingBudgetException("direct-call evidence constraint count");
        }

        private void Step()
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (steps++ >= options.MaxSteps) throw new BindingBudgetException("direct-call evidence steps");
        }

        internal RegionSolverOptions RemainingOptions()
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (steps >= options.MaxSteps) throw new BindingBudgetException("direct-call evidence steps");
            return new RegionSolverOptions(options.MaxRegions, options.MaxConstraints, options.MaxSteps - steps);
        }

        internal ImmutableDictionary<string, string?> Properties() => ImmutableDictionary<string, string?>.Empty
            .Add("SafeSharp.CallArguments", string.Join("; ", argumentDescriptions))
            .Add("SafeSharp.CallEvidence", "Stable declared inputs and transparent local aliases; mutation or unknown contents do not establish type provenance.");

        private static string Root(string name) => "$caller:" + name;
        private static string RemapScope(string scope, int source, int target)
        {
            var prefix = "parameter" + source;
            return scope == prefix || scope.StartsWith(prefix + ".", StringComparison.Ordinal) ? "parameter" + target + scope.Substring(prefix.Length) : scope;
        }

        // A same-named outer constructor does not prove corresponding nested delegate paths.
        // Nominal variance/projection conversions remain conservative until their path remapping
        // is represented explicitly; nullable annotations do not change Roslyn's default identity.
        private static bool SameShape(ITypeSymbol? source, ITypeSymbol? target) =>
            source is not null && target is not null && SymbolEqualityComparer.Default.Equals(source, target);

        private readonly struct ValueEvidence
        {
            internal ValueEvidence(IParameterSymbol? parameter, string region, string description)
            { Parameter = parameter; Region = region; Description = description; }
            internal IParameterSymbol? Parameter { get; }
            internal string Region { get; }
            internal string Description { get; }
        }
    }
}
