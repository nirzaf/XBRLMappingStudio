# Performance measurement harness

`benchmark_harness.py` records the launched process tree's duration, working set, private memory, and cumulative CPU seconds on Windows and Linux. It reads detailed memory counters only for the target tree; children observed before a parent exits remain tracked by process identity. Every raw JSON record includes the repository revision, declared build configuration, optional artifact/fixture digests, hardware description, measured clock resolution/calibration, sampling interval, and raw process-tree samples. The launched command and its arguments are not written to the record.

The harness labels a captured run `measured`; this means only that it collected samples. It always records `threshold_pass_claimed: false`. Summary peaks and p95 values use complete samples only; `complete_sample_count` lets readers detect gaps. Windows private memory is process private commit; Linux private memory is private resident pages. Working set may include shared pages, so these memory values must not be compared as equivalent metrics across operating systems. CPU values are cumulative CPU time summed across observed processes, not CPU utilization percentages. A timeout terminates the launched process group/tree and records the resulting exit status.

Create the fixed synthetic SQLite fixture without overwriting an existing file:

```powershell
python tools/performance/benchmark_harness.py create-sqlite-fixture --path artifacts/performance/financials-100k.db --rows 100000
```

Capture one Release command run; provide a non-secret scenario and command ID. Output must be a new file:

```powershell
python tools/performance/benchmark_harness.py run --scenario sqlite-source-100k --command-id integration-large-run-release --repo-root . --build-configuration Release --fixture artifacts/performance/financials-100k.db --output artifacts/performance/run-001.json -- dotnet test tests/XBRLMappingStudio.Integration.Tests/XBRLMappingStudio.Integration.Tests.csproj -c Release --no-build --filter FullyQualifiedName~HundredThousandRows_StreamsExactOutputAndKeepsBatchWorkingSetBounded
```

Run `python tools/performance/benchmark_harness.py self-test` for the monotonic-clock calibration, deterministic-fixture, process-sampling, and tracked-schema self-test. It records no product performance result. `scenarios.json` is a proposed scenario catalog; the native cold/warm startup, gesture/frame timing, Arelle foreground, and target-hardware runs remain unmeasured gates. The harness does not automate Avalonia gestures or infer UI readiness from process startup.
