# Reference storage in ownership analysis

C# distinguishes a value alias from a reference to storage. `var copy = value` captures the current
object; `ref var alias = ref value` captures the location containing that object. Replacing the
location changes what `alias` reads and leaves `copy` unchanged. SafeSharp carries these facts in
separate domains throughout ownership analysis.

## Transfer rules

`ReferenceStorageState` maps a reference place to immutable, flattened storage targets. A new
reference copies the targets selected at binding time, so rebinding its source cannot redirect an
older alias. `BindReference`, `MergeReference`, and `ReleaseReference` are explicit frontend
instructions; release removes an address snapshot without replacing its target's value.

Resource and ValueTask reads resolve the same storage domain. A write to one known target is a
strong replacement. A write through several possible targets is weak: each selected location
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
invalidate possible exceptional ref writes; normal fresh output allocations are not replayed on a
throwing edge. Precise interprocedural exceptional write histories are not inferred by the caller's
ownership graph.

Opaque ref returns retain possible reference-argument locations plus an explicit unknown target.
Proven external storage stays external. These facts do not establish new ownership of arbitrary
heap cells, dynamic dispatch, or native mutation. Mutable refs into the canonical element caches
used for async aggregate arrays are not yet composed with this storage domain; direct array effects
and local/parameter refs are analyzed separately. Source write postconditions retain the documented
bounds in [source summary analysis](source-summary-analysis.md).

## Protocols, diagnostics, and reports

ValueTask consumption capabilities follow the same storage references as resources. Consuming a
ValueTask through a ref prevents a second consumption through the original place or another alias.
A reference assignment copies the current capability identity; rebinding a reference changes only
its selected storage.

State snapshots include `ReferenceStorage`, with each reference's possible modeled target locations.
JSON exposes `referenceStorage` and HTML displays a reference-storage table beside ordinary value
aliases. This distinction explains whether an assignment changed a value or selected a new cell.

The domain permits at most 256 targets per reference, 4,096 bindings, and 4,096 characters per
projected location. Bounds guard pathological projection growth and alias unions. Exceeding one
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
