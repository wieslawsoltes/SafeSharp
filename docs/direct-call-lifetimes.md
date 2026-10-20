# Direct invocation lifetime contracts

`SS0050` checks the explicit borrowed type requirements of a method at the invocation itself. It complements `SS0047` at delegate conversion and `SS0042` at inherited method boundaries. A declaration with input `TypeLifetime` paths or an outer `TypeOutlives` requirement activates this check; ordinary unannotated calls retain their existing behavior.

The checker uses the caller's declared facts and the values actually supplied to each parameter. The callee's requirements are proof obligations. They never become assumptions merely because the method is being called.

## Examples

An invariant container cannot combine two independently supplied borrowed-content lifetimes into one invariant lifetime:

```csharp
using System.Collections.Generic;
using SafeSharp;

static void Combine(
    [TypeLifetime("item", "arg0")] List<Resource> first,
    [TypeLifetime("item", "arg0")] List<Resource> second) { }

static void Caller(
    [TypeLifetime("a", "arg0")] List<Resource> first,
    [TypeLifetime("b", "arg0")] List<Resource> second)
{
    Combine(first, second); // SS0050: a and b are independent.
}
```

Using the same declared lifetime for both caller parameters establishes the invariant relationship. Covariant borrowed positions may be shortened; writable storage requires both directions of the relationship. Nested callable positions use their own binders, so an outer captured lifetime cannot be generalized into an independently quantified callback lifetime.

An explicit generic requirement must also have a caller-supplied proof:

```csharp
[TypeOutlives("T", "scope")]
static void Use<T>(
    [Lifetime("input")] T value,
    [Lifetime("scope")] ref Resource owner) { }

[TypeOutlives("T", "b")]
static void Caller<T>(
    [Lifetime("a")] T value,
    [Lifetime("b")] ref Resource owner)
{
    Use(value, ref owner); // The caller supplies T : b.
}
```

Removing the caller's type bound leaves only the implied `T : a` input bound. That does not establish `T : b` unless an appropriate caller outlives relation connects them. The writable `ref` owner keeps `b` invariant. Callee lifetimes must cover a nonempty invocation extent; an absent type proof cannot pass by assigning every inferred lifetime the empty region.

## Evidence and binding

The supported supplied evidence is a stable caller parameter, the caller receiver for whole-value lifetime requirements, or a transparent local initializer alias of a supported input. Parentheses and identity/reference conversions preserving exact Roslyn type identity retain this evidence. Parameter names do not control argument mapping: named/reordered arguments use Roslyn parameter ordinals. An extension receiver is mapped to its original first parameter. Inherited declarations and constructed generic arguments use the existing contract provider and symbol-based type substitution.

The synthetic caller signature carries the selected borrowed type positions, caller root assumptions, relevant nested outlives closure, and explicit/implied type bounds. It is compared using the same structural binder and higher-ranked solver used for delegate and inherited contract checks. Scoped binder identities are rebased when a caller input is supplied to a differently numbered callee parameter. Source and callee generic substitutions remain distinct, including recursive calls that instantiate the same generic declaration with a different type argument.

Extra caller type positions do not create new callee demands. Missing evidence for a demanded position produces `SS0050`. The implementation does not infer nested lifetime contracts from an arbitrary allocation, factory body, field, conditional expression, user-defined conversion, or unannotated callback.

## Invalidation of input evidence

A compilation-local immutable effect summary is built once per enclosing callable. It records local/parameter replacement, reference exposure, writes into member or element storage, and calls/accessors whose mutation effects are not represented. Conservative alias components connect reference-bearing local aliases to their source inputs, so `alias[0] = replacement` invalidates borrowed-content evidence for the original container as well.

The query distinguishes the current invocation's effects from its preconditions. A nested invocation during argument evaluation is an earlier effect and can invalidate evidence before the outer call begins. Ordinary later statements do not invalidate an earlier call. An effect later in an enclosing loop may reach the next iteration, and deferred callback bodies or nonlocal branches retain conservative possibilities. Exposing an array element by reference invalidates the container's content evidence even if the root array variable was never reassigned.

A parameter explicitly marked `[Borrowed, DoesNotEscape]` supplies the modeled read-only, nonretaining boundary for preserving its argument's nested evidence across a call. `DoesNotEscape` alone does not promise immutability. Arbitrary property accessors and instance methods are not assumed pure. This is deliberately conservative: a method that happens not to mutate its arguments but has no relevant contract can invalidate the available proof.

Summaries cache the original work cost and preserve cancellation and configured budget behavior. Immediate effects are sorted by source position, while deferred effects are queried first; a call does not repeatedly scan all irrelevant later effects in its method. No symbols or operations are retained outside the current compilation.

## Diagnostic evidence and limits

`SS0050` points at the invocation and relates the source contract declarations. `SafeSharp.CallTarget`, `CallArguments`, `CallReason`, and `CallEvidence` explain the mapping and missing fact. Solver failures also retain the existing region assumptions, requirements, shortest proof, universe visibility and type-bound properties. HTML, DOT, Mermaid, JSON and SARIF can therefore consume the same recorded region witnesses; direct calls are identified separately from delegate conversions.

`safesharp_max_regions`, `safesharp_max_region_constraints`, and `safesharp_max_region_steps` bound evidence collection, alias/mutation scanning, type binding, nested region work and solving. Provenance alias traversal is limited to 64 levels. A resource limit produces `SS0043` and withholds a compatibility result. It is not reported as a successfully checked call.

This remains a finite explicit-contract analysis:

- Nested type annotations describe trusted input contents. The checker does not create CLR lifetime parameters or prove arbitrary collection implementations maintain them.
- Reassignment/content mutation invalidates the incoming contract; the checker currently does not infer a replacement content contract from the new value.
- Conditional/flow-merged inputs, factory result type-position inference, arbitrary nominal covariance/projection conversions and unannotated method-group/lambda argument generalization remain conservative boundaries.
- A direct call does not validate every nested object that a callee later returns. Return-value ownership and whole-value provenance remain handled by their existing domains.
- No arbitrary reflection/native effects, heap separation, concurrent mutation proof or Rust trait solver is implied.

## Rust design lineage

The implementation is original C#. The reusable structural relation follows Rust's separation of caller assumptions from callee obligations and the use of fresh inference variables and rigid placeholders for quantified callable inputs:

- [Rust 1.91 structural type relations](https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_type_ir/src/relate.rs).
- [MIR type relations and generated outlives requirements](https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_borrowck/src/type_check/relate_tys.rs).
- [Region inference and type-outlives tests](https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_borrowck/src/region_infer/mod.rs).
- [Rust Compiler Development Guide: region inference](https://rustc-dev-guide.rust-lang.org/borrow-check/region-inference.html).

C# argument evaluation order, by-reference storage, mutable CLR aliases, inherited metadata and ordinary garbage-collected objects require distinct adaptation rules. These links document the lineage of the proof structure; they do not establish language or runtime equivalence.
