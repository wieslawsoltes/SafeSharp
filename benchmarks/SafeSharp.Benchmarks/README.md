# SafeSharp engine benchmarks

This console project measures the Core liveness, region, reference-storage, and async composition engines without adding third-party benchmark dependencies. It is not part of the published analyzer or tool packages.

```bash
dotnet run --project benchmarks/SafeSharp.Benchmarks -c Release -- liveness --iterations 15 --warmup 5 --json artifacts/benchmarks/liveness.json
```

The liveness comparison uses the frozen SafeSharp `fd66dc0` HashSet solver shared with the differential unit tests. Named-region comparisons accept an independently built baseline Core assembly:

```bash
DOTNET_TieredCompilation=0 dotnet run --project benchmarks/SafeSharp.Benchmarks -c Release -- regions --baseline-core /path/to/baseline/SafeSharp.Core.dll --iterations 15 --warmup 5 --json artifacts/benchmarks/regions.json
```

Use `all`, `liveness`, `regions`, or `boundaries` to choose a suite. `--filter` selects scenario names by case-insensitive substring. Omitting the baseline assembly runs the current region scenarios without an old/new ratio. Inputs and typed assembly adapters are created outside timing; solver setup, convergence, and requested result materialization are included. Every scenario verifies semantic results before sampling.

The `boundaries` suite measures 128 local reference-storage bindings and disposals, 128 single-consumption ValueTask identities carried through `WhenAll`, and 64 independent higher-ranked binders. Its ownership scenarios time Core analysis without snapshots; a separate untimed snapshot pass checks every disposal or consumed capability. The region scenario verifies every required equivalence. These new domains have no historical implementation in the frozen baseline, so their JSON results contain current timings and allocations with `null` baseline and speedup fields.

CI runs `all --iterations 1 --warmup 0` without an external baseline assembly to exercise every scenario and its semantic checks. Those single-sample timings are informational and never form a timing threshold.

When reusing validated solution outputs while committing between commands, build the independent benchmark with `-p:BuildProjectReferences=false`, then execute its DLL. An independent Core rebuild after a commit can change Source Link/revision stamps while analyzer and tool output directories retain older Core copies. Alternatively, rebuild the complete solution coherently before packing; the package verifier checks exact dependency bytes. Normal CI keeps one checkout revision throughout, so its existing `all` command does not need this adjustment.

```bash
dotnet build benchmarks/SafeSharp.Benchmarks/SafeSharp.Benchmarks.csproj -c Release --no-restore -p:BuildProjectReferences=false
DOTNET_TieredCompilation=0 dotnet benchmarks/SafeSharp.Benchmarks/bin/Release/net10.0/SafeSharp.Benchmarks.dll all --iterations 15 --warmup 5 --json artifacts/benchmarks/all.json
```

```bash
DOTNET_TieredCompilation=0 dotnet run --project benchmarks/SafeSharp.Benchmarks -c Release -- boundaries --iterations 15 --warmup 5 --json artifacts/benchmarks/boundaries.json
```

Read [docs/performance.md](../../docs/performance.md) for the algorithm design, exact baseline, recorded measurements, JIT settings, interpretation limits, and instructions for building a baseline worktree.

The [0.4 measurement record](../../docs/performance-0.4.md) includes all 19 scenarios, their time and allocation costs, and the raw JSON. Historical 0.3 results remain separately labeled in the main performance guide.
