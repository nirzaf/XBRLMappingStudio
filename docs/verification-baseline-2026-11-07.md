# Verification baseline — 2026-11-07

This records local verification evidence for the XBR-04 set-collected text-fact
slice on `main`. It does not qualify a customer taxonomy or a filing profile.

## Set-collected language-tagged text facts

`FactConstruct` accepts a `textValues` string collection alongside the existing
scalar `textValue`. Each selected item follows the standard concept, period,
language and entity validation path. Missing and blank entries do not emit facts;
explicit nil and repeated text occurrences are retained. Per-item source
references and transformation paths reach accepted facts, and a rejected language
reports the selected item source. Scalar and set-collected text values present in
the same row are rejected as an ambiguous fact value.

Verification:

- Focused `CollectTextValues` engine tests passed 3/3, including repeated values,
  missing/blank entries, explicit nil, per-item provenance and unsupported-language
  diagnostics, plus rejection of simultaneous scalar and set-collected values.
- The final Release Engine suite passed 117/117; the focused tests passed 3/3.
- The Release Desktop suite passed 78/78 with
  `BaseOutputPath=artifacts/qa/desktop-bin/` because the open desktop process held
  the checkout's shared Release DLLs. The first shared-output attempt failed on
  those file locks; the running desktop process was left untouched.
- `git diff --check` passed after the implementation and documentation changes.

Exact-head GitHub Actions run
[42298685658](https://github.com/nirzaf/XBRLMappingStudio/actions/runs/42298685658)
verified source commit `0cac3fee5dfad0a0c8879b12c8366ddb3f291041`. Repository
hygiene, Ubuntu and Windows Release build/test, and pinned Arelle worker
integration all passed. Each .NET OS matrix passed 723 tests and skipped 5 gated
Arelle subprocess cases; the worker job passed 53 protocol/offline-validation
Python tests, 15 locked-inventory Python tests, and all 11 gated .NET Arelle
execution tests.

Remaining XBR-04/UX-06 work includes FactConstruct compound-unit creation, typed
dimensions and default-member handling, complete concept-specific dimension and
BCP-47 metadata, other non-monetary value types, automatic source-drift
revalidation, and installed real-profile/native workflow acceptance. Real taxonomy
qualification remains gated by GAT-01/GAT-02.

## FactConstruct compound units and standard unit encoding

`FactConstruct` now accepts supported unit products and ratios, such as
`USD*QAR/shares`, and the engine carries these through both its in-memory and
SQLite-spooled fact folds. A shared unit validator applies profile and concept
constraints from mapping execution through desktop editing, serialization, and
local filing validation. Monetary concepts require one supported currency;
shares and pure concepts require their standard XBRL measures. XML emits product
measure sequences and divide structures; negative simple powers serialize as a
pure numerator over repeated denominator measures. OIM omits a pure unit core
dimension and the profile-aware reader restores it when concept metadata marks
the fact numeric.

Verification:

- `dotnet build XBRLMappingStudio.slnx -c Release --no-restore` succeeded with
  0 warnings and 0 errors.
- `dotnet test XBRLMappingStudio.slnx -c Release --no-build -m:1` passed all
  eight .NET test projects: 747 passed, 0 failed, 5 skipped. The skipped cases require
  the pinned Arelle subprocess runtime, which was not configured in this local
  environment.
- XBRL: 105/105; Engine: 118/118; Desktop: 80/80; Integration: 187 passed,
  5 skipped. The integration suite includes the small and SQLite-spooled
  compound-unit fold, plus worker rejection of a deliberately corrupted unit.
- The full local Release build completed with 0 warnings and 0 errors; the
  full eight-project test run passed 747, failed 0, and skipped 5 Arelle-gated
  subprocess cases. `git diff --check` passed before commit.

Exact-head GitHub Actions run
[42308787441](https://github.com/nirzaf/XBRLMappingStudio/actions/runs/42308787441)
verified source commit `2887939e0be99c710faa6aa973337b7d81c671e5`. Repository
hygiene, Windows and Ubuntu Release build/test, pinned Arelle worker integration,
locked dependency inventory, and SPDX generation all passed. Each OS matrix
passed 747 tests and skipped the same 5 Arelle-gated cases; the pinned worker
job passed all 11 gated .NET Arelle execution tests.

The full XBR-04 acceptance remains open: typed dimensions and default-member
handling, profile-specific concept/dimension constraints, complete BCP-47
metadata, customer taxonomy qualification, source-drift revalidation, and native
workflow acceptance remain incomplete or gated. This synthetic implementation
does not establish regulator acceptance.
