# Aggregate cleanup and concurrency contracts

SafeSharp checks explicit C# ownership conventions at Roslyn analysis time. The aggregate checker establishes whether each tracked owned member is released at every modeled **normal exit** of each disposal entrypoint. Concurrency contracts describe a separate property: which explicitly annotated values may cross a thread boundary or be shared there. These checks do not turn C# references into Rust references, change runtime assignment, or prove arbitrary code free from data races.

## Aggregate cleanup: SS0020–SS0022 and SS0046

An instance field or readable property is an aggregate obligation when it has `[Owned]`, or its type has `[Owned]`, and its type implements `IDisposable` or `IAsyncDisposable`. SS0020 identifies an owner without a disposal interface. SS0022 identifies copyable structs with ownership responsibilities. SS0021 checks the cleanup implementation.

```csharp
sealed class Document : IDisposable
{
    [Owned] private readonly Stream input;
    private readonly bool buffered;

    public Document(Stream input, bool buffered)
    {
        this.input = input;
        this.buffered = buffered;
    }

    public void Dispose()
    {
        if (buffered)
            return;                 // SS0021: this normal exit keeps input alive.

        input.Dispose();
    }
}
```

A cleanup statement being present somewhere in a method is insufficient. The checker follows Roslyn control-flow branches, retains an obligation when either incoming path keeps it, and computes a fixed point for loops. A loop that may execute zero times cannot establish cleanup performed only inside that loop. An unconditional throw has no normal exit; the checker makes no termination claim.

### Supported cleanup flow

| Construct | Treatment |
| --- | --- |
| Branches and early returns | Every feasible modeled normal exit must release each obligation. Constant booleans and direct null tests refine branches. |
| Loops | A worklist retains obligations across back edges. Distinct initial and replacement resources remain separate. |
| `using`, `await using`, `try`/`finally` | Roslyn's finally regions execute through explicit continuation contexts, including returns and nested finally blocks. |
| Local aliases and casts | Aliases of an exact tracked resource preserve its obligation identity. Cleanup of a different owner instance is insufficient. |
| Replacement and clearing | Assigning a new resource does not release the previous resource; clearing a live field to `null` also leaves its obligation outstanding. |
| Direct source helpers | Summaries carry guaranteed releases, writes, replacement obligations, outstanding async work, and conservative exceptional effects. Disposable parameters and `ref`/`out` storage are mapped to caller arguments. |
| Disposal pattern inheritance | Concrete owners include inherited owned members. Virtual dispatch, `base` calls, directly called helpers, local functions, and constant `Dispose(true)` arguments are modeled. |
| Multiple disposal interfaces | `IDisposable.Dispose` and `IAsyncDisposable.DisposeAsync` are checked independently. A complete synchronous method cannot excuse an incomplete asynchronous entrypoint. |
| Async completion aliases | Awaiting or returning a cleanup completion, configured aliases, `AsTask`, `GetAwaiter().GetResult()`, `Wait`, and `Task.WhenAll` observe recognized completion obligations. |
| Recovered exceptions | Potential faults reach conservative visible catch and finally routes. A catch that returns normally still needs cleanup. Source helper effects on exceptional exits are propagated. |

An abstract owner can leave implementation to a concrete derived owner. Each concrete owner is checked against all inherited obligations. A derived override that omits base cleanup receives SS0021 at the derived type, with the inherited field declaration in related locations.

The checked release is a call to the actual `IDisposable`/`IAsyncDisposable` implementation or its override, using the frontend's recognized disposal identity. A same-name unrelated method does not release the resource. Ref-struct disposal patterns follow the frontend's validated pattern rules.

### Async disposal has two obligations

Calling `DisposeAsync()` fulfills the **invocation** obligation for the original resource. If the call returns an awaitable, that result creates a separate **completion-observation** obligation. An await, synchronous completion observation, or forwarding return must account for it on every normal disposal exit. Ignoring the awaitable is insufficient even when the original resource was already disposed previously.

```csharp
public async ValueTask DisposeAsync()
{
    var pending = input.DisposeAsync();
    await pending.ConfigureAwait(false);
}
```

The two events match the core engine's `SS0017` async completion protocol. A disposer that throws synchronously before returning an awaitable does not create an awaitable-observation obligation. Once an await is observed, an asynchronous failure does not make it unobserved again. SafeSharp checks cleanup invocation and observation; it cannot establish that a throwing disposer actually released every physical resource in its implementation.

### Summary and precision boundaries

Summaries are specialized by constant boolean arguments, which supports the usual `Dispose(true)` pattern without assuming `Dispose(false)` cleans managed members. Recursive strongly connected calls receive no assumed release at reentry. Cleanup after a recursive call can still establish an obligation; recursion alone cannot establish one. Metadata-only bodies and unknown callbacks do not supply a cleanup proof.

The aggregate abstraction tracks explicit member and parameter resource identities. It does not provide whole-program heap alias analysis, symbolic collection-element ownership, arbitrary custom awaiter semantics, or constructor-to-dispose state invariants. For example, a private `_disposed` flag is not automatically related to an earlier cleanup invocation. An idempotent implementation guarded only by that flag may need a directly provable null-and-cleanup pattern or a reviewed diagnostic suppression. Correlations between different aliases or unrelated conditions may be lost at joins.

Exception routing is conservative: potentially matching catch bodies are explored, filter side effects are not credited as guaranteed cleanup, and helper cleanup on an unhandled exceptional path may lose precision around pending finally continuations. Unknown external or recursive helper side effects are an interprocedural trust boundary. Normal-exit checking does not prove physical cleanup after an unhandled exception, process termination, reflection, unsafe memory access, native callbacks, or concurrent mutation of the same fields. See [the architecture boundaries](architecture.md) for the core flow engine's separate exception model.

SS0021 includes `SafeSharp.CleanupProof=normalExitMustRelease`, `SafeSharp.CleanupOwner`, and `SafeSharp.CleanupEntrypoints` diagnostic properties. Related locations preserve cleanup entrypoints, representative exits, and the member declaration. The CLI preserves these properties in JSON, SARIF, and HTML reports.

### Analysis limits

```editorconfig
[*.cs]
safesharp_max_contract_steps = 20000
```

`safesharp_max_contract_steps` is a positive integer, read from per-file configuration before global configuration. Its default is 20,000 and its maximum accepted effective value is 1,000,000. Invalid values use the default. The aggregate checker also limits an owner to 512 tracked members, 32 active helper summaries, 256 cached summaries, and 8,192 control-flow continuation contexts. These are analysis bounds, not guarantees about runtime iteration counts.

Reaching a bound emits informational SS0046; the safety property has not been established. Existing SS0021 findings can accompany this diagnostic. The CLI treats SS0046 as incomplete analysis even when its visible severity is suppressed.

## Explicit concurrency: SS0044–SS0046

`[Send]` and `[Sync]` can annotate classes, structs, interfaces, and generic parameters. They are independent promises. A type that supports shared access may still be unsuitable for exclusive transfer; a type that supports exclusive transfer may contain mutable state that cannot be shared.

| Contract | Checked meaning |
| --- | --- |
| `[Send]` | The value's declared structure supports transfer under exclusive ownership. Mutable scalar state is permitted. Reference components must themselves be Send and must either be `[Owned]` or also support Sync. |
| `[Sync]` | Instance storage is readonly or init-only and each component supports Sync. This is a structural sharing check. |
| `[ThreadSafe]` | A trusted synchronization contract can supply Sync and permit mutable or otherwise opaque component storage. Its synchronization implementation is not proved. |
| `[ThreadAffine]` | Conflicts with both capabilities, including inherited contracts and contained source-visible affinity. ThreadSafe cannot erase thread affinity. |
| `[TransfersToThread] Action callback` | Declares an exclusive transfer boundary for captured values. Captures require Send. The ownership engine applies this explicit transfer contract to visible captures. |
| `[SharesAcrossThreads] Action callback` | Declares that captured values may be shared across threads. Captures require Sync. |

```csharp
[Send]
sealed class WorkItem
{
    public int Attempts;
}

[Send, Sync]
sealed class WorkDescription
{
    public string Text { get; }
    public WorkDescription(string text) => Text = text;
}

static void Enqueue([TransfersToThread] Action callback)
{
    // This API's implementation must uphold its declared transfer protocol.
}

static void Publish([SharesAcrossThreads] Action callback)
{
    // This API's implementation may invoke the callback on another thread.
}

static void Schedule()
{
    var item = new WorkItem();
    Enqueue(() => item.Attempts++);  // Send permits owned mutable state.

    var description = new WorkDescription("compile");
    Publish(() => Console.WriteLine(description.Text));
}
```

The boundary attributes are parameter contracts and require delegate-typed parameters. Declaring both on the same parameter is invalid. Interface and override parameter contracts are honored when the implementation omits its annotations. Forwarding a callback parameter to a boundary requiring the same capability preserves that checked precondition.

### Composition and generic requirements

Source declarations are checked through all instance fields, including compiler-generated auto-property storage and inherited fields. An inherited type or interface contract applies to concrete source implementations. Recursive annotated structures are checked coinductively without caching an assumed success before their dependencies have been checked. Metadata-only annotated components remain trusted external contracts; an external base without the required contract is not silently accepted.

```csharp
[Send]
sealed class WorkBatch<[Send] T>
{
    [Owned] private T item;
    public WorkBatch(T item) => this.item = item;
}

[Sync]
sealed class Snapshot<[Sync] T>
{
    public T Value { get; }
    public Snapshot(T value) => Value = value;
}
```

An annotated generic parameter is an assumption inside the generic body and a requirement at constructed type uses and generic method calls, including inferred method arguments. A type parameter can also obtain a capability from an appropriately annotated interface or base constraint. An arbitrary `T`, `object`, delegate, native pointer, or `dynamic` value has no inferred capability. A method name resembling a concurrency contract has no effect; attributes are resolved by symbol identity.

The intrinsic immutable list is deliberately small: Boolean and numeric scalar types, `char`, `string`, enums, and the actual framework `DateTime`, `DateTimeOffset`, `TimeSpan`, and `Guid` types. Nullable scalar wrappers and tuples compose their component capabilities. `IntPtr` and `UIntPtr` are not accepted as ordinary immutable scalar payloads because their native ownership is unknown. Arrays can be Send when their elements are Send, but are not Sync because their elements remain writable through shared references. A Send owner containing such an array must declare ownership of that field. Other collection types require explicit or external trusted contracts; there is no blanket claim that all BCL collections are safe.

Readonly fields protect their slots, not their referenced objects. For example, `readonly object state` cannot establish Sync; neither can `readonly int[] items`. A writable scalar field can establish Send, but requires trusted synchronization to establish Sync. Init-only auto-properties are treated as construction-time state. Reflection and unsafe writes can invalidate these conventions and remain outside the proof.

### Callback captures and cells

The analyzer resolves direct lambdas, local functions, instance method groups, conditional/coalesced callback expressions, and visible local delegate aliases and assignments. Alias resolution conservatively considers visible assignments in the containing operation body; it is not a whole-program delegate points-to analysis. An opaque delegate at an explicit custom boundary receives SS0045 because its captures cannot be established.

For ordinary `Task.Run`, the new sharing check applies when a captured value has an explicit Send, Sync, or ThreadSafe contract. Existing SS0025 continues to report ThreadAffine Task.Run captures. Unannotated Task.Run code does not automatically acquire a new universal concurrency requirement. A Send-only mutable value captured by Task.Run receives SS0045 because the ordinary closure shares access to that value.

Captured **variable cells** are checked separately from value types. A captured `int` is an immutable scalar value, but `() => counter++` still mutates shared closure storage. Writes, increments, `ref`/`out` exposure, and direct ref aliases of a captured variable are rejected at explicit boundaries. The scan is conservative across the containing body; an assignment that happens before scheduling can still require restructuring into a stable local. A method-group delegate stores its receiver value when created, so later reassignment of the original receiver variable is not itself a shared-cell write.

Static fields and properties remain shared storage even at a transfer boundary and require Sync. Mutable static slots are rejected. Capturing `this` checks the receiver's contract and structure. Nested callback bodies are included when collecting visible captures; values created entirely inside a callback do not become outer captures. `nameof` references do not count as runtime captures.

SS0044 identifies invalid declarations or generic composition. SS0045 identifies the precise captured value at a violating boundary. Both retain `SafeSharp.ConcurrencyCapability`, `SafeSharp.ConcurrencyPath`, and `SafeSharp.ConcurrencyReason`; related source locations preserve component declarations and the scheduling invocation.

### What these contracts establish

The checks establish the declared structural conditions at analyzed source boundaries. They do not prove lock correctness, atomicity, temporal publication, deadlock freedom, custom scheduler behavior, arbitrary static state accessed inside methods, unmanaged resource thread affinity, or unrestricted data-race freedom. ThreadSafe and external metadata contracts are trusted assertions. Transfer-boundary implementations must honor their declared exclusivity and callback protocol; an attribute does not schedule work, copy a closure by value, or prevent reflection from obtaining an alias.

Concurrency checks share `safesharp_max_contract_steps` with aggregate cleanup, with an independent budget for each declaration, generic-use check, or callback boundary. Recursive type expansion and delegate alias resolution additionally stop at depth 128; each source-visible affinity traversal examines at most 128 distinct types. SS0046 records an incomplete check and is not evidence that the contract was satisfied.

## Relationship to Rust

Rust's drop elaboration uses initialization and move information to decide where destruction is required, including conditional drops. SafeSharp instead tracks explicitly declared C# disposal obligations and checks explicit cleanup methods. It does not insert C# cleanup, replace the garbage collector, or adopt Rust's destructor scheduling. The implementation takes the control-flow obligation idea from [Rust's drop elaboration documentation](https://rustc-dev-guide.rust-lang.org/mir/drop-elaboration.html); see the repository's [pinned Rust source references](rust-design.md) for the broader design mapping.

Rust distinguishes transferable owned values (`Send`) from values whose shared references can cross threads (`Sync`). Their language-level meaning depends on Rust's aliasing and mutability rules. SafeSharp's explicit contracts retain this distinction while checking C# structure and declared callback boundaries; they are not Rust auto traits. Primary references are the [Send API](https://doc.rust-lang.org/std/marker/trait.Send.html), [Sync API](https://doc.rust-lang.org/std/marker/trait.Sync.html), and [Rustonomicon discussion](https://doc.rust-lang.org/nomicon/send-and-sync.html).
