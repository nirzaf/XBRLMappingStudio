# Verification baseline — 2026-11-10

## Checkout

- Repository: `nirzaf/XBRLMappingStudio`
- Branch: `main`
- Exact commit: `925609ff5cac5a93429ead2828db166a108b5d52`
- `HEAD` and `origin/main` matched when captured.
- The only untracked repository file was the pre-existing
  `tests/XBRLMappingStudio.Fixtures/AcceptanceFixtures/mapping.xmap`; it was not
  modified, staged or included in the commit.
- The authoritative `XBRL_Mapping_Studio.md` guide was not changed.

## Exact-head CI

[GitHub Actions run 42777551773](https://github.com/nirzaf/XBRLMappingStudio/actions/runs/42777551773)
completed successfully on the exact commit above. Repository hygiene, Windows,
Ubuntu and pinned Arelle jobs all passed.

Each Windows and Ubuntu Release matrix ran the eight .NET test projects below:

| Project | Passed | Failed | Environment-gated / not executed |
|---|---:|---:|---:|
| Adapters | 310 | 0 | 0 |
| AI | 72 | 0 | 0 |
| Desktop | 174 | 0 | 0 |
| Engine | 159 | 0 | 0 |
| Integration | 522 | 0 | 7 |
| Ops | 553 | 0 | 0 |
| Project | 32 | 0 | 0 |
| XBRL | 225 | 0 | 0 |
| **Total per OS** | **2,047** | **0** | **7** |

The seven environment-gated Arelle subprocess tests were reported as
`NotExecuted` in the ordinary per-OS TRX files. They were exercised by the
separate pinned-worker job, which passed 82 Python worker tests and all 16 .NET
Arelle execution tests. The dependency-inventory test job passed 15 tests.

Five independent process-memory qualifications passed on each OS: 100,000-row
CSV streaming, SQLite streaming, SQLite table snapshot, partitioned GroupBy and
partitioned Join. Windows also passed the Arelle-worker cancellation regression
five times. CI generated the NuGet vulnerability report and SPDX dependency
inventory successfully.

## Local verification for the latest desktop selector change

- Focused synthetic xBRL occurrence chooser / digest-pinning regression: 1/1.
- Full Windows Release `XBRLMappingStudio.Desktop.Tests`: 174/174.
- The test uses two synthetic facts, selects the second occurrence from the
  chooser, checks the exact report digest and update-only target key, verifies
  changed report bytes fail closed, then verifies clearing the selector.

The local run did not execute the entire eight-project solution; the exact-head
CI matrix is the evidence for that scope.

## Qualification limits and owner gates

These synthetic/offline CI results do not prove IFRS taxonomy package identity,
dependency closure, redistribution rights, production rule completeness,
regulator acceptance, signed installation, target-hardware performance or full
native Windows accessibility. Those remain open in
[`docs/decisions/OPEN.md`](decisions/OPEN.md) and the atomic status matrix.

## DST-04 native retest

The user confirmed Windows was unlocked and visible. Computer Use listed the
main Release application window, but activation failed with
`failed to activate captured window`; refreshing the window list and retrying
failed the same way. No UI input or project changes occurred, so this attempt
made no DST-04 assertions and does not extend native acceptance. The prior
bounded synthetic native evidence remains the latest successful native run.
