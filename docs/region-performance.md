# Compact region solving

Named lifetime solving separates reachability from the explanations emitted for diagnostics. `RegionConstraintSolver` interns region names and constraint endpoints once, discovers strongly connected components iteratively, and runs a deterministic breadth-first search from each region. Each search records one predecessor edge per reachable region. The resulting forest preserves the shortest original-constraint witness for every implication.

The architecture draws on the separation of indexed region solving and diagnostic explanation in [Rust 1.91.0's region inference implementation](https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_borrowck/src/region_infer/mod.rs). SafeSharp's code is an original C# adaptation for explicit named constraints. It does not reproduce Rust's program-point region domain or use its SCC value propagation as a substitute for shortest-path evidence.

## Work and storage

For `R` regions and `E` constraints, the solving phase uses `O(R * (R + E))` work and `O(R² + E)` storage. Names and endpoints are resolved before graph traversal; inner searches operate on integer arrays. A fixed-size queue is reused between searches.

The previous implementation eagerly expanded the complete path for every reachable pair. A chain has quadratically many implications and cubic total path length. The new solver stores the predecessor forests and expands a path only when `RegionProof.Steps` is requested. `Outlives` and `AreEquivalent` use name lookup followed by indexed reads; they do not create proofs or concatenated pair keys.

`GetProof` caches immutable witness objects. `Implications` remains the same public `ImmutableArray<RegionProof>` API and is constructed lazily in ordinal name order. Each proof's path is also lazy. Concurrent callers share safely published results. Requesting and expanding every path still costs the size of that output; the optimization removes that cost from ordinary compiler checks that need only a few explanations.

## Limits and evidence

The existing region count, constraint count and step limits still apply. Steps now measure graph construction, SCC traversal and predecessor search; they no longer count eagerly copied diagnostic-path edges. A completed solve guarantees complete reachability and complete predecessor forests. An exhausted solve exposes no components, implications or partial proofs. Individual paths contain at most `R - 1` edges; reflexivity has an empty witness and no invented constraint.

`RegionForestTests` checks every relation and exact shortest path against an independent breadth-first oracle on 120 seeded random graphs. It also checks concurrent proof queries and full enumeration, absence of invented unknown-region relations, and budget failure. A 256-region chain now fits the default 100,000-step budget while retaining its exact 255-edge endpoint witness. This is a regression about algorithmic work and evidence, not a wall-clock performance assertion.
