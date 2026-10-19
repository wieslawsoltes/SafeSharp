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

The phase is a flags lattice: a branch join may retain `Reserved | Active`. Reads and new shared borrows are then diagnosed as possible conflicts. Activation on every incoming branch produces an active loan. Loan identity, phase, parent ancestry, absence, and activation evidence participate in joins and fixed-point equality; activation is not stored in an external instruction-side cache. Reports expose `LoanSnapshot.Phase` and immutable `ActivationSites`, and JSON/HTML include those values.

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

For a potentially throwing `DisposeAsync` invocation, the frontend can split the protocol:

1. Emit `Dispose` with `AsyncDispose` to record the attempted release.
2. Emit the invocation's exception boundary.
3. On the successful-return edge, emit `BeginAsyncDispose` with `DisposalAlreadyInvoked` to create the completion without repeating the owner's release checks.

This ordering does not create a nonexistent awaitable on a synchronous-throw edge. At an await, observation precedes propagation of an asynchronous fault: awaiting a failed operation still observes its completion. This verifies the caller's release and observation protocol; it does not prove that a failing disposal implementation released every physical resource. Pending completions may span suspension points; the owning resource's no-suspension restriction does not automatically apply to the completion value.

### Reports and precision limits

Snapshots and JSON expose `ResourceSnapshot.Kind`, lifecycle `Status`, and immutable `DisposedResourceIds`. Those provenance links do not make an already-disposed owner a live scope dependency of its completion. HTML displays owner disposal and pending/completed obligations separately. SARIF contains the protocol diagnostic and source-grounded evidence.

As with ordinary ownership resources, completion identities are allocation sites. Repeated initiation at a site with a still-pending previous completion is diagnosed; a live older alias makes that finding possible because the abstraction does not count an unbounded number of concrete task instances. Completing each iteration before starting the next reaches a safe fixed point. Observation is idempotent in this ownership model: rules concerning whether a particular `ValueTask` implementation permits repeated awaits are a separate API contract. Transferring a completion through an unmodeled heap or method boundary does not prove that its recipient will observe it.

## Alternative alias origins

`MergeAlias(target: result, source: candidate)` means that the target may retain its previous value or take the candidate value. It complements `Alias`, which still replaces its target. This supports a borrowed return whose declared lifetime can be supplied by more than one input.

The engine constructs both state alternatives, expires loans according to each alternative's live references, then joins them. Consequently, a temporary loan selected only on one alternative is possibly live rather than definitely live. A candidate retained by another live alias remains live independently of the result choice. Backward liveness treats the prior target as an input as well as a definition. Reference absence and resource correlations are retained; choosing one of two independently pending completions cannot accidentally observe both.
