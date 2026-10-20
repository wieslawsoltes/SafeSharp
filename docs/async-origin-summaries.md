# Source summaries for asynchronous results

SafeSharp keeps the identity of an asynchronous carrier separate from the provenance of its result. A newly constructed `Task<Resource>` is a newly constructed task; that fact alone does not establish ownership of the `Resource` which its delegate might eventually return. A source summary must prove the appropriate value before callers receive an ownership or borrowing postcondition.

This distinction applies to source inference, borrowed escape checking, returned lifetime checking, and annotated delegate compatibility. The ownership engine's handling of pending, completed, aliased, and repeatedly observed result carriers is documented in [Asynchronous result ownership](async-result-ownership.md).

## Carrier identities and payload promises

These two helpers establish different relationships:

```csharp
static T Identity<T>(T value) => value;

static Task<Resource> Payload(Resource value) => Task.FromResult(value);
```

For `Identity(task)`, the result is the same task carrier supplied by the caller. When that carrier has a known result, forwarding the carrier preserves that result. An arbitrary task parameter does not by itself establish the identity or lifetime of its result.

For `Payload(resource)`, the returned task contains the supplied resource. The source summary describes the resource which can be obtained from the task. A caller must not dispose this borrowed result as if the call created a new resource, and its returned lifetime must remain tied to the resource argument.

The internal `MethodSummary.ReturnsAsyncPayload` flag records this distinction. It is set for proven result provenance from an actual `async Task<T>` or `async ValueTask<T>` body, transparent framework producers, and explicit result contracts on those framework carrier types. An ordinary task identity retains value provenance without introducing another payload layer. Existing `[ReturnsOwned]`, `[ReturnsBorrowed]`, and returned `[Lifetime]` contracts on `Task<T>` and `ValueTask<T>` describe the result payload.

The distinction also matters for delegate conversions. A returned `Resource` lifetime promised by a `Task<Resource>` delegate cannot be justified merely because the callback returns a task whose carrier was a delegate argument. Inferred carrier aliases do not become inferred payload borrows.

## Implemented framework boundaries

`AsyncResultOriginModel` resolves the framework definitions once per compilation and compares Roslyn symbol identities. Source declarations with matching names and namespaces do not acquire framework producer behavior. The model accepts the established .NET framework assemblies, including the `System.Threading.Tasks.Extensions` implementation used by older targets.

| Operation | Source provenance behavior |
| --- | --- |
| `Task.FromResult<T>(value)` and `ValueTask.FromResult<T>(value)` | Store the evaluated value in one result layer. |
| `new ValueTask<T>(value)` | Store the evaluated `T` value in one result layer. |
| `new ValueTask<T>(task)` | Preserve a proven result carrier for the matching `Task<T>` constructor; an arbitrary task parameter supplies no inferred payload postcondition. |
| `ConfigureAwait`, `AsTask`, `Preserve`, and `GetAwaiter` on recognized framework carriers | Preserve the result-carrier identity through the appropriate wrapper. |
| `await`, `Task<T>.Result`, `ValueTask<T>.Result`, and recognized awaiter `GetResult` | Extract exactly one known result layer. |
| Generic or nonasync identity helper | Preserve the argument value, including any already-known carrier identity. |
| Actual `async Task<T>` or `async ValueTask<T>` return expression | Describe the returned `T` payload directly; the compiler supplies the outer carrier. |
| Nontransparent `Task<T>` construction, including a generic task subclass | Supply no inferred result provenance. A task allocation cannot prove its delegate's result. |

Known wrappers preserve provenance; they do not make claims about timing, successful completion on every execution, or the legality of repeatedly consuming a source-backed `ValueTask<T>`. The ownership engine handles its supported completion effects separately.

For example, each of these source bodies proves a fresh `Resource` result:

```csharp
static Task<Resource> CreateAsync()
    => Task.FromResult(new Resource());

static ValueTask<Resource> CreateValueAsync()
    => new ValueTask<Resource>(CreateAsync());

static async Task<Resource> UnwrapAsync()
    => await Identity(CreateAsync()).ConfigureAwait(false);

static Resource CreateSynchronously()
    => Task.FromResult(Task.FromResult(new Resource())).Result.Result;
```

In contrast, these bodies do not establish resource ownership:

```csharp
static Task<Resource> StartLater(Resource external)
    => new Task<Resource>(() => external);

static Resource ReadUnknown(Task<Resource> unknown)
    => Identity(unknown).Result;

static ValueTask<Resource> AdaptUnknown(Task<Resource> unknown)
    => new ValueTask<Resource>(unknown);
```

An explicit contract remains the mechanism for a producer whose body cannot supply a supported proof. Contracts must describe the producer's real behavior; the absence of an inferred postcondition does not establish safety.

## Finite carrier graph and source order

The origin solver retains the existing finite origins for a fresh value, null, an input parameter, the receiver, and an unknown value. It adds a separate payload map indexed by producing operation sites. A value can point to a carrier site, and that site's payload can in turn point to another site. Local values, storage references, flow-capture targets, and payload entries remain separate dataflow maps.

This representation has three useful properties:

1. A value snapshot retains the payload evaluated when its producer ran. Reassigning the original local or a `ref` alias afterward does not rewrite an already-created task's result.
2. A loop can create a cycle in the carrier graph without creating an unbounded sequence of nested origin strings. Operation sites are interned once, and immutable singleton origin sets are reused.
3. Multiple allocations from the same operation site may still be reachable through older aliases. Their payload entries therefore merge possible origins rather than replacing an earlier iteration's payload.

For example, this method still returns the first resource:

```csharp
static Resource Snapshot(Resource external)
{
    Resource local = new();
    ref Resource alias = ref local;
    var task = Task.FromResult(alias);
    alias = external;
    return task.Result;
}
```

Ordinary storage assignment can strongly update the local. The stored result origin remains the earlier allocation. Target-typed `new()` is preserved through Roslyn's object-creation conversion when the constructed operand and converted value have the same type; it is not lost merely because that conversion is classified separately from identity and reference conversions.

At control-flow joins, value and storage entries retain unknown information when required. A missing payload entry has a different meaning: that allocation site did not exist on that predecessor. It does not poison the known payload of a carrier created on another predecessor. When both predecessors can reach the same allocation site, their payload origins are united.

Fresh ownership is inferred only when every possible non-null returned origin is fresh. A fresh-or-external alternative remains unproven. Borrowed summaries likewise require every possible non-null origin to map to a known input or receiver; an unknown alternative prevents a borrowed-source assertion.

## Nested results and conservative boundaries

Explicit extraction inside one analyzed body can follow more than one known layer. Each extraction removes one carrier layer, so `Task<Task<Resource>>` is not treated as though it were `Task<Resource>`.

The method-summary contract exports bounded terminal provenance together with its carrier depth. Parameter terminals preserve an existing carrier identity, while fresh terminals remain deferred until every required successful receive occurs. Null carriers and carriers containing null remain distinct. The maximum exported depth is 16; cyclic and unrepresentable graphs remain opaque rather than being flattened into a fresh resource. See [nested asynchronous ownership](nested-async-ownership.md).

Further conservative boundaries remain:

- An unknown task parameter, field, or producer has no inferred payload origin.
- Direct framework Task combinators use the [composition domain](async-task-composition.md). Their dependency and cancellation semantics are not flattened into terminal-depth method summaries.
- A `ValueTask<T>` backed by an unmodeled `IValueTaskSource<T>` has no inferred payload origin.
- Custom awaitables and user-defined lookalikes require supported contracts; their `GetAwaiter` names do not establish framework behavior.
- Handler-bearing bodies, writable effects, ref-returned storage, and opaque callback mutations retain the limits described in [Source origin summaries](source-origin-summaries.md).
- Recursive source inference, deeply nested expressions, and large control-flow problems are bounded. The current per-body origin solver allows 100,000 steps, 4,096 worklist visits, and expression depth 128; recursive summary inference is limited to depth 24. Exhaustion produces an opaque summary with no guessed ownership or borrowing postcondition.

These limits describe what source inference can prove. They do not turn an unsupported body into a successful proof of lifetime or ownership safety.

## Rust design references

This is original C# code informed by Rust's separation of a future value from its output and by indexed dataflow over program places. It does not import Rust compiler source or impose Rust's runtime execution model on C#.

- [`Future::Output` and the `Poll::Ready` result in Rust 1.91.0](https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/library/core/src/future/future.rs) distinguish the asynchronous value from the value it yields. SafeSharp applies that distinction to carrier provenance; C# task scheduling, completion, and repeated observation have separate .NET semantics.
- [Rust 1.91.0 MIR move paths](https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_mir_dataflow/src/move_paths/mod.rs) provide the indexed-place design reference used by the source origin solver.
- [Rust 1.91.0 initialization effects](https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_mir_dataflow/src/impls/initialized.rs) motivate keeping storage effects and value facts distinct.
- [`.NET Task.FromResult`](https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.task.fromresult) and [`ValueTask<TResult>` implementation](https://source.dot.net/System.Private.CoreLib/src/runtime/src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/ValueTask.cs.html) define the framework producer and constructor behavior modeled here.

Regression tests cover real Roslyn source summaries and their caller effects: evaluated payload snapshots, strong storage updates through refs, branch and loop joins, actual async bodies, generic carrier identities, configured awaiters, nested results, unknown task constructors, task subclasses, framework lookalikes, borrowed escape evidence, and ownership diagnostics at caller source spans. The companion async result ownership and annotated delegate suites exercise the shared summary flag through the ownership engine and lifetime checker.

The framework compatibility suite also compiles fixtures against the actual `NETStandard.Library` 2.0.3 reference assembly and `SafeSharp.Contracts`, without importing the .NET 10 test host's runtime assemblies. It verifies that task definitions belong to `netstandard` and exercises owned receipt, configured awaiting, and borrowed payload results against those symbols. Ordinary non-generic `Task` factories keep their existing disposable-carrier ownership behavior.
