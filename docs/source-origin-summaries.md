# Source origin summaries

`MethodSummaryProvider` uses explicit ownership and lifetime contracts first. For eligible source methods, `MethodOriginAnalysis` proves a finite set of possible origins for a returned value and for each caller-visible `out` slot. The ownership engine then applies those boundary summaries and independently checks disposal, moves, loans, and lifetime validity.

An inferred fresh result means that every represented normal-return alternative is a fresh tracked allocation or `null`, with at least one fresh alternative. It does not mean that arbitrary execution succeeds, that disposal cannot throw, or that an allocation remains valid after unsupported external effects. A borrowed result must contain only named input or receiver origins, optionally with `null`. An unknown origin on any alternative prevents either proof.

## Values and storage are separate

C# copies a reference value when assigning it to an ordinary local. A `ref` local instead names a storage location whose contents can later change. The summary solver keeps these relationships in separate finite domains.

| State component | Meaning | Join |
| --- | --- | --- |
| Value origins | Fresh allocation, null, input ordinal, receiver, or unknown for a local/parameter/value capture | Union of possible origins; a missing predecessor slot contributes unknown |
| Reference targets | Possible storage slots selected by a `ref` local | Union of possible targets; a missing predecessor target contributes unknown storage |
| Capture targets | Storage selected when Roslyn captures an lvalue for later use | Union of saved targets, independently of the captured value |

Source symbols and Roslyn capture IDs are interned into integer slots once per analysis. Captured outer parameters are distinct symbols from local-function parameters even when their ordinals match.

```csharp
static Resource Create(Resource external)
{
    var result = external;
    ref Resource target = ref result;
    target = new Resource();
    return result;
}
```

The final assignment replaces `result` through its known storage alias, so this method establishes a fresh returned owner. The inverse assignment, replacing an initially fresh `result` with `external` through `target`, removes the fresh postcondition. A chain of ref locals resolves to the underlying storage; rebinding one ref does not rebind another ref that was previously copied from it.

A write to one definite target is a strong update. A conditional ref or control-flow join can select several targets; a write through that may-alias set weakly updates each candidate. This intentionally retains earlier origins of candidates that might not have been selected. The solver does not preserve correlations between a conditional choice and a later dereference, so some safe programs still need an explicit return contract.

Ref-local declarations, `ref readonly` reads, ref reassignment, conditional ref expressions, branches, loops, and aliases of ordinary parameter storage participate in the same fixed point. The analysis only establishes facts after convergence. It does not assume that a loop executes at least once unless the control-flow graph establishes that fact.

## Evaluation order and calls

Assignment target locations are saved before evaluating the right-hand side. Call receivers and ordinary arguments retain their evaluated values; writable argument locations are saved as their expressions are evaluated. Later arguments can reassign a local or rebind a ref without changing an earlier value or selected storage location.

```csharp
static Resource First(Resource value, Resource ignored) => value;

static Resource Create(Resource external)
{
    var result = new Resource();
    ref Resource target = ref result;
    return First(result, target = external);
}
```

The first argument still contains the fresh allocation, even though the second argument replaces `result`. The inferred return follows that evaluated first argument. Roslyn flow captures retain both their value snapshot and separate candidate storage targets so the same rule works when a later argument introduces branches.

Unknown `ref`/`out` effects invalidate the selected storage. A proven or explicitly owned output can establish a fresh replacement on normal return. The original contents are never treated as a postcondition of a writable argument.

Several writable actual arguments may refer to the same location. Their output effects are combined before updating the caller state: parameter-list order is not evidence of the callee's assignment order. A mixture of owned and opaque outputs to the same location therefore remains opaque. Two proven owned outputs to that same location still establish a fresh value.

Different C# `ref`, `in`, and `out` formal parameters may themselves alias at a call site. A write definitely replaces its selected formal and weakly updates the other potentially aliased formals. External field or element storage can also coincide with a caller's reference parameter. These may-alias effects are retained instead of assuming Rust's exclusive-reference rules apply to unrestricted C# code.

Property and indexer receiver/index evaluation can contain calls that replace reference arguments. Those effects are evaluated in source order. A property assignment evaluates its receiver and indices without invoking a getter. Getter summaries can return origins from the receiver or named index parameters.

## Captured variables and conservative boundaries

Nested functions and delegates can retain mutable captured cells. The solver gathers those cells and invalidates their current value origins at executable call or suspension boundaries. Already-evaluated receiver and ordinary-argument snapshots keep their original identities. Merely declaring a callback does not execute it. This conservative rule prevents a callback that replaces a local from leaving a stale fresh-return proof; it is not a complete callback mutation summary.

The following boundaries remain explicit:

- Bodies containing exception handlers or finalizers retain opaque inferred origins. Normal CFG edges alone cannot prove values returned by catches or replacements performed by cleanup continuations.
- Ref-returning methods, ref-return calls, and ref-return getters require explicit contracts. This domain does not infer interprocedural storage-return postconditions.
- Field, property, and array-element storage is external to the local origin map. The analysis does not infer a fresh transferable owner merely because a ref to an external field was assigned an allocation.
- Unknown target operations, dynamic invocation/construction/member access, function-pointer calls, and address-taking retain opaque summaries rather than silently preserving an earlier origin. Event accessor calls invalidate captured variable cells.
- Compound, deconstruction, and replacement-operator writes invalidate affected slots. A postfix result separately retains its pre-update value origin.
- General writable ref postconditions, whole-program aliasing, recursive/virtual dispatch, and exact callback mutation effects remain contract boundaries. Explicit contracts remain trusted at these boundaries.

Each source analysis has a 100,000-step work budget, a 4,096-block-visit cap, and a 128-level expression depth cap. Cancellation is checked during traversal, transfer, and joins. Exhaustion yields an opaque summary, never a partially inferred fresh or borrowed result. These are bounds for optional source inference; they do not change the ownership engine's configured limits.

## Rust design reference and implementation cost

The implementation applies Rust's separation between a place and its current initialization/value facts to Roslyn CFGs, while retaining C#'s additional writable aliasing possibilities. The design references are pinned to Rust 1.91.0, commit `f8297e351a40c1439a467bbbb6879088047f50b3`:

- [`rustc_mir_dataflow::move_paths`](https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_mir_dataflow/src/move_paths/mod.rs): indexed canonical place paths.
- [`rustc_mir_dataflow::impls::initialized`](https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_mir_dataflow/src/impls/initialized.rs): separate initialization dataflow facts and effects.
- [Rust Compiler Development Guide: MIR dataflow](https://rustc-dev-guide.rust-lang.org/mir/dataflow.html): finite joins, per-effect ordering, convergence, and call-return effects.

The C# code is an original implementation. No Rust source is copied. Integer slot lookup removes repeated symbol display formatting from transfer operations. Singleton target sets and common origin sets are reused; block queue membership uses a boolean array. Joins share immutable sets and only allocate a missing-key list when necessary. These choices keep the richer storage domain bounded without introducing syntax-text identity comparisons or whole-heap searches.

The [reference-origin regression tests](../tests/SafeSharp.Tests/Ownership/ReferenceOriginSummaryTests.cs) assert the inferred effect at the exact caller location, covering both accepted ownership/borrowing proofs and rejected stale postconditions. Existing [source-summary tests](../tests/SafeSharp.Tests/Ownership/MethodSummaryTests.cs) cover ordinary origins, operators, conversions, disposal summaries, and opaque boundaries.
