# 0.4 engine measurements — 2026-10-20

This run measured the local `0.4.0-preview.1` candidate after the async, reference-storage, higher-ranked lifetime, synchronization, and conservative array-boundary changes. All 19 harness scenarios completed their verification and measurement steps. Five liveness scenarios compared the current implementation with the frozen independent solver; eleven named-region scenarios and three new boundary scenarios measured the current implementation alone.

The complete, unmodified record is [2026-10-20-boundaries.json](benchmarks/2026-10-20-boundaries.json). The earlier [0.3 measurements and algorithm discussion](performance.md) remain historical results from a separate run. Cross-run differences are not a measurement of a 0.3-to-0.4 regression or speedup.

## Environment and measurement boundaries

| Setting | Recorded value |
| --- | --- |
| Runtime | .NET 10.0.13 |
| Operating system | Ubuntu 24.04.3 LTS |
| Architecture and available processors | x64, 8 logical processors |
| Warmup and sample count | 5 warmups, 15 measured samples per implementation and scenario |
| JIT setting | `DOTNET_TieredCompilation=0` |
| Report time | 2026-10-20 10:02:24 UTC |
| External baseline assembly | None; the liveness oracle is compiled into the harness |

The measured Core assembly SHA-256 is `549b1b101f9de73fde9895c8e55a093986c607dc3be93baa3076b103fa72d2b9`. This identifies the binary used for the run, including its build provenance metadata; a later coherent package build can have a different hash after a repository revision changes.

The [harness](../benchmarks/SafeSharp.Benchmarks/Program.cs) reports median elapsed milliseconds and median bytes allocated on the executing thread. Baseline and current liveness samples alternate. Graph construction, binder construction, and typed reflection adapters are prepared outside the timed operation. Solver setup, convergence, diagnostics or requested proof output are included. Allocated bytes are total managed allocations during an operation, not retained heap size, peak memory, or whole-process memory.

The reference-storage and async scenarios time the complete Core `Analyze` call without snapshots, including liveness, ownership convergence, diagnostic replay, and exit-obligation checks. Their separate untimed snapshot runs verify the resulting resource or capability states. These timings exclude Roslyn parsing/lowering and report serialization. The higher-ranked scenario measures solving a preconstructed problem. The package and CLI verification processes waited until timing finished, but runtime scheduling and garbage collection still affect elapsed time. This record contains medians rather than a distribution or confidence interval.

## Backward liveness: five comparisons

The baseline is the frozen `HashSet<string>` implementation from [`fd66dc0`](https://github.com/wieslawsoltes/SafeSharp/commit/fd66dc01a1687e42e7bddc6c1b33ec822b38dd8e), retained in the [independent oracle](../tests/SafeSharp.Tests/Core/HashSetLivenessOracle.cs). Each verification compares every live-in, live-out, before, and after set. Each timed operation includes discovery of places, compilation of structural effects, convergence, and all instruction-boundary liveness outputs.

| Scenario | Baseline ms | Current ms | Baseline / current | Baseline allocated bytes | Current allocated bytes |
| --- | ---: | ---: | ---: | ---: | ---: |
| `liveness/tiny` | 0.2516 | 0.1138 | 2.21× | 37,408 | 12,640 |
| `liveness/dense-joins` | 13.4351 | 0.7943 | 16.91× | 26,743,480 | 333,536 |
| `liveness/projection-loop` | 183.4701 | 2.6398 | 69.50× | 106,275,312 | 596,256 |
| `liveness/sparse-temporaries` | 7.5847 | 9.0612 | 0.84× | 3,016,632 | 1,626,368 |
| `liveness/unchanged-boundaries` | 8.6922 | 1.1112 | 7.82× | 1,738,296 | 254,840 |

The tiny graph has 4 blocks, 32 instructions, and 16 places. Dense joins use 64 blocks, 4,096 instructions, and 127 places. The projection loop uses 96 blocks and 3,072 instructions with projected places and backward edges. Sparse temporaries use one block with 8,192 instructions and 4,096 short-lived places. Unchanged boundaries use 32 blocks, 4,096 no-op boundaries, and one live input. These shapes deliberately expose setup costs, dense fixed points, sparse domains, and opportunities for immutable boundary sharing.

**The sparse-temporary case was slower in this run:** 9.0612 ms compared with 7.5847 ms, a 19.5% elapsed-time increase. It allocated 1,626,368 bytes compared with 3,016,632 bytes, a 46.1% reduction. That allocation improvement does not erase the time regression. The remaining four shapes improved elapsed time and allocations relative to this frozen baseline. The result does not establish a universal liveness speedup, and elapsed medians alone do not identify the cause of the sparse-case overhead. Domain discovery, effect compilation, and boundary materialization are included in both measurements.

## Inline-word follow-up

After the preceding run, [commit `162038d`](https://github.com/wieslawsoltes/SafeSharp/commit/162038deceb3abb489d0aba3c1eb7a2ba777c22b) changed place discovery to ordinal array sorting, maintained single-bit counts directly, and embedded a single occupied word/index inside an immutable `PlaceSet`. Larger dense and sparse representations remain available. Portable arithmetic preserves the `netstandard2.0` target.

The complete follow-up record is [2026-10-20-inline-liveness.json](benchmarks/2026-10-20-inline-liveness.json). It used five warmups and fifteen alternating measured samples, .NET 10.0.13 on Ubuntu 24.04.3 LTS/x64, `DOTNET_TieredCompilation=0`, and a two-logical-processor runtime limit. All five scenarios passed their independent liveness comparisons. The measured Core SHA-256 is `211ce92ac515727c394d710aba32565a79a4846073a9b1e91b569e6878002a85`.

| Scenario | Earlier indexed allocated bytes | Inline-word allocated bytes | Follow-up baseline ms | Follow-up indexed ms |
| --- | ---: | ---: | ---: | ---: |
| Tiny | 12,640 | 11,416 | 0.0585 | 0.0315 |
| Dense joins | 333,536 | 333,280 | 1,080.5253 | 5.9227 |
| Projection loop | 596,256 | 599,944 | 6,043.0251 | 18.3624 |
| Sparse temporaries | 1,626,368 | 1,314,896 | 89.4838 | 32.4193 |
| Unchanged boundaries | 254,840 | 253,720 | 42.0092 | 1.9667 |

Sparse-temporary allocation fell by 311,472 bytes, or 19.2%, relative to the preceding optimized record. Projection-loop allocation increased by 3,688 bytes; the change is not an allocation improvement for every shape.

The follow-up ran under substantial shared-host contention. Its elapsed medians and large within-run ratios are preserved as observations, but do not establish a performance improvement over the earlier candidate or a whole-build speedup. The allocation comparison is the useful narrow result here. These measurements also precede the later canonical-reference extension that keeps captured addresses live across value writes; they are a record of the identified checkpoint, not timings of every subsequent feature. The current independent HashSet oracle preserves its original fixed-point algorithm while extending transfer rules separately for those newer instructions.

## Named regions: eleven current-only measurements

No `--baseline-core` assembly was supplied, so every baseline and speedup field for these scenarios is `null`. The nine query cases solve the complete relation graph, perform two relation queries, and request one shortest proof. The two all-proofs cases solve the graph and materialize every implied relation's complete shortest path. Chains have `N - 1` edges; cycles have `N` edges; disconnected cases split the chain into groups of eight regions.

| Scenario | Current ms | Current allocated bytes |
| --- | ---: | ---: |
| `regions/chain-64/queries` | 0.6046 | 66,288 |
| `regions/cycle-64/queries` | 0.0664 | 47,760 |
| `regions/disconnected-64/queries` | 0.0532 | 64,616 |
| `regions/chain-128/queries` | 0.5483 | 163,528 |
| `regions/cycle-128/queries` | 0.8065 | 126,056 |
| `regions/disconnected-128/queries` | 0.2312 | 160,488 |
| `regions/chain-256/queries` | 2.2103 | 458,400 |
| `regions/cycle-256/queries` | 1.7153 | 383,040 |
| `regions/disconnected-256/queries` | 0.6170 | 452,648 |
| `regions/chain-128/all-proofs` | 25.0444 | 9,883,104 |
| `regions/cycle-128/all-proofs` | 123.3986 | 23,575,328 |

The current-only verification requires completion within the configured budget and exercises each requested result. Without an external baseline it does not perform the old/new all-pairs comparison available in the [region harness](../benchmarks/SafeSharp.Benchmarks/RegionBenchmarks.cs). Independent region relation and witness tests remain part of the full test suite.

The all-proofs rows expose materialization cost: the 128-region cycle took 123.3986 ms and allocated 23,575,328 bytes when every complete path was requested. The corresponding query row took 0.8065 ms and allocated 126,056 bytes. These are different requested outputs; the cheaper query result does not make expansion of all paths free. Proof output remains proportional to the paths actually requested. No named-region speedup is inferred from this current-only run.

## New boundary domains: three current-only measurements

The [boundary harness](../benchmarks/SafeSharp.Benchmarks/BoundaryBenchmarks.cs) supplies no historical implementation for these new domains. Its baseline time, baseline allocation, and speedup fields are `null` by design.

| Scenario | Current ms | Current allocated bytes | Verified outcome |
| --- | ---: | ---: | --- |
| `boundaries/reference-storage/128` | 8.2245 | 1,416,480 | Every one of 128 original owners is disposed through its storage reference; no issues or budget failure. |
| `boundaries/valuetask-whenall/128` | 78.8557 | 9,734,408 | All 128 original ValueTask capabilities reach the consumed state after passing through WhenAll result elements; no duplicate identity, issue, or budget failure. |
| `boundaries/higher-ranked/64` | 1.9699 | 324,672 | All 64 placeholder/inference pairs are equivalent in their own universe; the solve is complete and compatible, with no promotions. |

The reference graph contains 384 instructions: 128 owner allocations, address bindings, and disposals through references. The async graph contains 643 instructions: an input aggregate, 128 tracked ValueTasks wrapped in task carriers and stored as elements, one `WhenAll` proxy, successful result reception, and 128 element reads and consumes. The higher-ranked problem has 64 independent nested binders under one root, 128 explicitly declared variables, and 128 equivalence requirements; the solver also supplies its static upper-bound region.

The async case is the largest of these three ownership workloads: 78.8557 ms and 9,734,408 allocated bytes for the whole 128-capability graph. Those costs are reported directly. They are not per-operation latency, a Roslyn solution-build measurement, a comparison with Rust, or evidence that snapshots and graphical reports have the same cost. The current-only values provide a reproducible starting point for later profiling and regression comparisons.

## Reproduce and verify

The measured command ran the compiled harness directly after a Release build:

```bash
dotnet restore benchmarks/SafeSharp.Benchmarks/SafeSharp.Benchmarks.csproj
dotnet build benchmarks/SafeSharp.Benchmarks/SafeSharp.Benchmarks.csproj -c Release --no-restore -p:UseSharedCompilation=false -m:1
DOTNET_TieredCompilation=0 dotnet benchmarks/SafeSharp.Benchmarks/bin/Release/net10.0/SafeSharp.Benchmarks.dll all --iterations 15 --warmup 5 --json artifacts/benchmarks/final-boundaries-0.4.json
```

When reusing an already validated solution build across intervening commits, use `-p:BuildProjectReferences=false` for the independent benchmark build, or rebuild the full solution coherently before packing. Repository revision metadata can otherwise change only the independently rebuilt Core binary while analyzer and tool output directories still contain older copies. [Package verification](../eng/verify-packages.py) checks exact dependency bytes. The [benchmark README](../benchmarks/SafeSharp.Benchmarks/README.md) documents this build coordination.

For a quick wiring check, CI runs `all --iterations 1 --warmup 0` without an external baseline. Its semantic checks must succeed; its single-sample timings are informational and have no threshold. To reproduce a historical named-region comparison, supply a separately built `--baseline-core` as described in [the historical performance guide](performance.md). The three new boundary cases remain current-only even when that optional region baseline is supplied.

The final Release validation preceding this measurement restored and built the entire solution with zero warnings and zero errors, then passed 2,268 tests with no failures or skips. That suite includes the liveness differential checks, async/reference/lifetime regressions, synchronization/report tests, and a legacy binary-consumer constructor compatibility test. Correctness gates and benchmark timings serve different purposes; these medians are not release timing promises.
