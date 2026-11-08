# Verification baseline — 2026-11-09

Checkout: `main` at `e74337bd6e46c76cd2250de43c7c0a5f990c54c9` before this evidence-only documentation update.

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

## External qualification boundaries

No official IFRS package was downloaded or bundled. Package artifact identity, dependency closure, offline resolution, applicable evaluation rights, and redistribution rights remain unverified. No jurisdiction-specific filing profile, signed installer, production database provider, customer hardware floor, or production taxonomy authorization is claimed.

## DST-08 lexical identity follow-up

The focused Windows Release integration filter `Cli_MixedPublicationPublishesVersionedRecordFilesAndRetriesWithoutReplacement` passed **6/6** using an isolated artifact root. Each CSV v1/v2, XLSX v1/v2, JSON v1, and XML v1 mixed-publication journey now publishes, reopens through the corresponding source adapter, and asserts exact preservation of both the QName-valid entity identifier `Entity-00123-04` an escaped, multiline language-tagged text fact, and the exact decimal lexeme `12.5000` from a one-row synthetic workbook. Initial invalid entity-identifier and duplicated constant-text fixtures were rejected with XBR011 and duplicate-fact validation; the final fixture uses a QName-valid identifier and row-filtered source field.

## DST-08 mixed journal and atomic-move crash follow-up

`MixedPublicationCoordinatorTests.AbruptProcessTerminationAfterMixedFileMove_ReconcilesFileAndPublishesSqliteOnce` passed **1/1** on Windows Release using an isolated artifact root. The real child process is terminated after a new report is moved into place but before the file and mixed journals confirm it. Restart finalizes the digest-verified file journal, reconciles the mixed file leg as confirmed with SQLite pending, then retries only SQLite and records one fact plus one receipt. This covers the first-time atomic move for a synthetic report. Per-record-adapter mid-write termination and filesystem power-loss durability remain open.

## DST-08 pre-lock staging recovery follow-up

The focused OPS recovery/publication regression filter passed **116/116**, and the CLI recovery command integration tests passed **3/3**. `JournalRecoverySafetyTests` verifies digest-matched staging cleanup for SQLite and legacy journal storage, while mismatched staging bytes remain untouched. `JournalCrashRecoveryTests.AbruptTermination_RecoversOnlyLockedValidatedBytes` passed **6/6** for a real child-process kill immediately after the staged journal write across JSON/XML and absent/different/identical existing destination cases. Recovery records `Aborted`, removes only the matching unpublished sidecar, preserves any existing destination bytes, and writes no publication receipt. Full exact-head CI has not yet been run for this change.
