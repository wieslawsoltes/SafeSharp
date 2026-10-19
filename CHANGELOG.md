# Changelog

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
