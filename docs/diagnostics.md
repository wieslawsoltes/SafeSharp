# Diagnostic reference

SafeSharp implements 31 Roslyn diagnostics for ownership, borrowing, disposal, asynchronous lifetime boundaries, and dependency injection. They run during compiler and IDE analysis. The ownership engine computes effects over Roslyn control-flow graphs; the semantic analyzer checks declarations and service relationships; the boundary analyzer identifies contracts and operations requiring additional attention.

Every rule below is implemented. “Implemented” describes the stated check within the documented model, not equivalence to Rust's language-level guarantees. `Ownership.Move`, `Borrow`, and `BorrowMut` are metadata-bearing passthrough methods; C# still permits aliasing and value-type copies at runtime. See [architecture](architecture.md), [contracts](contracts-and-semantics.md), and [Rust design references](rust-design.md) for the analysis domain and its limits.

## Rule matrix

“Info” appears as a suggestion in typical IDE configurations. Every diagnostic can be configured with `dotnet_diagnostic.<ID>.severity`. SS0017–SS0019 are unassigned.

| ID | Implemented check | Analyzer | Default | Enabled |
| --- | --- | --- | --- | --- |
| [SS0001](#ss0001) | Use after disposal | Ownership | Warning | Yes |
| [SS0002](#ss0002) | Repeated disposal | Ownership | Warning | Yes |
| [SS0003](#ss0003) | Unreleased owned resource or pool lease | Ownership | Warning | Yes |
| [SS0004](#ss0004) | Use after ownership transfer | Ownership | Warning | Yes |
| [SS0005](#ss0005) | Move while a loan is active | Ownership | Warning | Yes |
| [SS0006](#ss0006) | Conflicting shared/exclusive loans | Ownership | Warning | Yes |
| [SS0007](#ss0007) | Mutation conflicting with a loan | Ownership | Warning | Yes |
| [SS0008](#ss0008) | Loan escapes its owner | Ownership | Warning | Yes |
| [SS0009](#ss0009) | Disposal without ownership | Ownership | Warning | Yes |
| [SS0010](#ss0010) | Live loan crosses suspension | Ownership | Warning | Yes |
| [SS0011](#ss0011) | Overwriting a live ownership obligation | Ownership | Warning | Yes |
| [SS0012](#ss0012) | Service exceeds the resolving scope's lifetime | Ownership | Warning | Yes |
| [SS0013](#ss0013) | Owner read conflicts with exclusive loan | Ownership | Warning | Yes |
| [SS0014](#ss0014) | Owner disposal conflicts with active loan | Ownership | Warning | Yes |
| [SS0015](#ss0015) | Analysis budget exhausted | Ownership | Warning | Yes |
| [SS0016](#ss0016) | `NoSuspend` value crosses suspension | Ownership | Warning | Yes |
| [SS0020](#ss0020) | Aggregate lacks a disposal contract | Semantic | Warning | Yes |
| [SS0021](#ss0021) | Owned member lacks a reachable release | Semantic | Warning | Yes |
| [SS0022](#ss0022) | Ownership-bearing struct remains copyable | Semantic | Warning | Yes |
| [SS0023](#ss0023) | Contradictory or invalid contract | Semantic | Warning | Yes |
| [SS0024](#ss0024) | Required result is ignored | Semantic | Warning | Yes |
| [SS0025](#ss0025) | Thread-affine value captured by `Task.Run` | Semantic | Warning | Yes |
| [SS0026](#ss0026) | Singleton transitively depends on scoped service | Semantic | Warning | Yes |
| [SS0027](#ss0027) | Borrowed parameter stored in persistent member | Semantic | Warning | Yes |
| [SS0028](#ss0028) | Escaping callback captures a using resource | Semantic | Warning | Yes |
| [SS0029](#ss0029) | Service graph exploration incomplete | Semantic | Info | Yes |
| [SS0030](#ss0030) | Exception can bypass later manual cleanup | Boundary | Info | Yes |
| [SS0031](#ss0031) | Asynchronous disposal result is dropped | Boundary | Warning | Yes |
| [SS0032](#ss0032) | Dynamic/pointer operation crosses model boundary | Boundary | Info | No |
| [SS0033](#ss0033) | Resource-returning API lacks a known contract | Boundary | Info | No |
| [SS0034](#ss0034) | Implementation changes inherited ownership mode | Boundary | Warning | Yes |

The public analyzer classes are `SafeSharp.Analyzers.OwnershipAnalyzer`, `SafeSharp.Analyzers.Semantics.SafeSharpSemanticAnalyzer`, and `SafeSharp.Analyzers.BoundarySafetyAnalyzer`. Their descriptors use categories `Ownership` and `SafeSharp.Ownership`; configure individual IDs when a policy spans both categories.

## Reading certainty, related locations, and graphs

Ownership diagnostics include `SafeSharp.Certainty`, `SafeSharp.ResourceId`, and `SafeSharp.Evidence` properties. `Definite` means the modeled alternatives reaching that check violate the rule; `Possible` means the abstract state also admits a nonviolating alternative. At a conditional merge, a resource disposed only on one branch typically yields a possible use-after-disposal. These labels describe the abstract model, including its conservative joins, trusted contracts, and recognized library effects. They are not proof that a particular execution reaches the issue.

Related locations identify acquisition, transfers, conflicting loans, releases, or relevant exits. JSON, SARIF, HTML, Mermaid, and DOT reports retain analysis evidence; see [reporting](reporting.md). Graph states are computed facts, and a displayed control-flow path is not an automatically solved concrete counterexample. Declaration and boundary diagnostics do not carry the core's certainty property unless their analysis produces it. Service diagnostics carry their own dependency-path properties.

The core runs forward ownership analysis to a fixed point and backward place liveness to determine loan ends. Consequently, a loan can end at its last use before its lexical block closes. This borrows the control-flow approach described by the [Rust compiler borrow-checker guide](https://rustc-dev-guide.rust-lang.org/borrow-check.html); SafeSharp does not implement Rust's complete region type system.

Examples use the following illustrative type unless a framework type is shown:

```csharp
using SafeSharp;

sealed class Resource : IDisposable
{
    public void Read() { }
    [Mutates] public void Mutate() { }
    public void Dispose() { }
}
```

## SS0001

**Use after disposal.** A read, mutation, or escaping reference reaches a resource whose modeled lifetime has ended. Aliases share resource identity, so disposing one alias invalidates the others. Returning a value whose using/finally cleanup disposes it can also produce this diagnostic at the returned expression.

```csharp
var owner = new Resource();
var alias = owner;
owner.Dispose();
alias.Read(); // SS0001
```

Move the read before disposal or extend the owning scope to cover every consumer. Returning a resource requires transferring a live ownership obligation; remove an enclosing using only when the caller is explicitly responsible for cleanup. The same rule covers accessing an array lease after `ArrayPool<T>.Return` and modeled memory views after their owner is disposed.

Calling a recognized release ends its static lifetime obligation even if the release implementation can throw. SafeSharp does not prove that the implementation successfully freed every operating-system resource, and it does not interpret arbitrary native or external release routines without a model.

## SS0002

**Repeated disposal.** The same resource or pool lease reaches a second recognized release. This includes release through different aliases and explicit disposal followed by implicit using cleanup.

```csharp
using var owner = new Resource();
owner.Dispose(); // The using cleanup supplies another release.
```

Choose one owner and one cleanup path. Prefer using/await using for local ownership. Some .NET implementations tolerate repeated `Dispose`; this diagnostic enforces an explicit single-release ownership protocol and can require a narrowly reviewed suppression for an intentionally idempotent API. Returning the same pool lease twice remains invalid even if a particular pool does not immediately reject it.

## SS0003

**Owned resource is not released.** An acquired obligation reaches a modeled method exit without disposal, pool return, or ownership transfer. Acquisitions include disposable construction, explicit/inferred owned factories, recognized framework factories, and supported pool rentals.

```csharp
void ReadData()
{
    var stream = File.OpenRead("data.bin"); // SS0003
    stream.ReadByte();
}
```

Use `using var stream = File.OpenRead("data.bin");`, `await using` for asynchronous cleanup, or a try/finally release. An API returning ownership should declare `ReturnsOwned`; a consuming argument should declare `Consumes` or `Owned`. A normal C# assignment copies a reference and does not independently discharge the obligation.

Unannotated external results and ordinary borrowed inputs are not automatically assumed to be locally owned. Use SS0033 to audit unknown factory boundaries. SS0030 complements this exit analysis with an advisory for recognized manual cleanup that an earlier exception can bypass.

## SS0004

**Use after ownership transfer.** A place or its aliases are used after a recognized move or consuming argument transfers ownership.

```csharp
var original = new Resource();
var next = Ownership.Move(original);
original.Read(); // SS0004
next.Dispose();
```

Use `next` after the move. If the callee only inspects the object, give its parameter a borrowing contract instead of a consuming contract. Passing through an interface or override inherits the declared parameter ownership by ordinal even when the implementation renames its parameter.

Move helpers return the same CLR reference. The diagnostic is the enforcement mechanism; runtime access through the old reference is not physically disabled.

## SS0005

**Move conflicts with an active borrow.** Ownership is transferred while a loan of the same resource or an overlapping modeled place is still needed.

```csharp
var owner = new Resource();
var view = Ownership.Borrow(owner);
var next = Ownership.Move(owner); // SS0005
view.Read();
next.Dispose();
```

Finish using `view` before the move, or keep the original owner alive until borrowers finish. Non-lexical liveness means an earlier final use of `view` can make the same move valid without adding a block. Unknown callback and interprocedural lifetimes still require explicit contracts or a design that makes the lifetime visible.

## SS0006

**Conflicting borrows.** A new loan overlaps another active loan and at least one is exclusive. Several shared loans can coexist. Exclusive loans reserve modeled mutation and access rights for their holder.

```csharp
var owner = new Resource();
var writer = Ownership.BorrowMut(owner);
var reader = Ownership.Borrow(owner); // SS0006
writer.Mutate();
reader.Read();
owner.Dispose();
```

Sequence the loans so the first reaches its last use before the second begins. If a method only reads, use `Borrowed` rather than `BorrowedMut`. Argument loans remain active through the call, so passing the same resource to conflicting parameters is diagnosed even when argument evaluation is sequential.

The reusable core understands projected places and reborrows. The Roslyn adapter's heap and library models are narrower than Rust's complete place/type rules; an unmodeled wrapper does not gain a verified lifetime merely because no conflict was found.

## SS0007

**Mutation conflicts with an active borrow.** The program writes an owner while an overlapping shared loan is live, writes through a shared borrowed parameter, or mutates a place reserved by a conflicting exclusive loan.

```csharp
void Inspect([Borrowed] Resource value)
{
    value.Mutate(); // SS0007
}
```

Use `BorrowedMut` when exclusive mutation is the API's intended requirement, and make the caller satisfy that contract. Otherwise, remove the mutation or perform it after all shared loans end. Direct field/property writes and modeled mutable `ref` arguments are effects; instance methods marked `Mutates` explicitly declare their receiver effect.

SafeSharp does not infer every possible mutation performed inside an unknown method. An absent `Mutates` annotation is not a language-level guarantee of purity. Inherited shared parameter contracts continue to constrain unannotated implementations.

## SS0008

**Borrow escapes its owner.** A borrowed reference is returned, stored, or moved across a boundary whose lifetime is not established by the owner/return contract.

```csharp
Resource CreateView()
{
    var owner = new Resource();
    return Ownership.Borrow(owner); // SS0008
}
```

Return an owned resource when the caller should own it. For a view over an input, declare a named source such as `[ReturnsBorrowed("source")]` and keep the source alive in the caller. Use `[ReturnsBorrowed("this")]` for an instance-owned view. A returned relation cannot validly name a newly created local owner that is about to die.

Borrowed parameter stores also have a complementary declaration-aware rule, SS0027. Arbitrary container aliases and callbacks are not a complete general heap-escape analysis.

## SS0009

**Borrowed resource must not be disposed.** A loan holder or a modeled container-owned service attempts to release a resource whose cleanup responsibility belongs elsewhere.

```csharp
void Inspect([Borrowed] Resource value)
{
    value.Dispose(); // SS0009
}
```

Let the owner release the resource. If the API truly consumes it, change the parameter contract to `Consumes` and check every caller for later uses. An exclusive mutable loan authorizes mutation, not ownership transfer or destruction. Services resolved from a modeled scope should be released by their scope/container unless ownership is explicitly transferred by an API with a different contract.

## SS0010

**Borrow crosses suspension.** A loan remains live across an `await` or another modeled suspension point.

```csharp
using var owner = new Resource();
var view = Ownership.Borrow(owner);
await Task.Yield(); // SS0010
view.Read();
```

Complete the borrowed operation before suspending, or reacquire a loan afterwards while retaining a valid owner. Materializing independent data before the await can also remove the lifetime dependency. A borrowed local whose last use precedes the await is accepted.

This is SafeSharp's conservative suspension policy for explicit loans. C# permits many references across awaits, and Rust permits some borrows within futures under its own lifetime and `Send` rules. SafeSharp does not claim to implement that full async type system. `safesharp_check_suspension` configures this analysis policy.

## SS0011

**Owned resource is overwritten.** Assignment or a modeled `out` effect removes a live owning reference without first discharging or transferring its obligation.

```csharp
var owner = new Resource();
owner = new Resource(); // SS0011 for the previous resource
owner.Dispose();
```

Release or transfer the first resource before replacing it. For reusable slots, make cleanup and reassignment explicit on each control-flow path. `ref`/`out` APIs need ownership contracts or summaries that accurately state their effects; general external out-result ownership is an analysis boundary.

The rule operates on resource identities and modeled aliases rather than variable spelling. Related locations identify the previous acquisition. A remaining alias can affect which reference is considered the last tracked owner, but it does not make an unowned resource safe to forget.

## SS0012

**Service outlives its scope.** A modeled service is used after its parent scope is released, or escapes a boundary that cannot retain the scope's lifetime.

```csharp
var scope = provider.CreateScope();
var service = scope.ServiceProvider.GetRequiredService<MyService>();
scope.Dispose();
service.Work(); // SS0012 when this resolution is modeled
```

Perform the service's work inside the scope and dispose the scope after every operation completes. Return independent data instead of retaining the scoped service. For long-lived background workers, inject a scope factory and create a scope for each bounded operation.

This rule follows recognized provider/scope operations and the parent relationships represented in the ownership IR. The separate service graph rule SS0026 finds singleton-to-scoped constructor/factory paths. Arbitrary container wrappers, runtime registration composition, and unmodeled providers require additional contracts/models.

## SS0013

**Read conflicts with an exclusive borrow.** The owner or another alias is read while an exclusive loan is active.

```csharp
using var owner = new Resource();
var writer = Ownership.BorrowMut(owner);
owner.Read(); // SS0013
writer.Mutate();
```

Read through the exclusive holder while it owns the access permission, or finish the exclusive loan before reading the owner. If exclusivity is unnecessary, replace it with a shared borrowing contract. This is a static access protocol; SafeSharp does not insert locks or synchronize threads.

## SS0014

**Disposal conflicts with an active borrow.** An owner is released while a derived loan remains live.

```csharp
var owner = MemoryPool<byte>.Shared.Rent(64);
var memory = owner.Memory;
owner.Dispose(); // SS0014
Console.WriteLine(memory.Length);
```

Move disposal after the loan's last use, usually by placing the owner in a using scope containing all consumers. `Memory<T>`, `ReadOnlyMemory<T>`, `Span<T>`, and `ReadOnlySpan<T>` views have modeled owner relationships for recognized property access. Not every slicing, extension method, custom view, or unsafe conversion has a model.

This diagnostic describes the invalid release event; subsequent use of the view can additionally produce SS0001. A borrowed reference calling `Dispose` itself receives SS0009.

## SS0015

**Ownership analysis budget exhausted.** Resource enumeration, backward liveness, or forward fixed-point analysis exceeded its configured budget. This is an incomplete result, not an ownership violation demonstrated for the method.

```editorconfig
[*.cs]
safesharp_max_iterations = 20000
safesharp_max_tracked_resources = 4096
```

First inspect the method for generated repetition or unnecessary complexity. Increase a budget when the method is legitimate and measured analysis cost is acceptable. Defaults are 10,000 iterations and 2,048 tracked acquisition sites; the analyzer caps configured values at 1,000,000 iterations and 100,000 resources. MSBuild global defaults are `SafeSharpMaxIterations` and `SafeSharpMaxTrackedResources`; per-file analyzer settings take precedence.

The engine returns an explicit budget result instead of silently presenting a partial fixed point as a clean analysis. Suppressing SS0015 removes the notification, not the limit.

## SS0016

**Resource must not cross suspension.** A live value of a type annotated `NoSuspend` spans a modeled suspension point, including through an inherited type annotation.

```csharp
[NoSuspend]
sealed class Handle { public void Read() { } }

async Task Work()
{
    var handle = new Handle();
    await Task.Yield(); // SS0016
    handle.Read();
}
```

Finish the handle-dependent operation before suspension or acquire a new handle afterwards. This can describe an application lock guard or context-bound token whose lifetime must remain synchronous. The annotation does not implement cleanup; pair it with a disposal contract when cleanup is also required. A final use before the await can end liveness even though the C# local remains in scope.

## SS0020

**An aggregate owner must provide disposal.** A type owns a disposable instance field/property but implements neither `IDisposable` nor `IAsyncDisposable`.

```csharp
sealed class Reader
{
    [Owned] private readonly MemoryStream stream = new(); // SS0020
}
```

Implement the appropriate disposal interface and release the owned members. If the member is borrowed from another owner, remove the false ownership annotation and document the caller's lifetime requirement. A method named `Dispose` alone does not provide the interface contract expected by this aggregate rule.

The check is declaration-based. Static resource ownership and arbitrary unannotated fields are outside this instance-aggregate rule. A nested type must satisfy its own contract even when the outer type is disposable.

## SS0021

**Owned member has no reachable release.** An aggregate exposes a disposal interface, but the analyzer finds no recognized release of an owned member reachable from its actual disposal implementation.

```csharp
sealed class Reader : IDisposable
{
    [Owned] private readonly MemoryStream stream = new(); // SS0021
    public void Dispose() { }
}
```

Release the member directly or through a recognized instance helper called by `Dispose`/`DisposeAsync`. The walker follows actual interface implementations, directly invoked local functions, and inherited virtual disposal helpers. An async release must be observed by awaiting or returning the awaitable; recognized framework wrappers are supported.

This establishes structural reachability of a release, not release on every execution path. A conditional release in the usual `Dispose(bool)` pattern is accepted. Uninvoked lambdas, unrelated overloads, arbitrary external cleanup helpers, and local alias indirection do not count as proof. If both disposal interfaces are exposed, a reachable release from one entry point satisfies this rule; equivalence of the two APIs is not established.

## SS0022

**Value-type ownership can be duplicated.** A struct is annotated as owned or contains an ownership-bearing instance member. C# assignment/passing can copy it and duplicate apparent cleanup responsibility.

```csharp
[Owned]
struct Lease // SS0022
{
    public Resource Value;
}
```

Prefer a reference owner with one explicit lifetime, or review a value-type design whose shared internal state safely coordinates release. `readonly struct` and `ref struct` do not create move-only semantics. SafeSharp cannot change these C# copy rules.

The design is informed by Rust's [mutual exclusion of `Copy` and `Drop`](https://doc.rust-lang.org/std/ops/trait.Drop.html#copy-and-drop-are-exclusive). It reports the language mismatch rather than treating an annotation as a new compiler-enforced affine type.

## SS0023

**Ownership contract is inconsistent.** Declaration validation finds incompatible ownership modes, an invalid return-loan source, an impossible result contract, inconsistent thread capabilities, or an invalid service lifetime.

```csharp
void Inspect([Borrowed, BorrowedMut] Resource value) { } // SS0023

[ReturnsBorrowed("missing")]
Resource View(Resource source) => source; // SS0023
```

Choose one effective parameter mode and name the actual source parameter or instance receiver for returned borrowing. A shared input cannot support an exclusive returned loan. Static members cannot borrow `this`; `void` methods cannot return owned or required values. `Owned` plus `Consumes` is accepted because both describe transfer. Resolve conflicting `ThreadAffine` and `ThreadSafe` declarations rather than treating one as an override of the other.

Validation checks actual SafeSharp contract identities; similarly named attributes in another namespace are ignored. Incompatible ownership changes across interfaces and overrides have the dedicated SS0034 rule.

## SS0024

**Required result is ignored.** An expression statement ignores a method/type marked `MustUse`, including an ignored value produced by awaiting a required `Task<T>` result.

```csharp
[MustUse]
static bool TryCommit() => true;

TryCommit(); // SS0024
bool committed = TryCommit();
_ = TryCommit(); // Intentional acknowledgement
```

Consume the result when it affects correctness. If non-use is intentional, use an explicit discard. The optional [code-fix package](code-fixes.md) provides a tested Fix All-compatible acknowledgement action, including safe handling of an existing variable named `_` and older language versions.

This follows Rust's [`must_use` acknowledgement convention](https://doc.rust-lang.org/reference/attributes/diagnostics.html#the-must_use-attribute). It is not a general dead-store or error-handling proof: assigning a result to a never-read local is outside the rule. Awaiting a non-generic task consumes completion even when it produces no value.

## SS0025

**Thread-affine value crosses a Task.Run boundary.** The framework's `Task.Run` callback accesses a captured value marked `ThreadAffine`, an inherited affine type, or an affine outer receiver.

```csharp
[ThreadAffine]
sealed class View { public void Refresh() { } }

Task Refresh(View view) =>
    Task.Run(() => view.Refresh()); // SS0025
```

Marshal access through the owning context's API, or create independent data suitable for the worker. Awaiting the task does not make its execution occur on the original thread. An object created inside the callback is not an outer capture.

The rule uses the [documented Task.Run thread-pool boundary](https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.task.run). It recognizes method groups and relevant field/receiver captures, but does not infer every scheduler, dispatcher, lock, or Rust-like `Send`/`Sync` capability. A namesake `Task.Run` in an unrelated namespace is not this boundary.

## SS0026

**Singleton may capture a scoped dependency.** A constructor/factory dependency path leads from a singleton through zero or more intermediates to a scoped registration.

```csharp
[ServiceLifetime(ServiceLifetime.Scoped)]
sealed class RequestStore { }

[ServiceLifetime(ServiceLifetime.Transient)]
sealed class Worker { public Worker(RequestStore store) { } }

[ServiceLifetime(ServiceLifetime.Singleton)]
sealed class Coordinator // SS0026: Coordinator -> Worker -> RequestStore
{
    public Coordinator(Worker worker) { }
}
```

Change the owning lifetime or inject a scope factory and perform scoped work within an explicit scope. Making an intermediate service transient does not prevent the singleton from retaining that instance and its scoped dependencies. The [.NET service lifetime documentation](https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection/service-lifetimes) describes this captive-dependency problem.

The implemented graph supports attributed services, canonical `AddSingleton`/`AddScoped`/`AddTransient` registrations, service-to-implementation mappings, open generic substitution, resolvable public constructors, `IEnumerable<T>`, and recognized provider resolutions in registration factories. Instance/factory registrations do not imply constructor injection. The shortest discovered witness is retained in `SafeSharp.DependencyPath`, `SafeSharp.DependencyServices`, and `SafeSharp.DependencyLifetimes`, with related source locations.

The graph is compilation-local and conservatively keeps registration alternatives. It does not reconstruct every runtime service collection, keyed registration, wrapper, reflection registration, or conditional last-registration precedence. See the [full service model](contracts-and-semantics.md#ss0026-scoped-services-do-not-flow-into-singletons).

## SS0027

**Borrowed argument escapes into persistent storage.** A parameter marked `Borrowed`, `BorrowedMut`, or `DoesNotEscape` is retained in a field/property.

```csharp
sealed class Cache
{
    private Resource? retained;
    public void Inspect([Borrowed] Resource value)
    {
        retained = value; // SS0027 at the borrowed source
    }
}
```

Keep access within the call or retain independent copied data. If the object should own the argument, redesign the method with a consuming contract and ensure the aggregate releases it. C# cannot express an arbitrary Rust-style lifetime parameter tying a persistent field to an external owner.

Origin tracking covers direct stores, conversions, conditional/coalescing results, tuples/arrays, and named returned-borrow relationships. Arbitrary aliases through containers, unknown methods, and every local-variable chain are outside this dedicated checker. The core supplies complementary modeled loan-escape analysis.

## SS0028

**Captured resource may outlive its using scope.** A returned or persistently stored delegate, event callback, or unjoined `Task.Run` captures a resource governed by an enclosing using lifetime.

```csharp
Func<int> CreateReader()
{
    using var stream = new MemoryStream();
    return () => stream.ReadByte(); // SS0028
}
```

Return a value computed within the scope, or transfer ownership into an object whose lifetime includes callback execution and whose disposal is explicit. For task work, await completion before leaving the using scope.

The rule recognizes direct awaits, framework `ConfigureAwait`, parameterless synchronous joins, and a task local immediately awaited in the next statement. A timeout/cancellation-capable `Wait` is not guaranteed completion. Returning a task from a method with a using declaration does not extend the resource's lifetime. General scheduler/callback registration APIs, combinators, and arbitrary delegate aliasing remain boundaries.

## SS0029

**Service graph analysis reached its budget.** Recursive generic expansion or a very large graph prevented complete exploration of a singleton's dependencies.

```csharp
sealed class Node<T>
{
    public Node(Node<List<T>> next) { }
}
// An open-generic registration expands Node<T>, Node<List<T>>, ... .
```

Inspect recursive registration and constructor design. Replace unintended expansion with a finite abstraction or a factory whose actual lifetime effects are explicit. The current service graph bounds each search to 4,096 implementation/registration pairs and 64 path nodes. Those limits are separate from the configurable method-analysis budgets in SS0015.

This Info diagnostic explicitly reports incompleteness; a missing SS0026 witness after exhausting the budget does not establish lifetime safety. Distinct factories producing the same implementation remain distinct search alternatives.

## SS0030

**Cleanup can be bypassed by an exception.** A recognized local acquisition has later manual cleanup and an intervening potentially throwing operation outside the protection of that cleanup.

```csharp
var owner = new Resource(); // SS0030
owner.Read();               // Can throw before cleanup
owner.Dispose();
```

Use `using var owner = new Resource();`, `await using`, or a try/finally beginning before the first hazardous operation. Placing a try/finally after an earlier throwing operation does not protect that earlier operation. Related locations show a hazard and the later cleanup.

This is an advisory over recognized local declaration/cleanup shapes. It looks for operations such as calls, construction, awaits, throws, and indexing; it is not a complete exception-effect system for property access, arithmetic, native code, external cleanup wrappers, or every control-flow shape. A clean result from this rule alone does not prove exception-safe cleanup. Explicit catch/finally paths modeled by the ownership engine still receive their ordinary flow diagnostics.

## SS0031

**Asynchronous disposal is not observed.** The returned awaitable from a recognized `DisposeAsync` invocation is dropped, including direct explicit discard and a discarded configured awaitable.

```csharp
owner.DisposeAsync();                         // SS0031
_ = owner.DisposeAsync().ConfigureAwait(false); // SS0031

await owner.DisposeAsync();                   // Observed
```

Await the disposal, return its awaitable from an API whose caller owns completion, or use `await using`. The [.NET async disposal guidance](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/implementing-disposeasync) describes `ValueTask` as the disposal operation's completion.

This rule distinguishes actual disposal-interface/pattern methods from unrelated namesake methods. Assigning the awaitable to a local for later use is not itself a dropped-expression warning, but SafeSharp does not prove general task-local completion/ownership handoff through arbitrary wrappers. Keep disposal observation direct when possible so the core can relate completion to the resource lifetime.

## SS0032

**Operation crosses the ownership model boundary.** An opt-in audit reports dynamic invocation/construction/indexing or recognized pointer/unsafe operations whose aliases and effects are not proved by the ownership model.

```editorconfig
[*.cs]
dotnet_diagnostic.SS0032.severity = suggestion
```

```csharp
dynamic value = GetDynamicValue();
value.Run(); // SS0032 when enabled

unsafe int Read(int* pointer) => *pointer; // SS0032 when enabled
```

Use a statically typed, contracted wrapper when its behavior can be modeled. Where unsafe code is essential, review the actual lifetime contract and keep the boundary small. The rule also recognizes address-of and explicit stack allocation syntax; it does not claim to enumerate every possible unsafe feature or native interop mechanism.

This is an audit notification, not a finding that the operation is necessarily incorrect. It is disabled by default to avoid conflating unsupported proof obligations with definite ownership violations.

## SS0033

**Resource result has no ownership contract.** An opt-in audit finds a tracked resource-returning invocation with no explicit, inferred, or recognized framework ownership/borrowing summary.

```csharp
interface IFactory
{
    Resource Create(); // Who owns this result?
}

var value = factory.Create(); // SS0033 when enabled
```

Annotate a fresh owned result with `ReturnsOwned`. Annotate a view with `ReturnsBorrowed("parameter")` or `ReturnsBorrowed("this")` and maintain that owner's lifetime. If an external API cannot be annotated, provide a thin, accurately contracted adapter. Enable the rule with `dotnet_diagnostic.SS0033.severity = suggestion`.

Known owned factories and recognized service resolutions are treated through their existing models. Limited direct-source inference can recognize obvious fresh construction and parameter-return relationships. An unannotated external result is otherwise external, avoiding an invented local disposal obligation. The diagnostic does not infer ownership from a method's spelling or from an unrelated `ReturnsOwnedAttribute`.

## SS0034

**Implementation changes an ownership contract.** An override or interface implementation explicitly declares a parameter mode different from the corresponding inherited contract.

```csharp
interface IReader
{
    void Inspect([Borrowed] Resource value);
}

sealed class Reader : IReader
{
    public void Inspect([Consumes] Resource renamed) { } // SS0034
}
```

Keep the implementation compatible with the interface/base contract. Omitted annotations inherit the declared mode; repeating the same mode is valid. Parameter matching uses ordinal position, so renaming a parameter does not change its contract. `Owned` and `Consumes` are treated as the same consuming mode. Named returned-borrow sources inherited by methods are remapped to the implementation parameter at that ordinal.

This check compares explicit parameter ownership modes. It does not implement a complete behavioral-subtyping proof for arbitrary mutation, exception, return-lifetime, or thread capabilities, and multiple inherited declarations can require separate compatibility work. Inherited contracts are trusted summaries at call sites while supported implementation effects are checked by the ownership engine.

## Configuration and adoption

Start with enabled rules, make contract boundaries explicit, then enable the two audit rules for APIs or unsafe subsystems under review. Standard compiler suppression and severity policies apply:

```editorconfig
[*.cs]
dotnet_diagnostic.SS0001.severity = error
dotnet_diagnostic.SS0026.severity = error
dotnet_diagnostic.SS0030.severity = suggestion
dotnet_diagnostic.SS0032.severity = suggestion
dotnet_diagnostic.SS0033.severity = suggestion

safesharp_report_possible_issues = true
safesharp_check_suspension = true
safesharp_max_iterations = 10000
safesharp_max_tracked_resources = 2048
```

`safesharp_report_possible_issues = false` filters possible core findings; it does not strengthen definite findings or disable declaration/service checks. `safesharp_check_suspension = false` changes the core suspension policy. Use standard `#pragma warning disable SSxxxx` or a targeted severity setting only after reviewing the stated boundary and related evidence.

Generated code is skipped by the analyzers. Cancellation is observed during compiler work and graph exploration. Test fixtures run real Roslyn compilations and fail on malformed source or analyzer exceptions; positive and negative cases cover exact spans, aliases, symbol shadows, inherited contracts, disposal patterns, callback joins, DI alternatives, and analysis budgets. See [integration](integration.md) for compiler/IDE package installation and CI use.
