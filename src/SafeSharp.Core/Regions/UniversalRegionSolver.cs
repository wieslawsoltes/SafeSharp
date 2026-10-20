using System;
using System.Collections.Immutable;
using System.Threading;

namespace SafeSharp.Core.Regions;

/// <summary>
/// Checks whether solving existential outlives requirements imposes an unassumed relation between
/// universal regions. Inference-only cycles and common shortened input regions are permitted.
/// </summary>
/// <remarks>
/// Original C# implementation of Rust's universal-region "too big" check, using the existing compact
/// indexed outlives solver and its lazy shortest original-edge witnesses:
/// https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_borrowck/src/region_infer/mod.rs#L1028
/// https://rustc-dev-guide.rust-lang.org/borrow-check/region-inference.html#inference-overview
/// Only a single binder of outlives constraints is modeled. Nested universes, placeholder leak
/// checks, trait solving, and full Rust higher-ranked subtyping are outside this domain.
/// </remarks>
public sealed class UniversalRegionSolver
{
    private readonly RegionConstraintSolver solver = new();

    public UniversalRegionSolution Solve(UniversalRegionProblem problem, RegionSolverOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (problem is null) throw new ArgumentNullException(nameof(problem));
        options ??= new RegionSolverOptions();
        cancellationToken.ThrowIfCancellationRequested();
        if (problem.CombinedProblem.Regions.Length > options.MaxRegions)
            return UniversalRegionSolution.Incomplete("named-region count", 0);
        if (problem.CombinedProblem.Constraints.Length > options.MaxConstraints)
            return UniversalRegionSolution.Incomplete("outlives-constraint count", 0);

        var assumed = solver.Solve(problem.Assumptions, options, cancellationToken);
        var steps = assumed.Steps;
        if (!assumed.IsComplete) return UniversalRegionSolution.Incomplete(assumed.BudgetReason!, steps);
        cancellationToken.ThrowIfCancellationRequested();
        if (steps == options.MaxSteps) return UniversalRegionSolution.Incomplete("region-solving steps", steps);

        // The two solves share one total budget. Resetting MaxSteps for the combined graph would
        // allow an ostensibly bounded compatibility check to spend twice its configured limit.
        var remaining = new RegionSolverOptions(options.MaxRegions, options.MaxConstraints, options.MaxSteps - steps);
        var closure = solver.Solve(problem.CombinedProblem, remaining, cancellationToken);
        steps += closure.Steps;
        if (!closure.IsComplete) return UniversalRegionSolution.Incomplete(closure.BudgetReason!, steps);

        var failures = ImmutableArray.CreateBuilder<UniversalRegionFailure>();
        foreach (var longer in problem.UniversalRegions)
        foreach (var shorter in problem.UniversalRegions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (steps == options.MaxSteps)
                return UniversalRegionSolution.Incomplete("universal-region comparison steps", steps);
            steps++;
            if (closure.Outlives(longer, shorter) && !assumed.Outlives(longer, shorter))
                failures.Add(new UniversalRegionFailure(longer, shorter, closure.GetProof(longer, shorter)!));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new UniversalRegionSolution(true, null, steps, failures.ToImmutable(), assumed, closure);
    }
}
