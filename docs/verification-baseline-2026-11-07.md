# Verification baseline — 2026-11-07

This records verification evidence for shipped XBR-04 and XBR-05 slices on
`main`, including the set-collected text-fact, compound-unit, and profile-aware
XML QName work. It does not qualify a customer taxonomy or filing profile.

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

## Profile-aware XML QName aliases

`XmlSerializer.ReadFacts(xml, profile)` reads XML facts against the selected
profile's namespace map and returns profile-canonical QNames for concepts,
explicit dimension axes and members, and custom unit measures. Prefixes are
treated as aliases: the reader resolves their namespace URI, then maps the
expanded name to the profile's canonical QName. Built-in ISO4217 currency and
XBRLI measure namespaces normalize to their supported local measure names.
An XML namespace that is not declared by the selected profile is rejected
rather than accepted as a fact or silently flattened. The existing
profile-free overload remains available for generic XML import.

Verification:

- `XmlExportTests.ProfileAwareReader_NormalizesXmlQNameAliasesByNamespaceUri`
  verifies alternate prefixes for concepts, explicit dimensions and members,
  currency normalization, and rejection of an undeclared taxonomy namespace.
- The focused profile-aware reader test passed; the XBRL Release suite passed
  106/106.
- The local Release build completed with 0 warnings and errors; all eight .NET
  projects passed 748 tests with 0 failures and 5 environment-gated Arelle
  subprocess skips.
- Exact-head GitHub Actions run
  [42311840089](https://github.com/nirzaf/XBRLMappingStudio/actions/runs/42311840089)
  verified source commit `70ca4c1f8f491ab29fd1a8caccb4dea0f45217be`.
  Repository hygiene, Windows and Ubuntu Release build/test, pinned worker
  integration, dependency inventory, and SPDX generation passed. Both .NET OS
  matrices passed 748 tests and skipped the same 5 gated cases; the pinned
  worker job passed all 11 gated .NET Arelle execution tests.

This improves QName normalization for supported XML imports; it does not prove
full OIM conformance, equivalence against a real filing profile, or regulator
acceptance. Those remain open under XBR-05 and GAT-01/GAT-02.

## Exact staged xBRL-XML publication

The CLI accepts `--output-format xbrl-xml` for an explicit report-file output;
the default remains xBRL-JSON, and the output format is not inferred from the
filename. XML is generated from the successful run's facts, recorded as an
`xbrl-xml` output identity, and staged through the same authorized publication
service. Before replacement, the service verifies strict UTF-8, the artifact
digest, and exact equality with a fresh serialization of the run facts. It
passes a private copy of those exact bytes to the validation worker; the Arelle
worker rehashes that copy, snapshots the selected offline taxonomy closure beside
the private report, and rewrites schemaRef values to relative local files before
loading the XML with network access disabled. This keeps file references valid
on both Windows and POSIX.

Verification:

- Release solution build succeeded with 0 warnings and 0 errors.
- The full Release solution suite passed 750 tests; 6 Arelle-backed tests were
  environment-gated locally, with 0 failures.
- Focused CLI publication and staged-artifact mutation tests passed 2/2.
- The pinned worker job ran 55 Python tests (1 skipped) and passed all 12
  Arelle integration tests on the exact pushed head in
  [CI run 42318170439](https://github.com/nirzaf/XBRLMappingStudio/actions/runs/42318170439).
- Windows, Ubuntu, repository hygiene, and Arelle jobs all passed on that exact
  head. The pinned Windows runtime also passed the focused staged-XML
  publication integration test locally.
- `git diff --check` passed.

Current limits: the CLI XML output path requires an in-memory full run and is
not available for the disk-backed large-run artifact or mixed file/SQLite
publication. Real-profile qualification remains owner-gated and the real
Arelle rule/plugin completion contract remains unavailable.
