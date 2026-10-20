# Source summaries, exceptional flow, and reference storage

SafeSharp infers caller-visible ownership and returned-source facts from source bodies when the
callee implementation is statically known. Explicit signatures remain authoritative. Virtual,
recursive, unavailable, unsupported, and budget-limited bodies never acquire an invented proof.

## Value snapshots and storage locations

The source solver has separate finite domains for values, reference targets, and asynchronous
payloads. Each parameter, local, and Roslyn flow capture receives an integer slot. A by-value
argument snapshots its origin when evaluated; a `ref`, `in`, or `out` argument snapshots the
location it denotes at that point. Later argument expressions can change a ref local's binding
without changing an earlier argument's saved target.

A source-proven `ref` or `ref readonly` return exports parameter location ordinals, together with
explicit external-storage and unknown-storage flags. Returning a conditional ref unions every
possible location. Pure ref-identity helpers preserve the caller's stored value. Writable calls
apply their normal-return postconditions simultaneously, because formal parameter order does not
specify write order when two actual arguments alias the same location. A write through one C#
reference parameter may also modify another reference parameter that aliases it. Normal ref-output
origins compose through source calls: reference arguments select their addresses during argument
evaluation, then snapshot their incoming values at call entry. A callee can return an earlier
by-value snapshot even when it subsequently replaces the referenced cell.

```csharp
static ref Resource Identity(ref Resource value) => ref value;

static Resource Factory(Resource external)
{
    var value = external;
    ref var alias = ref Identity(ref value);
    alias = new Resource();
    return value; // A source-proven fresh normal return.
}
```

An external field, array element, or unmodeled ref-return location cannot establish ownership of a
local value. An unknown reference target cannot establish a replacement proof. Readonly refs
preserve the same storage provenance; C# continues to enforce readonly writes and ref escape
legality independently.

## Normal and exceptional continuations

`SourceControlFlowRouter` expands Roslyn's protected regions into bounded continuation contexts.
Both source summaries and lifetime-return verification use the router. It follows the same
exception-routing rules as the ownership graph normalizer:

1. Potentially throwing operations supply their evaluation-point state to the router.
2. Exception filters run before unwinding; a throwing or rejecting filter proceeds to later
   handlers with the filter's observable effects retained.
3. Selected-handler cleanup runs from inner to outer finally regions before handler entry.
4. A suspended normal return resumes only after its finally blocks complete normally.
5. An unhandled exceptional path never contributes an owned-return or owned-out postcondition.

A call's owned-out promise applies only after normal completion. If the call throws after partially
writing its arguments, the exceptional state treats writable ref/out locations as unknown. Typed
throws refine compatible catches. An implicit exception has an unknown type unless more precise
information is available, so the analysis retains every possible compatible handler.

An ordinary return operand is a value snapshot. Reassigning its local during cleanup does not
replace the value that C# already captured for return:

```csharp
static Resource Factory(Resource external)
{
    var value = new Resource();
    try { return value; }
    finally { value = external; }
}
```

A `ref` or `ref readonly` return instead captures a storage location. Cleanup can replace the
value stored in that location; inference and lifetime checking read that cell at normal exit.
Rebinding the ref local used by the return does not redirect the already captured location.
The ownership engine uses the same distinction through its
[reference-storage domain](reference-storage-analysis.md).

Disposing the captured object is different. Allocations carry finite site identities, so disposal
through another alias invalidates the returned origin. `Ownership.Move` changes ownership
capability without changing object identity; disposal through an old C# alias still invalidates a
moved return. Conditional disposal invalidates the proof if any normal completion disposes it.

```csharp
static Resource InvalidFactory()
{
    var value = new Resource();
    var alias = value;
    try { return Ownership.Move(value); }
    finally { alias.Dispose(); } // No inferred owned result survives this cleanup.
}
```

Lifetime diagnostics validate the captured result at normal exit, after cleanup. A parameter
reassignment does not alter an already evaluated return; disposal or consumption of that result
removes its lifetime proof. Handler-local facts come from actual incoming exceptional states,
replacing the former method-wide handler approximation.

## Compositional asynchronous results

`MethodReturnOrigin` describes a terminal provenance atom beneath zero to sixteen constructed
carrier layers. The terminal is a parameter value, receiver, fresh owner, null, or unknown value.
A parameter value can itself be a carrier and retains its existing identity.

| Source result | Exported shape |
| --- | --- |
| `value` | parameter 0, depth 0 |
| `Task.FromResult(value)` | parameter 0, depth 1 |
| `Task.FromResult(Task.FromResult(value))` | parameter 0, depth 2 |
| `Task.FromResult(inputTask)` | parameter 0 (the existing task), depth 1 |
| `Task.FromResult<Resource>(null)` | null, depth 1 |
| a null task | null, depth 0 |

The source solver reconstructs these layers across statically known calls. Lifetime-return and
borrowed-escape analyses consume the same metadata. Allocating a task alone never establishes
ownership of its eventual result. The compatibility `ReturnsAsyncPayload` projection describes
one effective-result layer; deeper structures require the compositional metadata.

## Bounds and performance

The source solver uses indexed storage slots, cached singleton target sets, immutable origin
unions, an indexed continuation worklist, and allocation-site carrier identities. Continuations
are interned; exception plans are cached by context and exception kind. Loops reuse finite sites
instead of constructing growing origin strings. Source inference is bounded to 100,000 transfer
steps, 8,192 worklist iterations, 4,096 continuation contexts, expression depth 128, source call
depth 24, and carrier export depth 16. Exceeding a bound yields an opaque source summary, not a
partial ownership proof. The lifetime analyzer uses its configured step/depth budget and reports
an explicit incomplete-analysis diagnostic.

These are finite may-origin facts, not whole-program object-identity or dynamic execution proofs.
Reflection, dynamic dispatch without trusted contracts, native/function-pointer mutation,
arbitrary external storage, and unknown callback effects remain conservative. Recursive carrier
graphs stop at an unknown node. Allocation-site merging across loops may lose precision but cannot
turn a possibly invalidated origin into an owned or lifetime-valid postcondition.

## Rust design references

This is an original C# implementation, with .NET exception, aliasing, and asynchronous semantics.
It adopts the separation of places, values, initialization, and cleanup used in Rust's compiler;
it does not import Rust runtime drop behavior into C#.

Pinned Rust 1.91.0 sources:

- [MIR move paths](https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_mir_dataflow/src/move_paths/mod.rs)
  motivate indexed storage paths and separate alias/value domains.
- [Initialization dataflow](https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_mir_dataflow/src/impls/initialized.rs)
  motivates explicit transfer effects and conservative joins.
- [Drop elaboration](https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_mir_transform/src/elaborate_drops.rs)
  motivates explicit cleanup continuations and unwind edges.
- [Future::Output](https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/library/core/src/future/future.rs)
  motivates the distinction between a carrier and its result.

The executable regression suites are `ReferenceOriginSummaryTests`,
`ExceptionalOriginSummaryTests`, `LifetimeContractAnalyzerTests`, and
`ExceptionalLifetimeReturnTests`, and `ReferenceStorageOwnershipTests`. They compile valid fixtures with Roslyn before running the real
analyzers, and include both proofs that must be accepted and stale proofs that must be rejected.
