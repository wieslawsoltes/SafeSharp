# ValueTask consumption capabilities

SafeSharp reports `SS0048` when a represented `ValueTask` or `ValueTask<T>` is
consumed after a previous consumption or transfer. This is an independent protocol
analysis: a `ValueTask<int>` and a non-generic `ValueTask` need the same protection as
a task that produces an owned disposable object.

## Supported operations

| Operation | Capability effect |
| --- | --- |
| Input parameter, external getter/call, object construction, asynchronous disposal completion | Introduces a capability with a stable allocation-site identity |
| Assignment, flow capture, `ConfigureAwait`, `GetAwaiter` | Copies the same capability identity |
| `await`, `Result`, awaiter `GetResult` | Consumes the capability before the completion can succeed, fault, or be canceled |
| `AsTask`, `Preserve` | Consumes the original capability and produces a reusable carrier with the same completion and payload identity |
| `Ownership.Move` into a local | Invalidates previous local copies while preserving the destination's capability |
| Return, ownership-consuming call, escaping store | Transfers the represented capability |
| Branch join | Retains both available and consumed possibilities; a later use receives possible certainty |
| Repeated allocation in a loop | Creates a fresh capability only when no other live alias retains the preceding allocation-site instance |

```csharp
var pending = ReadAsync();        // ValueTask<int>
var copy = pending;
int first = await pending;
int second = await copy;          // SS0048

var reusable = ReadAsync().AsTask();
int x = await reusable;
int y = await reusable;           // permitted
```

`Preserve` does not copy a disposable result. Both successful awaits recover the
same result identity, so disposing that result between awaits still produces an
ordinary use-after-disposal diagnostic.

The diagnostic includes acquisition and earlier consumption/transfer evidence.
JSON state snapshots expose `valueTasks` with capability identity, possible state,
represented aliases, origin, and evidence sites. HTML reports show the same facts
in a dedicated capability table. These facts remain separate from resources and
loans, and cannot introduce a disposable leak for a scalar asynchronous result.

## Completion and exception semantics

Consumption occurs before the operation's modeled fault boundary. Retrying the
same `ValueTask` in a catch block therefore retains its consumed state. Observing
a failure does not materialize an owned payload; successful result reception
continues to use the existing asynchronous carrier/result model.

The capability domain follows the documented general consumption contract even
when a particular implementation happens to be backed by a reusable `Task` or an
inline result. An explicit `AsTask` or `Preserve` makes reusable intent visible.
`default(ValueTask)` has no underlying single-use source and introduces no
capability. SafeSharp does not prove the completion status required for synchronous
`Result`/`GetResult`, model arbitrary `IValueTaskSource` internals, or diagnose
every unknown external method that might consume an unannotated argument. Such
methods require a `[Consumes]` contract for ownership transfer. Capabilities also follow represented array elements and nested carrier caches; returning a consumed capability through those containers remains invalid. See [Task composition and aggregate identities](async-task-composition.md).

## ValueTasks received from tasks

A represented `Task<ValueTask<T>>` has one result capability even when the task's
producer is opaque. Two successful receives through the task, a configured
alias, or its `Result` property therefore cannot create independent permissions
to consume the same `ValueTask`. This identity fact follows from the framework
Task result cache; it does not assert that the payload is a fresh owned resource.

Capability caches participate in nested transfers and liveness. A fresh execution
of a carrier allocation site clears a dead earlier cache. If an older carrier
alias remains live, the cache retains weak alternatives so a new iteration cannot
reactivate a previously consumed result. Completed producers such as
`Task.FromResult` likewise preserve earlier represented payloads when one
allocation site summarizes multiple retained carriers.

## Design and primary references

The finite product domain stores capability identities in places independently
of resource facts. Its join is monotone over available, consumed, and transferred
alternatives. Stable alias identity and strong-update checks prevent loops or
simple struct copies from reviving a consumed capability.

This is an original C# implementation informed by Rust's separation between a
future's state and `Future::Output`; it is not a claim that .NET tasks have Rust's
polling, scheduling, or destructor semantics. Rust permits completed futures to
reject repeated polling, while the concrete contract enforced here comes from
.NET's `ValueTask` APIs.

- [.NET ValueTask<T> consumption contract](https://learn.microsoft.com/dotnet/api/system.threading.tasks.valuetask-1)
- [.NET ValueTask consumption contract](https://learn.microsoft.com/dotnet/api/system.threading.tasks.valuetask)
- [ValueTask.Preserve and use of the original value](https://learn.microsoft.com/dotnet/api/system.threading.tasks.valuetask.preserve)
- [Rust 1.91.0 Future::Output and completed-future polling contract, pinned commit](https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/library/core/src/future/future.rs)
- [Rust 1.91.0 borrow-checker dataflow, pinned commit](https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_borrowck/src/dataflow.rs)
