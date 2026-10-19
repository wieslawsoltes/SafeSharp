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
| `using`, `await using`, `try`/`finally` | Roslyn's finally regions execute through explicit continuation contexts, including returns and nested finalizers. |
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

Exception routing is conservative: potentially matching catch bodies are explored, filter side effects are not credited as guaranteed cleanup, and helper cleanup on an unhandled exceptional path may lose precision around pending finalizers. Unknown external or recursive helper side effects are an interprocedural trust boundary. Normal-exit checking does not prove physical cleanup after an unhandled exception, process termination, reflection, unsafe memory access, native callbacks, or concurrent mutation of the same fields. See [the architecture boundaries](architecture.md) for the core flow engine's separate exception model.

SS0021 includes `SafeSharp.CleanupProof=normalExitMustRelease`, `SafeSharp.CleanupOwner`, and `SafeSharp.CleanupEntrypoints` diagnostic properties. Related locations preserve cleanup entrypoints, representative exits, and the member declaration. The CLI preserves these properties in JSON, SARIF, and HTML reports.

### Analysis limits

```editorconfig
[*.cs]
safesharp_max_contract_steps = 20000
```

`safesharp_max_contract_steps` is a positive integer, read from per-file configuration before global configuration. Its default is 20,000 and its maximum accepted effective value is 1,000,000. Invalid values use the default. The aggregate checker also limits an owner to 512 tracked members, 32 active helper summaries, 256 cached summaries, and 8,192 control-flow continuation contexts. These are analysis bounds, not guarantees about runtime iteration counts.

Reaching a bound emits informational SS0046; the safety property has not been established. Existing SS0021 findings can accompany this diagnostic. The CLI treats SS0046 as incomplete analysis even when its visible severity is suppressed.

## Relationship to Rust

Rust's drop elaboration uses initialization and move information to decide where destruction is required, including conditional drops. SafeSharp instead tracks explicitly declared C# disposal obligations and checks explicit cleanup methods. It does not insert C# cleanup, replace the garbage collector, or adopt Rust's destructor scheduling. The implementation takes the control-flow obligation idea from [Rust's drop elaboration documentation](https://rustc-dev-guide.rust-lang.org/mir/drop-elaboration.html); see the repository's [pinned Rust source references](rust-design.md) for the broader design mapping.

Rust distinguishes transferable owned values (`Send`) from values whose shared references can cross threads (`Sync`). Their language-level meaning depends on Rust's aliasing and mutability rules. SafeSharp's forthcoming explicit contracts retain this distinction while checking C# structure and declared callback boundaries; they are not Rust auto traits. Primary references are the [Send API](https://doc.rust-lang.org/std/marker/trait.Send.html), [Sync API](https://doc.rust-lang.org/std/marker/trait.Sync.html), and [Rustonomicon discussion](https://doc.rust-lang.org/nomicon/send-and-sync.html).
