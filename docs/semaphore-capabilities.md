# SemaphoreSlim permits and asynchronous guard capabilities

SafeSharp extends synchronization checking to the actual framework `System.Threading.SemaphoreSlim` APIs. The checker keeps semaphore permits separate from Monitor, `System.Threading.Lock`, and reader/writer capabilities. A permit is not bound to the acquiring thread, so a correctly acquired permit may protect an asynchronous continuation.

The checks use `SS0049`, the existing synchronization diagnostic. They operate on the exception-normalized Roslyn CFG and join only capabilities established on every represented incoming path. They check a local declared synchronization convention; they do not prove whole-program race freedom, scheduler fairness, deadlock freedom, or the effects of arbitrary external releases.

## Exclusive access requires a binary semaphore

```csharp
sealed class Cache
{
    private readonly SemaphoreSlim gate = new(initialCount: 1, maxCount: 1);

    [GuardedBy(nameof(gate))]
    private int revision;

    public async Task<int> UpdateAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Yield();
            return ++revision;
        }
        finally
        {
            gate.Release();
        }
    }
}
```

A successful wait on a counting semaphore does not establish mutual exclusion. The analyzer requires evidence that the two-argument constructor's `maxCount` is the constant `1` before treating a permit as an exclusive guard. The one-argument `new SemaphoreSlim(1)` constructor has no such bound and cannot satisfy this proof.

The evidence follows local aliases of directly constructed instances. It also recognizes readonly source fields whose initializer constructs a binary semaphore, provided every visible constructor assignment preserves that bound and no constructor exposes a ref alias, passes the field by writable reference, or replaces a containing struct through whole-`this` assignment. Constructor-only initialization, unavailable metadata initializers, arbitrary factories, mutable field invariants, and unknown maximum counts are conservative boundaries. An ordinary `CurrentCount` read cannot prove a maximum or the caller's permit ownership.

A `[RequiresLock(nameof(parameter))]` contract on a `SemaphoreSlim` parameter requires an exclusive binary permit at each checked call. The callee assumes that precondition and must preserve the supplied permit. This is a trusted boundary when the callee's source is unavailable, just like existing lock contracts. Holding a Monitor lock on the semaphore object never satisfies a semaphore permit requirement.

## Modeled operations

| Operation | Capability effect |
|---|---|
| `Wait()` and `Wait(CancellationToken)` | One permit after normal return; no new permit on the exceptional route. |
| Timeout overloads of `Wait` | A possible permit; the returned Boolean refines success versus timeout. |
| `WaitAsync` | Creates a pending acquisition token. Merely obtaining or copying its Task grants no held capability. |
| `await` of the represented task | Observes that token and grants a permit only on successful completion; a timeout result still requires a true branch. |
| `ConfigureAwait(bool)`, standard task awaiters, `GetResult`, and `Task<bool>.Result` | Preserve/observe the same acquisition token. These operations never create a second independent acquisition. |
| `Release()` and constant positive `Release(count)` | Require the represented number of permits on every incoming path and decrement that count. |
| `Release` with a nonconstant count | Reports an unestablished count and weakens the remaining held-state proof. |
| `Dispose()` | Diagnoses possible outstanding acquired or pending permits and removes the must-hold proof. |

For a timeout, use the returned Boolean:

```csharp
if (!await gate.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken))
    return;

try
{
    UseProtectedState();
}
finally
{
    gate.Release();
}
```

The await belongs before the unconditional cleanup region. If the acquisition is inside that region and fails or is canceled, an unconditional `Release` in `finally` attempts to release a permit that was not established. A Boolean initialized to false and checked in the cleanup path can express conditional ownership.

Counting semaphores still receive local acquisition/release balance checking. Their permits remain insufficient for exclusive `GuardedBy` access. Release counts do not constitute source-inferred cross-method transfer or producer/consumer signaling contracts.

## A reusable Task represents one acquisition

```csharp
var pending = gate.WaitAsync();
var alias = pending;
await alias;
gate.Release();
await pending;                 // This observes the same completed task.
UseProtectedState();           // SS0049: no permit is held here.
```

The analysis stores an acquisition token separately from the Task value. Copies and recognized awaiter wrappers retain the token. The first successful observation changes its state from pending to completed; later observations return the cached result without acquiring again. A later release invalidates historical success-to-held-state relations, while retaining the Boolean result itself where known.

Pending, completed, faulted, and absent alternatives join in the finite state. A join that mixes pending and already completed execution histories does not invent a fresh must-hold proof. Different conditional task values whose identity cannot be resolved remain conservative. Repeated allocation sites invalidate prior task aliases before describing a fresh runtime acquisition, so an old task cannot borrow a new loop iteration's success.

An unobserved pending acquisition may succeed even after its handle is discarded. Such a token remains an obligation, including on exception paths; awaiting another task does not discharge it. The checker diagnoses a pending acquisition that can leave the method without observation and matching cleanup. It does not mistake loss of the task handle for cancellation of the semaphore wait.

## Suspension and evidence

Semaphore permits survive `await` and `yield` in this sequential capability model. Other held capabilities retain their thread-affinity restrictions: awaiting a semaphore while a Monitor, `Lock`, or `ReaderWriterLockSlim` capability may be held still produces `SS0049` for that capability.

Diagnostic metadata and HTML, DOT, Mermaid, JSON, and SARIF evidence use the distinct `SemaphoreSlim` protocol. A held interval describes computed permit counts; it is not an execution schedule or a measurement of `CurrentCount`. Declaration and acquisition locations remain attached to the witness.

## Explicit boundaries and budgets

- Only actual framework type/member identities activate the model. Source classes with framework names and unrelated custom awaitables grant no semaphore capability.
- `ConfigureAwaitOptions` is opaque. In particular, suppression of exceptions cannot be treated as successful acquisition. Custom task transformations, `Task.WhenAll`/`WhenAny` wrappers, stored task fields, arbitrary helpers, and custom semaphore abstractions need further summaries.
- Unknown virtual/native/reflection effects, unmodeled callbacks, and concurrent disposal/release are outside this sequential model. A matching release abstracts successful protocol cleanup; physical cleanup after arbitrary runtime corruption or external disposal is not proved.
- A `SemaphoreSlim` can legally implement signaling with releases from other code that did not perform a local wait. Such patterns do not satisfy this local guard discipline and need explicit future transfer/signaling contracts.
- Guard counts use the existing finite interval with a saturated upper bound. Binary-construction proofs scan at most 20,000 constructor syntax nodes, and a method tracks at most 256 wait tokens in addition to the existing capability, contextual-state, reference-target, and operation-depth limits. All traversals check cancellation. Exhaustion produces `SS0046` and withholds partial safety claims.

## Design and runtime references

This is original C# code, not copied Rust source. The design uses a capability to justify protected access and a finite obligation to check release, analogous to the relationship between a guard and its protected state in Rust. The asynchronous semaphore token is a .NET-specific model: a reusable Task is neither a Rust mutex guard nor an extra permit.

- [.NET `SemaphoreSlim` constructors](https://learn.microsoft.com/dotnet/api/system.threading.semaphoreslim.-ctor) explain the distinction between initial count and maximum count.
- [.NET `WaitAsync`](https://learn.microsoft.com/dotnet/api/system.threading.semaphoreslim.waitasync) documents Boolean timeout results and exceptions retained in the returned task.
- [.NET `Release`](https://learn.microsoft.com/dotnet/api/system.threading.semaphoreslim.release) documents release counts and maximum-count failure.
- [.NET 10 `SemaphoreSlim` implementation](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/Threading/SemaphoreSlim.cs) supplies the runtime protocol reference.
- [Rust `MutexGuard`](https://doc.rust-lang.org/std/sync/struct.MutexGuard.html) explains guard-mediated access and why its standard-library guard is thread-affine. SafeSharp keeps that restriction for thread-owned .NET locks and deliberately uses a separate semaphore capability.

Regression coverage is in `SemaphoreSynchronizationTests` and `SynchronizationReportTests`; ordinary synchronization regressions continue to exercise the existing lock protocols.
