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

Each by-value argument and instance receiver is captured into a stable alias when evaluated. Reassigning the caller's local while evaluating a later argument cannot change the value already passed to the callee. Inferred disposal and returned-borrow effects use those captured values. `ref`, `out`, and other by-reference arguments continue to designate storage.

`[Consumes]` and `[Owned]` argument transfers occur at call entry after every argument has been evaluated. A later argument can still read the original owner before this transfer. If a later argument throws, the callee has not received ownership and the caller retains its cleanup obligation on that exceptional path. Explicit `Ownership.Move` remains an evaluation-time operation and invalidates earlier aliases immediately.

Explicit `Ownership.BorrowMut` and ordinary `ref` argument borrows remain immediately active. All passed loan values stay live through the call, including explicit borrows supplied to unannotated parameters, so evaluating a later argument cannot silently end an earlier explicit loan. Returned borrow contracts derive their result from the applicable call loan before the caller releases the temporary parameter or receiver holder. This preserves parent ancestry without keeping an otherwise dead loan alive for the remainder of the lexical scope.

Property and indexer getters follow the same protocol. Getter arguments carry their declared input loans, a `[Mutates]` getter reserves and activates its receiver, and a returned borrow can be bounded by the receiver or several index arguments. Input sources are mapped through getter parameter ordinals, including inherited interface contracts with renamed implementation parameters. The result derives from those temporary loans before their holders are released.

Built-in receiver view inference recognizes `Memory<T>.Span`, `ReadOnlyMemory<T>.Span`, and the actual `IMemoryOwner<T>.Memory` member or implementation. A hiding property does not inherit the interface member's lifetime relation. Other memory-typed getters need an explicit or inferred contract to establish a receiver loan. An opaque getter produces an independent external result at its call site, rather than inventing a receiver dependency from the property's name or return type.

The IR reserves the `$t:call-` place prefix for these compiler call-value snapshots. `ReleaseLoan` with the same `Source` and `Target` keeps a snapshot live through its release without adding a semantic resource read, which would be invalid after some consuming or disposing effects. The frontend preserves returned aliases and loans before releasing the snapshots. Exceptional argument evaluation can abandon a snapshot before its normal release executes; the overwrite checker ignores that snapshot only when backward liveness proves it dead. Live snapshots and ordinary program aliases remain valid retained handles. The [Core call-value tests](../tests/SafeSharp.Tests/Core/CallValueSnapshotTests.cs) cover dead, live, and returned aliases independently of the Roslyn adapter.

### Relationship to Rust

The design follows Rust's distinction between reserving an implicit mutable borrow and activating it at use. Rust applies this to selected compiler-created borrows, including mutable method receivers; explicit mutable references remain immediate. See the [Rust Compiler Development Guide on two-phase borrows](https://rustc-dev-guide.rust-lang.org/borrow-check/two-phase-borrows.html) and the pinned [Rust 1.91.0 borrow-set implementation](https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_borrowck/src/borrow_set.rs).

SafeSharp computes possible phases through its abstract CFG state. It does not import Rust MIR, use Rust's exact dominance query for activation, or claim that arbitrary C# method calls satisfy Rust's lifetime guarantees. The frontend must emit reservation and activation only for operations whose contracts justify those effects.

## Source summaries and executable conversions

Source return and `out` postconditions use a finite may-origin analysis over Roslyn control-flow graphs. Origins distinguish fresh allocations, each method input, the receiver, null, and unknown values. A result is inferred to be owned only when every non-null alternative is fresh; a borrowed result must be bounded by known input or receiver origins. Unknown branches, recursion without a proven summary, reference-storage aliases, and exception-handler bodies do not establish a new ownership postcondition. Explicit contracts remain available for those boundaries.

Parameter storage identity includes its declaring method as well as its ordinal. An outer parameter captured by a local function cannot be confused with that local function's independent parameter zero. Compound assignments, replacement operators, deconstruction, `ref`/`out` calls, receiver evaluation, and object initializers invalidate the affected source origins when they may replace storage. A postfix replacement can still return its independently proven pre-update value.

User-defined conversions are calls. The frontend evaluates the operand, applies the conversion operator's input ownership and borrowing contracts, records its exception boundary, and derives the returned owner or borrow from the operator's own summary. It does not assume that `(Target)source` returns the same resource merely because both types are tracked. A conversion which provably creates a fresh disposable transfers that new obligation to its caller; an opaque conversion result remains external. Built-in reference-preserving conversions retain their source origin in summary inference.

Direct disposal inference is deliberately narrow: only an initial sequence of synchronous calls disposing the method's own parameters is recognized. Inference stops at an opaque intervening statement, since argument evaluation or a synchronously executed callback may replace a captured parameter before a later disposal. A disposal performed through a user-defined or potentially throwing explicit conversion is not attributed to the unconverted input. Captured outer parameters likewise never establish disposal postconditions for unrelated local-function inputs. Iterator methods have no eager disposal summary because invoking them does not start their bodies.

Inferred cleanups retain their original body order, including repeated disposal of one input. The caller graph places an exception boundary after each cleanup. If the first cleanup throws, a later input can still carry a possible leak through the caller's catch block; successful calls discharge every proven cleanup. Named arguments do not change the order of effects inside the callee. Collapsing the sequence to a set or applying all effects before one exception boundary would lose these distinctions.

The [source summary tests](../tests/SafeSharp.Tests/Ownership/MethodSummaryTests.cs) cover positive origin proofs and invalidated postconditions. The [call protocol tests](../tests/SafeSharp.Tests/Ownership/CallProtocolTests.cs) also verify conversion result identity, fresh conversion ownership, and consuming operator parameters.

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

## Ownership transferred into callbacks

Passing a delegate to a `[TransfersToThread]` parameter moves its tracked captured resources at the invocation boundary. The contract itself supplies this transfer; callers do not need to wrap every capture in `Ownership.Move`. Existing caller aliases become unusable, borrowed captures cannot escape through the boundary, and the caller's cleanup obligation transfers before the invocation can throw, following the same convention as `[Consumes]`.

A compilation-scoped adapter resolves original semantic operations, including direct lambdas, local functions, and visible local delegate aliases. It finds each transferred callback's actual captured symbols independently of analyzer callback order and seeds those symbols as owned inputs when analyzing that callback body. Disposal, return or consuming transfer, asynchronous disposal completion, and missing cleanup then use the ordinary ownership engine. An explicit borrowed parameter contract remains borrowed in the callback, preserving its restrictions even after an invalid attempted transfer. Ordinary shared callbacks receive no transferred ownership inputs.

Conditional or coalesced delegate choices retain alternative targets. A capture moves definitely only when every resolved alternative transfers it; otherwise the caller retains a possible move and possible cleanup obligation. Each callback body receives only its own captures. Stable captured reference aliases established by direct, reference-preserving local initializers share one incoming resource identity and one caller transfer. This also recognizes alternatives that capture different stable aliases of the same resource. Reassigned cells, user-defined conversions, `as` conversions, and alias chains through uncaptured intermediate storage do not establish this cross-callback identity proof.

Static fields and properties are shared storage and never become moved captures. A direct ordinary instance method group transfers its tracked receiver, but its method body remains a trusted cleanup boundary because that body can also run through ordinary calls. Unknown delegate implementations remain an explicit concurrency-analysis boundary. Local-delegate resolution unions visible initializer and assignment targets; it does not prove the exact target selected at every invocation. A callback used in both transfer and other contexts receives the transferred ownership precondition for its transfer context; this is not a full context-specialized interprocedural analysis of every invocation.

Exception traversal stops at each callback's own Roslyn CFG root. A callback's `try`, `catch`, and `finally` regions retain their cleanup behavior; the creator's enclosing `using` or `finally` regions are not executed as part of that independent callback body.

Capture resolution and individual callback input discovery each allow **20,000 steps**. Enclosing-scope discovery allows **20,000 operations**, **256 transfer boundaries**, and at most **128 visible targets per argument**. Exhausting one of these adapter limits raises `AnalysisBudgetException`; the compiler analyzer reports `SS0015` rather than silently claiming that missing transfer or cleanup effects were analyzed. These bounds are independent of the configurable structural `Send`/`Sync` analysis budget. Cancellation is checked throughout discovery.

The [transferred callback tests](../tests/SafeSharp.Tests/Ownership/TransferredCallbackTests.cs) verify accepted cleanup, stale caller aliases, rejected borrowed captures, callback leaks and use after disposal, stable resource aliases, conditional transfer certainty, local function and delegate targets, shared storage, nested cleanup regions, and explicit budget exhaustion.

## Declared lifetime-region graphs

Lifetime return diagnostics can carry `SafeSharp.RegionNames`, `SafeSharp.RegionConstraints`, `SafeSharp.RequiredRegion`, `SafeSharp.SourceRegions`, `SafeSharp.RegionProof`, and `SafeSharp.RegionComponents`. HTML, DOT, and Mermaid render a graph from the explicit names and declared constraints. An arrow from `a` to `b` means the declaration `a : b`: `a` outlives `b`. Repeated declarations share one visible edge; the original metadata remains intact.

The graphs never turn proof paths, reflexive implications, missing constraints, or component membership into additional declared edges. A declared cycle remains a cycle; isolated declared regions remain visible. HTML uses bounded reachability to place cyclic groups, but the paths it draws still enumerate only the recorded declarations. Source and required regions have both color and text labels, including a combined label when a region has both roles. Origin categories such as `<Unknown>` or `<Local>` remain textual evidence and do not become invented vertices or collide with regions named `Unknown` or `Local`.

The HTML graph is a self-contained SVG with accessible labels, a legend, and complete region/edge tables. DOT and Mermaid use generated identifiers and retain diagnostic metadata as separate notes. Every format escapes source-derived labels, and JSON/SARIF preserve the original properties exactly.

Graph previews accept at most **64 named regions**, **256 combined distinct assumption/requirement edges**, and **65,536 characters per graph-input property**. Malformed endpoints, missing essential metadata, empty named-region sets, and larger inputs produce an explicit explanation followed by the diagnostic properties. The reporter does not draw a partial graph that could conceal a required edge. These presentation bounds do not change the analyzer's region-solver budgets.

The [region-report tests](../tests/SafeSharp.Tests/Reporting/RegionReportTests.cs) verify exact topology, empty and disconnected graphs, cycles and self edges, role collisions, malicious metadata escaping, bounded fallbacks, deterministic output, accessible SVG identifiers, and machine-report property preservation.

## Stored asynchronous results

`BeginAsyncFactory`, `ObserveAsyncFactory`, `ReceiveAsyncResult` and `BindAsyncResult` distinguish an asynchronous carrier from its produced value. The analysis materializes a deferred factory's output on the successful receive continuation; this does not delay the actual .NET task's execution. Known completed producers retain existing resources and loans. Cached results preserve disposal/move history across repeated receipt, and transfer of a carrier also handles its materialized output. Possible unreceived output obligations use SS0003/SS0011, while SS0017 remains specific to disposal completions. See [async result ownership](async-result-ownership.md) for the full transfer table and supported boundaries.

## Universal delegate evidence

SS0047 checks annotated delegate conversions with a separate assumed universal graph and required callback graph. The [universal solver](universal-regions.md) rejects a requirement path that relates independent caller regions without a corresponding assumption. HTML, DOT and Mermaid draw declared assumptions as solid edges and exact requirements as dashed edges. The shortest proof and generated-to-signature name bindings remain available as diagnostic metadata. The [delegate guide](lifetime-delegates.md) covers shortened inputs, returned loans, invariant ref/out slots and captured receivers.
