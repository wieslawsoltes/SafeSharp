# Universal region compatibility

The reusable `SafeSharp.Core.Regions.UniversalRegionSolver` checks one binder of universally quantified outlives constraints. A caller declares the universal region names and the relations that are already assumed. Fresh inference names describe the candidate implementation or conversion. Required edges may constrain those inference names, but they must not establish an additional relation between universal regions that the caller never promised.

This API is the first-order outlives portion of a universally quantified signature check. It retains its original one-binder semantics. The separate [higher-ranked region API](higher-ranked-regions.md) adds lexical universes, placeholders, visibility/leak checking and type-outlives obligations; [TypeLifetime contracts](type-lifetimes.md) connect that API to nested Roslyn delegate/type paths and declared variance. Neither API reproduces arbitrary Rust trait/type solving.

## The compatibility condition

The solver computes two closures:

1. The transitive/reflexive closure of the caller's assumed universal relations.
2. The closure of those assumptions plus every requirement over universal and inference names.

For each ordered universal pair `longer`, `shorter`, a relation in the second closure must also occur in the first. If a requirement path adds `longer : shorter`, the result reports a `UniversalRegionFailure` carrying that pair and a shortest witness of the original edges that produced it. Inference-only cycles are valid; they do not by themselves require a new universal relation.

Two unrelated input lifetimes can both outlive a common, shorter inference region:

| Required edge | Interpretation |
| --- | --- |
| `U_a : I_shared` | The first input can be shortened to the common region |
| `U_b : I_shared` | The second input can be shortened to the same region |

These requirements do not relate `U_a` and `U_b`, so they are compatible. If the result must satisfy `I_shared : U_a`, the combined closure now implies `U_b : U_a`. Unless the target signature already assumes that relation, the conversion is incompatible. The witness is `U_b : I_shared`, then `I_shared : U_a`.

Keeping separate inference regions for independent inputs avoids that extra relation. A candidate that returns only its first input can use `U_a : I_first`, `U_b : I_second`, and `I_first : U_a` without relating `U_b` to `U_a`.

An adapter can encode immutable borrowed inputs using shortening edges and returned borrows using covariance edges. Writable `ref`/`out` storage adds reverse edges to enforce invariance where the language requires it. Captured receiver storage can be represented by a separate universal region; it does not acquire an arbitrary relation to each lifetime supplied by a future delegate caller.

## Public Core API

```csharp
using SafeSharp.Core;
using SafeSharp.Core.Regions;

var target = new RegionProblem(new[] { "U_a", "U_b" });
var problem = new UniversalRegionProblem(
    target,
    new[] { "I_shared" },
    new[]
    {
        new RegionConstraint("U_a", "I_shared",
            new SourceSite("Example.cs", 10, 5), "first borrowed input"),
        new RegionConstraint("U_b", "I_shared",
            new SourceSite("Example.cs", 20, 5), "second borrowed input"),
        new RegionConstraint("I_shared", "U_a",
            new SourceSite("Example.cs", 30, 5), "returned lifetime")
    });

var result = new UniversalRegionSolver().Solve(problem);
if (!result.IsComplete)
{
    // Treat this as an incomplete analysis; no partial compatibility proof is exposed.
    Console.WriteLine(result.BudgetReason);
}
else if (!result.IsCompatible)
{
    foreach (var failure in result.Failures)
        Console.WriteLine($"Required but unassumed: {failure.Longer} : {failure.Shorter}");
}
```

`UniversalRegionProblem` snapshots the input collections, validates every edge endpoint, sorts names ordinally, and rejects overlap between universal and inference names. Duplicate declarations are canonicalized. `Assumptions`, `InferenceRegions`, `Requirements`, and `CombinedProblem` expose immutable input data.

`UniversalRegionSolution` exposes `IsComplete`, `IsCompatible`, `Steps`, `BudgetReason`, and deterministic `Failures`. A complete result also exposes `AssumedClosure` and `Closure`, which support the ordinary Core region queries and report graphs. Every failure has `Longer`, `Shorter`, and `Proof`; proof steps preserve source sites and reasons from both assumptions and requirements.

An incomplete result has `IsCompatible == false`, empty failures, and null closures. Consumers must check completeness before interpreting missing failures. This prevents a budget cutoff after the first few checked pairs from being mistaken for a valid signature.

## Bounds, determinism, and cost

The existing `RegionSolverOptions` limits apply to the combined region and edge counts. One `MaxSteps` budget covers the assumed solve, the combined solve, and one comparison step per ordered pair of universal regions. Neither solve receives a fresh independent total budget. Cancellation is checked before solving, between solves, and throughout pair comparison; the underlying solver checks cancellation during its graph work.

Both closures use the compact indexed region solver. Pair checks query reachability without expanding proof paths. A shortest original-edge witness is requested only for a missing relation, and its full path is materialized lazily. Names, constraints, and failures have deterministic ordinal ordering. The solver has no mutable per-run fields and is safe to reuse concurrently; completed proof objects support concurrent readers.

## Rust source reference

The implementation adapts the universal-region validation rule from Rust 1.91.0, commit `f8297e351a40c1439a467bbbb6879088047f50b3`. It is original C# code and does not copy Rust implementation text.

- [`RegionInferenceContext::check_universal_regions`](https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_borrowck/src/region_infer/mod.rs#L1028) checks universal regions after propagation.
- [`check_universal_region` and `check_universal_region_relation`](https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_borrowck/src/region_infer/mod.rs#L1174) compare inferred universal outlives relations with the known relation set.
- The [Rust Compiler Development Guide's inference overview](https://rustc-dev-guide.rust-lang.org/borrow-check/region-inference.html#inference-overview) explains why a universal region must not gain an unpromised caller lifetime during inference.

The [Core universal-region tests](../tests/SafeSharp.Tests/Regions/UniversalRegionSolverTests.cs) cover input intersections, result constraints, invariance, captured universals, cycles, alpha-renaming, shortest evidence, stable ordering, immutable snapshots, combined and total budgets, cancellation, and concurrent reuse.
