# Ownership contracts and semantic analysis

SafeSharp has three complementary analyzers. The ownership engine follows resource identities, moves, loans, liveness, and control-flow transitions. `SafeSharpSemanticAnalyzer` checks declarations and relationships that cross method boundaries: aggregate ownership, callback escape, thread affinity, and dependency injection lifetimes. `BoundarySafetyAnalyzer` checks exception/disposal boundaries, inherited parameter contract compatibility, and opt-in audits for unsafe operations and unknown factories. All run as Roslyn diagnostics while the compiler analyzes C#. The [complete diagnostic reference](diagnostics.md) documents all 31 implemented rules.

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
| `Mutates` | Instance method | The method mutates its receiver. |
| `DoesNotEscape` | Parameter | The callee must not retain the argument after returning. |
| `MustUse` | Method, class, struct | An expression-statement result must be used or explicitly discarded. |
| `NoSuspend` | Class, struct | Values of this type must not remain live across suspension. |
| `ThreadAffine` | Class, struct | Access is tied to an owning thread or context. |
| `ThreadSafe` | Class, struct | A trusted declaration that sharing and use across threads are supported. |
| `ServiceLifetime(Transient/Scoped/Singleton)` | Class, interface | A service lifetime known without a dependency on a specific DI library. |

Thread-affinity and thread-safety contracts propagate through base types and generic type constraints. They describe access capabilities and must not be placed together on the same effective type. The ownership adapter inherits parameter and method contracts from implemented interfaces and overridden methods, including associated getter-property contracts. Parameters are matched by ordinal, and named returned-borrow sources are remapped when an implementation renames its parameter. Omitted implementation annotations inherit the declaration's mode; an explicit incompatible parameter mode receives [SS0034](diagnostics.md#ss0034). This compatibility check does not establish full behavioral subtyping for arbitrary method effects.

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

## Implemented semantic diagnostics

### SS0020: aggregate owners expose disposal

An instance field or property explicitly marked `Owned`, or whose type is marked `Owned`, is examined for `IDisposable`/`IAsyncDisposable`. If a disposable member belongs to a type implementing neither interface, the diagnostic points to that member.

The check uses interface identity. A method merely named `Dispose` is insufficient. Nested types are checked independently; an outer type's `IDisposable` implementation does not supply a disposal contract for a nested type. Static members have process/container-specific ownership and are outside this aggregate-instance rule.

### SS0021: owned members have a reachable release

For an aggregate that implements a disposal interface, SafeSharp resolves its actual interface implementation, then follows directly called instance helpers and invoked local functions. It recognizes direct field/property `Dispose` calls and observed `DisposeAsync` calls. Asynchronous disposal counts when awaited, returned to the caller, or awaited through the framework's `ConfigureAwait`/`AsTask` wrappers. Discarding the returned `ValueTask` does not establish completion.

The walker follows the usual inherited `Dispose()` → virtual `Dispose(bool)` pattern into a derived override. It does not treat an unrelated overload named `Dispose`, an uninvoked helper, or a lambda merely declared inside `Dispose` as proof of release.

This is a structural reachability check: it establishes that a recognized release is reachable in the disposal implementation's call graph. It does not prove release on every normal or exceptional execution path, and it does not count local aliases, external disposal helpers, or arbitrary wrapper methods as proofs. A conditional `if (disposing)` release in a standard disposal pattern is accepted. If both sync and async disposal are exposed, this rule requires a recognized release from at least one entry point; it does not establish equivalence of the two APIs.

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

The rule follows the documented [thread-pool scheduling behavior of Task.Run](https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.task.run). It does not infer the current UI dispatcher, prove synchronization, or implement Rust's automatic `Send`/`Sync` derivation. A method named `Run` on an unrelated `Task` class is not treated as a thread-pool boundary. Awaiting the task does not remove a thread-affinity violation.

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

Direct stores to fields or properties are checked for `Borrowed`, `BorrowedMut`, and `DoesNotEscape` parameters. Origin tracking follows conversions, conditional/coalescing results, tuple/array construction, and methods with a named `ReturnsBorrowed` relationship. The diagnostic points to the source argument, with the destination member as a related location.

A call returning a clone is not assumed to borrow merely because a borrowed parameter was passed to it. This rule does not independently solve arbitrary aliasing through local variables, containers, unknown methods, or external assemblies; the ownership engine handles its modeled local loan escapes.

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
- [Rust `Drop` and `Copy`](https://doc.rust-lang.org/std/ops/trait.Drop.html#copy-and-drop-are-exclusive): destruction obligations versus implicit duplication.
- [Rust `must_use`](https://doc.rust-lang.org/reference/attributes/diagnostics.html#the-must_use-attribute): result-use contracts and explicit acknowledgement.
- [.NET dispose pattern](https://learn.microsoft.com/en-us/dotnet/standard/design-guidelines/dispose-pattern): ownership and explicit resource release.
- [.NET dependency injection](https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection/overview): constructor resolution and captive scoped dependencies.

Reflection, dynamic dispatch without contracts, unmodeled native code, unsafe pointer access, arbitrary external library behavior, and user-suppressed diagnostics can bypass these checks. These are capability boundaries of this analyzer, not language-level restrictions. The test suite asserts both violating and accepted programs, exact diagnostic spans, symbol identity, inherited disposal/thread contracts, factory versus constructor registration behavior, generic graph substitution, and deterministic concurrent execution.
