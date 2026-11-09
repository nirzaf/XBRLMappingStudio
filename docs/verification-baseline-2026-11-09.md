# Verification baseline — 2026-11-09

The original full-suite baseline below was captured at `21f4672cffde7f9487c21f710b0c98018889a68c`; later exact-head follow-ups are recorded at the end of this file. Earlier local verification remains labeled by its original checkout where applicable.

## Release verification

- The complete .NET solution Release suite passed **1,856 tests** with **6 environment-gated Arelle skips** using an isolated artifact root at `C:\Users\DELL\AppData\Local\Temp\xms-full-qa-20261109`.
- The standard shared Release artifact directory could not be used because a running XBRLMappingStudio Desktop process held generated files open; the isolated run completed successfully without closing or changing that process.
- The Python worker support suite passed **15/15** tests.
- The performance harness self-test passed.
- Exact-head GitHub Actions run [42525985348](https://github.com/nirzaf/XBRLMappingStudio/actions/runs/42525985348) passed Windows, Ubuntu, pinned Arelle integration, and repository hygiene jobs on the checkout SHA above.

## DST-08 additions

The checkout supports mixed file/SQLite publication for xBRL-JSON/XML, CSV v1/v2, XLSX v1/v2, record-JSON v1, and record-XML v1. Real child-process termination tests cover mixed-state recovery before the file publisher writes staging bytes, file-confirmed/SQLite-in-progress, first-time atomic file move, and SQLite-commit/local-confirmation-loss boundaries across supported contracts. Adapter round trips and six mixed-publication CLI format journeys cover escaped text, embedded quotes and newlines, leading-zero identifiers, invariant decimal lexemes, and namespaced XML values. These are synthetic-fixture results. Adapter-internal serializer interruption, exhaustive reconciliation, and filesystem power-loss durability remain open.

## Native desktop boundary

A fresh native screenshot on 2026-11-09 showed the Windows lock screen. The app accessibility tree was readable, but the project loaded behind it was a stale `sample-main` mapping with no valid end-to-end publication fixture. No further native interaction was attempted. Prior disposable-fixture native evidence remains applicable only to its recorded narrow journeys; DST-04 native acceptance is not extended by this observation.

After the owner confirmed the desktop was unlocked, the Computer Use session listed three XBRLMappingStudio windows, including the existing `Sample Revenue Mapping` window. Activation of the uniquely titled returned window failed twice, including after refreshing the returned window list. No clicks, typing, or project changes were made. DST-04 native verification remains blocked by desktop input availability; the earlier locked-screen note is superseded only as to the reported lock state, not as to the missing valid fixture or acceptance boundary.

## External qualification boundaries

No official IFRS package was downloaded or bundled. Package artifact identity, dependency closure, offline resolution, applicable evaluation rights, and redistribution rights remain unverified. No jurisdiction-specific filing profile, signed installer, production database provider, customer hardware floor, or production taxonomy authorization is claimed.

## DST-08 lexical identity follow-up

The focused Windows Release integration filter `Cli_MixedPublicationPublishesVersionedRecordFilesAndRetriesWithoutReplacement` passed **6/6** using an isolated artifact root. Each CSV v1/v2, XLSX v1/v2, JSON v1, and XML v1 mixed-publication journey now publishes, reopens through the corresponding source adapter, and asserts exact preservation of both the QName-valid entity identifier `Entity-00123-04` an escaped, multiline language-tagged text fact, and the exact decimal lexeme `12.5000` from a one-row synthetic workbook. Initial invalid entity-identifier and duplicated constant-text fixtures were rejected with XBR011 and duplicate-fact validation; the final fixture uses a QName-valid identifier and row-filtered source field.

## DST-08 mixed journal and atomic-move crash follow-up

`MixedPublicationCoordinatorTests.AbruptProcessTerminationAfterMixedFileMove_ReconcilesFileAndPublishesSqliteOnce` passed **1/1** on Windows Release using an isolated artifact root. The real child process is terminated after a new report is moved into place but before the file and mixed journals confirm it. Restart finalizes the digest-verified file journal, reconciles the mixed file leg as confirmed with SQLite pending, then retries only SQLite and records one fact plus one receipt. This covers the first-time atomic move for a synthetic report. Per-record-adapter mid-write termination and filesystem power-loss durability remain open.

## DST-08 pre-lock staging recovery follow-up

The focused OPS recovery/publication regression filter passed **116/116**, and the CLI recovery command integration tests passed **3/3**. `JournalRecoverySafetyTests` verifies digest-matched staging cleanup for SQLite and legacy journal storage, while mismatched staging bytes remain untouched. `JournalCrashRecoveryTests.AbruptTermination_RecoversOnlyLockedValidatedBytes` passed **6/6** for a real child-process kill immediately after the staged journal write across JSON/XML and absent/different/identical existing destination cases. Recovery records `Aborted`, removes only the matching unpublished sidecar, preserves any existing destination bytes, and writes no publication receipt. Exact-head CI [42532765200](https://github.com/nirzaf/XBRLMappingStudio/actions/runs/42532765200) passed Windows, Ubuntu, pinned Arelle integration, and repository hygiene on `b1cb8cf06c25d61f86968ba6a6652cbfbf6d4ea3`.

## DST-08 generic record stage-write crash follow-up

The Windows Release OPS publication/recovery regression filter passed **125/125**, including real child-process termination after half of each CSV v1/v2, XLSX v1/v2, JSON-record v1, and XML-record v1 adapter artifact had been written to the shared staging path. Recovery acquired the run's destination and staging locks, recorded `Aborted`, removed only the abandoned partial sidecar, preserved the prior accepted output byte-for-byte, and created no receipt. Tests also verify recovery leaves a live writer's locked partial file untouched and rejects reuse of a finalized run identifier before modifying either output. The CLI recovery integration suite passed **3/3**.

## DST-08 single-use run identity and exact-head CI follow-up

On `21f4672cffde7f9487c21f710b0c98018889a68c`, the four retained SQLite/table snapshot publication retry cases now assert the documented `PUB006` receipt conflict when a finalized run ID is reused; the existing destination remains byte-for-byte unchanged. The focused Windows integration filter passed **4/4**. Exact-head CI [42535659999](https://github.com/nirzaf/XBRLMappingStudio/actions/runs/42535659999) passed all four jobs: Windows and Ubuntu each passed **1,878 tests** with **6 environment-gated Arelle skips**, all five separate memory qualifications passed on both platforms, the Windows worker cancellation regression repeats passed, and Arelle integration, SPDX/dependency inventory, and repository hygiene passed. This does not close adapter-internal serializer interruption or filesystem power-loss qualification.

## OPS-08 bounded unattended support diagnostics

`support-bundle --log` reads only the last **256 KiB** of a log, keeps at most **200 lines** and **64 KiB** of log text, omits any individual line over **8 KiB**, and redacts credential assignments. `SupportBundle.Build` also caps direct caller-supplied log enumerables at **4,096 lines**, with an omission marker, so an infinite sequence cannot keep bundle generation running forever. The focused support-bundle/redaction Ops tests now include this infinite-sequence bound; the real CLI subprocess test verifies the bounded file-tail path. A full local Ops run had one unrelated XML crash-harness timeout; the isolated 24-case publication crash matrix passed **24/24** on immediate rerun. Scheduler guidance now describes Task Scheduler and systemd examples, but no customer service identity or deployed scheduler has been qualified.

Exact-head CI [42545993025](https://github.com/nirzaf/XBRLMappingStudio/actions/runs/42545993025) passed all four jobs on `9a18c6c71af87c896f746fe1f61c0be8ef43045b`: Windows and Ubuntu each passed **1,887 tests** with **6** gated Arelle skips, all five memory qualifications passed on both platforms, and Arelle integration, repository hygiene, SPDX/dependency inventory and Windows worker-cancellation repeats passed. The suite counts were Adapters 309, AI 72, Desktop 170, Engine 155, Integration 509 (503 passed, 6 skipped), Ops 445, Project 32 and XBRL 201.

## OPS worker-capacity observation race follow-up

The documentation-only commit `6786684044ed1b5ee707cfc77a3ef7a99fa38d91` exposed a Windows-only failure in `ConcurrentWorkers_AreBoundedAcrossSupervisorInstances`: the child processes completed successfully, but the test observed zero active markers because each marker existed for only 350 ms. The test now holds workers at a release barrier, samples the active count while they remain running, waits briefly to detect excess starts, then releases them. The focused test passed eight consecutive local runs and the complete local Ops project passed **445/445**. Exact-head CI [42548631301](https://github.com/nirzaf/XBRLMappingStudio/actions/runs/42548631301) passed all four jobs on `6c59fc6510361f00e81f0cecbf9634b7a47903a1`: Windows and Ubuntu each passed **1,887 tests** with **6** gated Arelle skips; all five memory qualifications, repeated Windows worker-cancellation checks, Arelle integration, repository hygiene, and SPDX/dependency inventory passed.

## XBR06 mapped-aspect diagnostic evidence

Commit `575aa4a5306e785d73ca7dd40a9dd6b8a72a1a39` strengthens the dynamic XBRL report-import regression to verify the exact FactConstruct port and source locator for invalid mapped concepts, units, dimensions, decimals, and languages. The Engine Release suite passed **156/156** locally. Exact-head CI [42572016556](https://github.com/nirzaf/XBRLMappingStudio/actions/runs/42572016556) passed Windows, Ubuntu, pinned Arelle integration, and repository hygiene. This does not close XBR06's existing-target-row, tuple, typed-dimension-generation, or large-input streaming limitations.

## DST-08 explicit reconciliation cancellation follow-up

Commit `84754f8f78d6bdf5c96d17ce97cfedc219c11765` made explicit pre-cancelled read-only receipt reconciliation propagate cancellation; its focused regression and the complete Ops suite passed **446/446**. Commit `d445fd5da1a1d3edac918f8a6b1fcd8ddf536656` added the 15-case file-state reconciliation matrix; the full Ops suite passed **461/461** locally. Commit `0db79fb7e8ce7d7b847598363c00feca2f3f2ee4` added token-aware streaming file digests and checks cancellation before persisting file-state classification. Its local Ops suite passed **461/461**. Exact-head CI [42578520788](https://github.com/nirzaf/XBRLMappingStudio/actions/runs/42578520788) passed all four jobs: Windows and Ubuntu each passed **1,907 tests** with **6** gated Arelle skips, all five memory qualifications passed on both platforms, the Windows worker-cancellation regression repeats passed, and pinned Arelle integration, repository hygiene, and SPDX/dependency inventory passed. Explicit pre-cancellation preserves the stored outcome; completed file evidence may persist if a later SQLite receipt check is cancelled. Exhaustive SQLite receipt-transition reconciliation and filesystem power-loss durability remain open.

## OPS-09 active-spool lease and exact-head verification follow-up

Commits `9c65acd` and `fdba4ca9387bea477ee39de1672e084a92360af8` add approved cleanup for owner-marked abandoned large-run spools and prevent in-process active or concurrently inspected spools from entering cleanup. The focused retention regression passed **4/4** after the lease-registry correction; the exact-head full Ops suite passed **535/535** in CI. Exact-head CI [42610355219](https://github.com/nirzaf/XBRLMappingStudio/actions/runs/42610355219) passed Windows and Ubuntu: each matrix passed **1,981 tests** with **6** gated Arelle skips, all five memory qualifications and Windows cancellation regression repeats passed, and the pinned Arelle integration and repository hygiene jobs passed. Suite totals were Adapters 309, AI 72, Desktop 171, Engine 156, Integration 510 (504 passed, 6 skipped), Ops 535, Project 32, and XBRL 202. The separate worker passed **82 Python tests** and **15 .NET Arelle execution tests**. PDF evidence, AI metadata and operational-history retention remain unimplemented pending separate policies; snapshot-staging retention was added and verified in the follow-up below. No automatic purge or default age is enabled.

## OPS-09 SQLite snapshot-staging retention follow-up

Commit `5f1aeab13a80f60afed9e6fa09161de97641627c` adds bounded, explicitly
authorized cleanup for abandoned unpublished SQLite snapshot staging only. The
focused regression set passed **59/59** and the full local Ops suite passed
**540/540**. Exact-head CI [42617194161](https://github.com/nirzaf/XBRLMappingStudio/actions/runs/42617194161)
passed Windows and Ubuntu; docs-only evidence commit
`7126e7f47b2093da3ac08170bd7737392d3c84d4` also passed all four CI jobs in
[42618938295](https://github.com/nirzaf/XBRLMappingStudio/actions/runs/42618938295).
Each OS matrix passed **1,986 tests** with **6** environment-gated Arelle skips;
all five memory qualifications and Windows cancellation repeats passed. The
pinned worker job passed **82 Python tests** and **15 .NET Arelle execution
tests**. The cleanup service has no default age, production authorizer, UI/CLI
trigger, or automatic purge; completed snapshots, reports and receipts remain
outside its candidate set.

The focused Windows Release regression `Apply_RejectsEmptyDirectoryAddedAfterPreview`
also passes, confirming that even an empty directory added after approval changes
the tree digest and prevents cleanup before a pending audit is written. The full
Ops suite passes **541/541** with this hardening. Exact-head CI
[42623518977](https://github.com/nirzaf/XBRLMappingStudio/actions/runs/42623518977)
passed on commit `9142880c9c9db891336c2836841085796fb240d3`; Windows and Ubuntu
each passed **1,987 tests** with **6** environment-gated Arelle skips. All four
jobs passed, including Arelle integration, five memory qualifications and Windows
worker-cancellation regression repeats.

## XBR-07 structured reporting-period selection follow-up

`XbrlReportAdapter` now accepts exact entity-scheme and entity-identifier filters
alongside `periodKind`, `instantDate`, `periodStart`, and `periodEnd` filters and
the existing combined ISO period filter.
Malformed ISO dates, unsupported period-kind values, mixed instant/duration
filters, and non-positive bounded durations fail with `IO_SCHEMA_MISMATCH`
before report reading. A synthetic XML occurrence fixture confirms each entity
filter independently and both together exclude a second entity with the same
concept and period, while duplicate facts for the selected entity remain
distinct and retain source locators. The focused
reverse-mapping integration class passed **13/13**; after this entity-filter
follow-up, the complete Windows Release Integration suite passed **514/514
runnable tests** with **6** environment-gated Arelle skips. This does not complete destination
row selection, tuple flattening, typed-dimension generation, or full reverse
mapping qualification.

Exact-head CI [42639813409](https://github.com/nirzaf/XBRLMappingStudio/actions/runs/42639813409)
passed on `1105f2f1970db1d29836c7ca37cc9e16884c3fdc`. Windows and Ubuntu each
passed **1,992 tests** with **6** environment-gated Arelle skips; repository
hygiene and the pinned Arelle job passed, including **82 Python** and **15 .NET**
worker tests. Both matrix test suites include `ReverseMappingDiagnosticTests`
with independently exercised and conjunctive entity-scheme and entity-identifier selectors. Windows streaming and
snapshot memory qualifications and repeated worker-cancellation regressions
also passed. The synthetic entity fixture does not qualify a real taxonomy or
complete destination-row, tuple, and typed-dimension behavior.

## ENG-02 Join working-set budget and failure classification follow-up

Commit `5dc5e75c0725d6a2e8476ed13b945d61281bf6ca` adds a conservative retained-state
estimate for in-memory Join indexes, matched-key state, expanded records and staged
lineage, and drops the whole output if a later limit fails. The first exact-head
workflow exposed a result-classification regression when this fail-closed behavior
left no facts: MAP015 was being masked by the secondary XBR014 no-facts diagnostic as
`IncompleteConfiguration`. Commit `a7d830d3a73f09488fc7d41d25b8b0186a66edfd`
preserves `InvalidMapping` when a blocking graph error exists and asserts that
expansion and field-conflict failures return no partial facts. The focused
Integration regression passed **1/1**. Exact-head CI
[42658862587](https://github.com/nirzaf/XBRLMappingStudio/actions/runs/42658862587)
passed all four jobs. Windows and Ubuntu each passed **1,994 tests** with **6**
environment-gated Arelle skips; all five streaming/snapshot memory qualifications
passed on both platforms, as did the repeated Windows worker-cancellation checks,
pinned Arelle worker integration, and repository hygiene. This is deterministic
working-state budgeting, not a process-wide memory cap; general set-graph spilling,
within-key spill, and native desktop acceptance remain open.

## ENG-02 physical spool page-cap follow-up

Commit `dc41307e57dc84a14dee41961632279ac93cf333` derives SQLite's
`max_page_count` from `MaxSpoolBytes` and translates `SQLITE_FULL` into the
existing fail-closed spool-budget outcome. The cap applies to the private spool's
main database file, including group/join tables and indexes; it does not cap
SQLite temporary sort files or total process disk use. A direct regression
reached the page limit during fact writes and verified the database stayed at or
below **128 KiB**. The two direct spool-budget cases passed **2/2**, and the
partitioned grouping/join integration set passed **87/87** on Windows Release.
Exact-head CI [42662981902](https://github.com/nirzaf/XBRLMappingStudio/actions/runs/42662981902)
passed all four jobs. Windows and Ubuntu each passed **1,995 tests** with **6**
environment-gated Arelle skips; all five streaming/snapshot memory qualifications
passed on both platforms, along with the repeated Windows worker-cancellation
checks, pinned Arelle integration, and repository hygiene. Broader set-graph
spilling and all non-database temporary-disk accounting remain open.

## OPS-07 SQLite WAL backup and restore follow-up

`BackupAndRestore_CapturesCommittedWalAndRemainsIndependent` passed **1/1** on
Windows Release. It confirms that a committed setting still present in the live
WAL is included in `BackupTo`, that the copied backup opens with a successful
integrity check, and that later writes to the source do not change the restore.
This verifies the local SQLite-aware backup/restore path; installed-release
upgrade interruption, rollback and clean-machine recovery remain open. Exact-head
CI [42665410467](https://github.com/nirzaf/XBRLMappingStudio/actions/runs/42665410467)
passed all four jobs; Windows and Ubuntu each passed **1,996 tests** with **6**
gated Arelle skips, the pinned Arelle integration and repository hygiene passed,
all five memory qualifications passed on both platforms, and Windows worker
cancellation regressions passed repeatedly.

## DST-04 SQLite destination desktop regression

The isolated Windows Release Desktop test
`DesktopDestinationRequiresSchemaReviewDryRunAndFinalConfirmation` passed **1/1**.
It verifies destination schema/key review, saving the destination-bound semantic
revision, a read-only dry run, invalidation after selection changes, cancellation
without writes, explicit publish confirmation, and the resulting SQLite receipt.
The test used an isolated `bin-codex/dst04` output root because the open Desktop
instance held the shared Release assemblies. A native Windows click-through could
not be completed: Computer Use captured the workspace but window activation failed;
the visible project also reported `XBR017` because its synthetic development
profile was not explicitly enabled. This is an environment-blocked native journey,
not evidence of an application interaction failure. Native DST-04 acceptance remains
open.

## XBR-05 generated JSON/XML oracle parity

The focused Windows Release Integration test
`AcceptanceScenarios.FinancialCorrectness_RevenueMatchesGoldenOracle` passed **1/1**.
One mapping run over the pinned hand-authored workbook is now checked in both
generated xBRL-JSON and xBRL-XML against the same independent fact oracle. The test
compares concept, entity, duration, unit, value, decimals and semantic key, and checks
the exact XML value lexeme. Source locators remain validated against the in-memory
mapped fact; they are intentionally excluded from the serialized XML comparison
because they are application provenance, not XBRL aspects. This closes one synthetic
cross-format journey only; real-profile acceptance and full OIM conformance remain
open.
