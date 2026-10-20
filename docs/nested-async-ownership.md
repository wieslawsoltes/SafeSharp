# Nested asynchronous result ownership and custom await patterns

SafeSharp preserves the distinction between an asynchronous carrier and its result
through nested `Task<T>` / `ValueTask<T>` layers. A carrier can contain another
carrier, an already existing borrowed value, a fresh owned result, or null. Source
summaries retain the terminal provenance and the number of carrier layers, with a
maximum of 16 layers; unknown or cyclic shapes remain conservative.

## Nested results

```csharp
Task<Task<Resource>> Create() =>
    Task.FromResult(Task.FromResult(new Resource()));

async Task Use()
{
    var outer = Create();
    var inner = await outer;
    using var resource = await inner;
}
```

An inferred fresh terminal payload is deferred. Successfully awaiting the outer
carrier materializes only the next inner carrier. Successfully awaiting the last
carrier materializes the resource. A fault while observing the outer carrier
does not allocate an inner carrier or claim a successful resource acquisition.
The IR and JSON snapshots expose `asyncResultDepth` so this distinction can be
inspected directly.

Wrapping an existing carrier keeps its identity and cached payload:

```csharp
Task<Task<Resource>> Wrap(Task<Resource> input) => Task.FromResult(input);
```

`Wrap` does not prove fresh ownership of `input`'s result. Receiving the inner
carrier recovers the actual argument identity. Receiving the same payload again
therefore retains earlier disposal or move state.

The engine traverses represented payloads iteratively. It uses a finite visited
map, preserving may/must alias facts at cycles produced by allocation-site joins.
Loan retention, suspension checks, borrowed escapes, and carrier transfers follow
the entire represented chain. Returning a completed nested carrier transfers the
owned terminal value, while a nested borrowed value remains bounded by its owner.

## Custom await patterns

For an implicit `await`, the analyzer asks Roslyn for the compiler-selected
`GetAwaiter` and `GetResult` methods. It applies the actual `GetResult` ownership
summary after the successful completion boundary. An explicit `[ReturnsOwned]`
contract or a source-proven fresh return can therefore introduce an owned result.
An unknown custom `GetResult` produces an external value.

```csharp
public readonly struct Awaiter : INotifyCompletion
{
    public bool IsCompleted => true;
    public void OnCompleted(Action continuation) { }

    [ReturnsOwned]
    public Resource GetResult() => AcquireResource();
}
```

Receiver-bounded borrowing is supported when the selected `GetAwaiter` is proven
to return a borrow bounded by its receiver (or extension receiver) and `GetResult`
returns a borrow bounded by that awaiter. The resulting borrow remains tied to
the original awaitable owner. Allocating a custom awaitable or using a familiar
method name alone never establishes a payload-ownership contract.

Custom async builder internals, arbitrary awaiter storage layouts, and unknown
external transformations are outside this proof. Their `GetResult` methods need
an ownership/lifetime contract or an inferable source body.

## Rust design references

The implementation is original C# code. It adapts the separation between a
future's state and `Future::Output`, and finite dataflow over stable abstract
identities. It does not equate .NET task execution or disposal with Rust polling
or destructor behavior.

- [Rust 1.91.0 Future and Output, pinned commit](https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/library/core/src/future/future.rs)
- [Rust 1.91.0 borrow-checker dataflow, pinned commit](https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_borrowck/src/dataflow.rs)
- [Roslyn GetAwaitExpressionInfo](https://learn.microsoft.com/dotnet/api/microsoft.codeanalysis.csharp.csharpextensions.getawaitexpressioninfo)
- [C# await expression](https://learn.microsoft.com/dotnet/csharp/language-reference/operators/await)
- [ValueTask capability analysis](value-task-consumption.md)
- [Asynchronous result ownership](async-result-ownership.md)
