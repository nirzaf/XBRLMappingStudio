# Synthetic taxonomy index benchmark

This runner measures the current `TaxonomyIndex` against a deterministic,
digest-bound synthetic profile. The default profile has 20,000 concepts and a
role tree with bounded child pages. It records fixture generation, initial
indexing, role-network setup, index recreation, 100 warm searches, and 100 lazy
role-page requests, including every raw duration and the p50/p95/max summaries.
The p95 uses the nearest-rank definition. The report records OS, runtime,
processor description/count, architecture and stopwatch resolution metadata.

Run in Release and write a new record (existing output files are refused):

```powershell
dotnet run --project tools/performance/taxonomy-benchmark/XBRLMappingStudio.TaxonomyBenchmark.csproj -c Release -- --output artifacts/performance/taxonomy-search-20k.json
```

Capture the same run's process-tree resources and hash the compiled runner:

```powershell
python tools/performance/benchmark_harness.py run --scenario taxonomy-search-20k --command-id taxonomy-index-release-v1 --repo-root . --build-configuration Release --artifact tools/performance/taxonomy-benchmark/bin/Release/net10.0/XBRLMappingStudio.TaxonomyBenchmark.dll --output artifacts/performance/taxonomy-search-20k-process.json -- dotnet tools/performance/taxonomy-benchmark/bin/Release/net10.0/XBRLMappingStudio.TaxonomyBenchmark.dll --output artifacts/performance/taxonomy-search-20k.json
```

Both output paths must be new files. The index record contains the generated
profile digest; the process-tree record includes the machine description,
compiled artifact digest, and resource samples.

## Captured synthetic sample

One local Windows Release run was captured on 2026-11-07 at source revision
3a42b0bc1bc163169109ce1212495cb8bf3fbc5c. The synthetic profile contained
20,000 concepts and its digest was
sha256:122c292da2e92edb2917fbe79d220c11dbbe7d397ae668608f8134dd4914c14c.
Across 100 warm searches, p50 was 3.1895 ms, p95 was 6.3932 ms, and maximum
was 27.5184 ms. Across 100 bounded role-page expansions, p50 was 0.0016 ms,
p95 was 0.0073 ms, and maximum was 1.3213 ms. The process-tree harness ran for
1.59 seconds and captured four samples, three complete; its observed peaks
were 75,337,728 working-set bytes and 55,595,008 private bytes.

The raw index and process-tree records are
[taxonomy-search-20k-windows-release-run-01.json](../results/taxonomy-search-20k-windows-release-run-01.json)
and
[taxonomy-search-20k-process-windows-release-run-01.json](../results/taxonomy-search-20k-process-windows-release-run-01.json).
They record the local machine, runtime, artifact/profile digests and all query
samples. The process sample is short and coarse; these observations are
synthetic component measurements only. Both records explicitly claim no
threshold pass and do not qualify a real taxonomy, native UI interaction,
customer hardware or release performance.

The output schema is `taxonomy_benchmark.schema.json`. Measurements are labeled
`measured_synthetic_only` and always set `thresholdPassClaimed` to `false`.
They describe the .NET taxonomy index on the recorded machine; they do not
qualify a real taxonomy, desktop rendering, customer hardware, or a release
threshold. Use `tools/performance/benchmark_harness.py run` around the command
when process-tree working set, private memory, and CPU samples are also needed.

Check deterministic fixture identity, result counts, paging, cancellation, and
index recreation without writing a benchmark record:

```powershell
dotnet run --project tools/performance/taxonomy-benchmark/XBRLMappingStudio.TaxonomyBenchmark.csproj -c Release -- --self-test
```

The desktop's rapid superseding-query behavior is exercised separately by
`TaxonomyExplorerTests.RapidSearchCriteriaChanges_LeaveOnlyTheNewestTwentyThousandConceptQuery`.

## Graph canvas scaling fixtures

Generate a fresh set of four portable `.xmap` projects for native graph-canvas
measurement:

```powershell
dotnet run --project tools/performance/taxonomy-benchmark/XBRLMappingStudio.TaxonomyBenchmark.csproj -c Release -- --generate-graph-fixtures artifacts/performance/graph-canvas
```

The command creates 100, 500, 1,000 and 2,000 node projects in the new output
directory, plus `graph-canvas-fixtures.manifest.json`. Each graph is a typed
Constant-to-Scale chain using the built-in synthetic profile, stable IDs and a
serpentine grid layout. The manifest records node/edge counts, layout grid,
semantic/profile identity and the exact `.xmap` SHA-256. Existing output
directories are refused so fixture evidence is not silently replaced.

The runner self-test regenerates every size, checks byte stability, validates
the typed graph with the default catalog, round-trips each `.xmap`, and verifies
the manifest digests. These fixtures make native replay inputs available; they
do not measure pan/zoom frame intervals, allocations, rendering behavior or
target-hardware performance. Record those separately with the Windows native
gesture and rendering trace required by BEN-04/BEN-06.
