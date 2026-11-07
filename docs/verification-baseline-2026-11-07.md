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

## TAX-02 mapped-import network canary

The pinned-Arelle worker fixture now declares an external schema URI on a
loopback HTTP canary and maps that exact URI to a hash-pinned local package
file. The canary records every parsed HTTP method and path. Arelle completed
the taxonomy load with the expected concepts and made no HTTP request, showing
that this mapped import used the local file. This fixture does not establish
OS-level no-egress isolation or qualify unmapped/other external references.

Verification:

- The local worker suite ran 56 tests: 47 passed and 9 were skipped because
  this Windows environment lacks Arelle/lxml or symlink privileges.
- Exact-head CI [run 42329883479](https://github.com/nirzaf/XBRLMappingStudio/actions/runs/42329883479)
  passed repository hygiene, both Windows and Ubuntu build/test matrices, and
  pinned Arelle integration on commit `c421bd765dff256b901e7cee9763d17849d18617`.
  The pinned worker job ran all 56 Python tests and all 12 gated .NET Arelle
  execution tests.
- Arelle emitted a non-failing `ResourceWarning` for its temporary offline
  catalog ZIP stream; the taxonomy test and cleanup succeeded.

## XBR-03 streamed large xBRL-XML slice

The qualified large-run path now accepts `xbrl-xml` as an explicit output kind. It streams a byte-capped XML instance from the private SQLite fact spool, emits contexts before units and facts as required by the instance structure, and uses one context/unit pair per fact to avoid row-count-sized deduplication state. The resulting artifact kind, digest, byte count and path are carried through CLI publication. The validation worker receives a private copy whose digest is checked against the generated artifact; publication rechecks the exact staged bytes before replacement. The new integration case compares the XML round-trip semantic fact multiset with the small-mode output and publishes the validated large artifact.

Verification:

- The complete Release `LargeRunExecutionTests` class passed 21/21, including existing 100,000-row JSON streaming coverage and the new large XML publication case.
- The Release XBRL test project passed 110/110, including the new streamed XML byte-budget test.
- The full Release solution suite passed 823 tests, skipped 6 environment-gated Arelle tests, and had 0 failures after restoring all solution test projects.
- dotnet build XBRLMappingStudio.slnx -c Release --no-restore -m:1 succeeded with 0 warnings and 0 errors.

Limits: the XML semantic-parity publication case uses two facts under forced large mode. It does not qualify 100,000-row XML memory/throughput, Arelle's report-model memory, real filing-profile acceptance, multi-source or set-phase large runs, or desktop-native XML workflows. One context/unit per fact can increase output size; the configured report-byte cap remains enforced.

## TAX-03 explicit legacy profile adoption

`migrate-project-profile` accepts a legacy `.xmap` only when one or both profile
identity fields are missing and the caller supplies the exact profile ID and
digest. It validates the selected identity through the installed-profile
resolver, writes a create-only independent project copy, changes no source
bytes, and resets project identity and approvals. Synthetic adoption requires
`--allow-synthetic-profile`; unapproved real profiles continue to fail under
GAT-01/GAT-02. Unsupported project fields, duplicate identity keys, conflicting
existing identity values, same-path output and existing output files are
refused. No local SQLite cache is used to substitute profile identity or
package hashes.

Verification:

- CLI Release build succeeded with 0 warnings and 0 errors.
- Project Release tests passed 24/24, including missing and partial identity,
  approval reset, independent-copy preservation, duplicate keys, conflicting
  profile identity, and unsupported-field refusal.
- The two CLI subprocess adoption tests passed 2/2. They verify the synthetic
  opt-in gate, exact selected identity, unchanged source bytes, no same-path
  replacement, and preservation of an existing destination.
- The final Release Desktop suite passed 103/103, including independent-copy
  creation, synthetic opt-in refusal, source-change refusal, and the normal-open
  migration hint.
- The final Release solution suite passed 845 tests, skipped 6 environment-gated
  Arelle tests, and had no failures.
- The follow-up typed-error regression passed 5/5 across Project and Desktop
  tests: missing identity is typed, malformed JSON and wrongly typed identity
  are not, and the recovery hint still appears for a legacy project.
- Exact-head CI run [42397385532](https://github.com/nirzaf/XBRLMappingStudio/actions/runs/42397385532)
  passed the Windows and Ubuntu Release matrices, Arelle worker integration, and
  repository hygiene on commit `c306cb9`.
- Test build output was isolated under a temporary directory because another
  Desktop process held the normal Release output files. That process was left
  untouched.
- The native UI helper found the isolated QA window but failed to activate it
  twice (`failed to activate captured window`); no UI interactions were made.
  Native visual acceptance remains unverified; this is an automation limitation,
  not an observed application defect.
- `git diff --check` passed before commit.

The ordinary project loader still refuses unpinned legacy files; the explicit
CLI and Desktop adoption paths require a caller-selected profile and do not
infer identity. The Desktop File menu links failed legacy opens to `Adopt Legacy
Profile`; the chooser displays exact profile ID, taxonomy version and digest,
and the separate confirmation names the destination and cleared approvals. The
workflow uses a create-only Save As copy, preserves the source, blocks synthetic
adoption until explicit opt-in, and rejects a source that changed after profile
selection. Headless Desktop tests cover the success, synthetic opt-in, stale
source, and normal-open recovery-message paths. The final local Release solution
suite passed 845 tests and skipped 6 environment-gated Arelle cases. The native
UI helper could not activate the isolated app window after a refreshed retry;
no UI interaction occurred, so visual native acceptance remains unverified.
Real-profile adoption remains owner-gated.

## XBR-04 declared dimension validation

The XBRL Dimensions specification says a default dimension member is inferred when
the dimension is omitted and MUST NOT be reported explicitly in an instance
([Dimensions 1.0, Sections 3.1.4.1–3.1.4.2](https://www.xbrl.org/specification/dimensions/per-2011-11-20/dimensions-per-2011-11-20.html)).
`FilingRuleValidator` now applies that rule from the selected profile's
`dimension-default` definition arcs, validates explicit members against the
declared domain for that exact axis, and includes source evidence in dimension
failure diagnostics. Missing axis-specific profile metadata resolves to an
incomplete validation instead of accepting a member based on a different axis.
The XBRL suite covers explicit default-member rejection, implicit default by
omission, per-axis domain isolation, and source-linked diagnostics.

The focused XBRL Release suite passed 114/114. The full Release solution suite
passed 851 tests, skipped 6 environment-gated Arelle tests, and had no failures;
build artifacts were isolated under a temporary path because an open Desktop
process held the normal Release output assemblies. `git diff --check` passed.
These results do not establish customer taxonomy or regulator
qualification; typed dimensions, concept-to-hypercube applicability, complete
BCP-47 metadata, and owner gates remain open.

## OPS-01 SQLite worker-failure publication isolation

`SqliteDestinationWorkflowTests.WorkerFailure_CannotReplaceExistingSqliteFactsOrReceipt`
first publishes one accepted synthetic fact and receipt, then injects a worker crash
during validation of the next run. The publication is rejected before target mutation;
the test verifies prior database bytes, fact rows, and receipt count are unchanged, and
that the failed manifest contains no database receipt. The focused Windows Release
Integration test passed 1/1.

The first follow-up CI run [42410718767](https://github.com/nirzaf/XBRLMappingStudio/actions/runs/42410718767)
exposed a Windows-only timing failure in `Cli_MixedProcessLossAfterXmlConfirmationReconcilesAndRetriesWithoutDuplicateRows`:
the test stopped polling after four seconds before observing the durable operation key.
The implementation was not implicated. The two process-loss tests now allow 15 seconds
for child-process startup and include captured stdout/stderr if the expected durable state
is still absent. The focused Windows tests passed 2/2 locally.

Exact-head CI [42411292675](https://github.com/nirzaf/XBRLMappingStudio/actions/runs/42411292675)
verified commit `3c43a9bd25a64c6a56584a40905a5c19119c8750`: repository hygiene,
Windows and Ubuntu Release build/test, pinned Arelle worker integration, dependency
inventory tests, and SPDX generation passed. Each OS matrix passed 859 .NET tests,
skipped 6 environment-gated Arelle tests, and had no failures. The pinned job passed
72 Python worker tests, 15 gated .NET Arelle execution tests, and 15 dependency
inventory tests. This verifies the synthetic-profile failure-isolation path only;
full cancellation/process-crash coverage across all targets and owner-authorized
real-profile publication remain open.

## UX-07 bounded fact-result paging

The desktop now presents facts in 500-row pages from either the in-memory result
or the large-run SQLite spool. `JobResult.TransferLargeArtifactOwnership` keeps
the spool alive for later page reads while leaving it accessible to publication;
the viewmodel releases it when replacing/clearing results or closing the window.
The result panel exposes named Previous/Next buttons, disabled state, and a range
such as `Facts 1–500 of 501`.

The focused Avalonia headless paging test passed 2/2 for in-memory and forced
spool-backed runs. It observes the initial page controls, verifies their accessible
names and disabled state, runs 501 facts, advances to row 501, then returns to row
1. The focused artifact transfer/page test passed 1/1. The full Desktop suite
passed 105/105. The full Integration suite passed 240 tests and skipped 6
environment-gated Arelle cases. Builds used isolated `bin-codex` output because
the checked-out Desktop Debug assemblies were held open by an existing app process.
Native visual and screen-reader acceptance remain open.
