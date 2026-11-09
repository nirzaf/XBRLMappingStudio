# Verification baseline — 2026-11-09

Checkout: `main` at `21f4672cffde7f9487c21f710b0c98018889a68c` for the latest code and CI evidence below. Earlier local verification remains labeled by its original checkout where applicable.

## Release verification

- The complete .NET solution Release suite passed **1,856 tests** with **6 environment-gated Arelle skips** using an isolated artifact root at `C:\Users\DELL\AppData\Local\Temp\xms-full-qa-20261109`.
- The standard shared Release artifact directory could not be used because a running XBRLMappingStudio Desktop process held generated files open; the isolated run completed successfully without closing or changing that process.
- The Python worker support suite passed **15/15** tests.
- The performance harness self-test passed.
- Exact-head GitHub Actions run [42525985348](https://github.com/nirzaf/XBRLMappingStudio/actions/runs/42525985348) passed Windows, Ubuntu, pinned Arelle integration, and repository hygiene jobs on the checkout SHA above.

## DST-08 additions

The checkout supports mixed file/SQLite publication for xBRL-JSON/XML, CSV v1/v2, XLSX v1/v2, record-JSON v1, and record-XML v1. Real child-process termination tests cover the durable file-confirmed/SQLite-in-progress and SQLite-commit/local-confirmation-loss boundaries for the six generic record contracts and xBRL-XML. Adapter round trips cover escaped text, embedded quotes and newlines, leading-zero identifiers, invariant decimal lexemes, and namespaced XML values. These are synthetic-fixture results. Adapter-internal process loss, lexical edge values through the mixed CLI journey, exhaustive reconciliation, and filesystem power-loss durability remain open.

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

Exact-head CI [42538826418](https://github.com/nirzaf/XBRLMappingStudio/actions/runs/42538826418) passed all four jobs on `77568e710fb21be5d2233f270fb12b2209d90e97`: Windows and Ubuntu each passed **1,881 tests** with **6** gated Arelle skips, all five memory qualifications passed on both platforms, and Arelle integration, repository hygiene, SPDX/dependency inventory and Windows worker-cancellation repeats passed.
