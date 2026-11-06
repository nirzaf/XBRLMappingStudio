# Verification baseline — 2026-11-06

This records local verification evidence for the ENG-01 implementation in the
shared `main` worktree. Release and deployment verification are outside this
baseline.

## ENG-01 qualified row-scoped execution

The implementation preserves the existing in-memory path for runs that stay within
the injected row and byte probe limits. Large execution currently qualifies only a
single streamed SQLite binding, a compiled plan with no set-phase steps, no
`MappingProject.Destination` plan, and xBRL-JSON output. CLI/Desktop explicit file
publication uses the same staged artifact boundary. Other large inputs/plans fail closed after the
bounded probe. The private fact/source spool has row, fact, spool, report, namespace,
SQLite cache, probe-byte, and batch-byte limits. Large reports are streamed to a
temporary artifact, validated by a path-plus-digest worker request, and copied through
the same staged publication checks as small reports. Cancellation and failed worker
adjudication remove the temporary artifact.

The 100,000-row integration scenario passed exact byte comparison between the large
artifact and the same SQLite source executed through the small in-memory service path.
The run emitted 100,000 facts in a 33,800,482-byte xBRL-JSON artifact. Batch-time
managed-heap samples stayed within the test's 64 MiB retained-heap variance budget.
Source digest parity, small-run set-phase behavior, adapter-error/cancellation result
handling, cancellation cleanup, unsupported project-destination refusal, worker rejection cleanup, streamed publication success, and
preservation of an existing destination after staged-worker rejection also passed.

### Verification commands

- `dotnet test tests/XBRLMappingStudio.Integration.Tests/XBRLMappingStudio.Integration.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~LargeRunExecutionTests -m:1` — passed 10/10 after adding bounded-probe adapter-error and cancellation cases.
- `dotnet test tests/XBRLMappingStudio.Integration.Tests/XBRLMappingStudio.Integration.Tests.csproj -c Release --no-restore -m:1` — passed 114, skipped 5 environment-gated Arelle tests.
- `dotnet test XBRLMappingStudio.slnx -c Release --no-restore -m:1` — passed 505, skipped 5 environment-gated Arelle tests across all test projects.
- `python -m unittest tools.worker.tests.test_arelle_worker` — 23 tests run, 18 passed, 5 environment-gated Arelle tests skipped.
- `git diff --check` — passed.

### Limits and remaining qualification

This does not establish end-to-end constant memory for a real Arelle validation. The
worker protocol and taxonomy rewrite avoid placing the report text on the pipe and
stream-copy the facts bytes, but Arelle's in-process report model may retain data
proportional to fact count. The worker independently verifies the staged digest and
the bounded documentInfo/root framing; syntax and semantic validation inside the full
facts object is delegated to Arelle's report loader. The 100k equivalence test uses a digest-only test worker;
it verifies the application execution, serializer, and publication contracts, not
Arelle's peak memory. ENG-01 therefore remains partial, and PERF01–PERF07 remain open.
Set-phase/group spill, multiple bindings, other large source adapters, and
project-configured destination plans also remain unsupported.

SQLite row-size enforcement occurs after `SqliteDataReader.GetValue` materializes each
column. A single unusually large TEXT/BLOB cell can therefore allocate before the
4 MiB row budget rejects it. The row and batch budgets bound retained working sets for
ordinary records, but they are not a strict peak-allocation cap against a pathological
single cell; that input-memory edge remains open.

Large-run finalization checks cancellation while scanning source-digest rows and fact
rows, and immediately before and after each synchronous SQLite `CREATE INDEX` call.
The provider call itself cannot observe the cancellation token while SQLite is building
an index; cancellation during that call is handled when SQLite returns, after which the
run discards its private spool. Cancellation responsiveness during an unusually long
index build is therefore not bounded.

The current checkout also preflights project-bound non-SQLite file sizes before reading
source identities, limits identity hashing to those bindings, and caps/cancels the digest
read. Local Windows Release verification on the checkout based on `e5e303c` passed 510
.NET tests and skipped five Arelle-gated integration tests. Exact-head CI [42136073636](https://github.com/nirzaf/XBRLMappingStudio/actions/runs/42136073636)
passed on `ae15a9072cde61a6bef4d5e5a7b4de3582cf8230`: Windows and Ubuntu each passed
510 tests and skipped the same five gated cases; the pinned worker passed 23 Python
tests and all five gated .NET Arelle integration cases.
