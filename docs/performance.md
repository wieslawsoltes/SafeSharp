# Analysis engine performance

SafeSharp uses interned identities and word-parallel sets for backward place liveness, and compact predecessor forests for named-region relations. Both optimizations preserve the existing analysis meaning and diagnostic evidence. The measured gains below apply to these engine workloads; the benchmark does not measure a complete solution build or Roslyn's frontend work.

The [0.4 measurement record](performance-0.4.md) covers all 19 current scenarios, including reference storage, ValueTask composition, and higher-ranked universes. It reports the observed sparse-temporary slowdown and allocation reduction explicitly. The measurements below remain the historical 0.3 run and should not be compared across runs as an upgrade speedup.

## Historical 0.3 measurements (2026-10-19)

The dependency-free console harness is [`benchmarks/SafeSharp.Benchmarks`](../benchmarks/SafeSharp.Benchmarks). It uses the repository's existing Core dependency and the .NET runtime, without introducing a benchmark package into the analyzer distribution. It runs correctness comparisons before timing, warms both implementations, alternates baseline and optimized samples, and reports median wall-clock duration and bytes allocated on the current thread.

The recorded run used .NET 10.0.13, Ubuntu 24.04.3 LTS, x64, an AMD EPYC 9V74 host exposing eight logical processors, five warmups, and fifteen measured samples. `DOTNET_TieredCompilation=0` made the JIT policy consistent across short and long scenarios. This setting is a benchmark configuration, not an analyzer installation requirement. Machine contention, runtime versions, GC timing, and JIT settings affect elapsed time. The harness records runtime metadata and Core assembly hashes in its JSON output.

The liveness baseline is the frozen `HashSet<string>` solver from commit [`fd66dc0`](https://github.com/wieslawsoltes/SafeSharp/commit/fd66dc01a1687e42e7bddc6c1b33ec822b38dd8e), retained as an independent [test and benchmark oracle](../tests/SafeSharp.Tests/Core/HashSetLivenessOracle.cs). The region baseline is that commit's compiled Core assembly loaded in a separate assembly context. Both region implementations use the same precompiled delegate adapters, so the timed operations do not include reflection or graph construction.

### Backward liveness

Each timing includes domain discovery, structural effect compilation, fixed-point solving, and materialization of every instruction's before/after liveness. The synthetic input graph itself is built outside the measurement.

| Workload | Baseline ms | Indexed ms | Baseline / indexed | Baseline allocated bytes | Indexed allocated bytes |
| --- | ---: | ---: | ---: | ---: | ---: |
| Tiny method: 4 blocks, 32 instructions | 0.0362 | 0.0157 | 2.31× | 37,408 | 12,640 |
| Dense joins: 64 blocks, 4,096 instructions, 127 places | 17.5217 | 0.4373 | 40.07× | 26,743,480 | 333,536 |
| Projection loop: 96 blocks, 3,072 instructions | 121.8323 | 0.9386 | 129.80× | 106,276,376 | 596,256 |
| Sparse temporaries: 4,096 places, 8,192 instructions | 2.9401 | 2.5369 | 1.16× | 3,016,632 | 1,626,368 |
| Unchanged boundaries: 4,096 no-ops and one input read | 1.9147 | 0.1848 | 10.36× | 1,738,296 | 254,840 |

The sparse-temporary scenario is deliberate. A dense bitmap alone can slow a method that introduces many distinct places but keeps only one live at a time. SafeSharp tracks nonzero words with a second-level bitmap and stores sparse frozen sets compactly; this avoids scanning the complete place domain when freezing each temporary's boundary. Small methods include the interning and compilation overhead, so the harness also measures that setup cost.

The complete measurement record is [2026-10-19-liveness.json](benchmarks/2026-10-19-liveness.json).

### Named regions

The query workload solves the graph, asks two relation questions, and requests one shortest proof. The full-proof workload additionally materializes every implied relation's complete shortest path. Both implementations receive a common 100-million-step comparison budget so that the old eager implementation completes; timings do not compare an incomplete old result with a complete new one.

| Region workload | Baseline ms | Indexed ms | Baseline / indexed | Baseline allocated bytes | Indexed allocated bytes |
| --- | ---: | ---: | ---: | ---: | ---: |
| Chain, 64 regions, selected queries | 2.8492 | 0.0593 | 48.05× | 2,103,072 | 66,288 |
| Cycle, 64 regions, selected queries | 7.8610 | 0.0914 | 86.01× | 5,164,496 | 47,760 |
| Disconnected chains, 64 regions, selected queries | 0.2563 | 0.0372 | 6.89× | 196,272 | 64,616 |
| Chain, 128 regions, selected queries | 26.9268 | 0.2117 | 127.19× | 13,837,848 | 163,528 |
| Cycle, 128 regions, selected queries | 104.4457 | 0.2991 | 349.20× | 36,341,576 | 126,056 |
| Disconnected chains, 128 regions, selected queries | 0.8195 | 0.0905 | 9.06× | 456,568 | 160,488 |
| Chain, 256 regions, selected queries | 205.9004 | 0.6696 | 307.50× | 98,960,736 | 458,400 |
| Cycle, 256 regions, selected queries | 515.4901 | 0.8498 | 606.60× | 269,808,192 | 383,040 |
| Disconnected chains, 256 regions, selected queries | 1.2348 | 0.1823 | 6.77× | 1,174,680 | 452,648 |
| Chain, 128 regions, all proof paths | 29.0537 | 14.2297 | 2.04× | 13,837,872 | 9,883,104 |
| Cycle, 128 regions, all proof paths | 105.9264 | 48.8853 | 2.17× | 36,341,600 | 23,575,328 |

Deferring proof expansion produces the largest benefit when a compiler check needs a few witnesses. Asking for every complete path still requires output proportional to the sum of those path lengths. The full-proof cases expose that cost instead of treating lazy work as if it had disappeared.

The complete measurement record is [2026-10-19-regions.json](benchmarks/2026-10-19-regions.json). Relation and witness semantics are documented in [lifetime-regions.md](lifetime-regions.md).

## Liveness implementation

`PlaceUniverse` interns the method's source, target, and owner places into ordinal integer identities. Ordinal sorting permits field and element projections to be represented by two contiguous ranges. Defining `p` removes `p`, `p.field`, and `p[index]`; unrelated identifiers such as `pOther`, `p!`, or `p/field` remain untouched. An empty root preserves the existing IR rule that every place is its descendant.

`PlaceSetBuilder` is mutable scratch storage shared across successive solver operations. Its primary bitmap uses 64-bit words. A second-level bitmap identifies which primary words are nonzero. Counts are maintained when a word changes, so sparse equality can compare occupied words and cardinality rather than scan all possible places. Single-bit insertions and removals update cardinality directly; bulk word operations retain portable population counts. An XOR of occupied word indices identifies the sole occupied word in constant time whenever only one remains. Portable bit arithmetic preserves Core's `netstandard2.0` compatibility; the compiler engine does not acquire a .NET 10 runtime dependency.

`PlaceSet` is the immutable boundary representation. A set occupying one word embeds its index and bits directly in the set object, avoiding word and sparse-index array allocations. Larger dense boundaries retain a contiguous word array; larger sparse boundaries retain nonzero words and their indices. Empty sets are shared per analysis. Adjacent instruction boundaries and no-op effects reuse the same immutable instance where their contents are unchanged. The existing `ISet<string>` consumer interface remains available for membership and enumeration, with mutation rejected. No pooled or reusable mutable buffer escapes into a stored result.

`PlaceLiveness` compiles each instruction's structural definition and uses once. A block's backward transfer is:

```text
live-in = generated ∪ (live-out − killed)
live-out = union of each successor's live-in
```

The worklist operates on dense block indices with an occupancy array. Only predecessors of changed blocks are requeued. The initial block order and the count of worklist removals are preserved, so the existing `MaxIterations` decision remains stable. Block transfer uses precomputed GEN/KILL sets, then instruction effects are replayed after convergence to retain exact before/after facts.

Instruction semantics are unchanged: targets kill their descendants, sources and owners generate uses, `MergeAlias` also uses its previous target, and `Nop` ignores source/target metadata while retaining an explicit owner use. New IR instructions that use the existing source/target conventions participate in the same transfer rules.

The public engine still reports ordinal-sorted place names and the same ownership snapshots. `SS0015` continues to mark a liveness budget failure. An incomplete solve does not expose partially reconstructed instruction facts for diagnostic replay. Cancellation is checked during domain discovery, effect compilation, word traversal, fixed-point iteration, and boundary replay.

## Region implementation

The named-region solver interns endpoints once, retains iterative strongly connected component discovery, and stores one predecessor edge per reachable source/target pair. Relation queries use indexed reachability and component membership. `GetProof` expands a deterministic shortest witness on demand; `Implications` and individual witness steps are cached for concurrent readers.

For a chain of `R` regions, eagerly storing every complete path requires a cubic number of edge references. The predecessor forest stores a quadratic number of parent entries. Expanding all complete witnesses can still produce cubic output, but ordinary analyzer queries no longer pay that cost upfront. The 256-region chain is covered by a test that completes under the normal 100,000-step solve budget. Incomplete region results retain their explicit incompleteness status and expose no partial proofs.

## Correctness gates

The optimized liveness representation has 44 focused test cases. The randomized comparison executes 216 deterministic graphs with loops, joins, disconnected blocks, noncontiguous block IDs, field/index projections, and all current instruction kinds. Another 84 graph/budget combinations compare the old and new worklist decisions. Every converged live-in, live-out, before, and after set must agree with the independent string-set oracle.

Additional checks cover 63/64/65-bit boundaries, the 4,095/4,096/4,097-bit second-level boundary, 8,193-bit ranges, sparse and dense enumeration, immutable boundary sharing, cancellation, empty roots, projection delimiter traps, and public ownership snapshots. Range operations are compared to a scalar boolean-array oracle. The normal ownership, analyzer, CLI, report, and package tests remain the integration gates; timing ratios are not flaky unit-test assertions.

Each benchmark scenario performs its own semantic check before measuring. Liveness compares every boundary; region cases compare all pairs' outlives/equivalence results and shortest-proof lengths. The region unit tests additionally compare exact deterministic proof edges against an independent breadth-first reference and exercise concurrent result access.

## Rust design references and adaptation

The reference is Rust 1.91.0, commit [`f8297e351a40c1439a467bbbb6879088047f50b3`](https://github.com/rust-lang/rust/tree/f8297e351a40c1439a467bbbb6879088047f50b3):

- [`rustc_index::bit_set`](https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_index/src/bit_set.rs) supplies the design precedent for finite indexed domains, word-parallel operations, and choosing dense or mixed set representations.
- [`rustc_data_structures::work_queue`](https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_data_structures/src/work_queue.rs) combines a queue with indexed occupancy to avoid duplicate pending work.
- [`rustc_mir_dataflow::framework`](https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_mir_dataflow/src/framework/mod.rs) separates effect application from result inspection and reuses mutable solver state. Its current generic analysis framework removed an earlier GEN/KILL specialization after it failed to improve Rust's performance. SafeSharp's precompiled GEN/KILL choice is specific to its simpler ownership IR and is measured here independently.
- [`rustc_borrowck::region_infer`](https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_borrowck/src/region_infer/mod.rs) informs indexed region relationships and separating solving from the evidence needed by diagnostics.

These are original C# implementations of the relevant algorithms and data-structure ideas. Rust source code was not copied. SafeSharp's second-level occupancy map and sparse boundary format are tailored to its reusable C# engine; its named-region solver is not Rust's complete program-point region inference. Broader feature limits remain in [implementation-status.md](implementation-status.md).

## Running the harness

From the repository root:

```bash
dotnet run --project benchmarks/SafeSharp.Benchmarks -c Release -- liveness --iterations 15 --warmup 5 --json artifacts/benchmarks/liveness.json
```

To compare named regions against the recorded baseline, first build that commit in a detached worktree:

```bash
git worktree add --detach ../SafeSharp-baseline fd66dc01a1687e42e7bddc6c1b33ec822b38dd8e
dotnet build ../SafeSharp-baseline/src/SafeSharp.Core/SafeSharp.Core.csproj -c Release
DOTNET_TieredCompilation=0 dotnet run --project benchmarks/SafeSharp.Benchmarks -c Release -- all --baseline-core ../SafeSharp-baseline/src/SafeSharp.Core/bin/Release/netstandard2.0/SafeSharp.Core.dll --iterations 15 --warmup 5 --json artifacts/benchmarks/all.json
```

On PowerShell, set `$env:DOTNET_TieredCompilation = '0'` on its own line before running the equivalent `dotnet` command. Omitting `--baseline-core` runs region scenarios against the current engine alone. `--filter sparse`, `--filter chain-256`, or `--filter all-proofs` selects a smaller set of scenarios. `--iterations 1 --warmup 0` is useful for validating harness wiring, not for publishing timing claims.
