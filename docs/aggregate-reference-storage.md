# Canonical aggregate element references

SafeSharp represents a reference to a tracked array element as a reference to the
array's canonical element cell. Ordinary `array[index]` access and `ref`, `in`,
`out`, ref-local, and source-proven ref-return access use the same resource and
ValueTask consumption identities. Taking an element's address no longer weakens
an otherwise exact array merely because the address was exposed.

```csharp
var values = new[] { new MemoryStream() };
ref MemoryStream element = ref values[0];
var original = element;

element = new MemoryStream();
original.Dispose();       // Releases the value copied before replacement.
values[0].Dispose();      // Releases the new value written through element.
```

Disposing through either access path updates the same resource. Moving through a
reference invalidates existing value aliases. A `ValueTask<T>` read through an
element reference shares its single-consumption capability with an ordinary
read of that element; the ref does not create a second consumption allowance.

## Storage identity and evaluation order

The Roslyn frontend evaluates the array expression into a stable snapshot before
evaluating its index expressions. On the normal continuation after array-address
evaluation, `BindAggregateElementReference` binds a temporary reference to the
canonical cell identified by the represented aggregate resource and normalized
index. Existing `BindReference` and `MergeReference` instructions flatten that
binding when it flows into another reference or into an evaluated argument.

Replacing the array variable, rebinding another ref, or evaluating a later call
argument cannot redirect an earlier captured address. Replacing the **value in
the cell** remains visible through every reference to that cell. Value copies
made earlier retain their original identities. Jagged arrays resolve one
represented aggregate at each level; rectangular arrays use positional keys such
as `[0,1]`.

The engine stores cells using an internal length-prefixed aggregate identity.
Source names containing punctuation cannot change the parsed aggregate or index.
`StateSnapshot.ReferenceStorage` and all existing report formats expose the
resolved canonical targets. Resource element snapshots and ValueTask snapshots
therefore describe the same cells used by reference reads and writes.

## Strong and weak updates

An exact index into one nonsummarized represented array supports a strong write:
the new value replaces that cell. A conditional ref retains every possible
storage target and performs weak writes, preserving each unselected cell's old
value. Reads of alternative cells retain possible absence and cannot prove
cleanup of every alternative.

Dynamic indices use wildcard selectors. Their reference bindings capture the
array identity, while the engine expands the selector against current cells at
each use. This includes cells first materialized after the address was taken.
Unknown-index writes update matching known cells and a wildcard cache; when a
previously unseen exact cell is materialized, earlier matching wildcard effects
are included. A later exact strong replacement can establish that cell's new
value without retaining an unrelated stale wildcard alternative.

A ref into an element keeps its aggregate allocation site live. Reexecuting that
allocation site while the ref remains live summarizes the old and new concrete
arrays. Reads and writes through either generation's references remain weak. A
single abstract allocation identity is never treated as proof that all runtime
array instances have been replaced or released.

## Calls and exceptions

Source-proven normal ref/out outputs are installed into the same canonical cells.
Opaque writable-ref outputs retain possible old and replacement outcomes. Input
snapshots already captured by `Task.WhenAll` or `Task.WhenAny` remain independent
of subsequent element replacement.

An exceptional call can already have changed a writable array element before it
throws. Call-boundary metadata retains the evaluated ref/out addresses. The CFG
normalizer adds `InvalidateAggregateReference` only to the exceptional
continuation, before filter selection and finally unwinding. It weakens those
canonical cells while preserving independent value snapshots and the exact
normal-return summaries. Cleanup through the old cached array contents can no
longer falsely discharge an owner lost by a throwing ref write.

This exceptional rule is deliberately conservative: normal-exit source summaries
do not prove what a callee did before an exception. It does not invent replacement
resource identities, replay an arbitrary callee's mutation history, or establish
cross-call object invariants. Readonly `in`/`ref readonly` arguments do not acquire
a writable-cell exceptional effect.

## Bounds and remaining limits

Reference bindings retain the existing limits of 256 target locations, 4096
bindings, and 4096 characters per location. Wildcard expansion observes the same
target limit and yields `SS0015` with no partial snapshots if it is exceeded.
Analysis cancellation and fixed-point iteration limits remain unchanged.

External or structurally projected arrays whose element storage has no represented
aggregate identity remain opaque; they cannot borrow the enclosing object's
canonical element cache. General symbolic integer equalities, arbitrary collection
storage, span/ref-return implementation internals, pointer arithmetic, and
concurrent array mutation are outside this sequential domain. Unknown-index
access is conservative and does not prove complete array-element cleanup or
precise iteration counts.

## Design references and tests

This is original C# implementation. Rust's distinction between a place and the
value stored there motivates the separation between reference-storage bindings
and resource aliases. The concrete ref rebinding, managed array identity,
ValueTask copying, and exceptional-call effects follow C#/.NET semantics.

- Rust 1.91 [`places_conflict.rs`](https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_borrowck/src/places_conflict.rs)
  provides the structural place/conflict design reference.
- Rust compiler guide [MIR borrow checking](https://rustc-dev-guide.rust-lang.org/borrow-check.html)
  explains why normalized operations and explicit control flow are useful for
  checking ownership and borrowed places.
- C# specification [variable references](https://learn.microsoft.com/dotnet/csharp/language-reference/language-specification/variables#97-variable-references)
  and [argument lists](https://learn.microsoft.com/dotnet/csharp/language-reference/language-specification/expressions#1262-argument-lists)
  define storage references and argument evaluation.
- .NET [`ValueTask<TResult>`](https://learn.microsoft.com/dotnet/api/system.threading.tasks.valuetask-1)
  defines the single-consumption boundary that reference aliases must preserve.

[Core regressions](../tests/SafeSharp.Tests/Core/AggregateReferenceStorageTests.cs)
exercise canonical storage, weak alternatives, dynamic materialization, repeated
allocation generations, budgets, projections, and capabilities. [Roslyn
regressions](../tests/SafeSharp.Tests/Ownership/AggregateReferenceStorageOwnershipTests.cs)
cover C# refs, ref returns, out outputs, argument evaluation, rebinding, moves,
snapshots, ValueTask reuse, nested arrays, and exceptional cleanup. The existing
[stale-cache boundary tests](../tests/SafeSharp.Tests/Ownership/RefArrayBoundaryTests.cs)
remain regression requirements.
