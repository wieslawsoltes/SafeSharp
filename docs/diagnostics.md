# Diagnostic reference

SafeSharp implements **42 Roslyn diagnostics across five analyzers** for ownership, borrowing, disposal, explicit lifetime regions, concurrency contracts, and dependency injection. They run during compiler and IDE analysis. The ownership engine computes effects over Roslyn control-flow graphs; separate semantic, boundary, lifetime, and concurrency analyzers check their corresponding declarations, implementation obligations, and call boundaries.

Every rule below is implemented. “Implemented” describes the stated check within the documented model, not equivalence to Rust's language-level guarantees. `Ownership.Move`, `Borrow`, and `BorrowMut` are metadata-bearing passthrough methods; C# still permits aliasing and value-type copies at runtime. See [implementation status](implementation-status.md), [architecture](architecture.md), [contracts](contracts-and-semantics.md), and [Rust design references](rust-design.md) for the analysis domain and its limits.

## Rule matrix

“Info” appears as a suggestion in typical IDE configurations. Every diagnostic can be configured with `dotnet_diagnostic.<ID>.severity`. Within SS0001–SS0049, only SS0018–SS0019 and SS0035–SS0039 are unassigned.

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
| [SS0017](#ss0017) | Tracked async disposal completion remains pending | Ownership | Warning | Yes |
| [SS0020](#ss0020) | Aggregate lacks a disposal contract | Semantic | Warning | Yes |
| [SS0021](#ss0021) | Owned member is not released on every normal disposal exit | Semantic | Warning | Yes |
| [SS0022](#ss0022) | Ownership-bearing struct remains copyable | Semantic | Warning | Yes |
| [SS0023](#ss0023) | Contradictory or invalid contract | Semantic | Warning | Yes |
| [SS0024](#ss0024) | Required result is ignored | Semantic | Warning | Yes |
| [SS0025](#ss0025) | Thread-affine value captured by `Task.Run` | Semantic | Warning | Yes |
| [SS0026](#ss0026) | Singleton transitively depends on scoped service | Semantic | Warning | Yes |
| [SS0027](#ss0027) | Borrowed argument may escape into persistent storage | Semantic | Warning | Yes |
| [SS0028](#ss0028) | Escaping callback captures a using resource | Semantic | Warning | Yes |
| [SS0029](#ss0029) | Service graph exploration incomplete | Semantic | Info | Yes |
| [SS0030](#ss0030) | Exception can bypass later manual cleanup | Boundary | Info | Yes |
| [SS0031](#ss0031) | Asynchronous disposal result is syntactically dropped | Boundary | Warning | Yes |
| [SS0032](#ss0032) | Dynamic/pointer operation crosses model boundary | Boundary | Info | No |
| [SS0033](#ss0033) | Resource-returning API lacks a known contract | Boundary | Info | No |
| [SS0034](#ss0034) | Implementation changes inherited ownership mode | Boundary | Warning | Yes |
| [SS0040](#ss0040) | Invalid explicit lifetime declaration | Lifetime | Warning | Yes |
| [SS0041](#ss0041) | Returned provenance does not satisfy a lifetime/source promise | Lifetime | Warning | Yes |
| [SS0042](#ss0042) | Incompatible inherited lifetime or return contract | Lifetime | Warning | Yes |
| [SS0043](#ss0043) | Lifetime-region analysis is incomplete | Lifetime | Warning | Yes |
| [SS0044](#ss0044) | Invalid Send/Sync declaration or generic composition | Concurrency | Warning | Yes |
| [SS0045](#ss0045) | Captured value does not satisfy its concurrency boundary | Concurrency | Warning | Yes |
| [SS0046](#ss0046) | Cleanup, concurrency or borrowed escape analysis is incomplete | Semantic / Concurrency | Info | Yes |
| [SS0047](#ss0047) | Callback violates an annotated delegate lifetime contract | Lifetime | Warning | Yes |
| [SS0048](#ss0048) | ValueTask capability is consumed or transferred more than once | Ownership | Warning | Yes |
| [SS0049](#ss0049) | Required synchronization guard or lock protocol is not established | Concurrency | Warning | Yes |

The public analyzer classes and descriptor categories are:

| Analyzer | Public class | Category |
| --- | --- | --- |
| Ownership | `SafeSharp.Analyzers.OwnershipAnalyzer` | `Ownership` |
| Semantic | `SafeSharp.Analyzers.Semantics.SafeSharpSemanticAnalyzer` | `SafeSharp.Ownership` |
| Boundary | `SafeSharp.Analyzers.BoundarySafetyAnalyzer` | `Ownership` |
| Lifetime | `SafeSharp.Analyzers.Semantics.LifetimeContractAnalyzer` | `SafeSharp.Lifetimes` |
| Concurrency | `SafeSharp.Analyzers.Semantics.ConcurrencyContractAnalyzer` | `SafeSharp.Ownership` |

`SafeSharpAnalyzerCatalog.Create()` supplies the complete set for custom Roslyn hosts. Configure individual IDs when a policy spans categories.

## Reading certainty, related locations, and graphs

Ownership diagnostics include `SafeSharp.Certainty`, `SafeSharp.ResourceId`, and `SafeSharp.Evidence` properties. `Definite` means the modeled alternatives reaching that check violate the rule; `Possible` means the abstract state also admits a nonviolating alternative. At a conditional merge, a resource disposed only on one branch typically yields a possible use-after-disposal. These labels describe the abstract model, including its conservative joins, trusted contracts, and recognized library effects. They are not proof that a particular execution reaches the issue.

Related locations identify acquisition, transfers, conflicting loans, releases, or relevant exits. JSON, SARIF, HTML, Mermaid, and DOT reports retain analysis evidence; see [reporting](reporting.md). Graph states are computed facts, and a displayed control-flow path is not an automatically solved concrete counterexample. Declaration and boundary diagnostics do not carry the core's certainty property unless their analysis produces it. Service diagnostics carry dependency paths; SS0021 records cleanup entrypoints and normal-exit proof scope; lifetime diagnostics record declared regions, constraints, and return provenance; concurrency diagnostics record the required capability and failing component path. SS0049 retains the required guard, protocol, held-capability state and failure reason. Nested lifetime failures also retain binder/variable universes, placeholder visibility proofs, promotions and type-outlives assumptions/requirements.

The core runs forward ownership analysis to a fixed point and backward place liveness to determine loan ends. Consequently, a loan can end at its last use before its lexical block closes. This borrows the control-flow approach described by the [Rust compiler borrow-checker guide](https://rustc-dev-guide.rust-lang.org/borrow-check.html). SafeSharp also solves [explicit named lifetime regions](lifetime-regions.md), while Rust's complete region and type system remains outside the implementation. See [advanced protocols](advanced-engine.md) for reserved/active loans and first-class async completion states.

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

A stored owned Task/ValueTask factory can leave a possible result-production obligation. Dropping that carrier without receiving, transferring or otherwise discharging its modeled ownership receives SS0003 with Possible certainty: the factory may still fault before producing a resource. A successfully received result has its own identity and lifecycle; repeat receipt preserves it. Pending result carriers lost on overwrite receive SS0011. These are result ownership findings; SS0017 specifically covers asynchronous disposal completions. See [async result ownership](async-result-ownership.md).

Unannotated external results and ordinary borrowed inputs are not automatically assumed to be locally owned. Use SS0033 to audit unknown factory boundaries. SS0030 complements this exit analysis with an advisory for recognized manual cleanup that an earlier exception can bypass.

## SS0004

**Use after ownership transfer.** A place or its aliases are used after a recognized move or consuming argument transfers ownership.

```csharp
var original = new Resource();
var next = Ownership.Move(original);
original.Read(); // SS0004
next.Dispose();
```

Use `next` after the move. If the callee only inspects the object, give its parameter a borrowing contract instead of a consuming contract. Passing through an interface or override inherits the declared parameter ownership by ordinal even when the implementation renames its parameter. A supported TransfersToThread callback boundary also moves its tracked captures, so the caller must stop using those references and their aliases. Conditional callback targets retain possible-transfer alternatives.

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

Borrowed parameter retention also has a complementary declaration-aware CFG rule, SS0027, which follows aliases, modeled containers and captured cells. Neither checker establishes a complete general heap-escape proof.

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

The frontend also uses SS0015 when its transferred-callback ownership adapter exhausts fixed collection bounds: 20,000 capture steps, 20,000 scope operations, 256 transfer calls per scope, or 128 visible callback targets. These limits are independent of the structural Send/Sync budget in SS0046 and the region-solving budget in SS0043.

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

## SS0017

**Asynchronous disposal completion remains pending.** A recognized `DisposeAsync` call successfully returns an awaitable, but the resulting tracked completion reaches a modeled exit without observation or transfer, loses its last usable handle at an overwrite, or is replaced by another still-pending initiation at the same repeated allocation site.

```csharp
async Task Close([Consumes] IAsyncDisposable owner, bool finish)
{
    var pending = owner.DisposeAsync();
    if (finish)
        await pending;
    // SS0017: the other normal path still owns a pending completion.
}
```

Await the completion on every path, use `await using`, or forward it through a return or accurately declared consuming boundary. Local aliases and recognized `ConfigureAwait`, `AsTask`, and `GetAwaiter` wrappers preserve identity. A parameterless framework `GetResult()` or `Task.Wait()` observes completion; timeout/cancellation overloads, `IsCompleted`, an explicit discard, and disposing the Task itself do not establish unconditional observation.

The primary exit diagnostic identifies the initiating async disposal; an overwrite diagnostic identifies the lost handle and retains the initiation as related evidence. Conditional observation can produce a possible finding. The resource's disposal invocation and the completion obligation are distinct: a synchronous throw before an awaitable is returned creates no completion, while awaiting a faulted operation still observes it. This is an invocation/observation protocol, not proof that a failing disposer released every physical resource.

SS0031 checks a directly dropped disposal expression. SS0017 follows the deferred obligation through modeled aliases, joins, transfers, and loops, so assigning the result to an unused local does not hide it. Both rules can describe the same dropped operation. See [async completion analysis](advanced-engine.md#asynchronous-disposal-completion) for the complete protocol and its interprocedural limits.

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

**Owned member is not released on every normal disposal exit.** An aggregate exposes a disposal interface, but at least one modeled normal exit leaves an owned member's resource or asynchronous completion obligation outstanding.

```csharp
sealed class Reader : IDisposable
{
    [Owned] private readonly MemoryStream stream = new(); // SS0021
    private readonly bool leaveOpen;
    public Reader(bool leaveOpen) => this.leaveOpen = leaveOpen;
    public void Dispose()
    {
        if (leaveOpen)
            return; // This normal path omits cleanup.
        stream.Dispose();
    }
}
```

Release the member on every normal path, usually with a `finally`, a using scope, or an unconditional proven helper. The CFG analysis follows branches, early returns, loops, exact local aliases, replacements, using/finally continuations, directly called source helpers, and local functions. It resolves inherited owned fields, actual disposal implementations, virtual `Dispose(bool)` helpers, and `base` calls. Constant `Dispose(true)` arguments permit the ordinary managed-cleanup branch to be proved. Each exposed disposal interface is checked independently.

Starting asynchronous cleanup is insufficient when its completion is never observed or returned. Replacing or clearing a live field does not release the previous resource. A derived override that omits inherited cleanup is reported at the derived type, with the base member as related evidence. SS0021 properties include `SafeSharp.CleanupOwner`, `SafeSharp.CleanupEntrypoints`, and `SafeSharp.CleanupProof=normalExitMustRelease`.

The proof concerns modeled normal exits, including recovered exceptions. Unhandled exceptions, successful physical cleanup inside a throwing disposer, arbitrary heap aliases, unknown helper effects, and constructor-to-dispose invariants remain boundaries. Recursive helper reentry supplies no assumed release; ordinary `_disposed` flag invariants can lose precision. SS0046 reports exhausted analysis budgets. See [aggregate cleanup](aggregate-concurrency.md#aggregate-cleanup-ss0020ss0022-and-ss0046) for supported flow and limits.

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

**Borrowed argument may escape into persistent storage.** Converged CFG may-origin analysis finds a parameter marked `Borrowed`, `BorrowedMut`, or `DoesNotEscape` reaching a persistent storage or escape boundary.

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

Origin tracking covers local/ref aliases, conditional/coalescing results, branches, loops, modeled call summaries, allocation containers and captured cells. Destinations include fields, properties, events, external array/ref-out storage, and returned or persistently stored containers/delegates. `DoesNotEscape` additionally excludes direct return and explicit consuming calls. An ordinary borrowed return can use the supported returned-loan contract.

Each diagnostic supplies `SafeSharp.BorrowedSource`, `SafeSharp.EscapeDestination`, `SafeSharp.EscapeAnalysis`, `SafeSharp.Certainty`, and `SafeSharp.DoesNotEscape`, with source/destination locations. The solver reports **possible** retention, uses conservative exception/capture envelopes, and merges fields/elements within an allocation container. Unknown calls do not establish a general heap effect. SS0046 identifies budget exhaustion. See [borrowed escape analysis](borrowed-escape.md) for the exact model; the core supplies complementary resource-loan escape diagnostics.

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

This rule distinguishes actual disposal-interface/pattern methods from unrelated namesake methods. It is a syntactic dropped-result check: assigning the awaitable to a local for later use does not trigger SS0031 by itself. The separate [SS0017](#ss0017) flow rule tracks that completion through supported aliases, branches, overwrites, and handoffs and reports it if still pending. Arbitrary external awaiter wrappers and unmodeled helpers still require an accurate contract or model.

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

This check compares explicit parameter ownership modes. The separate [SS0042](#ss0042) rule checks inherited returned ownership, lifetime, and source promises; [SS0044](#ss0044) checks concurrency composition. These checks do not implement a complete behavioral-subtyping proof for arbitrary mutation, exceptions, or synchronization. Inherited contracts are trusted summaries at call sites while supported implementation effects are checked by the ownership engine.

## SS0040

**Explicit lifetime declaration is invalid.** A lifetime name is not a supported identifier, an outlives endpoint is absent from the signature, `this` is used by a static method, a void result carries a lifetime, or ownership and borrowing contracts contradict the stated region. Conflicting returned loan mutability also fails declaration validation.

```csharp
[Outlives("input", "missing")] // SS0040: missing is not introduced.
static void Inspect([Lifetime("input")] Resource value) { }
```

Introduce each region on an actual parameter or result, correct a misspelled endpoint, and use the reserved `this` region only for an instance receiver. Lifetime names are scoped to a method signature. `Outlives("a", "b")` requires `a` to remain valid at least as long as `b`; it does not introduce either name. Equal names share a region, transitive constraints compose, and cycles establish equivalent regions rather than an automatic contradiction.

A parameter with a borrowed lifetime cannot also consume ownership. Use `ReturnsOwned` for a newly owned result and reserve returned `Lifetime` annotations for loans. Resolve conflicting `Borrowed`/`BorrowedMut` return annotations and inconsistent `ReturnsBorrowed` mutability. Invalid declarations are diagnosed before body provenance is used as a proof. TypeLifetime additionally validates paths through actual generic arguments, array elements and nested callable parameters/results, duplicate annotations, binder-local names and explicit `^` outer references. Outlives.Scope and TypeOutlives.Scope must name an introduced callable binder; type bounds must name a real method/enclosing type parameter and a declared region. These annotations do not introduce an arbitrary field/heap path language. See [explicit lifetime declarations](lifetime-regions.md#ss0040) and [type lifetime declarations](type-lifetimes.md).

## SS0041

**Returned provenance does not satisfy its lifetime or source promise.** At least one modeled returned origin fails to outlive the promised region, comes from a fresh local owner, remains unknown, or violates a particular `ReturnsBorrowed` source restriction.

```csharp
[return: Lifetime("first")]
static Resource Choose(
    [Borrowed, Lifetime("first")] Resource first,
    [Borrowed, Lifetime("second")] Resource second,
    bool selectFirst) => selectFirst ? first : second; // SS0041
```

Return a source that satisfies the promised lifetime on every path. If both inputs are legitimate, declare a result region and accurate outlives relations from each supplying input, or give them the same intended region. Add a relation only when callers can uphold it. A stronger named-source promise still restricts origin even when another input has a sufficiently long lifetime.

The checker follows actual CFG assignments, aliases, branches, loops, supported source/call summaries, reference locations and bounded exception/filter/finally continuations. By-value return values are captured before cleanup and validated after it; ref returns preserve the selected storage location and inspect its normal-exit value. Unknown ref/out effects and captured-storage invalidation remain conservative. A valid branch cannot erase an unknown alternative. Returning a null creates no resource-lifetime obligation. Contracts on external calls remain trusted; an unknown helper or arbitrary property does not establish provenance by its name. Use an owned-return contract when returning a new owner rather than extending a local loan artificially.

Diagnostic properties preserve the required/source regions, original declared constraints, components, and available proof paths. Reports can render the declaration graph without inventing missing relations. Region proofs do not authorize disposal or use after move; the ordinary ownership rules still apply. See [return provenance](lifetime-regions.md#what-the-body-checker-follows) and [region reporting](advanced-engine.md#declared-lifetime-region-graphs).

## SS0042

**Implementation changes an inherited lifetime or return promise.** An override or interface implementation imposes an additional input outlives requirement, changes owned and borrowed return modes, redirects a named returned source, changes result mutability, or permits return origins that cannot satisfy the inherited promise.

```csharp
interface ISelector
{
    [return: Lifetime("a")]
    Resource Select([Lifetime("a")] Resource first,
                    [Lifetime("b")] Resource second);
}

sealed class Selector : ISelector
{
    [Outlives("b", "a")] // A new requirement on the caller's inputs.
    [return: Lifetime("a")]
    public Resource Select([Lifetime("a")] Resource first,
                           [Lifetime("b")] Resource second) => first; // SS0042
}
```

Keep the implementation's requirements and returned promise compatible with the interface/base API. Omitted annotations inherit the declaration, parameters are matched by ordinal, and consistent lifetime-name renaming is supported. Restating a relation already implied transitively does not introduce a new requirement. Change the public contract and its callers together when a genuinely stronger precondition is necessary.

SS0034 covers incompatible parameter ownership modes; SS0042 covers the lifetime/return comparison. Explicit nested TypeLifetime paths, scoped Outlives/TypeOutlives bounds, callable variance and inherited structural type substitutions participate in that comparison. Omitted nested contracts inherit the corresponding declaration. This does not prove arbitrary body contents, exception effects, synchronization or a complete Rust trait/type system. See [inherited lifetime compatibility](lifetime-regions.md#interface-and-override-compatibility) and [nested type contracts](type-lifetimes.md).

## SS0043

**Lifetime analysis is incomplete.** A named/higher-ranked region solve, nested type or inherited-contract comparison, generic substitution, or returned-origin flow analysis reaches its configured bound. A missing violation after this diagnostic is not an established lifetime proof.

```editorconfig
[*.cs]
safesharp_max_regions = 256
safesharp_max_region_constraints = 4096
safesharp_max_region_steps = 100000
```

Reduce an unnecessarily complex signature or method, inspect expanding relationships, or raise the relevant bound after measuring analysis cost. The displayed values are defaults. The analyzer caps effective values at 4,096 regions, 100,000 constraints, and 10,000,000 steps; invalid/nonpositive values use defaults. These limits are independent of SS0015's ownership iteration/resource limits and SS0046's aggregate/concurrency/escape budgets.

One shared budget covers assumptions, requirements, implicit static relations, type bounds, universe visibility checks and promotions for a higher-ranked problem. Type paths and structural substitution are bounded at depth 64, and path text is also bounded. The reusable solver returns an incomplete solution without partial components or proofs. Suppressing SS0043 suppresses the notification, not the missing proof. See [the reusable region solver](lifetime-regions.md#reusable-region-solver) and [higher-ranked limits](higher-ranked-regions.md).

## SS0044

**Concurrency declaration or composition is invalid.** A declared/inherited Send or Sync contract fails its structural conditions, a generic argument does not meet a required capability, or a callback boundary is attached to a non-delegate parameter or declares contradictory transfer and sharing modes.

```csharp
[Sync]
sealed class Counter // SS0044
{
    public int Value; // Mutable shared storage has no synchronization contract.
}

sealed class Snapshot<[Sync] T> { }
sealed class Consumer
{
    Snapshot<object> snapshot = new(); // SS0044: object has no Sync contract.
}
```

For Sync, make source-visible storage readonly or init-only and ensure each component also supports sharing. A readonly reference slot does not make a mutable array or unknown object shareable. Use ThreadSafe only when the implementation actually upholds the trusted synchronization promise. ThreadSafe cannot erase known ThreadAffine state, including state nested in base fields, tuples, nullable values, and constrained generic components.

For Send, require Send components and declare ownership of mutable reference fields unless their component type also supports Sync. Send and Sync are independent; adding one does not infer the other. State generic requirements with `[Send]`/`[Sync]` type-parameter annotations or suitable annotated constraints, and satisfy them at type construction and generic calls, including inferred calls. Metadata-only annotations remain trusted external contracts.

Related locations and `SafeSharp.ConcurrencyCapability`, `SafeSharp.ConcurrencyPath`, and `SafeSharp.ConcurrencyReason` identify the failing composition. This is structural contract validation rather than a proof of arbitrary race freedom. See [concurrency contracts](aggregate-concurrency.md#explicit-concurrency-ss0044ss0046) for the intrinsic type list and trust boundaries.

## SS0045

**Captured value does not satisfy a concurrency boundary.** A visible capture fails Send at a TransfersToThread boundary or Sync at a SharesAcrossThreads boundary; a captured cell or shared static slot is writable; or an explicit boundary receives an opaque delegate whose captured values cannot be established.

```csharp
[Send]
sealed class WorkItem
{
    public int Attempts;
}

void Schedule(WorkItem item)
{
    Task.Run(() => item.Attempts++); // SS0045: ordinary capture requires Sync.
}
```

For a shared callback, capture stable independent data or a type with a valid Sync contract. For exclusive work, use an API whose TransfersToThread contract actually transfers ownership, and honor that contract in its implementation and callers. A writable captured `int` cell can still be unsafe even though the value type is intrinsically shareable; make the captured variable stable instead of relying only on its type annotation. Static storage remains shared at a transfer boundary.

The new Task.Run sharing check applies to captured values with explicit Send, Sync, or ThreadSafe contracts; SS0025 separately checks ThreadAffine captures. The checker resolves visible lambdas, local functions, bound method groups, conditional/coalesced callbacks, and local delegate aliases conservatively. An incoming callback with the same checked parameter contract can be forwarded. External scheduler behavior, opaque delegate internals, arbitrary heap aliases, and synchronization implementations remain trusted or unsupported boundaries.

At a supported TransfersToThread call, the ownership adapter moves tracked captures before the scheduling invocation's exception boundary. Caller aliases become stale, and visible lambda/local-function targets receive their own cleanup and async-completion obligations. A capture present in only some resolved targets receives a conditional move. Shared/static storage remains shared. Ordinary method-group receivers transfer at the caller, while cleanup inside those ordinary methods remains a trusted boundary; opaque delegates receive SS0045 when the explicit structural boundary cannot establish their captures.

The primary diagnostic points to the capture; related locations retain the scheduling call and relevant declarations. Capability/path/reason properties are preserved in reports. See [capture and cell analysis](aggregate-concurrency.md#callback-captures-and-cells).

## SS0046

**Cleanup, concurrency, synchronization or borrowed escape analysis is incomplete.** Cleanup summaries, CFG continuation exploration, structural type composition, generic checking, callback resolution, guard capability flow, or borrowed-origin propagation exceeds an analysis bound. The corresponding contract property has not been established.

```editorconfig
[*.cs]
safesharp_max_contract_steps = 20000
```

Inspect recursive helpers, deeply expanding structures, or complex callback aliases. Simplify the model or raise the step budget after measuring compiler cost. This key uses per-file configuration before global configuration, defaults to 20,000, and caps positive effective values at 1,000,000. Invalid values use the default.

Aggregate checks additionally bound tracked members to 512, active summaries to 32, cached summaries to 256, and CFG continuation contexts to 8,192. Concurrency checks bound recursive type and delegate alias depth to 128 and each source-visible affinity traversal to 128 distinct types. Synchronization additionally bounds contextual states to 4,096, simultaneously tracked capabilities to 256, operation depth to 256 and candidate targets per reference binding to 256. Borrowed escape checks bound expression depth to 256 and charge propagation work to the per-method contract budget. Their budgets are separate from ownership fixed-point and lifetime-region limits. Existing SS0021 findings may accompany an incomplete aggregate result.

SS0046 is informational and enabled by default. The CLI treats it as incomplete analysis even if its visible diagnostic is suppressed. See [aggregate limits](aggregate-concurrency.md#analysis-limits), [concurrency limits](aggregate-concurrency.md#what-these-contracts-establish), [synchronization limits](synchronization-analysis.md), and [borrowed escape analysis](borrowed-escape.md).

## SS0047

**Callback does not satisfy its annotated delegate contract.** A method group, anonymous function or distinct typed delegate conversion cannot preserve the target's explicit lifetime, ownership or borrowing promise for every caller lifetime allowed by that delegate.

This check activates when the target delegate declares named lifetime regions, TypeLifetime/TypeOutlives contracts or an owned return. Borrowing, consuming or no-escape annotations alone do not activate SS0047.

```csharp
[return: Lifetime("a"), Borrowed]
delegate Resource Pick(
    [Borrowed, Lifetime("a")] Resource first,
    [Borrowed, Lifetime("b")] Resource second);

[return: Lifetime("y"), Borrowed]
static Resource Second(
    [Borrowed, Lifetime("x")] Resource first,
    [Borrowed, Lifetime("y")] Resource second) => second;

Pick callback = Second; // SS0047: the result requires b : a, which Pick never promised.
```

Use a callback returning the promised input, or declare an appropriate `[Outlives("b", "a")]` relationship on the delegate when every caller can honor it. Preserve its explicit borrowing, consuming and no-escape parameter modes. A captured receiver remains independent of arbitrary future delegate inputs; a task's own provenance does not prove the lifetime of its eventual payload.

The primary location is the callback conversion, or the invalid returned expression for a lambda analyzed under its target promise. Related locations identify declarations and origins. Universal failures carry exact assumed/required edges, generated-to-source region bindings and a deterministic shortest proof. Graphs distinguish assumptions from requirements. Budget exhaustion receives SS0043 and does not accept a partial result.

The higher-ranked adapter additionally checks corresponding TypeLifetime paths through generic arguments, arrays and nested delegates. Each callable boundary opens a binder; an outer capture cannot be generalized into an independent nested placeholder. Declared variance, writable-slot/array invariance, scoped outlives relations, explicit type bounds and supported implied input/owner bounds contribute to the obligations. A bound extension receiver with nested contracts needs corresponding evidence and is otherwise conservative.

For example, this delegate conversion attempts to promise a fresh nested lifetime for an outer captured object:

```csharp
[return: TypeLifetime("fresh", "return")]
delegate Func<object> Independent([Lifetime("a")] object owner);

[return: TypeLifetime("^input", "return")]
static Func<object> Capture([Lifetime("input")] object owner) => () => owner;

Independent callback = Capture; // SS0047: an outer lifetime cannot name the fresh placeholder.
```

A delegate whose result uses `TypeLifetime("^a", "return")` accurately preserves this capture relationship. Nested contents remain declared contracts; this check does not infer arbitrary collection/object contents or reproduce Rust's full higher-ranked trait/type solver. See [delegate contracts](lifetime-delegates.md), the [public universal solver](universal-regions.md), [higher-ranked regions](higher-ranked-regions.md), and [type lifetime contracts](type-lifetimes.md).

## SS0048

**ValueTask capability has already been consumed or transferred.** A represented ValueTask/ValueTask<T> handle is awaited, synchronously consumed, converted or transferred after an earlier consumption or transfer of the same capability. Copies and configured/awaiter aliases retain identity even when the eventual result has no disposal obligation.

```csharp
async Task Invalid()
{
    var pending = new ValueTask<int>(Task.FromResult(42));
    _ = await pending;
    _ = await pending; // SS0048: the same single-consumption capability.
}

async Task Reusable()
{
    var pending = new ValueTask<int>(Task.FromResult(42));
    var task = pending.AsTask();
    _ = await task;
    _ = await task; // The retained Task handle is reusable.
}
```

Consume the original handle once. When repeated observation is required, retain the result of the supported AsTask or Preserve conversion and use that result. The conversion consumes the original capability; using the original afterward can still fail. Explicit moves invalidate earlier copies, and known consuming parameters, returns or escaping storage transfer the capability. The model enforces the general ValueTask API protocol even when a particular represented construction wraps a Task or inline result; a default ValueTask with no represented inner source introduces no capability.

A join of available and consumed alternatives yields Possible certainty; reuse after every represented path consumed the capability yields Definite certainty. Related locations retain creation and prior consumption/transfer evidence. JSON/HTML snapshots expose the capability ID, state, aliases and sites. Unknown unannotated calls are not assumed to consume a ValueTask; declare Consumes where that boundary transfers it. This check does not establish completion readiness, correctness of arbitrary IValueTaskSource implementations or successful physical cleanup. SS0017 independently tracks unobserved asynchronous disposal. See [ValueTask consumption](value-task-consumption.md).

## SS0049

**Required synchronization guard or lock protocol is not established.** A guarded access or RequiresLock call lacks the matching capability on a modeled incoming path, a guarded reference escapes its required protection, or a supported lock operation violates its protocol. An invalid guard declaration also fails this contract check.

```csharp
sealed class Counter
{
    private readonly object gate = new object();
    [GuardedBy(nameof(gate))] private int value;

    public void Invalid() { value++; } // SS0049: gate is not held.
    public void Increment() { lock (gate) value++; }

    [RequiresLock(nameof(gate))]
    private void IncrementWhileHeld() { value++; }
}
```

Acquire the declared guard around the access or call, and release it on the required cleanup paths. A RequiresLock precondition is assumed when checking its body and required at callers; it does not acquire a lock at runtime. Valid guard paths identify the instance receiver, a method parameter or a supported stable field chain. The same field name on a different object is a different guard. Monitor, System.Threading.Lock and ReaderWriterLockSlim use distinct protocols; reader/writer operations additionally require the appropriate mode.

The diagnostic properties `SafeSharp.SynchronizationRequired`, `SafeSharp.SynchronizationProtocol`, `SafeSharp.SynchronizationHeld` and `SafeSharp.SynchronizationReason` preserve the requirement and observed abstract guard state. Structured HeldCount/Held records retain protocol and acquisition intervals; Access/AccessMode and RequiredIdentity preserve the member, mode and evaluated guard label when available. HTML/DOT/Mermaid show required-versus-recorded relationships without inferring identity equality from display names; JSON/SARIF retain the exact properties. Related locations identify the relevant declaration/acquisition. This is a sequential capability analysis over explicit contracts and supported APIs. GuardedBy does not make an otherwise invalid Sync structure valid; that composition check still has its own requirements. Guard analysis does not discover arbitrary synchronization, prove all thread interleavings, establish deadlock freedom or replace CLR memory-order rules. Complex/unknown effects retain the limitations in [synchronization analysis](synchronization-analysis.md); budget exhaustion receives SS0046 rather than a successful proof.

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
safesharp_max_regions = 256
safesharp_max_region_constraints = 4096
safesharp_max_region_steps = 100000
safesharp_max_contract_steps = 20000
```

`safesharp_report_possible_issues = false` filters possible core findings; it does not strengthen definite findings or disable declaration/service checks. `safesharp_check_suspension = false` changes the core suspension policy. Use standard `#pragma warning disable SSxxxx` or a targeted severity setting only after reviewing the stated boundary and related evidence.

Generated code is skipped by the analyzers. Cancellation is observed during compiler work and graph exploration. Test fixtures run real Roslyn compilations and fail on malformed source or analyzer exceptions; positive and negative cases cover exact spans, aliases, symbol shadows, inherited contracts, disposal patterns, completion protocols, callback boundaries, region provenance, concurrency composition, DI alternatives, and analysis budgets. See [integration](integration.md) for compiler/IDE package installation and CI use, and [implementation status](implementation-status.md) for the supported model and remaining boundaries.
