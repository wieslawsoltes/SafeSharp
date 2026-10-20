# Lifetime contracts inside types

`TypeLifetime` describes borrowed contents at a specific position inside a parameter or result type. The analyzer checks these explicit contracts when a method group, annotated anonymous function, or delegate is converted to a contracted delegate. It uses Roslyn's resolved generic variance and a [higher-ranked region solver](higher-ranked-regions.md) with nested binders, placeholder visibility, and type-outlives tests.

The annotations are boundary contracts. They do not add lifetime parameters to CLR types, replace C# type checking, or prove an arbitrary collection implementation stores its elements as declared. Existing `Lifetime` contracts continue to drive whole-value returned-provenance analysis. `TypeLifetime` describes the additional type shape which C# metadata does not otherwise express.

## A callback valid for every invocation lifetime

```csharp
using System;
using SafeSharp;

delegate void Visitor(
    [TypeLifetime("a", "param0"), TypeLifetime("a", "return")]
    Func<Resource, Resource> callback);

static void Visit(
    [TypeLifetime("item", "param0"), TypeLifetime("item", "return")]
    Func<Resource, Resource> callback)
{
    // The explicit contract says the callback's result borrows for its input lifetime.
}

Visitor visitor = Visit; // Compatible despite alpha-renamed lifetime names.
```

Selecting a delegate's parameter or result opens a fresh binder. Both paths above enter the same `Func` signature, so their `a` names share a region. An identical name in another callback parameter belongs to a different binder. The relationship is quantified for each invocation; it is not the lifetime of the `Func` object.

### Type path grammar

| Segment | Selected type | Lifetime variance |
| --- | --- | --- |
| Empty path | The whole annotated parameter/result type | Follows that outer parameter/result position. |
| `argN` | Generic argument at zero-based index `N` | Uses Roslyn's declared `in`, `out`, or invariant type parameter. |
| `element` | Array element | Invariant because the array exposes writable element storage. |
| `paramN` | Parameter `N` of a delegate's `Invoke` signature | Contravariant; writable `ref` and `out` slots are invariant. Opens a binder. |
| `return` | Result of a delegate's `Invoke` signature | Covariant; a writable ref result is invariant. Opens a binder. |

Segments are separated by dots. `arg0.param0` selects the input of a delegate stored as a generic argument. `return.return` enters two nested delegate signatures. `arg0.arg0` composes variance through two generic constructors. Numeric indices use canonical non-negative decimal syntax; `arg00` is invalid.

An ordinary method parameter begins contravariantly and its result begins covariantly. A writable outer `ref`/`out` parameter is invariant. Two contravariant positions compose covariantly. Invariance remains invariant through further generic storage positions.

Entering a quantified callable inside invariant storage requires both subtype directions. The analyzer creates fresh placeholders and inference variables for each direction. Sharing one inferred region assignment across the reverse check would incorrectly accept different quantified contracts.

The analyzer requires corresponding explicit type paths on both sides of a conversion. A missing path is not inferred from the CLR type, a method's name, or an arbitrary container body. The C# compiler still decides whether the underlying nominal types and ordinary method signatures are compatible.

## Referencing an outer lifetime

Prefix a lifetime name with `^` for each enclosing callable binder to leave. An unprefixed name is local to the innermost binder and can shadow an outer name.

```csharp
[return: TypeLifetime("^owner", "return")]
delegate Func<Resource> Capture([Lifetime("owner")] Resource owner);

class Factory
{
    [return: TypeLifetime("^input", "return")]
    public static Func<Resource> Make([Lifetime("input")] Resource input)
        => () => input;

    public Capture Valid() => Make;
}
```

Both contracts describe a callback tied to the outer input. In contrast, this target promises a fresh lifetime for every future invocation:

```csharp
[return: TypeLifetime("fresh", "return")]
delegate Func<Resource> UniversalFactory([Lifetime("owner")] Resource owner);

// UniversalFactory invalid = Factory.Make; // SS0047: an outer capture cannot be generalized.
```

The target's `fresh` is a rigid placeholder in a child universe. The implementation's `input` is inferred in the outer universe. The solver records the path from captured storage to the placeholder, finds that its effective universe cannot name the placeholder, and applies the visible static upper bound. The outer input cannot satisfy that new universal requirement.

`^^owner` can refer through two enclosing callable signatures. An outer reference must resolve to an existing declaration in that binder. It never silently declares a new region. The reserved `this` lifetime is available only in the outer binder of an instance method; a nested path refers to it with the appropriate `^` prefixes.

### Bounds within a nested binder

Use the optional `Outlives.Scope` property to constrain the local names of one callable binder:

```csharp
[Outlives("second", "first", Scope = "result")]
[return: TypeLifetime("first", "param0"),
         TypeLifetime("second", "param1"),
         TypeLifetime("first", "return")]
delegate Func<Resource, Resource, Resource> SelectFactory();
```

`result` names the returned delegate's binder. Within that binder, the second input is valid for at least the first input's region, so returning a borrow of the second input can satisfy the promised result. Reversing the bound does not justify that return.

Scopes start with `parameterN` or `result`, then follow the type path to the nested delegate itself: `parameter0.arg0` names a delegate inside the first parameter's generic argument, and `result.return` names a second returned delegate. The scope must already be introduced by `TypeLifetime` paths, and both endpoint names must be declared in that binder. Null or empty `Scope` preserves the existing outer-signature `Outlives` behavior. Scoped endpoints do not silently refer to identically named outer regions.

## Generic type-outlives bounds

`TypeOutlives` supplies an explicit analogue of a Rust `T: 'a` obligation:

```csharp
[TypeOutlives("T", "scope")]
delegate void Read<T>(
    [Lifetime("input")] T value,
    [Lifetime("scope")] ref Resource scope);
```

The bound concerns the borrowed contents represented by `T`. It does not claim that every CLR generic argument contains borrowed references. Roslyn's constructed method and containing-type arguments resolve the type identity at the conversion; equal display names do not equate distinct type parameters.

An input whose type is a generic parameter and which has `[Lifetime("a")]` implies `T: a`. Selecting an explicitly annotated generic-parameter type with `TypeLifetime` also supplies a bound when that input's region is local to the selected binder. Explicitly annotated borrowed contents must outlive their annotated containing owner in the same binder. For example:

```csharp
delegate void Inspect<T>(
    [Lifetime("container"), TypeLifetime("item", "arg0")]
    ref System.Collections.Generic.IEnumerable<T> items);
```

This signature supplies `T: item` and `item: container`. Those facts can justify a callback's `T: container` requirement. A delegate's signature does not imply that its quantified input/result lifetimes outlive the delegate object: well-formedness propagation never crosses a callable binder.

The solver checks each rigid lower bound of a required type lifetime against the caller's assumed type bounds and outlives relations. A callback cannot invent a caller assumption. An unconstrained inferred region may remain empty; once it must contain a caller lifetime, a sufficient type bound is required. Failure is SS0047 with the required type identity, region path, and available type assumptions.

The optional `TypeOutlives.Scope` uses the same callable binder paths as `Outlives.Scope`. This expresses a generic bound inside a quantified signature, including a bound which is stronger than its implied input well-formedness facts:

```csharp
[TypeOutlives("T", "result", Scope = "result")]
[return: TypeLifetime("input", "param0"),
         TypeLifetime("result", "param1"),
         TypeLifetime("result", "return")]
delegate Func<T, Resource, Resource> GenericFactory<T>();
```

The callback's generic input implies `T: input`; the explicit contract additionally supplies `T: result` in that returned callback's binder. The selected region must be declared locally in the selected scope. A same-named outer region does not satisfy a missing local declaration. A callback implementation requiring this bound cannot be converted to a target that supplies only the implied `T: input` bound.

## Inheritance, diagnostics, and evidence

Omitted `TypeLifetime` and `TypeOutlives` metadata is read from the nearest applicable inherited method contract. Parameters are matched by ordinal. Lifetime names at outer type positions and inherited scoped bounds participate in positional alpha-renaming. Interface implementations and overrides must preserve the inherited type-lifetime promises; changing a nested result origin or adding an unsupported generic bound reports SS0042. Generic method parameter names are matched by ordinal rather than by spelling. The inherited receiver is a caller-supplied input rather than an independent captured delegate receiver.

Constructed inherited type bounds preserve the argument supplied to the actual interface or base class. For `C<U> : I<List<U>>`, a bound on `I<T>.T` means `List<U>`, and a conversion using `C<Resource>.M` resolves it to `List<Resource>`. It does not become `U` merely because both type parameters occupy ordinal zero. Substitution also follows arrays, nested nominal types, and enclosing generic type arguments, using Roslyn symbol identity throughout. Contracts read from an emitted reference assembly use the same checks as source declarations.

Explicitly annotated anonymous functions use the same conversion check; unannotated nested type promises are not guessed from a lambda body. Reduced extension methods normalize the remaining parameter paths after binding the receiver.

| Diagnostic | Type-lifetime meaning |
| --- | --- |
| SS0040 | An invalid path, duplicate path, absent generic type parameter, missing region, invalid outer reference, or other malformed declaration. |
| SS0042 | An implementation changes an inherited nested lifetime, variance, scoped bound, or type-outlives promise. |
| SS0047 | A delegate conversion changes a type-lifetime promise, adds a required bound, violates variance, or leaks a nested placeholder. |
| SS0043 | The combined binding/solving budget is exhausted; compatibility is not established. |

Existing region properties still contain a complete graph: region names, assumptions, requirements, strongly connected components, and a shortest failure path. Higher-ranked failures additionally expose `RegionUniverses`, `RegionVariableUniverses`, `RegionUniversePromotions`, and `RegionVisibilityProofs`. Type failures add `TypeOutlivesAssumptions`, `TypeOutlivesRequirements`, `TypeBindings`, and `TypeOutlivesType`, all under the `SafeSharp.` prefix. Related diagnostic locations include the original contract declarations.

Generated static-promotion edges are marked as solver consequences. Their separate original placeholder and visibility paths preserve the evidence that caused the promotion. The analyzer does not fabricate a source span for a generated inference edge.

HTML, DOT, and Mermaid previews render the reserved `$static` upper-bound vertex and the exact recorded assumption/requirement edges. Universe descriptions, visibility paths, and type-outlives metadata remain explanations; they never create additional graph edges. JSON and SARIF retain the complete original properties. The [higher-ranked report regressions](../tests/SafeSharp.Tests/Reporting/HigherRankedRegionReportTests.cs) run actual analyzer diagnostics through every format and verify both topology and metadata preservation.

## Resource limits and supported boundary

Contract models are cached per compilation and original method definition. Paths use bounded iterative symbol traversal: at most 64 segments and 1,024 characters per path/name. Invalid metadata reports SS0040. Ancestor well-formedness bounds use indexed path prefixes rather than pairwise scans of all annotations.

Constructed type substitution memoizes repeated subtrees per comparison side. Each visited type or containing-type lookup consumes the shared binding work budget, and substitution depth is capped at 64. A depth or work limit reports SS0043 without establishing compatibility.

`safesharp_max_regions`, `safesharp_max_region_constraints`, and `safesharp_max_region_steps` bound the resulting proof. Nested invariant checks allocate independent universes under the same limit. Binding work and all region solves share the configured step budget; static edges, promotions, and type bounds count toward the combined constraint limit. Cancellation is observed while parsing, binding, and solving. Incomplete solves never produce a success claim or partial diagnostic proof.

The supported domain is explicit borrowed type positions, their declared Roslyn variance, and nested callable lifetime binders at contracted delegate conversions and inherited method boundaries. These contracts are trusted descriptions of nested contents; SafeSharp does not infer arbitrary container storage or verify every nested element produced by a method body. Arbitrary direct calls and runtime reflection do not gain a new CLR type system. A bound extension receiver with its own `TypeLifetime` paths currently lacks delegate-supplied evidence and is reported conservatively. Full Rust trait resolution, higher-kinded type unification, automatic closure generalization, and every possible nominal generic-type projection remain outside this finite contract model.

## Design references and regression tests

The original C# implementation follows these pinned Rust mechanisms:

- [Variance-aware structural relations and binder boundaries](https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_type_ir/src/relate.rs).
- [Reachable-universe intersection and placeholder constraint lowering](https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_borrowck/src/handle_placeholders.rs).
- [Region checks and type-outlives tests](https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_borrowck/src/region_infer/mod.rs).

The [Roslyn type-lifetime suite](../tests/SafeSharp.Tests/LifetimeContracts/TypeLifetimeTests.cs) checks accepted and rejected contracts, generic polarity, writable arrays and generic storage, nested and sibling scopes, shadowing, captured receiver/input leaks, invariant callable reverse checks, constructed generic bounds, implied owner bounds, inherited and emitted assembly metadata, malformed declarations, exact spans, diagnostic evidence, budgets, and concurrent determinism. [Core variance tests](../tests/SafeSharp.Tests/Regions/RegionVarianceTests.cs) cover every composition, while the [higher-ranked solver tests](../tests/SafeSharp.Tests/Regions/HigherRankedRegionSolverTests.cs) independently exercise universe visibility, old empty regions, type obligations, budgets, and source-backed evidence.
