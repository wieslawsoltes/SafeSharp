# Deferred asynchronous result ownership

SafeSharp follows the owned result of a recognized `Task<T>` or `ValueTask<T>` factory through local storage, aliases, branches, loops, and supported framework wrappers. A successful receive acquires `T`. Storing the task itself neither acquires the payload early nor satisfies its eventual cleanup obligation.

```csharp
[ReturnsOwned]
static async Task<Connection> OpenAsync()
{
    await ConnectTransportAsync();
    return new Connection();
}

static async Task UseAsync()
{
    var pending = OpenAsync();
    var configured = pending.ConfigureAwait(false);
    using var connection = await configured;
    connection.Send();
}
```

The return contract can be explicit (`ReturnsOwned` or an owned return annotation) or established by SafeSharp's finite source summary. The framework type and supported wrappers are identified by semantic symbols and their framework assemblies. A similarly named user type or awaiter does not acquire this model automatically.

## The carrier and its output are separate identities

The ownership IR contains four effects. They operate inside the existing ownership fixed-point solver; the frontend does not need an additional task-origin CFG pass.

| Effect | When the frontend emits it | Result |
| --- | --- | --- |
| `BeginAsyncFactory` | After a recognized factory invocation returns its carrier successfully. | Creates an `AsyncFactoryCarrier` with `Pending` status and `Deferred` result state. The potential output's type flags are retained. No output resource exists yet. |
| `ObserveAsyncFactory` | At a result-producing `await`, `Result` read, or typed framework `GetResult`, before its modeled fault boundary. | Records that completion was observed. The carrier becomes `Completed`, but the output remains `Deferred` unless it was already received. |
| `ReceiveAsyncResult` | Only on the successful continuation after that fault boundary. | Materializes an owned output, or recovers the same cached output if it was already materialized. The carrier's result state becomes `Materialized`. |
| `BindAsyncResult` | After a recognized completed-result producer wraps an existing value, or an asynchronous borrowed-return contract identifies an existing output source. | Caches that existing identity, preserving its ownership, aliases, null state, and loans without allocating another output. Known completed producers use `Completed`/`Materialized`; a promised borrowed result uses `Pending`/`Materialized` with `DeferredAsyncResult`. |

A synchronous exception before a factory returns creates neither carrier nor output. An asynchronously faulted or canceled completion is observed without inventing a successful result. On the normal edge, a discarded `await` or `GetResult` still materializes the output and therefore retains the requirement to dispose or transfer it.

The carrier status and result state are separate finite domains. `Completed` means that completion was observed; it does not assert that `T` was produced. `Materialized` means that the result identity is represented: this can be a successfully produced owned value or an existing alias promised by a borrowed-return contract. It does not establish that a promised borrow's task has completed. At a join, `Deferred | Materialized` retains both identity alternatives. This distinction also survives ownership transfer and repeated execution of a factory site.

An output first produced by a deferred factory uses a stable identity derived from the carrier's allocation-site identity. A completed producer instead retains the identity of its already available value. A task is not an owning disposal scope for that output: disposing the `Task` cannot invalidate, release, or observe the contained resource. Output type contracts such as `NoSuspend` become active after successful production. A live task that can recover an already materialized `NoSuspend` result keeps that result relevant to suspension analysis.

`Task.FromResult<T>`, `ValueTask.FromResult<T>`, and `new ValueTask<T>(value)` bind a statically tracked `T` that already exists before the await. They preserve an owned allocation, an external value, or a borrowed loan instead of inventing freshness. Consequently, wrapping a `NoSuspend` resource does not postpone its contract until a later receive, and putting a borrowed value in a completed task does not grant permission to dispose or transfer that value. The framework `ValueTask<T>(Task<T>)` constructor preserves the existing task carrier.

## Borrowed outputs and carrier aliases

The source-summary contract distinguishes an asynchronous output's origin from the origin of its carrier. `MethodSummary.ReturnsAsyncPayload` identifies the former. Explicit owned/borrowed return contracts on the supported generic framework types, actual `async` returns, and inferred transparent result producers describe the output. A nonasync identity method such as `T Identity<T>(T value) => value` describes the returned value itself; when instantiated with a task, its source remains a carrier alias.

For a borrowed output, the lowerer binds a loan of the declared or inferred source into a pending carrier. The source already exists, so its ownership, disposal state, and suspension restrictions remain active while the output can be recovered. Receiving the result preserves that loan, and disposing it remains **SS0009**. A faulted receive creates no new owned allocation. The same behavior applies to supported property and conversion return contracts.

```csharp
static Task<Connection> Echo(Connection value) => Task.FromResult(value);

static void Read(Connection connection)
{
    var task = Echo(connection);
    var loan = task.Result;
    loan.Dispose();             // SS0009: the returned output is borrowed.
}
```

A loan of an opaque task does not establish any alias to the task's unknown output. Receiving that output creates an external result identity. If a carrier alias already represents a known factory, it preserves that factory's cached output instead. The summary's finite operation-site carrier model unwraps one represented layer for each supported await or result observation; this does not supply arbitrary task-combinator semantics to the ownership engine. See [asynchronous source-origin summaries](async-origin-summaries.md) for its finite origin graph and exact cross-method inference rules.

An inferred fresh result crossing a method boundary remains a deferred factory promise. The caller does not infer synchronous completion merely because the callee's source contains a completed producer. Directly visible framework producers retain their already available output immediately.

## Aliases, repeated reception, and moves

The same `Task<T>` returns the same cached `T` through `await`, `Result`, or `GetAwaiter().GetResult()`. SafeSharp therefore preserves one payload identity across those operations:

```csharp
var pending = OpenAsync();
var first = await pending;
var second = await pending;
first.Dispose();
second.Send();                  // SS0001: the same output is already disposed.
```

The cached reference participates in ordinary ownership move invalidation. Receiving again cannot regenerate a resource whose earlier result was moved or consumed:

```csharp
var pending = OpenAsync();
var first = await pending;
using var owner = Ownership.Move(first);
var stale = await pending;      // SS0004: the cached result is a stale alias.
```

Returning or consuming a pending carrier transfers responsibility for receiving its future result. If the result has already been received, transferring the carrier also transfers the associated payload responsibility. A carrier move invalidates older payload aliases; a local move preserves access through the destination carrier. Live loans of the payload prevent transferring the carrier, and returning a task whose payload was disposed during `finally` or `using` cleanup reports the disposed result at the return boundary.

The result cache is an analysis fact, not a newly invented C# local that owns cleanup. It is considered a recoverable handle only when a carrier remains usable in the relevant liveness context. An abandoned task cache cannot suppress an overwrite diagnostic indefinitely. Final borrowed-return validation reads represented outputs after `finally` cleanup without transferring a borrowed value. Ordinary task metadata reads do not dereference the output.

## Supported reception and wrapper forms

| Form | Treatment |
| --- | --- |
| `await task` or `await valueTask` | Observes completion, then receives the result only on success. |
| A local alias or a CFG flow capture of a carrier | Preserves the carrier and payload identity. |
| Framework `ConfigureAwait` | Preserves the represented carrier through the configured awaitable and its awaiter. |
| Framework `ValueTask<T>.AsTask()` | Preserves result identity in the resulting task wrapper. |
| Framework `ValueTask<T>.Preserve()` | Preserves result identity in the returned reusable carrier. |
| Framework `Task.FromResult<T>(value)`, `ValueTask.FromResult<T>(value)`, or `ValueTask<T>(value)` | Binds an already available statically tracked output without duplicating ownership; an existing output loan remains live while the carrier can recover it. |
| Framework `ValueTask<T>(Task<T>)` | Preserves the supplied task carrier and its current output state. |
| Framework `GetAwaiter()` followed by typed `GetResult()` | Preserves the carrier through the awaiter and receives the output after a successful `GetResult`. The awaiter can be stored first. |
| Framework `Task<T>.Result` or `ValueTask<T>.Result` | Receives the result on the successful property-read edge. |
| Return, a declared consuming argument, or modeled persistent storage | Transfers the carrier's responsibility; a materialized output follows that transfer. |
| A conditional choice between exclusive factory allocations | Receives the corresponding conditional outputs without inventing a requirement to dispose a result from the unselected branch. |
| Repeated factory creation in a loop | Uses the existing finite allocation-site abstraction; receiving and disposing each iteration converges, while abandoning earlier pending carriers or outputs is diagnosed. |

`Wait()` remains a completion observation for SafeSharp's separate asynchronous **disposal** protocol. It does not claim an owned factory output. Waiting without retrieving `T`, reading `IsCompleted`, discarding the task, or disposing the `Task` therefore does not establish cleanup of an owned result. Timeout and cancellation waits do not prove successful reception either.

The analyzer models payload ownership when `ValueTask<T>` is used, but does not establish its single-consumption API discipline. Repeated arbitrary `ValueTask` use, mixed `AsTask`/`await` consumption, and use before completion remain separate rules of the framework API. [`Preserve()`](https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.valuetask-1.preserve) provides a carrier suitable for repeated future use, subject to its requirement that the original value is no longer used. Preserving the same abstract payload on repeated use prevents an ownership reset; it does not validate every sequence of `ValueTask` consumption.

## Diagnostics and evidence

An unreceived owned factory carrier reports **SS0003 with `Possible` certainty**, because the asynchronous computation might fault instead of producing a resource. The message identifies the missing reception or transfer obligation, rather than claiming a definitely created resource. **SS0011** reports overwriting the last usable carrier or abandoning a pending generation at a repeated factory site.

After successful reception, the output participates in the ordinary rules: **SS0003** for an undisposed output, **SS0001/SS0002** for use or disposal after disposal, **SS0004** for stale moved aliases, **SS0005** for transfer while a loan remains live, and **SS0016** for a live `NoSuspend` output across suspension. An already allocated output inside a completed producer retains these obligations immediately, and its leak can be definite. The carrier's own task-disposal interface is not confused with the output's disposal obligation, and disposal-completion **SS0017** remains a separate protocol.

Evidence records distinguish the factory invocation, completed-result binding, completion observation, successful result production, later transfer, and reachable exit. JSON snapshots add `resultState`, `producedResourceIds`, and a singular `producedResourceId` when the identity is unique for `AsyncFactoryCarrier` resources. HTML resource tables show the same phase and payload links; ordinary instruction snapshots and the other graph/report formats retain the four explicit effects. Existing resource fields, kind values, and previously assigned IR enum values remain compatible; the new enum values are appended.

The engine counts the carrier and its possible output against the tracked-resource budget. Cancellation, convergence limits, diagnostic replay after convergence, and snapshot capture settings use the existing ownership engine gates. Exhausting a budget still produces an incomplete-analysis result rather than treating a missing result fact as proof of safety.

## Boundaries

The model covers the recognized factory's immediate `T`, not arbitrary `Task.WhenAll`, `WhenAny`, `ContinueWith`, custom schedulers, task unwrapping, nested task combinators, or user-defined awaiter semantics. Such APIs need explicit models of their output identity and ownership protocol. It does not infer cleanup simply because a method accepts a task or observes it internally without a consuming contract.

For an uncontracted task result, the frontend creates an external result identity after reception. It does not infer that the payload aliases the task object or that the payload is freshly owned. Borrowed payload contracts retain their source loans in a distinct carrier, while borrowed task-carrier aliases do not become payload aliases. Arbitrary task inputs, heap recovery through unknown aliases, reflection, and external method behavior remain subject to the documented source-summary and heap boundaries.

The analysis is a may/must product over allocation sites, not a count of concrete task instances. Multiple live generations from one loop site can therefore retain conservative alternatives. It does not predict runtime completion, cancellation, physical cleanup success, or whether a `ValueTask` implementation permits a particular sequence of consumption operations.

## Rust and framework references

This is an original C# adaptation of the ownership separation between an asynchronous computation and its eventual output. Rust's [`Future::Output` and `poll`](https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/library/core/src/future/future.rs) distinguish pending computation from a ready output. The reference is pinned to Rust 1.91.0, commit `f8297e351a40c1439a467bbbb6879088047f50b3`; no Rust source text was copied into the implementation.

C# task behavior requires its own protocol: a task may already be executing before it is awaited, and a `Task<T>` retains its result for subsequent receptions. Rust's prohibition on polling a completed future is not transplanted onto C# `Task<T>`. See Microsoft's [async return types](https://learn.microsoft.com/en-us/dotnet/csharp/asynchronous-programming/async-return-types), [`Task<TResult>`](https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.task-1), [`ValueTask<TResult>` consumption requirements](https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.valuetask-1), and [`ValueTask<TResult>.AsTask`](https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.valuetask-1.astask).

Regression coverage lives in `Core/AsyncFactoryCarrierTests`, `Core/CompletedAsyncResultTests`, `Ownership/AsyncFactoryTests`, `Ownership/DeferredAsyncFactoryTests`, `Ownership/KnownAsyncResultProducerTests`, `Ownership/BorrowedAsyncResultTests`, and `Reporting/AsyncFactoryReportTests`, alongside the existing asynchronous disposal, source-summary, and call protocol suites.
