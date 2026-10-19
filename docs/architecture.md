# Architecture and analysis contracts

## Boundaries

SafeSharp is an independent C# implementation inspired by Rust's borrow checker design. It does not embed Rust compiler code, translate C# into Rust, replace the CLR, or claim a proof of memory safety for arbitrary C#. Explicit ownership contracts and recognized resource APIs give the analyzer a model it can check. Ordinary unannotated C# reference assignment retains its runtime aliasing semantics.

```mermaid
flowchart TD
    C["C# symbols and operations"] --> R["Roslyn CFG normalization"]
    A["Contracts and method summaries"] --> R
    R --> I["Language-independent ownership IR"]
    I --> L["Backward place liveness"]
    I --> F["Forward ownership fixed point"]
    L --> F
    F --> D["Diagnostic replay and evidence"]
    D --> IDE["Compiler and IDE diagnostics"]
    D --> G["Graphs and reports"]
```

`SafeSharp.Core` owns IR validation, abstract state, place overlap, loan liveness, transfer semantics, the fixed-point schedule, and results. It has no Roslyn dependency. `SafeSharp.Analyzers` owns symbol classification, contract summaries, CFG normalization, diagnostic descriptors, and compiler registration. Its services live for one compilation; there is no mutable global compilation cache. `SafeSharp.Reporting` only depends on Core and formats immutable results. `SafeSharp.Tool` owns file I/O, workspace loading, compiler-error handling, output selection and exit status. `SafeSharp.Contracts` contains the small public annotation vocabulary. `SafeSharp.CodeFixes` is an optional independent IDE package.

## Abstract domain

A place is a stable parameter/local/capture identity followed by zero or more projections. A resource is identified by its allocation site or external input. Places contain sets of abstract reference alternatives: resource identity, availability, loan origin, field projection, and moved subpaths. Resources carry ownership kind, lifecycle flags, acquisition evidence, and optional owning-scope identities. Loans carry shared/exclusive kind, holder, origin, projection, reborrow ancestry, and suspension policy.

At a control-flow merge the engine retains alternatives. It does not collapse `alive OR disposed` into either `alive` or `disposed`. A finding is **definite** only when all represented alternatives violate the rule; otherwise it is **possible**. Absent allocations and place/resource correlations matter: two allocations in mutually exclusive branches must not look like two simultaneously leaked resources.

Moves invalidate existing aliases to the transferred value. A moved-to place obtains the current ownership handle; stale aliases do not regain access. Ordinary assignment preserves alias identity, so disposal through any alias affects the others. Reassignment starts a new local ownership value. Structural projections support overlapping aggregate/field access and partial moves; unknown array indices conservatively overlap. Heap aliases introduced by unmodeled APIs, reflection, native code or pointer arithmetic remain outside the proof boundary.

Allocation-site abstraction makes loops finite. It does not count concrete allocation instances. Reusing an allocation site must not reactivate a stale alias from an earlier iteration. The model and tests distinguish branch-local allocations, loop-carried aliases, repeated disposal, and owner overwrites.

## Solvers and non-lexical lifetimes

The backwards solver computes live places at each instruction. The forward worklist joins predecessor states, applies ownership effects, and expires loans according to those live sets. A loan can end before its variable's lexical block ends. Multiple shared borrows are compatible; an exclusive borrow conflicts with overlapping loans. Reborrows retain the necessary parent ancestry so the parent cannot be used while a child loan is active.

Diagnostics are replayed after convergence, preventing warnings based solely on transient solver states. Related locations identify concrete source origins and conflicting effects. Evidence is an abstract explanation, not a fabricated single feasible counterexample path: a may-analysis join may combine facts from different execution paths. JSON/SARIF/HTML preserve that distinction.

The engine is reentrant, uses finite resource/place/loan identities, checks cancellation, and imposes configurable resource and iteration limits. Reaching a limit emits `SS0015`; service dependency exploration has its own `SS0029`. Snapshot capture is off inside ordinary compiler analysis, so detailed per-instruction snapshots are only materialized when a reporting caller requests them.

## C# control flow and exceptions

Roslyn represents `using` and `try/finally` with implicit cleanup continuations. Normalization makes these edges explicit and clones cleanup contexts for distinct return destinations. A return expression is evaluated before cleanup, but the returned ownership value is validated after cleanup. This catches returning a resource that a `using` declaration disposes while leaving the method.

Null tests are edge assumptions. Impossible null branches after known allocations must not create false leaks through skipped implicit disposal. The exception model separates explicit throw/catch/finally control flow from implicit exception boundaries; the implementation and tests cover the supported normalized cases. Ordinary unhandled exception safety is also audited by `SS0030`, which recommends structured cleanup where a later manual release can be bypassed.

An exception thrown by arbitrary native code or a partially executing disposal implementation cannot be reconstructed completely from an analyzer's method-local model. Arbitrary library calls also need summaries to expose retained aliases or ownership transfers. See the precise [Rust support matrix](rust-design.md) and [diagnostic limitations](diagnostics.md).

The caller's disposal obligation is discharged when the recognized cleanup operation is invoked, including when its implementation subsequently throws. The analyzer verifies the caller's release protocol; successful physical cleanup remains the implementation's responsibility. Consequently, a correct `using` inside a catchable region does not become a leak merely because `Dispose` itself may throw. Reusing the resource after that attempted cleanup is still diagnosed.

## Interprocedural and service analysis

Boundary summaries recognize explicit consuming, borrowing, mutation and return contracts. Source inference is deliberately restricted to direct unambiguous returned allocations, returned parameters, and simple direct disposal effects. It is not a whole-program virtual-dispatch or recursive effect proof. Known BCL factories, pools, memory-owner views and dependency-injection resolution methods supply additional summaries.

An implementation that omits parameter annotations inherits the interface or overridden declaration's contracts. Parameter relationships are mapped by ordinal, so renaming a parameter does not disconnect an inherited returned borrow. Incompatible explicit parameter modes produce `SS0034`. Getter summaries consult associated base/interface property annotations. Expression-bodied properties and indexers participate in direct fresh-result inference and receive the same report graphs as ordinary methods.

Cleanup recognition resolves the actual `IDisposable`/`IAsyncDisposable` interface implementation and its virtual overrides; a same-name unrelated method does not release a resource. Valid disposal patterns on ref structs are also recognized. Assignments through `ref`/`out` parameters store into caller-visible locations and transfer modeled ownership just as a longer-lived heap store does; `out` parameters have no incoming value. This transfer model does not infer arbitrary output ownership or retained aliases for unknown external calls, and explicit contracts remain necessary at those boundaries.

The service analyzer builds a separate constructor-dependency graph from annotated lifetimes and supported registration syntax. It detects transitive singleton capture of scoped dependencies and retains the dependency path as metadata. Container-created values remain borrowed/container-owned. A local scope relationship allows the ownership engine to detect service use after scope disposal. Factories, custom containers, runtime registration mutation, keyed matching and reflection require the documented qualifications.

## Extension points

- Add another language frontend by producing `AnalysisGraph` and `FlowInstruction` values; Core remains reusable.
- Embed `OwnershipCompilationAnalysis.Analyze` in a custom Roslyn tool and request snapshots explicitly.
- Use `SafeSharpAnalyzerCatalog.Create()` to run the same complete compiler analyzer set as the CLI.
- Implement `IAnalysisReportWriter` for another deterministic export format.
- Extend contract summaries and registration recognition independently of the solver.

Each abstraction owns one task. Exporters do not reach into Roslyn, compiler callbacks do not write files, and the core domain does not depend on IDE services.
