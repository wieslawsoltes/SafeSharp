# Ownership contracts and semantic analysis

SafeSharp has **five complementary analyzers and 39 implemented diagnostic rules**. All run as Roslyn diagnostics while the compiler analyzes C#.

| Analyzer | Responsibility | Descriptor category |
| --- | --- | --- |
| `OwnershipAnalyzer` | Resource identities, moves, loans, liveness, disposal, async completion, and control-flow transitions. | `Ownership` |
| `SafeSharpSemanticAnalyzer` | Aggregate normal-exit cleanup, declaration consistency, borrowed/callback escape, thread affinity, and dependency injection lifetimes. | `SafeSharp.Ownership` |
| `BoundarySafetyAnalyzer` | Exception/disposal boundaries, inherited parameter ownership compatibility, and opt-in audits of unsafe operations and unknown factories. | `Ownership` |
| `LifetimeContractAnalyzer` | Explicit named-region declarations, outlives solving, returned provenance, and inherited lifetime/return compatibility. | `SafeSharp.Lifetimes` |
| `ConcurrencyContractAnalyzer` | Send/Sync composition, generic capability requirements, and captures at declared thread boundaries. | `SafeSharp.Ownership` |

The [diagnostic reference](diagnostics.md) provides every rule, its default severity, trigger, and remediation. [Implementation status](implementation-status.md) distinguishes the implemented model from remaining language and library boundaries. `SafeSharpAnalyzerCatalog.Create()` supplies the full analyzer set for a custom Roslyn host.

`SafeSharp.Contracts` targets .NET Standard 2.0 and has no Roslyn dependency. Its attributes are ordinary metadata. `Ownership.Move`, `Ownership.Borrow`, and `Ownership.BorrowMut` return their input unchanged. These methods do not clear variables, wrap objects, perform reference counting, restrict runtime access, or make C# a language with affine types. Suppressing the analyzer also suppresses the corresponding checks.

## Contract vocabulary

All contracts live in namespace `SafeSharp`. Analyzer matching resolves the actual metadata symbols from the compilation and compares symbols with `SymbolEqualityComparer.Default`; a short name in an unrelated namespace is not a contract. Namespace aliases and fully qualified spellings preserve identity.

| Contract | Targets | Meaning |
| --- | --- | --- |
| `Owned` | Class, struct, field, property, parameter, return value | The annotated value or member carries ownership responsibility. A disposable aggregate member creates a release obligation. |
| `Borrowed` | Parameter, return value | Shared loan; ownership remains with the source. |
| `BorrowedMut` | Parameter, return value | Exclusive mutable loan; ownership remains with the source. |
| `Consumes` | Parameter | Passing the argument transfers ownership to the callee. |
| `ReturnsOwned` | Method, property | The caller receives ownership of the result. |
| `ReturnsBorrowed("parameter", mutable: false)` | Method, property | The returned loan is bounded by the named argument's lifetime. `"this"` selects an instance receiver. |
| `Lifetime("a")` | Parameter, return value | Names the method-scoped borrowed lifetime supplied by an input or promised by a result. |
| `Outlives("a", "b")` | Method; repeatable | Requires region `a` to remain valid for at least the duration promised by region `b`. |
| `Mutates` | Instance method | The method mutates its receiver. |
| `DoesNotEscape` | Parameter | The callee must not retain, return, or explicitly transfer ownership of the argument. |
| `MustUse` | Method, class, struct | An expression-statement result must be used or explicitly discarded. |
| `NoSuspend` | Class, struct | Values of this type must not remain live across suspension. |
| `ThreadAffine` | Class, struct | Access is tied to an owning thread or context. |
| `ThreadSafe` | Class, struct | A trusted declaration that sharing and use across threads are supported. |
| `Send` | Class, struct, interface, generic parameter | Declares structural suitability for transfer under exclusive ownership; does not itself create exclusive runtime ownership. |
| `Sync` | Class, struct, interface, generic parameter | Declares structural suitability for shared access across threads. |
| `TransfersToThread` | Delegate-typed parameter | Declares an exclusive transfer boundary for callback captures; requires Send and ownership transfer. |
| `SharesAcrossThreads` | Delegate-typed parameter | Declares a shared callback boundary; requires Sync and stable captured storage. |
| `ServiceLifetime(Transient/Scoped/Singleton)` | Class, interface | A service lifetime known without a dependency on a specific DI library. |

Thread-affinity and thread-safety contracts propagate through base types and generic type constraints. They describe access capabilities and must not be placed together on the same effective type. Send and Sync promises additionally apply through annotated interfaces; source implementations are checked against inherited structure. Send and Sync are independent: immutable sharing and exclusive transfer are different requirements, and ThreadSafe remains a trusted synchronization assertion.

The ownership adapter inherits parameter and method contracts from implemented interfaces and overridden methods, including associated getter-property contracts. Parameters are matched by ordinal, and named returned-borrow sources are remapped when an implementation renames its parameter. Omitted implementation annotations inherit the declaration's mode; an explicit incompatible parameter mode receives [SS0034](diagnostics.md#ss0034). Explicit lifetime names can also be consistently renamed, while [SS0042](diagnostics.md#ss0042) checks inherited lifetime/return promises. These checks do not establish full behavioral subtyping for arbitrary method effects.

```csharp
using SafeSharp;

public sealed class BufferOwner : IDisposable
{
    [Owned]
    private readonly MemoryStream stream = new();

    [ReturnsBorrowed("this")]
    public Stream View() => stream;

    public void Dispose() => stream.Dispose();
}

public static class BufferOperations
{
    [ReturnsOwned]
    public static MemoryStream Create() => new();

    public static long Inspect([Borrowed] Stream stream) => stream.Length;

    public static void Close([Consumes] Stream stream) => stream.Dispose();
}
```

The named returned-loan relation is especially important for library boundaries. C# return types do not encode Rust-style lifetime parameters, so the relation is carried by an attribute. A missing source name, a static method borrowing `"this"`, and conflicting shared/mutable/owned declarations produce SS0023. `Owned` and `Consumes` on the same parameter are compatible declarations of ownership transfer.

## Explicit lifetime regions

Lifetime contracts relate the duration of an input or receiver to a returned loan. Access capability remains separate: use Borrowed/BorrowedMut for shared/exclusive access and ReturnsBorrowed when the result must originate from one specific input.

```csharp
[Outlives("owner", "view")]
[return: Lifetime("view")]
static Stream View([Borrowed, Lifetime("owner")] Stream owner) => owner;
```

The region `owner` must outlive `view`. Names are scoped to the signature, repeated names share a region, and the reserved name `this` denotes an instance receiver. Outlives edges imply transitive relations; cycles form equivalent-region components. A constraint cannot introduce a name absent from the parameter/result signature.

The reusable `SafeSharp.Core.Regions` solver produces deterministic implications, components, and original-constraint proof paths. `LifetimeContractAnalyzer` checks actual returned origins over CFG assignments, aliases, branches, loops, supported calls, and conservative invalidation of replaced or unknown storage. All possible returned sources must satisfy the promised region, including when several inputs supply a result. A fresh local owner cannot establish an external borrowed lifetime, and an unknown source does not become valid merely because another branch is valid.

SS0040 validates declarations; SS0041 checks returned provenance and named-source promises; SS0042 checks interface/override compatibility; SS0043 reports an incomplete solve or flow analysis. These are explicit method-region proofs rather than Rust's complete higher-ranked type/lifetime system. See [lifetime-region contracts and APIs](lifetime-regions.md), [region graphs in reports](advanced-engine.md#declared-lifetime-region-graphs), and the [implementation matrix](implementation-status.md).

## Async cleanup and deferred completion

A recognized DisposeAsync invocation and observation of its returned completion are separate ownership effects. The resource's static cleanup obligation is discharged when disposal is invoked. A successfully returned awaitable creates a pending completion obligation that must be observed or transferred.

```csharp
var pending = owner.DisposeAsync();
var alias = pending;
await alias.ConfigureAwait(false);
```

SS0031 diagnoses a directly dropped disposal expression, including a direct discard or discarded configured awaitable. SS0017 follows the completion itself through supported locals, aliases, branches, overwrites, loops, and returns; storing the completion in an unused local does not establish observation. Await, recognized parameterless synchronous completion operations, and appropriately declared transfer boundaries account for the obligation. Timeout/cancellation waits, reading IsCompleted, and disposing the Task are not unconditional observation.

A synchronous throw before the awaitable is returned creates no completion obligation. Awaiting an asynchronously faulted completion still observes it. This checks the release/observation protocol, not whether a throwing cleanup implementation released every physical resource. See [advanced async ownership](advanced-engine.md#asynchronous-disposal-completion), [SS0017](diagnostics.md#ss0017), and [SS0031](diagnostics.md#ss0031).

## Explicit transfer and sharing boundaries

Send supports transfer of exclusively owned state. Mutable scalar fields are permitted, while a mutable reference component must have a Send contract and declared ownership unless it also supports Sync. Sync requires readonly/init-only source storage and recursively shareable components, unless a trusted ThreadSafe contract supplies synchronization. Known ThreadAffine state remains incompatible even when nested inside a synchronized owner.

```csharp
[Send]
sealed class WorkItem { public int Attempts; }

[Sync]
sealed class Description
{
    public string Text { get; }
    public Description(string text) => Text = text;
}

static void Queue([TransfersToThread] Action callback) { /* Transfer protocol. */ }
static void Publish([SharesAcrossThreads] Action callback) { /* Sharing protocol. */ }
```

The callback API must uphold the protocol it declares. TransfersToThread is an ownership-transfer contract, not a request to schedule work or a runtime closure-copying mechanism. SharesAcrossThreads permits sharing and requires Sync. Ordinary Task.Run captures of explicitly contracted Send/Sync/ThreadSafe values are checked for sharing; a Send-only mutable value is insufficient. SS0025 continues to check ThreadAffine Task.Run captures independently.

For a supported transfer callback, the ownership adapter moves tracked captures before the scheduling call's exception boundary, and caller aliases become stale. Each visible lambda or local-function target receives its own captured ownership obligations, including disposable cleanup and async completion. Captures present in only some callback alternatives receive conditional moves; shared/static storage never moves. A visible local delegate alias can resolve to these same targets. An ordinary bound method-group receiver transfers in the caller, but its method body retains its normal analysis context and is a trusted cleanup boundary. Opaque delegates remain subject to SS0045.

SS0044 validates type/interface composition, inherited requirements, generic arguments and inferred generic calls, and callback-parameter declarations. SS0045 validates visible captured values and their variable cells. A captured int value may be intrinsically shareable while its writable closure cell is unsafe. Direct ref aliases and ref/out exposure are considered; static storage remains shared even at a transfer boundary. Visible lambdas, local functions, method groups, conditional callbacks, and local delegate aliases are supported, while opaque delegates require a usable contract or source model. Forwarding a callback parameter preserves the same checked capability requirement.

The analyzer retains the required capability, failing component path, source declarations, and scheduling location. SS0046 reports budget exhaustion rather than accepting an incomplete check. These contracts do not prove arbitrary synchronization, publication, scheduler behavior, unmanaged thread affinity, or whole-program race freedom. See [aggregate and concurrency analysis](aggregate-concurrency.md) for precise structure, intrinsic types, callback support, budgets, and external trust boundaries.

The ownership adapter's callback collection has separate fixed limits of 20,000 capture steps, 20,000 scope operations, 256 transfer calls per scope, and 128 visible targets. Exhaustion produces SS0015. Those frontend limits are independent of the configurable structural concurrency budget reported by SS0046.

## Implemented semantic diagnostics

### SS0020: aggregate owners expose disposal

An instance field or property explicitly marked `Owned`, or whose type is marked `Owned`, is examined for `IDisposable`/`IAsyncDisposable`. If a disposable member belongs to a type implementing neither interface, the diagnostic points to that member.

The check uses interface identity. A method merely named `Dispose` is insufficient. Nested types are checked independently; an outer type's `IDisposable` implementation does not supply a disposal contract for a nested type. Static members have process/container-specific ownership and are outside this aggregate-instance rule.

### SS0021: every normal disposal exit releases owned members

For an aggregate implementing a disposal interface, SafeSharp resolves the actual entrypoints and computes remaining obligations over their CFGs. Every modeled normal exit must release every tracked owned member, including any replacement resource and pending asynchronous cleanup completion. If both disposal interfaces are exposed, each entrypoint must satisfy the obligations independently.

The analysis handles branches, early returns, loops, exact local aliases, replacements, using/await-using cleanup, and finally continuations. Bounded source summaries propagate releases, writes, pending async work, and conservative exceptional effects through directly called helpers and local functions. Actual disposal identity matters: a same-name unrelated method, an uninvoked helper, or a lambda merely declared inside Dispose supplies no cleanup proof.

Inherited members remain obligations of concrete owners. The standard inherited Dispose() to virtual Dispose(bool) pattern is followed into the effective override with constant argument specialization; base calls can supply base cleanup. An abstract owner is checked through its concrete implementations. A derived override that omits inherited cleanup is diagnosed at the derived type, with the field declaration in related evidence.

Async cleanup must account for both disposal invocation and completion observation. A stored or configured completion can be awaited or returned; merely discarding it is insufficient. Clearing a field or replacing it does not release its previous resource. A recursive helper reentry supplies no assumed release, and budget exhaustion produces SS0046.

This is a must-release check for modeled normal exits, including recovered exceptions. It does not prove physical cleanup after a throwing disposer, termination, arbitrary unknown helper side effects, or constructor-to-dispose invariants such as a `_disposed` flag implying an earlier release. Exception filters and complex aliases can lose precision. [Aggregate cleanup](aggregate-concurrency.md#aggregate-cleanup-ss0020ss0022-and-ss0046) documents the exact supported flow and limits.

### SS0022: C# owner structs remain copyable

An `Owned` struct or a struct containing an ownership-bearing member is flagged because assignment and passing by value can duplicate ownership. This applies to `readonly struct` and `ref struct` as well: those modifiers restrict mutation or escape, but do not introduce move-only value semantics. Unannotated ordinary value types are unaffected.

This design is informed by Rust's rule that [`Copy` and `Drop` are exclusive](https://doc.rust-lang.org/std/ops/trait.Drop.html#copy-and-drop-are-exclusive). The diagnostic identifies a semantic boundary between the languages; SafeSharp cannot alter the C# compiler's copy behavior.

### SS0023: contracts are internally consistent

Validation covers mutually inconsistent owned/shared/exclusive declarations, result contracts on `void`, invalid `ReturnsBorrowed` source names, conflicting result mutability, an exclusive returned loan derived from a shared argument, a static `Mutates` declaration, conflicting thread capabilities, and invalid service-lifetime enumeration values. It diagnoses the contract or declaration rather than a later use that depends on the invalid contract.

Attribute implementations with unrelated metadata identities are ignored. This prevents a project's own `BorrowedAttribute`, `OwnedAttribute`, or similarly named DI API from being interpreted as a SafeSharp contract.

### SS0024: `MustUse` results are acknowledged

Calls to an annotated method, calls returning an annotated type, and discarded construction of an annotated type are checked when they appear as expression statements. Assigning or returning the result consumes it. An explicit discard is an intentional acknowledgement, consistent with Rust's [`must_use` convention](https://doc.rust-lang.org/reference/attributes/diagnostics.html#the-must_use-attribute):

```csharp
[MustUse]
static bool TryCommit() => true;

TryCommit();       // SS0024: implicit non-use.
_ = TryCommit();   // Explicit acknowledgement.
bool committed = TryCommit();
```

Awaiting a non-generic `Task` consumes its completion. An ignored value produced by awaiting an annotated `Task<T>` method can still be diagnosed. This rule is not a general dead-store analysis: assigning a result to a local and never reading the local is outside its scope.

### SS0025: thread-affine captures in `Task.Run`

The analyzer identifies the framework's actual `System.Threading.Tasks.Task.Run` symbol. It checks captured locals and parameters, existing fields accessed through an outer receiver, captured `this`, inherited thread-affinity contracts, and bound method-group receivers. A thread-affine object created inside the worker callback is not an outer capture.

```csharp
[ThreadAffine]
sealed class View
{
    public void Refresh() { }
}

static Task RefreshOnPool(View view) =>
    Task.Run(() => view.Refresh()); // SS0025.
```

The rule follows the documented [thread-pool scheduling behavior of Task.Run](https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.task.run). It does not infer the current UI dispatcher, prove synchronization, or implement Rust's automatic Send/Sync derivation. The separate ConcurrencyContractAnalyzer validates explicit Send/Sync composition and contracted callback sharing with SS0044/SS0045. A method named Run on an unrelated Task class is not treated as the framework boundary. Awaiting the task does not remove a thread-affinity violation.

### SS0026: scoped services do not flow into singletons

The service analyzer builds a compilation-local dependency graph from both `ServiceLifetime` attributes and calls to the actual `Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions` API. Supported registration forms include:

- `AddSingleton<T>`, `AddScoped<T>`, and `AddTransient<T>`.
- Service-to-implementation generic registrations, such as `AddScoped<IStore, Store>`.
- Non-generic `typeof(IService)`/`typeof(Implementation)` registrations.
- Closed and open generic mappings, substituting constructed service arguments into implementation types.
- Constructor injection through the longest public constructor whose parameters are known to be resolvable; optional defaults and built-in provider/scope-factory dependencies are recognized.
- Transitive dependencies through transient or singleton intermediates.
- `IEnumerable<T>` dependencies, including registered scoped elements.
- Direct `GetService`, `GetRequiredService`, and `GetServices` calls made on the provider parameter of a registration factory. These are recognized by framework symbol identity.

Instance registrations do not imply constructor injection. Factory registrations use the factory's recognized resolutions instead of assuming that the container invokes the implementation's constructor. A dependency on `IServiceScopeFactory` is a lifetime boundary; a resolution performed through an explicitly created scope is not treated as a root-provider resolution.

```csharp
[ServiceLifetime(ServiceLifetime.Scoped)]
sealed class RequestStore { }

[ServiceLifetime(ServiceLifetime.Transient)]
sealed class Worker
{
    public Worker(RequestStore store) { }
}

[ServiceLifetime(ServiceLifetime.Singleton)]
sealed class Coordinator
{
    public Coordinator(Worker worker) { } // SS0026 is reported on Coordinator.
}
```

The resulting witness is `Coordinator -> Worker -> RequestStore`, with lifetimes `Singleton -> Transient -> Scoped`. Diagnostics include the involved registration locations and the following stable property keys for reporting:

| Property | Content |
| --- | --- |
| `SafeSharp.DependencyPath` | Implementation-type path. |
| `SafeSharp.DependencyServices` | Service-type path, retaining interface mappings. |
| `SafeSharp.DependencyLifetimes` | Lifetime at each node. |

Breadth-first exploration returns the shortest discovered scoped-dependency witness. Registrations and diagnostics are ordered deterministically. Constructor cycles terminate through a visited set. The lifetime rule and constructor behavior follow the [.NET dependency injection documentation](https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection/overview), particularly constructor selection and scope validation.

This is a conservative, compilation-local graph. It does not reconstruct runtime control flow between independent service collections, enforce last-registration precedence across conditional composition roots, interpret arbitrary registration wrappers, analyze reflection-driven registrations, understand every keyed-service/container extension, or prove that a factory's returned object does not retain something resolved from a local scope. If a service has several registrations, the graph retains the alternatives and reports a *possible* captive path. Constructor injection is not presumed for unregistered concrete types. Dynamic or external composition requires explicit contracts or further models.

### SS0027: borrowed parameters do not enter persistent storage

The bounded CFG solver follows `Borrowed`, `BorrowedMut`, and `DoesNotEscape` origins through local aliases, ref slots, branches, loops, allocation containers and captured storage. Persistent fields, properties, events, array/ref-out storage and escaping containers/delegates are retention boundaries. Returning an ordinary borrowed value can be governed by a returned-loan contract; `DoesNotEscape` is stricter and also excludes direct return or an explicit consuming sink.

Diagnostics are emitted after convergence and carry the borrowed source, escape destination, related locations and possible-certainty metadata. A possible retention does not establish a concrete runtime path. Source summaries and explicit contracts carry known call-result origins; arbitrary calls do not establish either retention or a fresh clone. Containers are field/element insensitive, and exception handlers and mutable captures use conservative envelopes. The [borrowed-escape guide](borrowed-escape.md) describes transfer rules, configuration, evidence and limits. The ownership engine supplies complementary resource-loan escape diagnostics.

### SS0028: escaping callbacks do not retain `using` resources

The analyzer detects a using-bound local or resource captured by a directly returned delegate, a delegate assigned to a field/property/event, or a `Task.Run` callback whose completion is not observed within that using lifetime. Using declarations, using statements, and using statements over an existing resource are recognized.

```csharp
static Func<int> CreateReader()
{
    using var stream = new MemoryStream();
    return () => stream.ReadByte(); // SS0028: delegate survives resource disposal.
}

static async Task UseReader()
{
    using var stream = new MemoryStream();
    await Task.Run(() => stream.ReadByte()); // Completed before disposal.
}
```

Direct `await`, framework `ConfigureAwait`, and synchronous task joins are recognized. A task assigned to a local and immediately awaited in the following statement of the same block is also recognized. Returning a task is not equivalent to awaiting it before leaving a using declaration. Locally declared and invoked delegates are not presumed to escape.

The checker deliberately does not claim general interprocedural delegate escape analysis. It does not prove joins through arbitrary task combinators, model every callback registration API, infer every local alias of a resource/delegate, or analyze every scheduler. More complicated task coordination can require restructuring or an explicit reviewed suppression. A safely joined task can still violate thread affinity and receive SS0025.

### SS0029: bounded graph analysis reports incompleteness

Open-generic constructor dependencies can expand indefinitely, for example `Node<T>` depending on `Node<List<T>>`. Service exploration is bounded to 4,096 visited implementation/registration pairs and 64 path nodes. Distinct factories for the same implementation remain distinct graph nodes. If a singleton's exploration hits the budget without finding a scoped witness, SS0029 reports that lifetime safety has not been established. Cancellation is checked throughout collection, declaration traversal, and graph exploration.

## Rust design references and scope

The implementation is an original C#/Roslyn implementation informed by primary Rust and .NET material; it does not embed Rust compiler source. The repository's Rust references identify corresponding concepts rather than asserting an equivalent soundness guarantee.

- [Rust references and borrowing](https://doc.rust-lang.org/book/ch04-02-references-and-borrowing.html): shared and exclusive loans, validity, and last-use lifetime examples.
- [Rust compiler borrow checking](https://rustc-dev-guide.rust-lang.org/borrow-check.html): MIR-based analysis, move data, region inference, and borrow checking.
- [Rust lifetime bounds](https://doc.rust-lang.org/reference/trait-bounds.html#lifetime-bounds): the meaning of explicit outlives relationships.
- [Rust region constraint propagation](https://rustc-dev-guide.rust-lang.org/borrow-check/region-inference/constraint-propagation.html): equivalent-region components and implications.
- [Rust Send and Sync](https://doc.rust-lang.org/nomicon/send-and-sync.html): independent transfer and shared-reference capabilities.
- [Rust `Drop` and `Copy`](https://doc.rust-lang.org/std/ops/trait.Drop.html#copy-and-drop-are-exclusive): destruction obligations versus implicit duplication.
- [Rust `must_use`](https://doc.rust-lang.org/reference/attributes/diagnostics.html#the-must_use-attribute): result-use contracts and explicit acknowledgement.
- [.NET dispose pattern](https://learn.microsoft.com/en-us/dotnet/standard/design-guidelines/dispose-pattern): ownership and explicit resource release.
- [.NET dependency injection](https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection/overview): constructor resolution and captive scoped dependencies.

Reflection, dynamic dispatch without contracts, unmodeled native code, unsafe pointer access, arbitrary external library behavior, and user-suppressed diagnostics can bypass these checks. These are capability boundaries of this analyzer, not language-level restrictions. The test suite asserts both violating and accepted programs, exact diagnostic spans, symbol identity, inherited cleanup/lifetime/concurrency contracts, branch and loop obligations, pending completion states, factory versus constructor registration behavior, generic graph substitution, and deterministic concurrent execution. Use the [diagnostic catalog](diagnostics.md), [lifetime model](lifetime-regions.md), [aggregate/concurrency model](aggregate-concurrency.md), [advanced protocols](advanced-engine.md), and [implementation status](implementation-status.md) together when deciding which guarantee a clean result establishes.
