# Synchronization capabilities and guarded references

SafeSharp checks declared synchronization obligations with `SS0049`. The analysis proves that a matching capability is held on every represented incoming path before a guarded access. It also checks acquisition/release balance, caller-supplied capabilities, protected reference escapes, and suspension while a thread-affine lock might be held.

This analysis is separate from structural `[Send]` / `[Sync]` checking. Adding `[GuardedBy]` does not make an otherwise invalid `[Sync]` declaration valid. It describes a local synchronization convention; it does not prove whole-program race freedom, deadlock freedom, fairness, or the behavior of arbitrary external implementations.

## Contracts

```csharp
using SafeSharp;

sealed class Counter
{
    private readonly object gate = new();

    [GuardedBy(nameof(gate))]
    private int value;

    public int Increment()
    {
        lock (gate)
            return ++value;
    }

    [RequiresLock(nameof(gate))]
    private void IncrementWhileLocked() => value++;

    [GuardedBy(nameof(gate))]
    private int Value
    {
        get => value;
        set => this.value = value;
    }
}
```

`[GuardedBy]` applies to fields, properties, and returned references. A property contract is also a precondition for its accessors: its getter assumes read access and its setter assumes write access. A reference read from guarded storage retains the obligation in locals, captures, and supported ref aliases. Copying an immutable value, such as an `int`, ends that obligation after the protected read.

`[RequiresLock]` applies to methods, constructors, and delegates. Calls must establish the requirement; the body assumes it. `LockAccess.Read` accepts a reader, upgradeable reader, or writer capability for a reader/writer lock. The default `LockAccess.Write` requires an exclusive/writer capability. Monitor, `System.Threading.Lock`, and proven binary `SemaphoreSlim` permits provide exclusive capabilities in either case.

A guard path can start at `this`, a by-value reference parameter, or a readonly static field. Further components must be readonly, nonvolatile reference fields. Properties, mutable fields, boxing-dependent/value-type roots, dynamic roots, unresolved type parameters, writable or readonly by-reference parameter roots, empty components, and paths longer than 32 fields do not establish a stable contract. A local by-value snapshot can still hold the result of reading a mutable field.

Overrides and interface implementations inherit method, property, and return guards. Parameter roots are rebound by ordinal, so renaming an implementation parameter does not change the contract. An implementation cannot add a stronger method/property precondition or add a guard restriction to an inherited return promise. A delegate conversion must preserve the target method's lock precondition in the delegate signature; holding a lock while constructing a callback does not establish it when that callback later runs. Malformed delegate declarations are checked even when unused.

Constructor requirements are checked at `new` expressions, including parameter and static guards. A requirement on the new receiver itself cannot be satisfied by the caller before that object exists.

## Supported protocols

| Protocol | Acquisitions and observations | Releases and checks |
|---|---|---|
| `Monitor` | `lock` on an ordinary reference; `Enter`, `TryEnter`; `ref bool lockTaken` overloads; `IsEntered` | `Exit`; `Wait`, `Pulse`, and `PulseAll` require current-thread ownership. Normal `Wait` completion retains the same monitor capability. |
| `System.Threading.Lock` | Statically typed `lock`; `Enter`, `TryEnter`, `EnterScope`; `IsHeldByCurrentThread` | `Exit`; `Lock.Scope.Dispose`; lexical/using cleanup. Scope values copied before disposal remain distinct disposal obligations on the same underlying lock depth. |
| `ReaderWriterLockSlim` | `EnterReadLock`, `EnterUpgradeableReadLock`, `EnterWriteLock`; their `TryEnter` variants; `IsReadLockHeld`, `IsUpgradeableReadLockHeld`, `IsWriteLockHeld` | Matching mode exits. Reads accept read/upgradeable/write; mutation requires write. Direct read-to-write upgrades are diagnosed. |
| `SemaphoreSlim` | Successful `Wait`; successful observation of a represented `WaitAsync` task; Boolean timeout results refine acquisition. Task copies and supported awaiter wrappers retain one acquisition token. | `Release` checks permit counts; `Dispose` checks outstanding acquired or pending permits. Exclusive guarded access requires a proven `maxCount` of `1`. |

Framework intrinsics are matched by resolved type and member symbols from supported framework assemblies. Source-defined lookalikes named `System.Threading.Monitor`, `Lock`, `ReaderWriterLockSlim`, or `SemaphoreSlim` do not grant capabilities; unrelated custom awaitables do not establish semaphore acquisition.

The four protocols remain distinct even when they refer to the same object. For example, `Monitor.Enter(aThreadingLock)` and `lock ((object)aThreadingLock)` do not establish the `System.Threading.Lock` capability expected by a guard whose static type is `System.Threading.Lock`. Holding a Monitor lock on a `SemaphoreSlim` object likewise does not establish a semaphore permit. See [SemaphoreSlim capabilities](semaphore-capabilities.md) for binary-construction evidence, asynchronous acquisition tokens, release counts, and conservative protocol boundaries.

Recursive acquisition counts use finite intervals. A minimum greater than zero establishes a must-capability; the maximum preserves possible unreleased acquisitions. Counts saturate after 16 nested acquisitions, with an unbounded upper approximation. Deep recursion can therefore produce conservative diagnostics. The checker does not infer `ReaderWriterLockSlim.LockRecursionPolicy`: in particular, the mixed upgradeable-read plus read to write transition permitted by `SupportsRecursion` remains conservatively diagnosed. It does not certify every recursion-policy exception.

## Receiver identity and ref storage

Identity is semantic, not a comparison of source text. A readonly field path is interned using its field symbols and the evaluated receiver. Two instances' identically named `gate` fields are different capabilities. Assignments and ordinary aliases preserve an already evaluated object identity.

Mutable field reads are separate snapshots. This distinction matters when a field can be replaced:

```csharp
var snapshot = current;
lock (snapshot.Gate)
{
    snapshot.Update(); // Can satisfy a requirement on snapshot.Gate.
}

lock (current.Gate)
{
    current = replacement;
    current.Update();  // The previously acquired guard does not protect this receiver.
}
```

The method-local storage domain separates copied values from reference targets. It handles local/parameter ref aliases, ref rebinding, conditional target sets, captured addresses, and bounded source-proven ref-return parameter targets. Argument addresses are evaluated in source order and remain fixed while later arguments execute. A write through a single known target replaces that cell; a write through multiple possible targets conservatively updates all candidates. Opaque writable-ref effects invalidate identity, including on exceptional call paths. Ref aliases to external guarded storage retain their guard obligations without receiving a guessed stable heap identity.

Mutable captured locals lose their receiver and saved-condition proofs across calls that can execute arbitrary callbacks; a previously copied, uncaptured snapshot remains stable. Source allocation/read sites inside loops deliberately do not become must-alias identities for different runtime generations.

This guard-specific domain is separate from the ownership engine's [reference-storage analysis](reference-storage-analysis.md). It does not provide a complete heap, pointer, array-element, reflection, or native alias analysis.

## Branches, cleanup, and suspension

A `TryEnter` result carries a relational fact about the exact acquisition. Boolean copies, negation, and comparisons with Boolean constants preserve that fact. Another acquire/release invalidates historical facts for the same lock, so a saved `true` cannot restore a capability that has already been released.

The `lockTaken` relation survives a merge between acquisition failure and acquired cleanup paths. Compiler-generated initialization omitted from the public CFG is recognized only for the implicit Monitor lock pattern. An observed `IsEntered`/`Is...Held` capability is recorded as supplied by the caller, rather than creating a new release obligation. Releasing that caller-supplied capability is diagnosed.

The engine reuses the exception-normalized [source CFG router](source-summary-analysis.md): catches, filters, finally continuations, explicit throws, potential exceptional calls, returns, and ordinary exits are distinct routes. Filters execute before unwinding. Consequently, an outer filter cannot assume a lock was acquired when the acquisition itself may have thrown. Lexical `System.Threading.Lock` scopes are partitioned through filter continuations so the acquired and unacquired cases do not become a false cleanup proof.

Roslyn 4.14 can expose the valid C# `lock (System.Threading.Lock)` construct as an invalid CFG placeholder without an explicit scope-disposal edge. SafeSharp normalizes only that shape after verifying the original bound `ILockOperation` and exact framework type. It models the lexical scope's entry and normal/exceptional exit; arbitrary invalid operations never grant a capability.

A Monitor, `System.Threading.Lock`, or `ReaderWriterLockSlim` capability that may cross `await` or `yield` produces `SS0049`. These protocols have thread-affine ownership, so the continuation cannot reuse its former must-hold proof. A proven `SemaphoreSlim` permit retains its local capability across suspension because that protocol is not thread-affine. Merely obtaining a `WaitAsync` task grants no permit, and awaiting it while a thread-affine lock may be held still diagnoses that lock.

## Protected references and call boundaries

Reading a mutable reference from guarded storage attaches its guard obligation to the value. Using it after release, storing it in unguarded state, capturing it in a closure, or passing it to a parameter without `[DoesNotEscape]` is diagnosed. A destination with the same evaluated guard/protocol can retain it. A matching `[return: GuardedBy(...)]` contract carries the requirement to callers.

`[Borrowed, DoesNotEscape]` parameters accept a read capability. Ordinary potentially mutating reference arguments require write access. `[DoesNotEscape]` alone restricts retention and does not promise read-only behavior.

External contracts remain trusted boundaries. The checker does not infer arbitrary interprocedural lock acquisition/release effects, synchronous callback execution guarantees, or cross-thread schedules. A method body with a declared precondition is checked for preserving caller capabilities, but unavailable external bodies cannot be verified. Unknown return-object identities, arbitrary custom lock abstractions, and captured-local identities in separately analyzed callbacks remain conservative boundaries.

## Diagnostics, graphs, and bounds

`SS0049` identifies the member/access, required guard, protocol, and reason. Related source locations identify the declaration and represented acquisitions. Diagnostic properties retain:

- `SafeSharp.SynchronizationAccess`, `AccessMode`, `Required`, `RequiredIdentity`, `Protocol`, and `Reason`;
- a readable `Held` interval summary;
- `HeldCount` and `Held.{i}.Name`, `Protocol`, `Minimum`, and `Maximum` records (`many` represents the saturated upper bound).

HTML, DOT, and Mermaid reports show the declared requirement alongside the computed held state. Those diagrams are diagnostic witnesses, not thread schedules or proofs of race freedom. JSON and SARIF retain exact properties and related locations. Oversized or malformed graph metadata is not interpreted as a valid capability graph; the raw evidence remains available.

Analysis is compilation-local, caches resolved contracts and source summaries, interns identities as integers, and runs a queued forward fixed point. Diagnostics are emitted only after convergence. The configurable `safesharp_max_contract_steps` budget applies; additional limits include 4096 contextual states, 256 simultaneously tracked lock capabilities, operation depth 256, and 256 candidate targets for a reference binding. Budget exhaustion produces `SS0046` and withholds the incomplete synchronization result. It does not publish partial safety claims.

## Rust and runtime references

This implementation is original C#. Its guard-capability design follows the relationship between protected data, a live guard, and cleanup in Rust; it does not copy Rust source or give C# Rust's type-system guarantees.

- Rust 1.91's [`Mutex` / `MutexGuard` implementation](https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/library/std/src/sync/poison/mutex.rs): guard-mediated access and release on drop.
- Rust 1.91's [`RwLock` guards](https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/library/std/src/sync/poison/rwlock.rs): different capabilities for reading and writing.
- Rust's [`MutexGuard` documentation](https://doc.rust-lang.org/std/sync/struct.MutexGuard.html): thread-affine drop and the `!Send` boundary.
- Microsoft's [`lock` statement documentation](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/statements/lock) and [`Monitor.Enter` reference](https://learn.microsoft.com/en-us/dotnet/api/system.threading.monitor.enter): compiler lock patterns and acquisition flags.
- Microsoft's [`Lock.EnterScope` reference](https://learn.microsoft.com/en-us/dotnet/api/system.threading.lock.enterscope) and [.NET 10 `ReaderWriterLockSlim` source](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/Threading/ReaderWriterLockSlim.cs): disposal, mode, and recursion-policy semantics.

The CLR's explicit Monitor exits, reentrancy, filter-before-unwind behavior, object aliases, and callback dispatch require distinct C# transfer rules. Rust references explain the design lineage; they are not evidence that these runtime models are equivalent.
