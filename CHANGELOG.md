# Changelog

## 0.4.0-preview.1

### Analysis

- Added reusable higher-ranked region problems, lexical binder trees, placeholders, inference variables and a static upper bound. Universe visibility is intersected across reachable regions; unnameable placeholder requirements produce explicit promotion and leak evidence. Nested and first-order solves share budgets and retain deterministic source-backed proofs without returning partial compatibility results.
- Added `[TypeLifetime]` paths and `[TypeOutlives]` bounds, plus scoped `[Outlives]` relationships. Roslyn delegate and inherited-contract checks compose declared generic variance, array/ref invariance, nested callable binders, outer captures, explicit and supported implied bounds, and structural generic substitution. Unsupported nested contents remain explicit contract boundaries.
- Added SS0050 for direct calls that do not establish explicit input type-lifetime or outer type-outlives requirements, bringing the catalog to 43 diagnostics. The existing nested binder checks stable caller evidence with inherited, generic, named-argument and reduced-extension mapping. Compilation-scoped cached mutation summaries conservatively invalidate content evidence after writes, reference exposure, opaque effects and represented alias escapes; missing evidence never becomes a proof.
- Added SS0048 for ValueTask capabilities consumed or transferred more than once. Aliases, moves, joins, stored/configured awaiters, result access, asynchronous cleanup, returned values and consuming calls share capability identity. `AsTask` and `Preserve` consume the original capability and produce modeled reusable handles.
- Extended asynchronous ownership through bounded nested Task/ValueTask layers and actual Roslyn-selected custom awaiter contracts. Framework `WhenAll`, `WhenAny`, `Unwrap` and `WaitAsync` preserve represented result and completion identities, including canonical aggregate element caches, original-task alternatives, fault boundaries and loser obligations. Unknown calls invalidate represented array contents before potential faults while preserving independent values and captured composition inputs.
- Added a shared bounded source control-flow router for source origins and lifetime returns. It preserves filter search before cleanup, throwing/rejecting filter effects, nested finally continuations, captured return values and normal-exit postconditions. Unknown effects and exhausted source inference remain conservative.
- Added a separate core reference-storage domain with flattened targets, reference rebinding, conditional references, evaluated ref/out argument locations, source ref-return targets and simultaneous normal-call write postconditions. Represented array refs and ordinary indexed accesses share canonical resource/ValueTask cells. Singleton writes update strongly; uncertain targets update weakly. Exceptional writable-ref calls weaken captured cells before filter selection and cleanup without replaying normal-return summaries. Captured-address liveness keeps old aggregate generations represented across writes, while earlier value snapshots and composition inputs retain their identities. Deep-container parent-loan permission propagation remains a separate limitation.
- Added `[GuardedBy]` and `[RequiresLock]` contracts and SS0049 for modeled guarded-access and lock-protocol violations. The concurrency analyzer tracks explicit Monitor, `System.Threading.Lock`, `ReaderWriterLockSlim` and `SemaphoreSlim` capabilities with receiver identity, aliases and control-flow joins. Exclusive semaphore access requires supported evidence that maxCount is 1. Pending acquisition identities, timeout results and release counts survive Task aliases without allowing repeated observation to acquire again. Semaphore permits are not thread-affine; existing thread-owned lock restrictions remain. Unknown wrappers, maximum counts and external signaling/release patterns retain explicit limits.

### Implementation and evidence

- Kept the new domains finite and cancellation-aware. Higher-ranked ancestry uses indexed intervals and binary lifting; placeholder-free problems avoid universe scans. Reference storage has a singleton fast path, async chains use visited identities, and summary/continuation worklists reuse indexed states. Direct-call effect summaries are cached per callable within a compilation, share immutable alias effects, and preserve their budget cost. Semaphore construction/token scans retain explicit bounds.
- Extended JSON/HTML snapshots with canonical reference storage, ValueTask capability state, nested async payloads and composition aggregates. Region diagnostics preserve binder and variable universes, visibility witnesses, promotions, type bounds and original proof edges; all report formats retain this evidence, including the reserved static-region node. Direct-call reports preserve SS0050 caller evidence and exact region graphs. Synchronization HTML/DOT/Mermaid diagrams distinguish the required guard from structured observed capability intervals, including the separate SemaphoreSlim protocol, with bounded parsing and exact metadata preservation.
- Validated source checkpoint [`66ae53d`](https://github.com/wieslawsoltes/SafeSharp/commit/66ae53da165fae4dc08034bc3da1e08fd9474ea2) in [CI](https://github.com/wieslawsoltes/SafeSharp/actions/runs/39428653978): 2,490 tests passed on each of Linux, Windows and macOS, with no failures or skips and zero Release-build warnings or errors. CLI checks, benchmark smoke, six-package verification, clean SDK consumers and the Roslyn 4.14 host all passed. The earlier 2,268-test result is historical. Positive, negative and adversarial coverage includes direct-call evidence, nested binders, generic substitution, exception routing, canonical reference aliases, async compositions, capability reuse and guard protocols. Original Core constructor signatures remain available; a previously compiled consumer is tested against the new assembly.
- Added three boundary benchmarks and archived an earlier 19-scenario measurement checkpoint with five warmups and fifteen samples. In that record, four liveness workloads improved against the historical HashSet baseline; sparse temporaries were 19.5% slower while allocating 46.1% less. New domains have current-only results. The comparison oracle retains the original HashSet algorithm with transfer rules extended for new IR semantics, including captured-address liveness. The [0.4 measurement record](docs/performance-0.4.md) labels the measurement checkpoints and raw JSON separately from the earlier 0.3 run. No whole-build speedup is claimed.

### Packages and integration

- Updated all six package projects and portable symbol packages to `0.4.0-preview.1`, with the same .NET Standard 2.0 contracts/analyzer boundary and minimum Roslyn 4.14 analyzer host.
- Extended clean SDK consumers and installed CLI checks with nested lifetime leaks, direct-call obligations, ValueTask reuse, semaphore/guarded access and canonical aggregate-ref behavior. The independent Roslyn 4.14 package host exercises the higher-ranked Core API, safe and unsafe nested captures, SS0048, SS0049, SS0050, report evidence and the live code fix.
- Updated architecture, diagnostic references, package installation, reporting, pinned Rust design references and the implemented/remaining boundary matrix. Packages remain unpublished until the release workflow runs.

## 0.3.0-preview.1

### Analysis

- Added stored asynchronous result ownership. Deferred factory carriers separate potential production, observation and successful receipt; repeated Task/ValueTask receipt retains cached resource identity and cannot revive disposed or moved results. Known completed producers bind existing resources and loans. Pending factory loss has possible certainty, separate from disposal-completion diagnostics.
- Extended bounded source origins through aliased reference storage, conditional refs, ref reassignment and evaluated ref/out argument locations. Strong singleton and weak multi-target writes preserve valid fresh results while unknown storage and executable effects remain conservative.
- Separated asynchronous payload origins from task-carrier origins in source summaries and their consumers. Transparent completed producers and actual async returns establish one payload layer; an ordinary Task identity or unknown constructor cannot invent payload freshness or lifetime.
- Added the reusable `UniversalRegionProblem`, `UniversalRegionSolver` and `UniversalRegionSolution` API. One total budget covers assumed and required closure plus universal-pair validation, with deterministic shortest failure evidence and no partial compatibility proof.
- Added SS0047 for annotated delegate conversions. Method groups, reduced extensions, lambdas, inherited contracts and distinct delegate types preserve lifetime, ownership and borrowing modes. One universal binder models input shortening, return covariance, ref/out invariance and independent captured receivers. `[Outlives]` now accepts delegate declarations.

### Performance

- Interned place identities, compiled block GEN/KILL effects and replaced string hashing in liveness with dense mutable bitsets plus an occupied-word index and sparse/dense immutable boundaries. Identical instruction boundaries share snapshots; independent randomized oracles preserve convergence and budget behavior.
- Replaced eager all-path region proof storage with compact integer predecessor forests. Reachability queries allocate no proof, while deterministic shortest paths and the complete implication view materialize lazily and support concurrent readers.
- Added a reproducible benchmark harness, frozen-baseline comparisons and raw measured results. Recorded synthetic liveness cases improve by 1.16–129.80×. A 256-region chain with selected queries improves by 307.50×; a 128-region chain materializing every proof improves by 2.04×. These measure engine workloads, not whole-build speedups.

### Reports and integration

- Exposed async result state and cached resource identities in snapshots and JSON/HTML reports. Region reports distinguish exact assumed and required relationships without inventing edges from proof text.
- Expanded delegate, reference-slot, asynchronous protocol, provenance, reporting and package-consumer regressions. Clean consumers exercise the new rule and result protocol, and the minimum Roslyn 4.14 host executes the universal Core API and packaged delegate analyzer.
- Validated 1,702 unit/analyzer/report tests, the complete CLI integration suite including 61 budget-policy cases, five independent liveness benchmark equivalence scenarios, and fresh installed-package consumers. The package host also applies the packaged code fix to a live Roslyn 4.14 compilation.
- Added a computed async/delegate HTML example and refreshed the ownership example. State-table headers and lifecycle values remain readable at desktop and narrow widths; region graphs distinguish recorded requirements from assumptions.
- Prepared all six NuGet packages and symbols as `0.3.0-preview.1`; updated contracts, installation, diagnostics, architecture, Rust references and the implementation matrix. Packages remain unpublished until the release workflow runs.

## 0.2.0-preview.1

### Analysis

- Added first-order named lifetime contracts, outlives solving, strongly connected region equivalence, deterministic proof witnesses, and body validation of borrowed returns. Overrides, interfaces, getters and indexers preserve effective contracts, including parameter renaming.
- Connected implicit mutable receiver and argument borrows to reservation and activation phases. Explicit mutable borrows remain active when evaluated.
- Preserved the resource identity of evaluated by-value arguments and receivers across later argument assignments. Attribute-driven ownership transfers occur only after all operands have evaluated successfully; explicit `Ownership.Move` remains an expression-time effect.
- Replaced direct-store borrowed checks with bounded, converged CFG may-origin analysis. Aliases, ref/out slots, allocation containers and captured storage participate in SS0027; `DoesNotEscape` additionally rejects returning or explicitly consuming the argument. Conservative exception/capture envelopes and unknown-call boundaries are documented.
- Separated asynchronous disposal invocation from completion observation. Pending completions retain their identity through supported aliases, wrappers, transfers, joins and overwrites.
- Added bounded source summaries for fresh ownership, input/receiver aliases, alternative borrowed origins and owned output parameters. Executable conversions use their own contracts, and inferred disposal effects retain source order and exception boundaries.
- Distinguished an awaitable object from its result payload in lifetime proofs. Modeled framework producers and explicit contracts carry payload provenance through stored and configured awaitables; opaque payloads remain unknown.
- Added all-normal-exit aggregate cleanup analysis, independent synchronous/asynchronous entrypoints, helper summaries, inherited owned members, replacement obligations and cleanup-completion checks.
- Added explicit Send/Sync composition and generic requirements, sharing and transfer callback boundaries, transferred callback ownership inputs and capture restrictions.
- Corrected nested-function exception traversal so enclosing control-flow regions from another function cannot satisfy local cleanup obligations.

### Reports and integration

- Added loan phase, activation, completion and declared-region evidence to the report formats and interactive HTML inspector.
- Kept incomplete-analysis status independent of diagnostic suppression, configured severity and the CLI finding threshold. Ownership, service, lifetime and structural contract budget failures remain observable.
- Prepared all six packages and their portable symbols for the 0.2 preview. Strengthened package checks for version consistency, dependency isolation, matching bundled assemblies and installed-tool build-host assets.
- Added a minimum-version Roslyn 4.14 package host that discovers the packaged analyzers, composes the actual code-fix export, applies it to a live compilation, and executes the reusable Core/Reporting packages.
- Expanded clean-consumer validation to installed source/project tool modes, compiler-visible MSBuild budgets, EditorConfig precedence and configured diagnostic completeness.

The [implementation status](docs/implementation-status.md) and [Rust comparison](docs/rust-design.md) define the supported C# model and its remaining boundaries. Package preparation and CI artifacts do not imply publication to nuget.org; the [integration guide](docs/integration.md) describes the trusted-publishing setup.

## 0.1.0-preview.1

- Established compiler-independent ownership and loan dataflow, Roslyn control-flow lowering, contract and service analysis, diagnostics and five report formats.
- Added Contracts, Core, Analyzers, Reporting, Tool and CodeFixes package boundaries, compiler/IDE integration, source/project/solution analysis, regression suites and CI/release workflows.
