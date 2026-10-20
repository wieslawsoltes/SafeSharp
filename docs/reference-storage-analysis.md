# Reference storage in ownership analysis

C# distinguishes a value alias from a reference to storage. `var copy = value` captures the current
object; `ref var alias = ref value` captures the location containing that object. Replacing the
location changes what `alias` reads and leaves `copy` unchanged. SafeSharp carries these facts in
separate domains throughout ownership analysis.

## Transfer rules

`ReferenceStorageState` maps a reference place to immutable, flattened storage targets. A new
reference copies the targets selected at binding time, so rebinding its source cannot redirect an
older alias. `BindReference`, `MergeReference`, `ReleaseReference`, and
`BindAggregateElementReference` are explicit frontend instructions. The aggregate binding captures
an evaluated array identity and normalized index; release removes an address snapshot without
replacing its target's value.

Resource and ValueTask reads resolve the same storage domain. A write to one exact target that
represents a single instance is a strong replacement. Alternative targets, wildcard indices, and
summarized aggregate generations require weak writes: each selected location
retains both the old and incoming values, and their presence is conditional. Joins retain every
possible target. The analyzer never disposes both alternatives merely because a conditional ref
can designate either one.

```csharp
var first = new Resource();
var second = new Resource();
ref var alias = ref first;
ref var older = ref alias;
alias = ref second;
older.Dispose(); // Disposes first. It does not dispose second.
first.Read();    // SS0001.
second.Dispose();
```

Overwriting a referenced location uses its actual targets when checking the last owner. The cell
being overwritten is not counted again as a surviving alias. Ordinary by-value copies still
retain the old resource and remain valid independent owners when appropriate.

## Calls and returns

The Roslyn frontend preserves separate value captures and address captures. It snapshots each
ref/in/out argument's selected location before evaluating later arguments. A source-proven
ref-return summary maps its parameter ordinals back to those saved argument locations.

```csharp
static ref Resource First(ref Resource value, Resource ignored) => ref value;

Resource first = external;
Resource second = external;
ref var alias = ref first;
ref var returned = ref First(ref alias, (alias = ref second));
returned = new Resource();
first.Dispose(); // The returned location is first, selected before alias was rebound.
```

A normal by-value return captures its object before cleanup. A ref return captures its location;
cleanup may change the value stored there. Both summary inference and lifetime checking validate
the value available on normal completion. A throwing cleanup does not establish a normal-return
postcondition. Readonly refs use the same location identities; the C# compiler enforces readonly
writes and ref escape legality.

Source-proven normal writes through `ref` parameters also compose with caller storage. Parameter
and null origins are captured before any outputs are installed. A void or ref-returning helper with
one writable parameter can establish one fresh owned output; separate fresh outputs and a separate
by-value result need a richer allocation-identity relation and are not guessed. Unknown writable
ref effects retain both the original value and an opaque replacement outcome.

These postconditions apply only after normal call completion. Source summary and lifetime inference
invalidate possible exceptional ref writes. For represented aggregate-element refs, the caller's
CFG applies `InvalidateAggregateReference` on the exceptional continuation before filter selection
and finally unwinding. It weakens the saved ref/out targets while preserving independent by-value
snapshots and normal-return summaries; normal fresh output allocations are not replayed on a
throwing edge. Precise interprocedural exceptional write histories are not inferred.

Opaque ref returns retain possible reference-argument locations plus an explicit unknown target.
Proven external storage stays external. These facts do not establish new ownership of arbitrary
heap cells, dynamic dispatch, or native mutation. Source write postconditions retain the documented
bounds in [source summary analysis](source-summary-analysis.md).

For represented arrays, ordinary element access, local refs, conditional refs, source-proven ref
returns, and ref/out outputs share canonical resource and ValueTask cells. Captured addresses stay
attached to the evaluated array when the original variable or another ref is rebound. Unknown
indices expand conservatively over matching cells, including cells materialized later, and repeated
allocation generations remain weak. Earlier value copies and `Task.WhenAll`/`Task.WhenAny` input
snapshots keep their captured identities. Value writes through refs keep the address live; rebinding
or releasing a ref ends its former binding's liveness.

External or structurally projected arrays without represented aggregate identities remain opaque.
Symbolic integer equalities, arbitrary collection storage, pointer arithmetic, and concurrent
mutation are outside this sequential model. Canonical identity does not derive child loans from
parent container loans or prove that a later ref write respects every subsequently created loan of
the containing array. See [canonical aggregate references](aggregate-reference-storage.md) for the
supported storage rules, exceptional effects, and deep-container permission boundary.

## Protocols, diagnostics, and reports

ValueTask consumption capabilities follow the same storage references as resources. Consuming a
ValueTask through a ref prevents a second consumption through the original place or another alias.
A reference assignment copies the current capability identity; rebinding a reference changes only
its selected storage.

State snapshots include `ReferenceStorage`, with each reference's possible modeled target locations.
JSON exposes `referenceStorage` and HTML displays a reference-storage table beside ordinary value
aliases. This distinction explains whether an assignment changed a value or selected a new cell.

The domain permits at most 256 targets per reference, 4,096 bindings, and 4,096 characters per
projected location. Bounds guard pathological projection growth, alias unions, and wildcard target expansion. Exceeding one
stops ownership analysis with `SS0015`; a truncated target set is never treated as a completed
safety result. Singleton reads and writes use direct storage lookup, while only conditional targets
need resource-fact unions.

## Rust design reference

This is original C# code adapting Rust's separation of places from values and its conservative
place-overlap reasoning. It retains CLR ref reassignment, reference types, and garbage-collected
storage semantics; it does not claim Rust's lifetime or memory-safety guarantees.

- [Rust 1.91 place conflict analysis](https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_borrowck/src/places_conflict.rs)
- [Rust 1.91 MIR move paths](https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_mir_dataflow/src/move_paths/mod.rs)

`ReferenceStorageTests` exercises transfer functions, joins, capability aliases, and bounded
projection growth. `ReferenceStorageOwnershipTests` compiles valid C# with Roslyn and checks
returned refs, readonly refs, conditional targets, writes, saved values, and argument ordering.
