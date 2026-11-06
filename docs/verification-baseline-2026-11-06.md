# Verification baseline — 2026-11-06

This records local verification evidence for the ENG-01 implementation in the
shared `main` worktree. Release and deployment verification are outside this
baseline.

## MAP06 application-level join expansion guard

`CollectionAndJoinTests.JoinOverlappingFields_FollowsConfiguredConflictPolicy`
verifies left/right selection, MAP026 on differing values under `Fail`, acceptance of
equal values under `Fail`, and fail-closed MAP027 handling for an unknown policy.
`LargeRunExecutionTests.JoinExpansionBudget_BlocksPublicationAndPreservesPriorFile`
creates a many-to-many SQLite row join with `MaxJoinExpansionRows=2`, then repeats the
application run with `fieldConflict=Fail`. MAP015 and MAP026 each prevent worker report
validation and file publication, and both paths preserve the existing destination
bytes after producing only a bounded partial result.

The focused Release engine and integration tests passed 1/1 each. The normal
integration project-reference build was blocked because the running Desktop process
held shared Release output files; the test project was rebuilt against the existing
dependency binaries with `-p:BuildProjectReferences=false`. `git diff --check` passed.

The follow-up join-key suite also passed 20/20: missing, blank and nil key values stay
unmatched and honor Inner/Left/Right/Full cardinality; invalid `duplicateKey` values
block with MAP028 instead of falling through to `All`.

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

## Current main verification snapshot — source commit `563f74d`

This snapshot records the pushed source head before the evidence-only README and status
updates in the following commit. At capture time, local `main` and `origin/main` both
resolved to `563f74d260af0a0f5e5066db11f135814d1ddbec`, and `git status --short --branch`
reported a clean worktree. No existing changes were discarded. The active architecture
guide is `XBRL_Mapping_Studio.md`; the pending-features specification remains the
user-provided `XBRLMappingStudio_Pending_Features_Atomic_Implementation_Spec_2026-11-04.md`.

### Local Release build and test matrix

The verification command was:

```powershell
dotnet test XBRLMappingStudio.slnx --configuration Release -p:UseArtifactsOutput=true "-p:ArtifactsPath=$env:TEMP\XBRLMappingStudio-verification"
```

The .NET SDK was `10.0.401`; all projects targeted `net10.0`. `UseArtifactsOutput`
isolated output under the Windows temp directory because an already-running Desktop
process held the normal build outputs. The process was left untouched. The command built
the full solution and produced these eight suite results:

| Test project | Passed | Skipped | Failed |
|---|---:|---:|---:|
| `XBRLMappingStudio.Adapters.Tests` | 89 | 0 | 0 |
| `XBRLMappingStudio.AI.Tests` | 72 | 0 | 0 |
| `XBRLMappingStudio.Desktop.Tests` | 64 | 0 | 0 |
| `XBRLMappingStudio.Engine.Tests` | 65 | 0 | 0 |
| `XBRLMappingStudio.Integration.Tests` | 158 | 5 | 0 |
| `XBRLMappingStudio.Ops.Tests` | 72 | 0 | 0 |
| `XBRLMappingStudio.Project.Tests` | 15 | 0 | 0 |
| `XBRLMappingStudio.XBRL.Tests` | 77 | 0 | 0 |
| **Total per platform** | **612** | **5** | **0** |

These Windows local totals matched the separate Ubuntu and Windows CI artifacts.

The five default-matrix skips are `AuthorizedPublicationRevalidatesExactStagedArtifactWithOfflineArelle`,
`CliPreviewUsesArelleToValidateExcelMappingOutput`,
`CliValidateArtifactUsesLocalSyntheticRuleCompletionWithArelle`,
`ExcelToXbrlMappingIsAdjudicatedByOfflineArelleWorker`, and
`ReviewedDigitalPdf_MapsOnlyTargetYearAndBlocksWrongYearOnlySource`. Their
`RequiresArelleRuntimeFact` gate requires `XMS_ARELLE_E2E=1` and the pinned Python
runtime; a skip in the standard suite is not counted as execution evidence.

### Exact-head CI and archived artifacts

GitHub Actions run [42192658514](https://github.com/nirzaf/XBRLMappingStudio/actions/runs/42192658514)
ran on the exact source SHA above. Repository hygiene, Ubuntu Release build/tests,
Windows Release build/tests, and the pinned Arelle worker integration job all succeeded.
Both OS matrices matched the local eight-suite totals: 612 passed, 5 explicitly gated
skips, 0 failures. The workflow uploaded eight TRX result files per OS; those archived
files were downloaded and their per-suite counters checked.

The separate worker job installed Python 3.12 using
`python -m pip install --require-hashes -r tools/worker/requirements.lock`, ran
`python -m unittest discover -s tools/worker/tests -v` (46 passed), then set
`XMS_ARELLE_E2E=1` and `XMS_ARELLE_PYTHON=python` for the filtered Arelle integration
suite (11 passed, including all five cases gated in the regular matrix). This evidence
does not qualify a customer filing profile, customer data rights, native Windows GUI
accessibility, or production hardware performance.

## OPS-05 dependency lock and vulnerability evidence

On the Windows checkout, `dotnet restore XBRLMappingStudio.slnx --locked-mode
-warnaserror:NU1900,NU1901,NU1902,NU1903,NU1904` completed successfully. The
solution has 19 project lock files covering `win-x64`, `linux-x64`, and `osx-x64`.
A local `dotnet list XBRLMappingStudio.slnx package --vulnerable
--include-transitive --format json --no-restore` report parsed for all 19 projects
and contained zero vulnerable package records at verification time. CI now uploads
the equivalent per-OS JSON report with test results.

CLI release publish probes succeeded with the locked multi-RID restore followed by
`--no-restore` for `win-x64`, `linux-x64`, and `osx-x64`. Desktop initially restored
under Debug and its publish included the Debug-only diagnostics package; this was
corrected with `packages.Release.lock.json` and a Release-configuration restore.
The Release `win-x64` Desktop publish then succeeded with `--no-restore`, and both
its output directory and `.deps.json` excluded `Avalonia.Diagnostics`. `git diff --check`
passed. These checks do not close OPS-05: complete SPDX/per-file
license inventories (including native/OCR components), redistribution review,
artifact hashes, and locked release bundles remain open.

## OPS-05 SPDX and release-asset evidence

`tools/dependency_inventory.py` and its standard-library tests generate an SPDX
2.3 document for the CLI/Release-Desktop package graphs and pinned worker requirements.
Generation with the exact hash-locked worker dependencies parsed 69 unique
components and 147 dependency/document relationships; all referenced SPDX IDs
resolved. It embedded 40 extracted license texts, with source-file SHA-256
evidence, and retained `NOASSERTION` for 11 packages. CI and release verification
install the exact hash-locked worker dependencies before generation, and preserve
any remaining unknown licenses as `NOASSERTION` for review.

Artifact review found that NuGet package cache paths preserve periods in package
IDs; the first generator version applied PyPI normalization and therefore missed
NuGet nuspec metadata. The lookup now uses NuGet's lowercase-but-not-normalized
folder name. License text declared by a nuspec or present in an installed Python
distribution is embedded verbatim as SPDX `LicenseRef` evidence with its source
SHA-256. Ten SkiaSharp/HarfBuzzSharp packages retain legacy license URLs, and
`Avalonia.BuildServices` has no machine-readable license metadata or captured
license file; all 11 remain `NOASSERTION`. The corrected local document contains
69 components, 40 extracted license texts, and 11 unresolved license assertions.

Release packaging now hashes every uploaded archive and the SPDX document into
`SHA256SUMS.txt`. This provides package/artifact provenance evidence, not per-file
license review, redistribution approval, native/OCR qualification, signatures, or
clean-machine installation evidence.

## TAX-07 profile-bound presentation relationships

Filing-profile manifest schema v4 carries ordered presentation links in the
profile digest; schema-v2/v3 digest contracts remain unchanged. The catalog and
worker bound link count, validate role URIs and indexed endpoints, and reject
duplicate arcs. `TaxonomyIndex` keeps relationships separate from concept
identity, exposes per-parent child paging, and retains bounded cycle-safe path
enumeration; taxonomy cache schema v2 preserves the links. No licensed real
taxonomy network was loaded, so calculation/definition relationship ingestion
and real-profile qualification remain open.

Focused Release verification passed: 23 profile-catalog integration tests, 80
XBRL tests, 72 operations/cache tests, and 2 desktop explorer tests. The 48 Python
worker tests passed with 6 environment-dependent skips.

## TAX-02 exact offline reference loading

The worker accepts an external taxonomy dependency only when its exact HTTP(S)
file URI maps through the installed profile to a hash-pinned package file. It
copies that dependency into the rehashed private snapshot and supplies Arelle
with a temporary OASIS rewrite catalog containing only those URI directories
and files. A pinned-Arelle integration fixture successfully loaded a schema
through a percent-encoded URI using the local target; Arelle remained in
`workOffline` / `internetConnectivity="offline"` mode. ZIP taxonomy packages
and OS-level network-egress isolation remain unsupported/unverified.

Local verification passed all 50 Python worker tests (one symlink-privilege
skip) and all 4 focused profile-catalog .NET tests. Arelle 2.47.4 emitted one
non-failing `ResourceWarning` while closing the temporary ZIP package stream;
the taxonomy load and temporary-directory cleanup completed successfully.

## TAX-08 streamed synthetic rule completion

`FilingRuleValidator.ValidateStreaming` reduces an arbitrary fact sequence to the
same collapsed rule outcomes as the in-memory validator. Large-run duplicate evidence
comes from a query over the SQLite fact spool, ordered by the first input occurrence to preserve
the in-memory result. The application passes these local outcomes to the completion
gate only for the synthetic profile and only as a replacement for unavailable worker
rule rows. `LocalSyntheticWorker` can produce its normal profile-bound completion
response from streamed fact locators without parsing a whole large report into memory.
Staged validation checks the staged byte count and digest against the completed
artifact before using the retained spool for its local synthetic rule pass.

Focused Release verification passed `FilingRuleValidatorTests` 85/85 and the combined
`LargeRunExecutionTests`, `WorkerAdjudicationTests`, and `PublicationGateTests` filter
54/54. New coverage compares streaming/in-memory outcomes for pass, duplicate, failing,
and incomplete fixtures; checks cancellation; verifies the Arelle-unavailable local
fallback; and runs a large synthetic artifact through both run validation and staged
revalidation. `git diff --check` passed. These tests do not supply real-profile Arelle
rule/plugin execution evidence; real-profile completion remains owner-gated, and
worker diagnostic storage can still grow with the count of per-fact findings.

## AIM-04 durable audit lock failure

The desktop acceptance path was exercised against a real SQLite writer lock rather
than only an injected failing sink. The test holds a WAL write transaction while the
suggestion session appends its acceptance event; the resulting database lock is shown
as an audit-persistence failure, the graph semantic digest stays unchanged, the
suggestion remains pending, and no durable acceptance row appears. The focused
`DesignerSessionTests` Release filter passed 55/55. Organizational reviewer authority,
retention policy, and multi-user governance remain owner decisions.

## DST-08 mixed file/SQLite publication reconciliation

CLI `execute --output <path> --publish-destination` writes xBRL-JSON and the saved
SQLite destination as independent legs. A schema-v7 local record captures the stable
combined intent and each destination state before writes; the manifest reports each
receipt separately. File publication is create-or-confirm for this flow: same-digest
retries do not rewrite, and different existing bytes are refused. SQLite publication
persists its exact operation key and effective intent before the target transaction.
`reconcile-publication <mix-key>` reads the file digest and SQLite receipt without
publishing and refreshes the local outcome record.

Verification: the Ops suite passed 75/75; the CLI parity and SQLite destination
workflow filters passed 32/32; the focused mixed CLI test passed 1/1 and exercised
both destinations plus explicit reconciliation. The mixed integration failure test
injected a SQLite trigger rollback after file confirmation, then retried and verified
one fact row and one receipt without rewriting the report. Ops coverage also verifies
refusal of a different existing file, same-digest confirmation, missing-receipt
classification, unavailable-target unknown state, and receipt matching after an
ambiguous response. `git diff --check` passed. Process-kill coverage at every replace
and journal transition and cross-process crash qualification remain open; this is a
local SQLite implementation and does not imply cross-filesystem atomicity.
