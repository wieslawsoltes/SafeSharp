# Higher-ranked region engine

`HigherRankedRegionSolver` extends the existing public named-region APIs with explicit lexical binders, universe visibility, rigid placeholders, and type-outlives obligations. `RegionConstraintSolver` and `UniversalRegionSolver` retain their existing behavior and public surface.

This is an original C# implementation inspired by the [Rust compiler's region inference implementation at the pinned revision](https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_borrowck/src/region_infer/mod.rs) and its documented [placeholder/universe model](https://rustc-dev-guide.rust-lang.org/borrow-check/region-inference/placeholders-and-universes.html). The C# implementation uses the existing compact indexed outlives closure and lazy shortest-path evidence. It does not embed Rust code or run a Rust compiler.

## Public problem representation

| Type | Meaning |
| --- | --- |
| `RegionBinder` | A named lexical universe with an optional parent. A problem has exactly one root. |
| `RegionVariable` | A globally unique region identity, its binder, and its quantification. |
| `RegionVariableKind.Universal` | A free arbitrary region in the root universe. |
| `RegionVariableKind.Placeholder` | An arbitrary region introduced by a quantified binder. |
| `RegionVariableKind.Inference` | A region selected by inference within its binder's visibility. |
| `RegionVariableKind.Static` | The root-visible upper bound that outlives all other regions. |
| `TypeOutlivesConstraint` | An explicit opaque type identity and a region its borrowed contents must outlive. |
| `HigherRankedRegionProblem` | Immutable binders, variables, assumed relations, requirements, and type bounds. |

The problem constructor snapshots all inputs and rejects missing parents, duplicate identities, cyclic binder trees, undeclared endpoints, and assumptions about existential variables. If the caller omits a static variable, the problem adds a fresh `$static` identity; a user region with that name remains distinct.

Names are stable external identities. Internally the binder tree is traversed without recursion and assigned entry/exit indices, so `CanName(observer, owner)` is a constant-time ancestry query after dictionary lookup. A universe can name its own placeholders and those of its ancestors. Sibling universes are isolated. Rust can represent visibility with a monotonic counter because its inference-context discipline prevents sibling communication; SafeSharp's reusable public API accepts arbitrary trees and therefore checks actual ancestry.

```csharp
var problem = new HigherRankedRegionProblem(
    new[] { new RegionBinder("outer"), new RegionBinder("callback", "outer") },
    new[]
    {
        new RegionVariable("owner", "outer", RegionVariableKind.Universal),
        new RegionVariable("capture", "outer", RegionVariableKind.Inference),
        new RegionVariable("call", "callback", RegionVariableKind.Placeholder),
    },
    requirements: new[]
    {
        new RegionConstraint("owner", "capture", reason: "Captured storage comes from owner."),
        new RegionConstraint("capture", "call", reason: "Callback result claims any caller lifetime."),
    });

var result = new HigherRankedRegionSolver().Solve(problem);
// Complete and incompatible: an outer capture cannot acquire an arbitrary inner lifetime.
```

## Solving and leak checking

The solver closes the caller assumptions and the combined requirements under one total step budget. Static upper-bound edges are explicit solver-generated constraints. For each strongly connected component, it intersects the visibility of every reachable region and checks its reachable placeholders. This follows the pinned compiler's [`RegionTracker` and placeholder lowering](https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_borrowck/src/handle_placeholders.rs). Even an older universe's empty inference region restricts a predecessor's available names. If a required placeholder cannot be named, the solver adds a visible static upper bound and preserves the paths that caused the promotion.

Requiring an older inference variable to outlive a younger placeholder is not itself an error: an unconstrained inference variable can choose static. The error arises when a rigid finite region must also outlive that variable. After promotions, the solver closes the graph again and checks every rigid relation against the caller's assumptions. A newly required relation is a failure. This distinction permits common shortened input regions and valid static choices while rejecting placeholder equality that would escape a binder.

Only one additional closure is necessary. When a promotion makes an inference variable reach static, every predecessor reaches static through that same variable. Repeating universe-promotion passes would add redundant edges.

Problems without placeholders skip the universe scan entirely, following the pinned Rust lowering implementation's fast path. Ordinary type variance and root type-outlives tests therefore do not pay for nested-universe processing.

`RegionUniversePromotion` records the component's representative, invisible placeholder, effective binder, the reachable region restricting visibility, both original shortest paths, and the generated upper-bound edge. `UniversalRegionFailure` retains the final shortest proof. Generated edges have explicit reasons and no fabricated source location. The original paths' caller-supplied `RegionConstraint` objects and `SourceSite` evidence are preserved.

## Type-outlives obligations

A required `T : r` is checked against each rigid region that `r` must contain. A caller-assumed `T : a` discharges that requirement when the caller's assumed closure proves `a : r`. Callback requirements do not manufacture caller assumptions. Distinct type identities do not share bounds.

An unconstrained inference region may remain empty, so `T : inferred` does not require a caller bound until the region acquires a rigid lower bound. A required static lifetime needs a sufficient static type bound. Missing bounds are reported as `TypeOutlivesFailure`, retaining both the exact type requirement and its region witness.

The type identity is intentionally opaque to Core. An adapter must decide how its language decomposes concrete types and supplies implied bounds. Core does not infer that arbitrary CLR generic arguments contain borrowed references, implement trait resolution, or solve higher-kinded type equations.

## Limits, determinism, and validation

`RegionSolverOptions` bounds the region/universe count, combined explicit and implicit constraints, type bounds, and total work across closures, promotion scans, rigid comparisons, and type tests. An incomplete result has `IsCompatible == false` and exposes no partial closures, failures, or promotions. Cancellation throws rather than returning an incomplete proof.

Input order does not affect the sorted identities, chosen first promotion, or shortest-path ordering. Problem and result snapshots can be shared by concurrent readers. The main retained graph representation remains quadratic in region count with linear edge storage, rather than retaining every expanded path. Binder entry/exit indexing is linear and uses no recursive descent. A binary ancestor table adds `O(B log B)` storage for `B` binders and computes the intersection of two visibility sets in `O(log B)` time.

The [focused tests](../tests/SafeSharp.Tests/Regions/HigherRankedRegionSolverTests.cs) cover nested and sibling visibility, legal shortening, visible and invisible inference, exact source evidence, promotion through predecessors, static bounds, independent type identities, total budgets, cancellation, concurrent reuse, input snapshots, invalid domains, and deeply nested binder trees. This finite region/type-obligation engine does not claim complete Rust trait solving, MIR point inference, automatic closure generalization, or a proof of arbitrary C# runtime behavior.
