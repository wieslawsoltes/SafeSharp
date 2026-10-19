# Lifetime contracts on delegate conversions

SafeSharp checks an annotated delegate conversion for every assignment of lifetimes allowed by the delegate's signature. **SS0047** identifies a callback that cannot preserve that promise. This connects Roslyn method groups and anonymous functions to the reusable [universal region solver](universal-regions.md).

The implemented domain is one universal binder of explicit outlives constraints. It does not add lifetime parameters to CLR types or implement Rust's complete higher-ranked type and trait system. This check activates for delegate signatures with named lifetime regions or an owned return. A delegate with only `Borrowed`, `Consumes` or `DoesNotEscape` annotations, and an ordinary unannotated `Func<...>`, does not activate SS0047; those attributes retain their existing call-boundary meaning.

## Declare the callback promise

```csharp
using System.IO;
using SafeSharp;

[return: Lifetime("a"), Borrowed]
delegate MemoryStream Pick(
    [Borrowed, Lifetime("a")] MemoryStream first,
    [Borrowed, Lifetime("b")] MemoryStream second);

static class Callbacks
{
    [return: Lifetime("x"), Borrowed]
    public static MemoryStream First(
        [Borrowed, Lifetime("x")] MemoryStream first,
        [Borrowed, Lifetime("y")] MemoryStream second) => first;

    [return: Lifetime("y"), Borrowed]
    public static MemoryStream Second(
        [Borrowed, Lifetime("x")] MemoryStream first,
        [Borrowed, Lifetime("y")] MemoryStream second) => second;

    public static Pick Valid() => First;
    public static Pick Invalid() => Second; // SS0047 at Second.
}
```

`Pick` promises a result valid for its first input's region `a`. It allows the caller to choose an unrelated region `b` for the second input. `First` preserves that relationship even though its names are `x` and `y`. `Second` requires `b : a`, a relationship the delegate never promised. Both callback bodies are honest about their own return origins; the failed conversion is a separate contract violation.

Names are local to each signature. Matching spelling between a callback and its delegate is unnecessary. `[Outlives]` can also annotate the delegate declaration:

```csharp
[Outlives("b", "a")]
[return: Lifetime("a"), Borrowed]
delegate MemoryStream OrderedPick(
    [Borrowed, Lifetime("a")] MemoryStream first,
    [Borrowed, Lifetime("b")] MemoryStream second);
```

An `OrderedPick` conversion can use `Callbacks.Second`, because its callers explicitly promise `b : a`. A delegate has no implicit receiver region: `Lifetime("this")` and `Outlives("this", ...)` are invalid on its signature and receive SS0040.

## Input shortening, results and writable storage

The adapter gives the delegate's named regions fresh universal identities. The callback's names receive separate inference identities. It then records the following relationships:

| Boundary | Required relationship |
| --- | --- |
| Ordinary or readonly borrowed input | The supplied delegate region outlives the callback input region. |
| Borrowed result | The callback result region outlives the delegate's promised result region. |
| Writable `ref` or `out` slot | Both directions hold, making the storage relationship invariant. |
| Bound instance receiver or reduced extension receiver | A separate captured universal outlives the callback's receiver/input region. |
| Delegate `[Outlives]` | An assumption available to all conversions of this delegate type. |
| Callback `[Outlives]` | A requirement that must remain compatible with every permitted caller lifetime. |

Two independent readonly inputs may both shorten to a common callback region if that callback returns no borrowed value. If it returns the common region as the first input's full lifetime, the second input would need to outlive the first. That conversion requires a corresponding delegate assumption. Writable slots need both directions because the callback can replace the caller's value.

Captured receiver storage remains independent of later delegate inputs. Binding `reader.View` or `receiver.Extension` cannot establish that a borrow into that captured object lasts as long as any future input. A bound method that returns its ordinary input can satisfy the contract; one that returns its unrelated receiver cannot. SafeSharp models this relationship without guessing a runtime delegate object's lifetime.

Inherited interface and override contracts are used by position. Generic methods and delegates are checked after Roslyn resolves the conversion. This check does not replace the C# compiler's type compatibility and variance rules.

## Ownership and borrowing modes

Lifetime relationships do not erase parameter behavior. The conversion also checks these explicit restrictions:

- An input's `Owned`/`Consumes` transfer mode must be preserved.
- A callback cannot require `BorrowedMut` where the delegate supplies only a shared borrow.
- A callback accepting a delegate's `Borrowed` or `BorrowedMut` input must itself declare a borrowing mode. A shared callback can accept an exclusive input.
- A delegate's `DoesNotEscape` guarantee must also occur on the callback parameter.
- A borrowed result must preserve the promised shared/exclusive return mode.
- An owned-result delegate needs an explicit or proven source owned-result contract; an owned result cannot replace a borrowed-result promise.

Explicit contracts are trusted descriptions of behavior, subject to the analyzer's separate body checks. Conversion analysis does not establish arbitrary synchronization, external retention, disposal exceptions or every ownership postcondition of the callback.

## Anonymous functions and source inference

A lambda without a declared result contract is checked using its actual Roslyn control-flow graph under the target delegate's region promise:

```csharp
static Pick GoodLambda() =>
    ([Borrowed] MemoryStream first, [Borrowed] MemoryStream second) => first;

static Pick BadLambda() =>
    ([Borrowed] MemoryStream first, [Borrowed] MemoryStream second) => second;
    // SS0047 points to the returned second expression.
```

Aliases, conditional returns, loops, local reference aliases and supported method summaries use the existing returned-provenance analysis. Lambda graphs in fields, properties, local functions and other lambdas are handled independently. Fresh local resources, unrelated captures and unknown origins do not satisfy a borrowed-input lifetime.

A shared input, `Ownership.Borrow` result or explicitly shared helper result cannot be upgraded into an exclusive returned loan by the target delegate. This restriction survives aliases and supported asynchronous wrapping. Input mode annotations are not silently inherited by lambda parameters. Write `[Borrowed]` and `[DoesNotEscape]` where the delegate requires them. An owned-result anonymous function currently needs an explicit `[return: Owned]` contract. A source method group can instead establish an owned result through the bounded source-origin summary. An already typed delegate retains the contract supplied at its creation or by its external producer; converting between distinct annotated delegate types compares their signatures.

For `Task<T>` and `ValueTask<T>`, a result lifetime describes the payload `T`. Explicit result contracts, actual async returns and supported completed producers can establish that payload's origin. Merely returning a `Task<T>` parameter describes the carrier and cannot prove a lifetime for `T`. Unmodeled task combinators and custom awaitables remain opaque. See [source-origin summaries](source-origin-summaries.md) and [async-result ownership](async-result-ownership.md).

## Diagnostics and graphs

SS0047 normally points to the incompatible method group or delegate conversion. For an anonymous body checked under the target promise, it points to the invalid returned expression. Additional locations retain the delegate declarations, callback contract and relevant source origins.

Universal comparison failures include:

| Property | Meaning |
| --- | --- |
| `SafeSharp.DelegateType` | Target delegate type. |
| `SafeSharp.RegionNames` | Generated universal and inference identities. |
| `SafeSharp.RegionBindings` | Mapping from generated identities to signature regions, inputs and captures. |
| `SafeSharp.RegionConstraints` | Assumed delegate outlives edges. |
| `SafeSharp.RegionRequirements` | Exact input, result, storage and callback requirement edges. |
| `SafeSharp.RegionRequirementReasons` | Human-readable reason for each required relationship. |
| `SafeSharp.SourceRegions`, `SafeSharp.RequiredRegion` | The unpromised universal relationship. |
| `SafeSharp.RegionProof` | Deterministic shortest path through original edges explaining that relationship. |
| `SafeSharp.RegionQuantification` | `one universal delegate binder`. |

HTML, DOT and Mermaid distinguish solid assumption edges from dashed requirement edges. They draw the supplied edges; a proof string never invents an extra graph relationship. JSON and SARIF retain the full diagnostic properties. Body-origin and ownership-mode failures supply the evidence relevant to their own check and need not contain a universal graph.

The existing `safesharp_max_regions`, `safesharp_max_region_constraints` and `safesharp_max_region_steps` options bound the analysis. The adapter checks combined name and edge limits before inserting new graph entries, rejects an oversized input-edge lower bound before allocating its input map, and observes cancellation during construction. A universal solve shares one step budget across the assumed closure, combined closure and universal-pair comparison. Exhausted region work or unavailable anonymous-function control flow emits SS0043; it does not accept a conversion using a partial proof. Source-summary inference has a separate fixed bound: an opaque or exhausted source summary supplies no inferred provenance and can lead to SS0047, rather than a region-budget diagnostic. Region graph previews have their own bounded presentation limits and retain diagnostic metadata when a graph cannot be drawn.

The [computed example report](examples/async-delegates.html) combines an invalid method-group conversion, an invalid lambda, and stored async-result diagnostics. Its [source fixture](../samples/AsyncDelegateExamples.cs.txt) also includes accepted conversions and correct disposal for comparison.

## Rust design and verification

This is an original C# adaptation of Rust 1.91.0's [universal-region validation](https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_borrowck/src/region_infer/mod.rs). The relevant rule checks that propagation does not add a relation between caller-supplied universal regions beyond the known assumptions. The [Core API guide](universal-regions.md) gives the exact graph condition and reusable example.

Nested universes, placeholder leak checks, implied type bounds, lifetime relationships inside arbitrary generic type constructors, full Rust higher-ranked trait solving and automatic closure lifetime generalization are not implemented. The [delegate regression suite](../tests/SafeSharp.Tests/LifetimeContracts/LifetimeDelegateTests.cs) checks accepted and rejected conversions, exact spans, inherited and generic signatures, reduced extensions, captures, asynchronous payloads, budgets, suppression and concurrent deterministic evidence. The [Core universal tests](../tests/SafeSharp.Tests/Regions/UniversalRegionSolverTests.cs) independently exercise the relation domain and total budget.
