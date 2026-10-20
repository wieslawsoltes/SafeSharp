using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using SafeSharp.Core.Regions;

namespace SafeSharp.Analyzers.Semantics;

internal static partial class LifetimeDelegateAnalysis
{
    internal static string? InheritedTypeMismatch(LifetimeContractModel actual, LifetimeContractModel promised,
        IMethodSymbol actualMethod, IMethodSymbol promisedMethod, RegionSolverOptions options, Compilation compilation, ref int remainingSteps,
        CancellationToken cancellationToken, out string? budgetReason, out ImmutableDictionary<string, string?>? properties)
    {
        budgetReason = null;
        properties = null;
        if (!HasTypeContracts(actual) && !HasTypeContracts(promised)) return null;
        if (remainingSteps <= 0) { budgetReason = "inherited type-lifetime comparison steps"; return null; }
        try
        {
            var comparisonOptions = new RegionSolverOptions(options.MaxRegions, options.MaxConstraints, remainingSteps);
            var binding = new Binding(promised, actual, 0, actualMethod.Locations.FirstOrDefault() ?? Location.None,
                comparisonOptions, cancellationToken, compilation, receiverSuppliedByCaller: true);
            if (actual.ReturnRegion is not null && promised.ReturnRegion is not null)
                binding.AddReturn(actual, Analysis.MethodSummary.Unknown, promisesAsyncPayload: false);
            if (binding.AddTypes(actual, actualMethod, promisedMethod, 0, alignMethodParameters: true) is { } mismatch)
            { remainingSteps -= binding.TypeBindingSteps; return mismatch; }
            var result = new HigherRankedRegionSolver().Solve(binding.RankedProblem(), binding.RemainingOptions(), cancellationToken);
            remainingSteps -= binding.TypeBindingSteps + result.Steps;
            if (!result.IsComplete) { budgetReason = result.BudgetReason; return null; }
            if (result.IsCompatible) return null;
            if (!result.Failures.IsEmpty)
            {
                var failure = result.Failures[0];
                properties = binding.Properties(failure, result.Closure!, result);
                return "the implementation requires " + binding.Describe(failure.Longer) + " to outlive " + binding.Describe(failure.Shorter) + " beyond the inherited type-lifetime promise";
            }
            var typeFailure = result.TypeFailures[0];
            properties = binding.TypeProperties(typeFailure, result);
            return "the implementation requires type '" + binding.DescribeType(typeFailure.Requirement.Type) + "' to outlive " + binding.Describe(typeFailure.Region) + " without an inherited type bound";
        }
        catch (BindingBudgetException exhausted)
        {
            remainingSteps = 0;
            budgetReason = exhausted.Reason;
            return null;
        }
    }

    private sealed partial class Binding
    {
        private readonly List<RegionBinder> binders = new() { new RegionBinder("root") };
        private readonly Dictionary<string, string> variableBinders = new(StringComparer.Ordinal);
        private readonly List<TypeOutlivesConstraint> assumedTypes = new();
        private readonly List<TypeOutlivesConstraint> requiredTypes = new();
        private readonly Dictionary<ITypeSymbol, string> typeNames = new(SymbolEqualityComparer.Default);
        private readonly Dictionary<string, string> typeDescriptions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<ScopeInstance>> scopeInstances = new(StringComparer.Ordinal);
        private int typeSteps;
        internal int TypeBindingSteps => typeSteps;

        internal string? AddTypes(LifetimeContractModel actual, IMethodSymbol actualMethod, IMethodSymbol promisedMethod, int boundParameters,
            bool alignMethodParameters = false)
        {
            var sourcePositions = new Dictionary<(int Parameter, string Path), LifetimeTypePosition>();
            foreach (var position in actual.TypePositions)
            {
                TypeStep();
                if (position.Parameter >= 0 && position.Parameter < boundParameters)
                    return "a bound receiver's TypeLifetime paths need caller-supplied evidence which this delegate conversion does not provide";
                sourcePositions.Add((position.Parameter < 0 ? -1 : position.Parameter - boundParameters, position.Path), position);
            }
            var pairs = new List<(LifetimeTypePosition Actual, LifetimeTypePosition Promised)>();
            foreach (var position in promised.TypePositions)
            {
                TypeStep();
                if (!sourcePositions.TryGetValue((position.Parameter, position.Path), out var source))
                    return "the callback has no TypeLifetime contract for " + Position(position);
                if (source.Variance != position.Variance)
                    return "the declared generic or writable-storage variance differs at " + Position(position);
                pairs.Add((source, position));
                sourcePositions.Remove((position.Parameter, position.Path));
            }
            if (sourcePositions.Count != 0)
                return "the callback adds a TypeLifetime requirement absent from the delegate at " + Position(sourcePositions.Values.First());

            var actualScopes = actual.TypeScopes.ToDictionary(scope => Normalize(scope.Key, boundParameters), StringComparer.Ordinal);
            var root = new ScopeInstance(string.Empty, "root", false, null);
            scopeInstances.Add(string.Empty, new List<ScopeInstance> { root });
            foreach (var scope in promised.TypeScopes.OrderBy(scope => scope.Key.Length).ThenBy(scope => scope.Key, StringComparer.Ordinal))
            {
                TypeStep();
                if (!actualScopes.TryGetValue(scope.Key, out var actualScope) || actualScope.Variance != scope.Variance || Normalize(actualScope.Parent, boundParameters) != scope.Parent)
                    return "the nested callable binder or its variance differs at '" + scope.Key + "'";
                var instances = new List<ScopeInstance>();
                foreach (var parent in scopeInstances[scope.Parent])
                {
                    // Equality of higher-ranked callable types needs both subtype directions,
                    // with fresh placeholders in each check. Reusing one existential solution
                    // for the reverse direction incorrectly accepts unequal quantified contracts.
                    AddScope(parent, scope.Key, scope.Variance == RegionVariance.Contravariant ? !parent.Reversed : parent.Reversed, instances);
                    if (scope.Variance == RegionVariance.Invariant) AddScope(parent, scope.Key, !parent.Reversed, instances);
                }
                scopeInstances.Add(scope.Key, instances);
            }
            if (actualScopes.Count != promised.TypeScopes.Length) return "the callback changes the number of nested callable binders";
            foreach (var pair in pairs)
            foreach (var instance in scopeInstances[pair.Promised.Scope])
            {
                TypeStep();
                var source = Map(pair.Actual.Region, pair.Actual.RegionScope, true, instance, boundParameters);
                var target = Map(pair.Promised.Region, pair.Promised.RegionScope, false, instance, 0);
                var forward = (pair.Promised.Variance == RegionVariance.Covariant) != instance.Reversed;
                var reason = "TypeLifetime " + Position(pair.Promised) + " follows " + pair.Promised.Variance.ToString().ToLowerInvariant() + " borrowed type variance.";
                var location = LifetimeContractProvider.Source(pair.Actual.Location);
                if (pair.Promised.Variance == RegionVariance.Invariant)
                {
                    AddEdge(requirements, source, target, location, reason);
                    AddEdge(requirements, target, source, location, reason);
                }
                else AddEdge(requirements, forward ? source : target, forward ? target : source, location, reason);
            }
            AddScopedConstraints(actual, true, boundParameters);
            AddScopedConstraints(promised, false, 0);
            AddTypeBounds(actual, actualMethod, promisedMethod, true, boundParameters, alignMethodParameters);
            AddTypeBounds(promised, promisedMethod, promisedMethod, false, 0, false);
            descriptions["$static"] = "the static lifetime";
            return null;
        }

        private void AddScope(ScopeInstance parent, string key, bool reversed, List<ScopeInstance> instances)
        {
            TypeStep();
            if (binders.Count >= options.MaxRegions) throw new BindingBudgetException("named-region/universe count");
            var universe = "b" + binders.Count;
            binders.Add(new RegionBinder(universe, parent.Universe));
            instances.Add(new ScopeInstance(key, universe, reversed, parent));
        }

        private string Map(string region, string regionScope, bool actual, ScopeInstance instance, int boundParameters)
        {
            TypeStep();
            if (regionScope.Length == 0) return actual ? inference[region] : universals[region];
            var scope = Normalize(regionScope, boundParameters);
            var owner = instance;
            while (owner.Key != scope)
            {
                TypeStep();
                owner = owner.Parent ?? throw new InvalidOperationException("A lifetime reference must resolve to its current or an enclosing binder.");
            }
            var key = "$type:" + (actual ? "callback:" : "delegate:") + owner.Universe + ":" + region;
            var description = (actual ? "callback" : "delegate") + " region '" + region.Substring(region.LastIndexOf(':') + 1) + "' in '" + scope + "' (" + owner.Universe + ")";
            var name = actual == owner.Reversed ? Universal(key, description) : Inference(key, description);
            variableBinders[name] = owner.Universe;
            return name;
        }

        private void AddScopedConstraints(LifetimeContractModel model, bool actual, int boundParameters)
        {
            var scopes = model.TypePositions.GroupBy(position => position.Region, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().RegionScope, StringComparer.Ordinal);
            foreach (var edge in model.Constraints)
            {
                TypeStep();
                if (!scopes.TryGetValue(edge.Longer, out var scope) || scope.Length == 0) continue;
                if (!scopes.TryGetValue(edge.Shorter, out var shorterScope) || shorterScope != scope)
                    throw new InvalidOperationException("A scoped contract bound must stay within its declared binder.");
                foreach (var instance in scopeInstances[Normalize(scope, boundParameters)])
                {
                    var longer = Map(edge.Longer, scope, actual, instance, boundParameters);
                    var shorter = Map(edge.Shorter, scope, actual, instance, boundParameters);
                    AddEdge(actual == instance.Reversed ? assumptions : requirements, longer, shorter, edge.Site,
                        "Nested " + (actual == instance.Reversed ? "caller assumption: " : "callback requirement: ") + edge.Reason);
                }
            }
        }

        private void AddTypeBounds(LifetimeContractModel model, IMethodSymbol method, IMethodSymbol promisedMethod, bool actual,
            int boundParameters, bool alignMethodParameters)
        {
            var substitutions = new Dictionary<ITypeSymbol, ITypeSymbol>(SymbolEqualityComparer.Default);
            foreach (var bound in model.TypeBounds)
            foreach (var instance in scopeInstances[Normalize(bound.Scope, boundParameters)])
            {
                TypeStep();
                if ((long)assumptions.Count + requirements.Count + assumedTypes.Count + requiredTypes.Count >= options.MaxConstraints)
                    throw new BindingBudgetException("outlives/type-constraint count");
                var resolved = ResolveType(bound.Type, method, promisedMethod, alignMethodParameters, substitutions);
                if (!typeNames.TryGetValue(resolved, out var type))
                {
                    type = "t" + typeNames.Count;
                    typeNames.Add(resolved, type);
                    typeDescriptions.Add(type, resolved.ToDisplayString());
                }
                var region = Map(bound.Region, bound.Scope, actual, instance, boundParameters);
                var requirement = new TypeOutlivesConstraint(type, region, LifetimeContractProvider.Source(bound.Location),
                    bound.IsImplied ? "Annotated borrowed input implies type well-formedness." : "Declared generic type-outlives bound.");
                (actual == instance.Reversed ? assumedTypes : requiredTypes).Add(requirement);
            }
        }

        internal HigherRankedRegionProblem RankedProblem() => new(binders,
            universals.Values.Select(name => new RegionVariable(name, variableBinders.TryGetValue(name, out var scope) ? scope : "root",
                    variableBinders.ContainsKey(name) ? RegionVariableKind.Placeholder : RegionVariableKind.Universal))
                .Concat(inference.Values.Select(name => new RegionVariable(name, variableBinders.TryGetValue(name, out var scope) ? scope : "root", RegionVariableKind.Inference))),
            assumptions, requirements, assumedTypes, requiredTypes);

        internal RegionSolverOptions RemainingOptions()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (typeSteps >= options.MaxSteps) throw new BindingBudgetException("type-lifetime binding steps");
            return new RegionSolverOptions(options.MaxRegions, options.MaxConstraints, options.MaxSteps - typeSteps);
        }

        private void TypeStep()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (typeSteps == options.MaxSteps) throw new BindingBudgetException("type-lifetime binding steps");
            typeSteps++;
        }

        private ImmutableDictionary<string, string?> AdditionalProperties(ImmutableDictionary<string, string?> properties, HigherRankedRegionSolution? ranked)
        {
            if (ranked is null) return properties;
            var names = universals.Values.Concat(inference.Values).Append("$static").OrderBy(name => name, StringComparer.Ordinal).ToArray();
            var edges = requirements.Concat(ranked.Promotions.Select(promotion => promotion.UpperBound)).ToArray();
            return properties
                .SetItem("SafeSharp.RegionNames", string.Join(", ", names))
                .SetItem("SafeSharp.RegionConstraints", string.Join("; ", assumptions.Select(edge => edge.Longer + " : " + edge.Shorter)
                    .Concat(names.Where(name => name != "$static").Select(name => "$static : " + name))))
                .SetItem("SafeSharp.RegionRequirements", string.Join("; ", edges.Select(edge => edge.Longer + " : " + edge.Shorter)))
                .SetItem("SafeSharp.RegionRequirementReasons", string.Join("; ", edges.Select(edge => edge.Longer + " : " + edge.Shorter + " (" + edge.Reason + ")")))
                .SetItem("SafeSharp.RegionQuantification", binders.Count == 1 ? "one universal delegate binder" : "nested universal delegate binders: " + (binders.Count - 1))
                .Add("SafeSharp.RegionUniverses", string.Join("; ", binders.Select(binder => binder.Name + " <- " + (binder.Parent ?? "root scope"))))
                .Add("SafeSharp.RegionVariableUniverses", string.Join("; ", names.Select(name => name + " @ " + (variableBinders.TryGetValue(name, out var scope) ? scope : "root"))))
                .Add("SafeSharp.RegionUniversePromotions", string.Join("; ", ranked.Promotions.Select(promotion => promotion.Variable.Name + " : $static (cannot name " + promotion.Placeholder.Name + " from " + promotion.EffectiveBinder + ")")))
                .Add("SafeSharp.RegionVisibilityProofs", string.Join("; ", ranked.Promotions.Select(promotion => Witness(promotion.Proof) + " / " + Witness(promotion.VisibilityProof))))
                .Add("SafeSharp.TypeOutlivesAssumptions", string.Join("; ", assumedTypes.Select(bound => bound.Type + " : " + bound.Region)))
                .Add("SafeSharp.TypeOutlivesRequirements", string.Join("; ", requiredTypes.Select(bound => bound.Type + " : " + bound.Region)))
                .Add("SafeSharp.TypeBindings", string.Join("; ", typeDescriptions.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + " = " + pair.Value)));
        }

        internal ImmutableDictionary<string, string?> TypeProperties(TypeOutlivesFailure failure, HigherRankedRegionSolution ranked) =>
            AdditionalProperties(Properties(failure.Requirement.Region, failure.Region, failure.Proof, ranked.Closure!), ranked)
                .Add("SafeSharp.TypeOutlivesType", failure.Requirement.Type);

        internal string DescribeType(string type) => typeDescriptions.TryGetValue(type, out var description) ? description : type;
        private static string Witness(RegionProof proof) => string.Join(" -> ", proof.Steps.Select(edge => edge.Longer).Concat(new[] { proof.Shorter }));
        private static string Position(LifetimeTypePosition position) => (position.Parameter < 0 ? "the result" : "parameter " + position.Parameter) + " type path '" + position.Path + "'";

        private static string Normalize(string scope, int boundParameters)
        {
            if (scope.Length == 0 || boundParameters == 0 || !scope.StartsWith("parameter", StringComparison.Ordinal)) return scope;
            var end = scope.IndexOf('.');
            if (end < 0) end = scope.Length;
            return "parameter" + (int.Parse(scope.Substring(9, end - 9), System.Globalization.CultureInfo.InvariantCulture) - boundParameters) + scope.Substring(end);
        }

        private sealed class ScopeInstance
        {
            internal ScopeInstance(string key, string universe, bool reversed, ScopeInstance? parent)
            { Key = key; Universe = universe; Reversed = reversed; Parent = parent; }
            internal string Key { get; }
            internal string Universe { get; }
            internal bool Reversed { get; }
            internal ScopeInstance? Parent { get; }
        }
    }
}
