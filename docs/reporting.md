# Reports, graph inspection, and the command-line tool

SafeSharp separates analysis from presentation. The Roslyn analyzers run inside the compiler and report diagnostics without writing files. `SafeSharp.Core` returns immutable ownership results. `SafeSharp.Reporting` turns those results into deterministic JSON, SARIF, Graphviz DOT, Mermaid, or a self-contained HTML inspector. `SafeSharp.Tool` loads real compilations, runs the analyzer catalog, and captures the corresponding engine states.

## Install and run

After a package version is published:

```sh
dotnet tool install --global SafeSharp.Tool --version 0.3.0-preview.1
safesharp --project src/App/App.csproj --format html --output artifacts/ownership.html
safesharp --solution App.sln --format sarif --output artifacts/ownership.sarif
```

For a package built from this checkout:

```sh
dotnet pack src/SafeSharp.Tool/SafeSharp.Tool.csproj -c Release -o artifacts/packages
dotnet tool install --tool-path artifacts/tools SafeSharp.Tool \
  --add-source artifacts/packages --version 0.3.0-preview.1
artifacts/tools/safesharp --project samples/SafeSharp.Sample/SafeSharp.Sample.csproj \
  --format html --output artifacts/sample-ownership.html
```

Run directly from source during development:

```sh
dotnet run --project src/SafeSharp.Tool -- \
  --source examples/Example.cs --format json --output artifacts/example.json
```

`--source` can be repeated. Add third-party assemblies with repeated `--reference` options and conditional compilation symbols with `--define`. SafeSharp contracts are available automatically in this mode.

```sh
safesharp --source Owner.cs --source Consumer.cs \
  --reference dependencies/ExternalLibrary.dll --define DEBUG \
  --format json --output ownership.json
```

### Loading project and solution inputs

`--project` loads the requested C# project, its references, compiler settings, and analyzer configuration through `MSBuildWorkspace`. `--solution` analyzes the C# projects in the loaded solution. Project references participate in compilation; selecting a single project does not separately analyze every dependency's implementation.

The tool requires an installed .NET SDK for project and solution inputs. MSBuild Locator selects SDK assemblies before workspace initialization. NuGet MSBuild assemblies are excluded from the tool's runtime payload, so a bundled copy cannot take precedence over the selected SDK.

A normal invocation performs `dotnet restore` first. `--no-restore` skips that step when the assets are already available. `--configuration Release` selects the configuration. For explicit target selection use `--framework net10.0`. For a multi-targeted project, run once per target framework when each target matters; conditions and compilation references can change the ownership behavior. A report is a snapshot of the compilations that MSBuild loads for that invocation.

Standalone source mode has no project system. It uses the tool's .NET runtime target, explicit files, supplied metadata references, and supplied preprocessor symbols. It does not recreate another target framework, package graph, source generator pipeline, or MSBuild condition set. Use project mode to evaluate those settings.

### Exit status and failure handling

| Exit code | Meaning |
| --- | --- |
| `0` | The configured analyses completed with no findings at the requested threshold. |
| `1` | Analysis completed, with findings at `--fail-on warning` or `--fail-on error`. |
| `2` | Invalid input, restore/load/compiler failure, analyzer exception, or analysis budget exhaustion. |
| `130` | Canceled, including Ctrl+C. |

The default threshold is `warning`; informational diagnostics remain in the report. `--fail-on none` permits diagnostics without failing a CI job, but does not convert an incomplete or failed analysis into success. SS0015, SS0029, SS0043 and SS0046 remain incompleteness markers even when suppressed in the compiler-host results. The CLI enables these markers for its single analyzer-catalog execution, records whether an analysis exhausted its budget, then reapplies the original diagnostic policy to the visible report. Per-rule, category and global analyzer severities, pragmas, `SuppressMessage`, `NoWarn`, warning levels and warning promotion retain their compiler semantics. This prevents a hidden budget diagnostic from turning an incomplete check into successful verification. Compilation errors prevent an ownership report from being generated for invalid source. Workspace failures and analyzer exceptions are surfaced explicitly.

Report output goes to stdout unless `--output` is supplied. Progress and errors go to stderr. A file output is written to a temporary sibling and replaced after successful rendering, so a canceled or failed rendering does not truncate an existing report. No generated timestamp is included in a report.

## HTML ownership inspector

The repository includes a [generated example ownership report](examples/ownership.html) from [`samples/UnsafeExamples.cs.txt`](../samples/UnsafeExamples.cs.txt). Download the HTML file and open it locally to inspect the actual diagnostics, CFGs, and captured states. It is a generated sample, and its source locations retain the absolute checkout path used during generation. Regenerate it in your own checkout with:

```sh
safesharp --source samples/UnsafeExamples.cs.txt --format html \
  --output docs/examples/ownership.html --fail-on none
```

The HTML report includes:

- Summary counts and an explicit incomplete-analysis status.
- Effective compiler diagnostic severity and suppression results, including semantic service-lifetime diagnostics.
- Source locations and related ownership evidence.
- A method selector and name filter.
- An SVG graph of the actual lowered basic blocks and successor edges. Select a block to jump to its state inspector; scroll to pan and use the zoom controls to adjust scale.
- Every captured instruction with the state before and after it, plus the engine's actual live-before/live-after place sets.
- Resource identities, ownership obligations, resource/completion kinds, possible lifecycle states, aliases, moved field projections, reserved and active loans, activation sites, reborrow parents, and suspension permissions.
- Nullability, potentially absent references and loans, resource coverage of projections, and explicit infeasible-state markers for pruned branches.
- Diagnostic metadata and diagrams of the recorded dependency service, implementation, and lifetime paths for captive-service diagnostics.
- Lifetime-region graphs with required-return and candidate-source roles, assumed constraints, distinct required edges, region bindings, equivalence components and recorded proof metadata. Isolated regions remain visible.

The report has no network dependency, external JavaScript library, or graph-rendering service. SVG diagrams are rendered by the browser. The method filter and zoom controls use a fixed inline script; source text is never interpolated into JavaScript. Source names, diagnostic text, graph labels, and metadata cross explicit HTML/XML escaping boundaries. With JavaScript disabled, all methods and the native `<details>` inspectors remain available.

A graph node shows a short preview for readability. Complete instructions remain in the tables and JSON. A block with no captured snapshot is explicitly marked: it can be unreachable, or the caller may have disabled snapshot capture. An empty captured state and an absent snapshot are different facts. When an assumption makes a captured state infeasible, the node is marked `PRUNED` and the state inspector identifies its retained facts as abstract debugging data.

## JSON interchange format

The JSON report has `schemaVersion: "1.0"`. Within this major version, consumers should tolerate additional properties. Coordinates are zero-based UTF-16 units, matching Roslyn `TextSpan` and `LinePosition`. File offsets and lengths are preserved independently of line coordinates.

```text
report
  schemaVersion, tool, toolVersion, title, sourceRoot
  columnKind, coordinateBase, analysisComplete
  diagnostics[]
    id, title, message, severity, certainty, method, resourceId, properties
    source { path, uri, start, length, line, column }
    relatedLocations[], evidence[]
  methods[]
    name, source, entryBlock, iterations, budgetExceeded
    issues[]                          raw engine findings
    blocks[]
      id, successors[], instructions[], hasSnapshots
      liveIn[], liveOut[]              present when a snapshot exists
      entryState, exitState
      steps[]
        index, instruction, liveBefore[], liveAfter[], before, after
```

Each state contains `isReachable`, `places`, `resources`, and `loans`. Places expose `mayBeNull`; references expose `mayBeAbsent`, `coversResource`, and field-move projections; loans expose `mayBeAbsent`, `phase`, activation evidence, kind and reborrow parent. Resources distinguish ordinary resources, asynchronous disposal completions and asynchronous result carriers. Completion facts retain disposed resource IDs; result carriers expose `resultState`, `producedResourceId` and `producedResourceIds` for the cached output alternatives. Those IDs refer to actual resource/loan identities in the computed state. The JSON includes all captured facts; the writer does not infer new aliases or invent histories. Flag values such as `Alive, Disposed` or `Reserved, Active` represent joined possibilities, rather than one concrete object's simultaneous runtime state.

`Disposed` records a recognized disposal invocation. The analyzer's release obligation is discharged before an exception from the disposer body propagates; it does not establish that partial or throwing cleanup released every underlying resource successfully.

`Pending` and `Completed` describe the separate completion-observation obligation. A resource may have a recognized disposal invocation while its returned completion remains pending. Awaiting that completion observes the obligation even if the await later propagates a fault. The report preserves this distinction; it does not certify successful physical cleanup or repeated-await validity for arbitrary `ValueTask` instances.

The top-level `diagnostics` list is the compiler-host result when the CLI is used: configured severity, suppressions, and the complete analyzer catalog are honored. The per-method `issues` list is the underlying engine result before compiler diagnostic suppression. An issue can therefore be visible in raw method facts while being absent from the top-level diagnostics. This distinction allows inspection of suppressed findings without changing a build policy.

Methods are sorted by source path, offset, and name; blocks and unordered state collections have stable ordering. Instruction and evidence sequence retain their analysis meaning. The same compilation inputs, options, and tool version produce the same report bytes; different absolute checkout paths, compiler-generated inputs, or target frameworks are different inputs.

## SARIF 2.1.0

SARIF output includes the analyzer identity, rule table, configured diagnostic level, precise source regions, related locations, diagnostic properties, and stable partial fingerprints. It uses `columnKind: "utf16CodeUnits"`. SARIF `startLine` and `startColumn` are one-based; `charOffset` and `charLength` preserve the original Roslyn offsets. End-line coordinates are not guessed for multiline spans.

Source paths are encoded as file URIs. Windows drive and UNC paths are recognized independently of the host operating system. Relative source paths can use the report's explicit `%SRCROOT%` base. An unavailable source location does not acquire a fabricated file or line number.

Ownership provenance is emitted through `codeFlows` and `relatedLocations`. These code flows are labeled **abstract provenance**. A fixed-point join may combine facts from multiple paths, and SafeSharp does not claim that the listed evidence is a concrete executable counterexample. `executionOrder` is not manufactured for such evidence. Service diagnostics retain their actual `SafeSharp.DependencyPath`, `SafeSharp.DependencyServices`, and `SafeSharp.DependencyLifetimes` properties. Region and concurrency diagnostic properties are retained without translating a failed contract into an invented runtime trace.

The report follows the [OASIS SARIF 2.1.0 specification](https://docs.oasis-open.org/sarif/sarif/v2.1.0/os/sarif-v2.1.0-os.html). SARIF carries diagnostic results; use JSON or HTML for the complete ownership state lattice and CFG.

## DOT and Mermaid

```sh
safesharp --project App.csproj --format dot --output ownership.dot
safesharp --project App.csproj --format mermaid --output ownership.mmd
```

Each method becomes a subgraph, and each successor relation becomes an edge. Captive-service diagnostics with a recorded `SafeSharp.DependencyPath` additionally produce a dependency-witness subgraph. These edges preserve the reported path; they do not reconstruct every service registration in a container. Service and lifetime labels appear when the corresponding metadata paths have matching lengths.

Lifetime diagnostics carrying `SafeSharp.RegionNames` and `SafeSharp.RegionConstraints` additionally produce a region subgraph. Its solid edges are the declared outlives assumptions. When `SafeSharp.RegionRequirements` is supplied, dashed edges show the exact callback/input/result requirements. `SafeSharp.RegionRequirementReasons` and `SafeSharp.RegionBindings` retain the source interpretation without changing topology. The renderer preserves isolated names, cycles and source/required roles; it does not add reflexive edges, transitive closure edges or nonexistent proof paths. `SafeSharp.RegionProof` and `SafeSharp.RegionComponents` remain recorded evidence beside the graph. Non-region origins use explicit tokens such as `<Unknown>`, `<Local>` and `<Null>`, so they cannot be confused with valid declared region names.

Region graph previews are bounded at 64 names, 256 combined distinct assumption/requirement edges and 65,536 characters per property. Malformed or oversized metadata produces an explicit unavailable-preview explanation while the raw properties remain available. No partial graph is presented as the complete constraint set. The JSON and SARIF diagnostic properties retain the original evidence independently of these visualization limits.

Node identifiers are generated independently from source names. DOT string quoting and Mermaid entity escaping prevent names from becoming graph directives. The raw Mermaid output is intended for a Mermaid-capable renderer; it contains no Markdown fences. For a large solution, use the HTML method selector or consume the JSON to focus on selected methods.

## Reuse the reporting library

`SafeSharp.Reporting` targets `netstandard2.0` and depends on `SafeSharp.Core`. It has no Roslyn or MSBuild dependency. Its streaming writers implement `IAnalysisReportWriter`; callers own the output stream and cancellation policy.

```csharp
using SafeSharp.Analyzers;
using SafeSharp.Core;
using SafeSharp.Reporting;

// compilation is the real Roslyn Compilation supplied by your host.
// Check compilation diagnostics before invoking this API.
var results = OwnershipCompilationAnalysis.Analyze(
    compilation,
    new AnalysisOptions(captureSnapshots: true),
    cancellationToken,
    configOptions: project.AnalyzerOptions.AnalyzerConfigOptionsProvider).ToArray();

var report = new AnalysisReport(results);
using var writer = File.CreateText("ownership.html");
new HtmlReportWriter().Write(report, writer, cancellationToken);

// Or produce an in-memory string for a custom integration.
string json = AnalysisReportRenderer.Render(report, ReportFormat.Json, cancellationToken);
```

With the constructor above, `AnalysisReport` creates presentation diagnostics from engine issues. A compiler host can instead provide an authoritative `IEnumerable<ReportDiagnostic>` populated from the entire analyzer catalog, as the CLI does. This preserves host severities, suppressions, properties, and semantic-only findings. Custom frontends can supply `AnalysisResult` objects directly without loading Roslyn.

Snapshot capture deliberately remains opt-in in the core engine. The compiler analyzer's fast path needs diagnostics, not a retained state copy for every instruction. The CLI captures snapshots for JSON and HTML explainability and therefore uses more memory in those formats. SARIF, DOT, and Mermaid generation omit those unneeded state copies; diagnostic evidence and the actual CFG remain available.

## Delegate and async example

[Async and delegate report](examples/async-delegates.html) is generated from [AsyncDelegateExamples.cs.txt](../samples/AsyncDelegateExamples.cs.txt). It includes valid and invalid delegate conversions, explicit outlives assumptions, a possible abandoned factory result, and repeated receipt of a disposed payload.

```sh
dotnet run --project src/SafeSharp.Tool -c Release -- \
  --source samples/AsyncDelegateExamples.cs.txt --format html \
  --output docs/examples/async-delegates.html --fail-on none
```

The report uses computed diagnostics, graph edges and snapshots. Opening the file does not execute its illustrative unsafe C# input.

## Async result evidence

An `AsyncFactoryCarrier` represents the obligation to receive or transfer a possible owned factory result, or a known binding to an existing payload. `Deferred` result state means successful production has not yet materialized its result in the analysis; `Materialized` records the cached value. Flag combinations preserve joined alternatives. A pending owned factory lost at an exit has Possible certainty because execution may fault before producing the result.

After a successful receive, the cached output ID is stable across aliases and repeated receives. The output retains its own disposal and transfer state. `Task.FromResult`, `ValueTask.FromResult` and `ValueTask<T>(T)` bind the already evaluated value and preserve its loans; they do not create a second owned resource. Disposing the task itself does not dispose the contained resource. The HTML state inspector and JSON snapshots expose these facts. See [async result ownership](async-result-ownership.md) for the instruction protocol and exact modeled framework operations.

## Verification

The reporting test suite runs real core analysis before checking serialized states, loan phases, activation evidence and completion obligations. It also covers asynchronous result identity, exact assumed/required region graphs, equivalence cycles, disconnected names, malformed/oversized metadata, all-format determinism under a different process culture, exact URI and UTF-16 coordinate handling, missing snapshots, budget failure, authoritative host severity/suppression, arbitrary diagnostic metadata, Unicode/control-character round trips, cancellation, and HTML/DOT/Mermaid injection boundaries. The CLI is additionally exercised against standalone files, MSBuild projects, and the installed local tool package.

For the semantic guarantees and their scope, see [the Rust design comparison](rust-design.md), [contracts and semantic analysis](contracts-and-semantics.md), and [the diagnostic reference](diagnostics.md).
