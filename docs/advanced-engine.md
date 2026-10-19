# Advanced ownership protocols

SafeSharp models these protocols in its language-independent intermediate representation and fixed-point state. The C# frontend decides which source operations have those effects; annotations alone do not change C# runtime semantics.

## Two-phase exclusive borrowing

`ReserveExclusiveBorrow(source: owner, target: reservation)` creates a live exclusive reservation. `ActivateBorrow(source: reservation)` activates the same loan, including when the source is an alias of the original holder. `BorrowExclusive` remains immediately active, so an explicit mutable borrow does not silently gain more permissive semantics.

| Operation on overlapping storage | Reserved phase | Active phase |
| --- | --- | --- |
| Read through the owner | Allowed | `SS0013` |
| Create a shared loan | Allowed | `SS0006` |
| Create another exclusive loan or reservation | `SS0006` | `SS0006` |
| Mutate through the owner | `SS0007` | `SS0007` |
| Mutate through this loan | `SS0007` | Allowed |
| Move or transfer ownership | `SS0005` | `SS0005` |
| Dispose the owner | `SS0014` | `SS0014` |

Activation checks for other live overlapping loans and emits `SS0006` if a shared argument or competing loan still exists. A shared argument whose last use precedes activation has already expired through backward place liveness. A reservation that is never activated can likewise end at its last use; it does not have to survive to the end of the lexical scope. Disjoint field projections retain their existing independent-borrow behavior. Reservation and active phases both obey the loan's suspension policy.

Mutation through a reborrow requires active exclusive permission throughout its loan ancestry. Creating or activating an invalid mutable child does not grant permission to mutate through a shared or still-reserved parent; the subsequent write also receives `SS0007`. Valid mutable reborrows retain their active exclusive ancestors until the returned child reaches its last use.

The phase is a flags lattice: a branch join may retain `Reserved | Active`. Reads and new shared borrows are then diagnosed as possible conflicts. Activation on every incoming branch produces an active loan. Loan identity, phase, parent ancestry, absence, and activation evidence participate in joins and fixed-point equality; activation is not stored in an external instruction-side cache. Reports expose `LoanSnapshot.Phase` and immutable `ActivationSites`, and JSON/HTML include those values.

### C# call lowering

The Roslyn adapter reserves a `[Mutates]` receiver before evaluating its arguments, then activates that reservation at the call. For example, `resource.Append(resource.Count)` can read `Count` during argument evaluation. A shared loan that must remain live inside `Append` conflicts with activation instead. `[BorrowedMut]` parameters use the same reservation and activation protocol in C# argument evaluation order. Distinct owned arguments can be borrowed independently; passing two aliases of the same resource as mutable arguments is diagnosed.

Explicit `Ownership.BorrowMut` and ordinary `ref` argument borrows remain immediately active. All passed loan values stay live through the call, including explicit borrows supplied to unannotated parameters, so evaluating a later argument cannot silently end an earlier explicit loan. Returned borrow contracts derive their result from the applicable call loan before the caller releases the temporary parameter or receiver holder. This preserves parent ancestry without keeping an otherwise dead loan alive for the remainder of the lexical scope.

### Relationship to Rust

The design follows Rust's distinction between reserving an implicit mutable borrow and activating it at use. Rust applies this to selected compiler-created borrows, including mutable method receivers; explicit mutable references remain immediate. See the [Rust Compiler Development Guide on two-phase borrows](https://rustc-dev-guide.rust-lang.org/borrow-check/two-phase-borrows.html) and the pinned [Rust 1.91.0 borrow-set implementation](https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_borrowck/src/borrow_set.rs).

SafeSharp computes possible phases through its abstract CFG state. It does not import Rust MIR, use Rust's exact dominance query for activation, or claim that arbitrary C# method calls satisfy Rust's lifetime guarantees. The frontend must emit reservation and activation only for operations whose contracts justify those effects.

## Asynchronous disposal completion

Invoking asynchronous disposal and observing its completion are distinct effects:

```csharp
var completion = resource.DisposeAsync(); // The owner's release operation has been invoked.
await completion;                        // The completion obligation is observed.
```

`BeginAsyncDispose(source: owner, target: completion)` applies the ordinary disposal checks to the owner and creates a new owned resource of kind `AsyncDisposalCompletion` in the `Pending` state. The owner is already `Disposed` under SafeSharp's invocation-based cleanup policy. `CompleteAsyncDispose(source: completion)` changes the completion to `Completed`. It leaves ordinary resources unchanged, allowing a frontend to emit completion observation at generic await sites.

The completion is a first-class resource in the same abstract state as other resources. Ordinary `Alias` instructions preserve its identity, including across frontend-recognized configured awaitables. A local `Move` transfers access to a new place. Returning, escaping, or passing the completion to a consuming boundary transfers the obligation. Observation through any valid alias discharges the same completion. A pending completion cannot be discharged by disposing the awaitable object itself.

`SS0017` reports a pending completion at a reachable exit or when overwriting its last usable local handle. The primary exit diagnostic identifies the initiating disposal; overwrite diagnostics identify the overwrite and retain the initiating disposal as related evidence. A dead frontend temporary does not count as a surviving completion handle. Branch-local completions keep their resource-presence correlations, so awaiting a value selected from mutually exclusive disposal calls does not invent leaks. If only one branch observes a completion, the joined state retains `Pending | Completed` and the finding is possible.

### Exceptions and suspension

For a potentially throwing `DisposeAsync` invocation, the Roslyn frontend splits the protocol:

1. Emit `Dispose` with `AsyncDispose` to record the attempted release.
2. Emit the invocation's exception boundary.
3. On the successful-return edge, emit `BeginAsyncDispose` with `DisposalAlreadyInvoked` to create the completion without repeating the owner's release checks.

This ordering does not create a nonexistent awaitable on a synchronous-throw edge. At an await, observation precedes propagation of an asynchronous fault: awaiting a failed operation still observes its completion. This verifies the caller's release and observation protocol; it does not prove that a failing disposal implementation released every physical resource. Pending completions may span suspension points; the owning resource's no-suspension restriction does not automatically apply to the completion value.

### C# completion observation

The adapter preserves completion identity through locals, ordinary assignment aliases, and the framework `ConfigureAwait`, `AsTask`, and `GetAwaiter` wrappers. Direct or stored `await`, parameterless framework `GetResult()`, and parameterless `Task.Wait()` observe that identity. `await using` follows the same disposal and completion protocol. Returning a `ValueTask` completion or its `Task` wrapper transfers the obligation to the caller.

An explicit discard, reading `IsCompleted`, or calling `Dispose()` on a `Task` does not observe the disposal completion. Timeout or cancellation overloads of `Wait` are not treated as unconditional observation. Methods with matching names in unrelated user types are not recognized as framework completion operations. Interprocedural observation inside an unmodeled helper requires an appropriate transfer boundary; it is not inferred from a method name.

### Reports and precision limits

Snapshots and JSON expose `ResourceSnapshot.Kind`, lifecycle `Status`, and immutable `DisposedResourceIds`. Those provenance links do not make an already-disposed owner a live scope dependency of its completion. HTML displays owner disposal and pending/completed obligations separately. SARIF contains the protocol diagnostic and source-grounded evidence.

As with ordinary ownership resources, completion identities are allocation sites. Repeated initiation at a site with a still-pending previous completion is diagnosed; a live older alias makes that finding possible because the abstraction does not count an unbounded number of concrete task instances. Completing each iteration before starting the next reaches a safe fixed point. Observation is idempotent in this ownership model: rules concerning whether a particular `ValueTask` implementation permits repeated awaits are a separate API contract. Transferring a completion through an unmodeled heap or method boundary does not prove that its recipient will observe it.

## Alternative alias origins

`MergeAlias(target: result, source: candidate)` means that the target may retain its previous value or take the candidate value. It complements `Alias`, which still replaces its target. This supports a borrowed return whose declared lifetime can be supplied by more than one input.

The engine constructs both state alternatives, expires loans according to each alternative's live references, then joins them. Consequently, a temporary loan selected only on one alternative is possibly live rather than definitely live. A candidate retained by another live alias remains live independently of the result choice. Backward liveness treats the prior target as an input as well as a definition. Reference absence and resource correlations are retained; choosing one of two independently pending completions cannot accidentally observe both.

## Regression coverage

The [Core two-phase tests](../tests/SafeSharp.Tests/Core/TwoPhaseBorrowTests.cs) cover phase joins, loops, activation evidence, ancestor permissions, and suspension. The [Core completion tests](../tests/SafeSharp.Tests/Core/AsyncDisposalCompletionTests.cs) exercise pending obligations and their alias, transfer, overwrite, and fixed-point behavior. The [Roslyn call protocol tests](../tests/SafeSharp.Tests/Ownership/CallProtocolTests.cs) compile real C# fixtures, check diagnostic spans and evidence, and inspect reservation, activation, completion, and exceptional-flow snapshots. They include accepted programs as well as programs with ownership violations.

## Declared lifetime-region graphs

Lifetime return diagnostics can carry `SafeSharp.RegionNames`, `SafeSharp.RegionConstraints`, `SafeSharp.RequiredRegion`, `SafeSharp.SourceRegions`, `SafeSharp.RegionProof`, and `SafeSharp.RegionComponents`. HTML, DOT, and Mermaid render a graph from the explicit names and declared constraints. An arrow from `a` to `b` means the declaration `a : b`: `a` outlives `b`. Repeated declarations share one visible edge; the original metadata remains intact.

The graphs never turn proof paths, reflexive implications, missing constraints, or component membership into additional declared edges. A declared cycle remains a cycle; isolated declared regions remain visible. HTML uses bounded reachability to place cyclic groups, but the paths it draws still enumerate only the recorded declarations. Source and required regions have both color and text labels, including a combined label when a region has both roles. Origin categories such as `<Unknown>` or `<Local>` remain textual evidence and do not become invented vertices or collide with regions named `Unknown` or `Local`.

The HTML graph is a self-contained SVG with accessible labels, a legend, and complete region/edge tables. DOT and Mermaid use generated identifiers and retain diagnostic metadata as separate notes. Every format escapes source-derived labels, and JSON/SARIF preserve the original properties exactly.

Graph previews accept at most **64 named regions**, **256 distinct declared edges**, and **65,536 characters per graph-input property**. Malformed endpoints, missing essential metadata, empty named-region sets, and larger inputs produce an explicit explanation followed by the diagnostic properties. The reporter does not draw a partial graph that could conceal a required edge. These presentation bounds do not change the analyzer's region-solver budgets.

The [region-report tests](../tests/SafeSharp.Tests/Reporting/RegionReportTests.cs) verify exact topology, empty and disconnected graphs, cycles and self edges, role collisions, malicious metadata escaping, bounded fallbacks, deterministic output, accessible SVG identifiers, and machine-report property preservation.
