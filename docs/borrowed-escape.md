# Borrowed arguments and persistent storage

`SS0027` checks whether a value originating from a `[Borrowed]`, `[BorrowedMut]`, or `[DoesNotEscape]` parameter may be retained outside the current invocation. It follows local aliases and control flow through Roslyn's real `ControlFlowGraph`. The diagnostic describes a possible retention and includes its source parameter and destination; it does not claim to exhibit a concrete execution path.

The rule is part of `SafeSharpSemanticAnalyzer` in the analyzer package. No additional runtime dependency or opt-in analyzer is required. The ownership engine's move, disposal, and active-loan checks remain complementary. See [integration](integration.md), [diagnostics](diagnostics.md#ss0027), and [lifetime regions](lifetime-regions.md).

## Contract policy

| Parameter contract | Persistent storage | Direct borrowed return | Explicit ownership-consuming boundary |
| --- | --- | --- | --- |
| `[Borrowed]` | SS0027 when the tracked origin may be retained | Allowed by this rule; returned-loan and lifetime analysis applies separately | Ownership analysis checks the transfer contract |
| `[BorrowedMut]` | Same retention check, with mutable loan rules handled by ownership analysis | Same returned-loan boundary policy | Ownership analysis checks the transfer contract |
| `[DoesNotEscape]` | SS0027 | SS0027, including aliases | SS0027 for an argument explicitly marked `[Consumes]` or `[Owned]` |

Parameter attributes inherited through interface implementations and overrides are mapped by parameter position, so renaming an implementation parameter does not remove its contract. The normal return exception for a borrow does not apply to an unbounded returned container, delegate, or bound method group that retains it. An explicit `[ReturnsBorrowed("source")]` or named returned-lifetime relationship can declare a bounded result; this storage rule respects the declared source while the lifetime analyzer checks the return implementation. A return contract never overrides `[DoesNotEscape]`.

```csharp
using SafeSharp;

sealed class Cache
{
    private object? retained;

    public void Remember([Borrowed] object argument, bool condition)
    {
        object alias = new object();
        if (condition)
            alias = argument;

        retained = alias; // SS0027: argument may be retained in retained
    }

    public void Copy([Borrowed] object argument)
    {
        object alias = argument;
        alias = new object();
        retained = alias; // No borrowed origin remains in this local slot.
    }

    public object Identity([DoesNotEscape] object argument)
        => argument; // SS0027: the no-escape argument is returned.
}
```

## Implemented flow

The analysis associates a finite set of possible source parameters with each value. Local and parameter assignments replace the previous value; joins take the union from reachable predecessors, and loop back edges are solved to a fixed point. Constant Boolean branches are respected. Diagnostics are emitted in a second traversal using converged input states, so a visit early in loop solving cannot become a spurious final diagnostic.

The domain also represents allocation sites, closure cells, and `ref` targets. This distinction is necessary for C#: a Roslyn value capture records the value already evaluated, while a closure captures a variable that can change later. Receiver evaluation precedes index and argument evaluation. A later argument that reassigns a parameter cannot retroactively change an earlier captured argument value.

| Construct | Implemented behavior |
| --- | --- |
| Locals, reassigned parameters, conditionals, coalescing, patterns, loops | Propagate and merge possible parameter origins; known fresh or null assignment clears a scalar slot |
| `ref` locals and ref reassignment | Keep storage targets separate from values and update the referenced slot |
| Fields, properties, indexers, external arrays, `ref`/`out` parameters | Treat writes as potential retention destinations |
| Local objects, anonymous objects, arrays, tuples, initializers | Track contents at their allocation site; a local write becomes a retention issue when the containing value escapes |
| Container aliases | Share the tracked contents; an escape through one alias applies to later writes through another |
| Delegates, local functions, bound method groups, events | Track captured cells or receiver values; check return, storage, and event subscription |
| Writes after a closure/container has escaped | Carry the earlier escape to new nested containers and captures, so subsequent writes remain visible |
| Method calls and property getters | Use explicit borrowed results and bounded source-inferred identity/fresh summaries; inherited result sources are mapped to current parameters |
| Extension methods | Map a reduced receiver to the unreduced first parameter when resolving a borrowed source |
| Constructors | Conservatively retain unannotated constructor inputs in the local result; trust `[Borrowed]`, `[BorrowedMut]`, and `[DoesNotEscape]` inputs as non-retaining constructor contracts |
| Explicit consuming calls, constructors, conversions, operators | Check the stronger `[DoesNotEscape]` promise, including discarded results |
| Async carriers | Preserve known payloads and explicit async borrowed-return contracts through `Task.FromResult`, `ValueTask.FromResult`, `ValueTask<T>(T)`, `ValueTask<T>(Task<T>)`, `ConfigureAwait`, `AsTask`, and known awaiter operations; distinguish a borrowed task from its unrelated result |

Calling an arbitrary method is not automatically classified as an escape. An opaque return is not automatically classified as an alias of its arguments. Declared contracts and source summaries provide the interprocedural boundary. A known fresh-return summary clears the result's borrowed origin; this prevents a clone or factory result from being confused with an input loan. `[Owned]` output summaries can establish a fresh value in a `ref`/`out` argument. Unknown `ref`/`out` mutations are not a proof of a fresh value.

For example, storing `await Task.FromResult(argument)` retains the known payload. Storing the result of awaiting a `[Borrowed] Task<object>` does not establish that the task object itself was retained. Nested known carriers unwrap one payload at a time.

Payload markers distinguish these modeled results from arbitrary carrier contents. A custom awaitable or `Task<T>` constructor may retain an argument without returning that argument as its payload; the analysis does not assume such an identity.

## Exceptions and captured execution

Roslyn's ordinary predecessor graph does not expose catch and finally entry as ordinary control-flow edges. This analysis seeds such roots with a conservative history of possible values, reference targets, and escaped cells from the method. Finally continuations receive that history as well. Nested functions use the enclosing captured-cell history; an invoked local function or callback conservatively propagates captured origins to cells that it can affect.

This history can include states from different times or mutually exclusive paths. It is deliberately reported as *possible* retention. It does not model exception type matching, filter feasibility, exact throw order, callback scheduling, or invocation counts. The ownership engine has a separate exceptional cleanup model; SS0027's handler envelope is not an exception-path witness.

Local aggregate contents are field- and index-insensitive. Allocation sites inside loops summarize multiple runtime objects, and writes to aggregate contents are weak updates. Overwriting one field or array element therefore does not prove that every previously retained origin has been removed. This trades precision for bounded analysis and makes joins over container aliases conservative. Reference targets and closure dependencies are also may sets.

## Bounds, diagnostics, and report evidence

`safesharp_max_contract_steps` controls the traversal and domain-operation budget. The default is 20,000 steps per analyzed body, with the existing maximum of 1,000,000. Operation recursion has an additional depth bound. Traversal, joins, provenance resolution, and replay check cancellation. Budget exhaustion discards partial results for that body and reports informational `SS0046`; it does not certify the method as safe. The CLI's analysis-completeness policy includes this diagnostic even when its presentation is suppressed.

```editorconfig
[*.cs]
safesharp_max_contract_steps = 50000
dotnet_diagnostic.SS0027.severity = warning
dotnet_diagnostic.SS0046.severity = warning
```

Diagnostics keep direct parameter occurrence spans for existing cases and use the alias or escaping expression when that occurrence is no longer present. Related locations identify the parameter declaration, destination, and retention site where source locations are available. Structured report properties are:

| Property | Meaning |
| --- | --- |
| `SafeSharp.BorrowedSource` | Source parameter name |
| `SafeSharp.EscapeDestination` | Field, property, event, output, return, or consuming boundary |
| `SafeSharp.EscapeAnalysis` | `converged may-origin dataflow` |
| `SafeSharp.Certainty` | `Possible` |
| `SafeSharp.DoesNotEscape` | Whether the origin has the stronger no-escape contract |

These are semantic diagnostic facts exposed through the normal report pipeline. SS0027 does not invent a path graph from the set union. Ownership CFG graphs and loan facts in the reporting package describe their corresponding ownership analyses; a graph should not be read as a demonstrated SS0027 execution.

Use the ordinary per-rule severity setting to control SS0027. The `safesharp_report_possible_issues` option applies to the core ownership engine and does not suppress this semantic rule.

## Rust design reference and boundaries

Rust checks body constraints against the lifetimes permitted by a signature. Its region inference propagates outlives constraints over control-flow locations and then validates the resulting obligations. That separation of body facts, signature promises, propagation, and final checking informs this implementation. Primary references are the [Rust compiler's region inference guide](https://rustc-dev-guide.rust-lang.org/borrow-check/region-inference.html), the [`rustc_borrowck::region_infer` source pinned to Rust 1.91.0](https://github.com/rust-lang/rust/tree/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_borrowck/src/region_infer), and the [Rust book's lifetime chapter](https://doc.rust-lang.org/book/ch10-03-lifetime-syntax.html).

SS0027 implements a finite C# provenance and retention check. It does not reproduce Rust's type system, complete heap aliasing, all callback effects, unsafe-code provenance, or a whole-program escape analysis. Reflection, dynamic invocation, native code, arbitrary library containers, custom awaitable semantics, and uncontracted callbacks require additional contracts or conservative review. Source-inferred summaries retain their own documented recursion and precision limits. The absence of SS0027 is meaningful only within these modeled boundaries.

The implementation is in `src/SafeSharp.Analyzers/Semantics/BorrowedEscapeAnalysis.cs`. Real Roslyn compilation regressions live in `BorrowedEscapeTests`, `BorrowedEscapeReviewTests`, and the original `CallbackOwnershipTests`; they cover aliases, reassignments, inherited contracts, snapshots, loops, containers, captured mutation, exceptional regions, strict no-escape boundaries, async payloads, diagnostic spans/properties, deterministic execution, and budget exhaustion.
