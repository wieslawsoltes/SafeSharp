# Changelog

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
