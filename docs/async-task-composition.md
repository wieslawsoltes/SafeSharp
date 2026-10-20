# Task composition and aggregate result identities

SafeSharp recognizes the actual framework symbols for `Task.WhenAll`,
`Task.WhenAny`, `TaskExtensions.Unwrap`, and `Task.WaitAsync`. The models retain
input identities and separate construction, observation, and successful result
reception. A method with the same name does not acquire these contracts.

This is an original C# abstract interpretation informed by Rust's separation of
`Future::Output` from polling and by its ownership transfer rules. It does not
copy Rust's execution semantics into C#: .NET tasks can be shared, `WhenAny`
leaves losing tasks running, and stopping `WaitAsync` does not cancel the source
operation.

## Implemented effects

| Operation | Successful completion | Failure or cancellation | Transfer out of the method |
| --- | --- | --- | --- |
| `WhenAll<T>` | Each represented input yields its original result at the corresponding result-array index. Repeated awaits retain the same array and payload identities. | No fresh owned result is asserted. Unreceived producer obligations remain possible, including siblings that may have succeeded. | The represented producers and already represented payloads transfer with the result-bearing proxy. |
| Non-generic `WhenAll` | Every represented disposal completion is observed. Converting `Task<T>` to `Task` does not make its owned `T` disappear from the analysis. | A completed `WhenAll` has observed every represented disposal completion even if one faults. Owned result production is still not assumed. | Known cleanup completion responsibilities can transfer; a discarded generic payload does not gain a disposal or transfer proof. |
| `WhenAny` | The result is an alternative among the existing input tasks. It does not receive a task's payload or complete its cleanup obligation. | Input responsibilities remain unchanged. | One candidate transfers definitely; multiple candidate identities transfer only possibly, preserving possible loser obligations. |
| `Unwrap` | Successful reception passes through the outer and inner carriers, retaining the terminal identity. Nested owned source summaries remain deferred until the relevant success edges. | A failed observation does not invent an inner carrier or terminal payload. | A result-bearing proxy carries its represented producer responsibility. |
| `WaitAsync` | The proxy receives the same represented result as the source task. A successful wait can observe represented cleanup. | Timeout or cancellation may leave the source running, so its ownership/cleanup obligations remain. | Returning a cancellable wait proxy alone does not transfer an unresolved source operation's responsibility. |

The last rule deliberately exposes code such as:

```csharp
[return: Owned]
Task<Resource> CreateAsync();

Task<Resource> GetAsync(CancellationToken cancellationToken)
    => CreateAsync().WaitAsync(cancellationToken);
```

If the wait is canceled, the source operation can still produce a resource. The
caller has only the canceled proxy. SafeSharp retains the source obligation and
can report `SS0003`. Keep the original task accessible and provide a cleanup or
transfer path for the outcome after cancellation.

## Snapshot inputs; preserve mutable result aliases

The framework enumerates `WhenAll` and `WhenAny` input collections at invocation.
SafeSharp snapshots represented input array elements at that point:

```csharp
var first = Task.FromResult(new Resource());
var second = Task.FromResult(new Resource());
var inputs = new[] { first };
var combined = Task.WhenAll(inputs);
inputs[0] = second;

var result = await combined;
result[0].Dispose(); // Disposes first's resource, not second's.
```

Local arrays use allocation identities and canonical index caches. Copies of an
array reference read and write the same represented element. A `WhenAll<T>`
result array is a stable aggregate: reading an element through another array
alias or awaiting the same proxy again does not produce a fresh owner.

```csharp
var combined = Task.WhenAll(CreateAsync());
var first = await combined;
first[0].Dispose();
var again = await combined;
again[0].Read(); // SS0001: the original result is already disposed.
```

Fixed array initializers, explicit constant-index stores, multidimensional
constant indices, aliases, fixed array/span collection expressions, and the
two-task `WhenAny` overload are represented. This includes the implicit collection
expressions used when C# 13 selects a `params ReadOnlySpan<Task>` overload.
Unknown indices produce alternatives; they do not prove that every element was
visited or disposed. Allocation sites retained across loop iterations use weak
updates so a new array cannot silently reactivate an older aliased element.

An unmodeled call can replace an array element. SafeSharp weakens represented
element contents before the call's possible exception boundary, including for
array values passed through `object`, interfaces, constructors, and writable
reference arguments. Cleanup through a later element read therefore cannot
definitively discharge an owner that the call may have removed from the array:

```csharp
var values = new[] { new Resource() };
Unknown(values);       // May replace values[0], even before throwing.
values[0].Dispose();   // The original resource can still have an SS0003 obligation.
```

An independently captured resource value remains available for explicit cleanup.
The input snapshot already captured by `WhenAll` or `WhenAny` also retains its
original task identities. Trusted async operations, explicit shared `[Borrowed]`
parameter contracts, and modeled framework array queries preserve their stated
semantics. Ordinary source helpers do not acquire a no-mutation promise merely
because their source is available: general array-element effect summaries remain
a boundary. This conservative content invalidation does not infer arbitrary
callee disposal, retention, or replacement ownership.

Exposing an element's storage through `ref`, `out`, or an explicit `in` argument
also weakens the represented contents. This includes conditional ref expressions
lowered through CFG captures. Mutable element references currently use the general
storage domain rather than a coherent canonical array cell, so a write through
such a reference must not leave a stale exact cleanup proof in the array cache.
Values copied before the address exposure and task inputs already captured by a
composition keep their independent identities. Ordinary by-value conditional
element reads retain their precision.

ValueTask consumption capabilities also follow represented array elements and
nested carrier caches. Copying a `ValueTask` out of the same element twice retains
one capability, and returning an already consumed capability inside an aggregate
can report `SS0048`. Opaque `Task<ValueTask<T>>` inputs also retain one cached
capability across repeated receives, without an inferred fresh-ownership claim.

## Reports and bounded execution

The language-neutral IR includes explicit aggregate creation, element reads and
writes, composition construction, and successful composition completion.
Snapshots and JSON reports expose:

- The trusted composition kind and captured dependency resource IDs.
- Stable received result identities and their completion/result-production state.
- Canonical array indices, resource IDs, and ValueTask capability IDs.
- Whether one aggregate allocation site represents multiple retained instances.

HTML reports show composition dependencies and aggregate element identities next
to the ordinary resource, reference storage, loan, and ValueTask tables. CFG
reports retain the source-grounded construction and success-boundary instructions.

Graph traversal uses an iterative queue and a visited reference map, including
allocation-site cycles. The finite domain joins possible aliases instead of
recursively expanding an unbounded concrete heap. The existing resource budget
is checked both before analysis and against actual materialized states; exceeding
a limit reports `SS0015` rather than silently dropping dependencies.

## Explicit remaining boundaries

- Arbitrary `IEnumerable<Task<T>>`, custom collection implementations, unknown
  span contents, spread elements, callbacks, `ContinueWith`, and custom schedulers do not gain an invented
  per-element ownership shape. Their unrepresented results remain external.
- A dynamic-index loop or `foreach` does not yet prove complete element coverage.
  The analysis can therefore retain possible element leaks even when a programmer
  knows that every element was disposed.
- Source method summaries currently carry terminal provenance and nested carrier
  depth. They do not serialize the full composition dependency graph. Direct
  framework calls have the semantics above; an otherwise opaque helper returning
  a composition requires a suitable explicit contract or deeper summary support.
  In particular, a helper returning `WaitAsync` is never collapsed to an alias of
  its source task.
- A faulted `WhenAll` can contain successful siblings. SafeSharp keeps unresolved
  obligations instead of claiming that all results were produced or all siblings
  failed. It does not infer which individual tasks succeeded from an exception.
- Unknown-index and external heap contents remain conservative. Precise mutable
  ref aliases into aggregate element caches and arbitrary container ownership
  contracts require additional storage models; exposing an element address is
  conservatively invalidated until that integration exists. An unrepresented structural
  projection cannot reuse its enclosing aggregate's element or task-result cache.
- Runtime readiness, races with concurrent array mutation, custom awaiter or
  builder internals, and scheduler behavior are not proven by this sequential
  ownership domain.

## Primary design references

- Rust `Future` at pinned Rust 1.91.0 revision
  [`f8297e351a40c1439a467bbbb6879088047f50b3`](https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/library/core/src/future/future.rs).
- Rust borrow-checker dataflow at the same revision:
  [`compiler/rustc_borrowck/src/dataflow.rs`](https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_borrowck/src/dataflow.rs).
- [.NET `Task.WhenAll`](https://learn.microsoft.com/dotnet/api/system.threading.tasks.task.whenall),
  [`Task.WhenAny`](https://learn.microsoft.com/dotnet/api/system.threading.tasks.task.whenany),
  [`TaskExtensions.Unwrap`](https://learn.microsoft.com/dotnet/api/system.threading.tasks.taskextensions.unwrap),
  and [`Task.WaitAsync`](https://learn.microsoft.com/dotnet/api/system.threading.tasks.task.waitasync).
- [C# 13 overload resolution prefers params span overloads](https://learn.microsoft.com/dotnet/core/compatibility/core-libraries/9.0/params-overloads)
  and [Roslyn collection-expression operations](https://learn.microsoft.com/dotnet/api/microsoft.codeanalysis.operations.icollectionexpressionoperation).
- [.NET array queries and mutation APIs](https://learn.microsoft.com/dotnet/api/system.array),
  including [`Initialize`](https://learn.microsoft.com/dotnet/api/system.array.initialize).
- [.NET `ValueTask<TResult>` consumption contract](https://learn.microsoft.com/dotnet/api/system.threading.tasks.valuetask-1)
  and [C# `await` semantics](https://learn.microsoft.com/dotnet/csharp/language-reference/operators/await).
