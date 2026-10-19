# SafeSharp engine benchmarks

This console project measures the Core liveness and named-region engines without adding third-party benchmark dependencies. It is not part of the published analyzer or tool packages.

```bash
dotnet run --project benchmarks/SafeSharp.Benchmarks -c Release -- liveness --iterations 15 --warmup 5 --json artifacts/benchmarks/liveness.json
```

The liveness comparison uses the frozen SafeSharp `fd66dc0` HashSet solver shared with the differential unit tests. Named-region comparisons accept an independently built baseline Core assembly:

```bash
DOTNET_TieredCompilation=0 dotnet run --project benchmarks/SafeSharp.Benchmarks -c Release -- regions --baseline-core /path/to/baseline/SafeSharp.Core.dll --iterations 15 --warmup 5 --json artifacts/benchmarks/regions.json
```

Use `all`, `liveness`, or `regions` to choose a suite. `--filter` selects scenario names by case-insensitive substring. Omitting the baseline assembly runs the current region scenarios without an old/new ratio. Inputs and typed assembly adapters are created outside timing; solver setup, convergence, and requested result materialization are included. Every scenario verifies semantic results before sampling.

Read [docs/performance.md](../../docs/performance.md) for the algorithm design, exact baseline, recorded measurements, JIT settings, interpretation limits, and instructions for building a baseline worktree.
