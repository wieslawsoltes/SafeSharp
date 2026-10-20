using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;

namespace SafeSharp.Core.Regions;

/// <summary>Checks scoped universal lifetimes, placeholder visibility, and explicit type-outlives obligations.</summary>
/// <remarks>
/// Original C# adaptation of rustc's universe-aware outlives propagation and universal-region checks:
/// https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_borrowck/src/region_infer/mod.rs
/// https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_borrowck/src/handle_placeholders.rs
/// https://rustc-dev-guide.rust-lang.org/borrow-check/region-inference/placeholders-and-universes.html
/// A variable which cannot name a required placeholder is conservatively raised to static. It is
/// not rejected merely for mentioning a younger lifetime: an unconstrained existential may choose
/// static. A finite universal upper bound then exposes a genuine leak. All closures, promotions,
/// comparisons and type tests share one caller budget. This is not a Rust trait or type unifier.
/// </remarks>
public sealed class HigherRankedRegionSolver
{
    public HigherRankedRegionSolution Solve(HigherRankedRegionProblem problem, RegionSolverOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (problem is null) throw new ArgumentNullException(nameof(problem));
        options ??= new RegionSolverOptions();
        cancellationToken.ThrowIfCancellationRequested();
        if (problem.Regions.Length > options.MaxRegions || problem.Binders.Length > options.MaxRegions)
            return HigherRankedRegionSolution.Incomplete("named-region/universe count", 0);
        var implicitEdges = problem.Regions.Where(region => region.Name != problem.StaticRegion)
            .Select(region => new RegionConstraint(problem.StaticRegion, region.Name, reason: "Static is a visible upper bound for every region.")).ToArray();
        var constraintCount = (long)problem.Assumptions.Length + problem.Requirements.Length + implicitEdges.Length +
            problem.AssumedTypeBounds.Length + problem.RequiredTypeBounds.Length;
        if (constraintCount > options.MaxConstraints) return HigherRankedRegionSolution.Incomplete("outlives/type-constraint count", 0);
        var budget = new Budget(options, cancellationToken);
        try
        {
            var rigid = new HashSet<string>(problem.RigidRegions.Select(region => region.Name), StringComparer.Ordinal);
            var assumed = budget.Solve(new RegionProblem(rigid, problem.Assumptions.Concat(implicitEdges.Where(edge => rigid.Contains(edge.Shorter)))));
            var allNames = problem.Regions.Select(region => region.Name).ToArray();
            var combinedEdges = problem.Assumptions.Concat(problem.Requirements).Concat(implicitEdges).ToList();
            var closure = budget.Solve(new RegionProblem(allNames, combinedEdges));
            var promotions = ImmutableArray.CreateBuilder<RegionUniversePromotion>();
            var byName = problem.Regions.ToDictionary(region => region.Name, StringComparer.Ordinal);
            // Mirror rustc's no-placeholder fast path: ordinary generic variance/type bounds do
            // not need the quadratic universe scan when no higher-ranked binder was entered.
            var components = problem.Regions.Any(region => region.Kind == RegionVariableKind.Placeholder)
                ? closure.Components : ImmutableArray<RegionComponent>.Empty;
            foreach (var component in components)
            {
                budget.Step();
                // RegionTracker in rustc intersects names across all reachable regions. Even
                // an older empty existential lowers the universe of its predecessors.
                var variable = component.Members.Select(name => byName[name])
                    .OrderBy(region => region.Kind == RegionVariableKind.Universal ? 0 : region.Kind == RegionVariableKind.Placeholder ? 1 : 2)
                    .ThenBy(region => region.Name, StringComparer.Ordinal).First();
                if (closure.Outlives(variable.Name, problem.StaticRegion)) continue;
                var visible = variable.Binder;
                var reachable = new List<RegionVariable>();
                foreach (var region in problem.Regions)
                {
                    budget.Step();
                    if (!closure.Outlives(variable.Name, region.Name)) continue;
                    visible = problem.CommonBinder(visible, region.Binder);
                    reachable.Add(region);
                }
                foreach (var placeholder in reachable)
                {
                    budget.Step();
                    if (placeholder.Kind != RegionVariableKind.Placeholder || problem.CanName(visible, placeholder.Binder)) continue;
                    if (++constraintCount > options.MaxConstraints) throw new Exhausted("outlives/type-constraint count");
                    var proof = closure.GetProof(variable.Name, placeholder.Name)!;
                    var visibilityBound = reachable.First(region => !problem.CanName(region.Binder, placeholder.Binder));
                    var upper = new RegionConstraint(variable.Name, problem.StaticRegion,
                        reason: "Effective universe '" + visible + "' cannot name placeholder '" + placeholder.Name + "' from '" + placeholder.Binder + "'; use the static upper bound.");
                    promotions.Add(new RegionUniversePromotion(variable, placeholder, proof, visibilityBound,
                        closure.GetProof(variable.Name, visibilityBound.Name)!, visible, upper));
                    combinedEdges.Add(upper);
                    break;
                }
            }
            // One second closure suffices: any newly affected predecessor already reaches the
            // promoted variable, hence reaches static without another promotion round.
            if (promotions.Count != 0) closure = budget.Solve(new RegionProblem(allNames, combinedEdges));
            var failures = ImmutableArray.CreateBuilder<UniversalRegionFailure>();
            foreach (var longer in problem.RigidRegions)
            foreach (var shorter in problem.RigidRegions)
            {
                budget.Step();
                if (closure.Outlives(longer.Name, shorter.Name) && !assumed.Outlives(longer.Name, shorter.Name))
                    failures.Add(new UniversalRegionFailure(longer.Name, shorter.Name, closure.GetProof(longer.Name, shorter.Name)!));
            }

            var bounds = problem.AssumedTypeBounds.GroupBy(bound => bound.Type, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            var typeFailures = ImmutableArray.CreateBuilder<TypeOutlivesFailure>();
            foreach (var requirement in problem.RequiredTypeBounds)
            foreach (var region in problem.RigidRegions)
            {
                budget.Step();
                if (!closure.Outlives(requirement.Region, region.Name)) continue;
                var satisfied = false;
                if (bounds.TryGetValue(requirement.Type, out var candidates))
                    foreach (var bound in candidates)
                    {
                        budget.Step();
                        if (!assumed.Outlives(bound.Region, region.Name)) continue;
                        satisfied = true;
                        break;
                    }
                if (!satisfied) typeFailures.Add(new TypeOutlivesFailure(requirement, region.Name, closure.GetProof(requirement.Region, region.Name)!));
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new HigherRankedRegionSolution(true, null, budget.Steps, failures.ToImmutable(), promotions.ToImmutable(),
                typeFailures.ToImmutable(), assumed, closure);
        }
        catch (Exhausted exhausted)
        {
            return HigherRankedRegionSolution.Incomplete(exhausted.Reason, budget.Steps);
        }
    }

    private sealed class Budget
    {
        private readonly RegionSolverOptions options;
        private readonly CancellationToken cancellationToken;
        private readonly RegionConstraintSolver solver = new();
        internal Budget(RegionSolverOptions options, CancellationToken cancellationToken)
        { this.options = options; this.cancellationToken = cancellationToken; }
        internal int Steps { get; private set; }
        internal void Step()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Steps == options.MaxSteps) throw new Exhausted("higher-ranked region-solving steps");
            Steps++;
        }
        internal RegionSolution Solve(RegionProblem problem)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Steps == options.MaxSteps) throw new Exhausted("higher-ranked region-solving steps");
            var solved = solver.Solve(problem, new RegionSolverOptions(options.MaxRegions, options.MaxConstraints, options.MaxSteps - Steps), cancellationToken);
            Steps += solved.Steps;
            if (!solved.IsComplete) throw new Exhausted(solved.BudgetReason!);
            return solved;
        }
    }

    private sealed class Exhausted : Exception
    {
        internal Exhausted(string reason) => Reason = reason;
        internal string Reason { get; }
    }
}
