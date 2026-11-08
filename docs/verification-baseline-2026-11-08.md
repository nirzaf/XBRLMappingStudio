# Verification baseline — 2026-11-08

This records local evidence for the UX-04 function-authoring slice on `main`.
The guide remains incomplete; this update does not close native accessibility,
broader function signatures, or external profile gates.

## UX-04 / MAP07 function authoring and revision selection

The Functions tab exposes Undo/Redo for the isolated draft graph. Synthetic
preview and intermediate-value evaluation now await the execution plan instead
of synchronously blocking the UI thread. Revision save and package import
validate snapshots before applying changes, so project or draft changes during
the asynchronous validation fail without replacing newer project state.

`FunctionAuthoringWindowSupportsDraftUndoRedoPreviewSaveAndPin` drives the
headless Avalonia UI through adding and editing a Scale operation, Undo/Redo,
synthetic preview, immutable revision save, and pinning the saved revision.
The existing function-editor tests cover revision stability, recursive or
invalid refusal, Ratio/Coalesce previews, package import/export, and preview
limits.

Verification:

- Focused headless UI test passed 1/1.
- `FunctionLibraryEditorTests` passed 10/10.
- Release Desktop test suite passed 106/106 with the isolated `bin-codex/`
  output path.
- Exact-head GitHub Actions run [42418772909](https://github.com/nirzaf/XBRLMappingStudio/actions/runs/42418772909)
  passed repository hygiene, Ubuntu and Windows Release build/tests, and the
  pinned Arelle worker integration for source
  `b336c5157ae52f6b95b55abafb865a6fd814375b`.
- The separate native UI observation could not activate the isolated window
  after one refreshed retry; no controls were changed. The isolated process was
  closed. Native visual layout, screen-reader accessibility, broader function
  signatures/operations, and installed-profile acceptance remain open.

## OPS-01 SQLite cancellation preservation

`SqliteDestinationWorkflowTests.CancelledSqlitePublicationPreservesPriorFactsAndReceipt`
publishes a successful run, captures the existing database bytes, fact rows and
receipt count, then cancels a separate full-run publication before its write
boundary. It verifies cancellation does not alter the database and does not add
a receipt to the cancelled run. Existing tests cover SQLite transaction rollback
on an injected row-write failure and cancellation after staging for all four
file destination formats; exhaustive cancellation/process-crash timing across
all targets remains open.

`SqliteSourceAdapterTests.CancellationAfterFirstSqliteUpsertRollsBackRowsAndReceipt`
uses an internal post-row test hook to cancel after the first upsert has executed
inside the transaction. It confirms the prior database bytes and fact value are
restored, the accepted receipt remains, and no receipt is added for the canceled
operation.

Verification:

- The focused cancellation test passed 1/1.
- The Release Integration suite passed 241 tests and skipped 6 environment-gated
  Arelle cases (247 total).
- The focused mid-transaction cancellation regression passed 1/1, and the full
  Release Adapters suite passed 95/95.
- Exact-head GitHub Actions run [42420628157](https://github.com/nirzaf/XBRLMappingStudio/actions/runs/42420628157)
  passed repository hygiene, Windows and Ubuntu Release build/tests, and the
  pinned Arelle integration on source `df53aa671eafa92eb4c4d6280082ac66699e44d5`.

## ENG-02 set-phase cancellation

Set-phase engine reducers now check the run token inside collection, aggregate,
GroupBy, join, provenance and sink-emission loops. Decimal-domain validation
also checks cancellation while traversing nested collections and records.
`CollectRows_CancellationStopsSetPhaseAndDiscardsPartialOutput` cancels while
the set-phase collection reads its second record and verifies no third record
is read and no fact is returned. Before the implementation change, the same
fixture read all 8 records (24 indexed field reads) before observing cancellation.

Verification:

- The focused cancellation regression passed 1/1.
- `Cancellation_IsReportedForAnEmptyInput` verifies a pre-cancelled run with no loaded rows returns `Cancelled` instead of `Success`.
- The Release Engine suite passed 123/123.
- `LargeRunExecutionTests.GroupByBudgetOverflow_BlocksPublicationAndPreservesPriorFile`
  passed 1/1, confirming an over-budget aggregate still cannot replace the prior
  output.
- Exact-head GitHub Actions run [42424002481](https://github.com/nirzaf/XBRLMappingStudio/actions/runs/42424002481)
  passed repository hygiene, Windows and Ubuntu Release build/tests, and the
  pinned Arelle integration on source `4b0c5503d33171917630c72c078cadeb99c3600c`.
- Large-run set-phase streaming and aggregation spill/recovery remain open.

## ENG-03 invariant RunParameter integer parsing

RunParameter integer values use `NumberStyles.Integer` with
`CultureInfo.InvariantCulture`. `RunParameterIntegerParsing_IsInvariantAcrossCurrentCultures`
sets a custom current-culture negative sign and confirms the invariant `-1234`
parameter still emits the expected fact. Before the fix, the same test failed
because the parameter was silently treated as missing.

Verification:

- The focused regression passed 1/1 after reproducing the failure before the fix.
- The Release Engine suite passed 124/124.
- Exact-head CI [42425497947](https://github.com/nirzaf/XBRLMappingStudio/actions/runs/42425497947)
  passed on Windows and Ubuntu. Each OS ran 867 tests with 6 Arelle-gated skips;
  the pinned job passed 72 Python, 15 .NET Arelle, and 15 dependency-inventory tests.
- `git diff --check` passed. Profile-specific precision and rounding policies
  remain open.

## TAX-07 Arelle relationship extraction

During exact hash-verified profile loading, the worker extracts effective
presentation, calculation and definition relationships from the loaded Arelle
model and compares endpoints and supported arc attributes with the profile's
digest-bound metadata. A mismatch blocks taxonomy load. The pinned fixture uses
a local synthetic linkbase; it does not qualify a permitted real taxonomy.

Verification:

- The pinned Arelle 2.47.4 Python worker suite passed 74 tests with 1 local
  Windows symlink-capability skip; `tools/tests` passed 15/15.
- `py_compile` and `git diff --check` passed.
- Exact-head CI [42428282683](https://github.com/nirzaf/XBRLMappingStudio/actions/runs/42428282683)
  passed on commit `1a96c01de19dc172b75c89ed67bd0e2d7d3ac1d0`. Windows and Ubuntu
  each passed 867 tests and skipped 6 environment-gated Arelle cases; the pinned
  job ran 74 Python worker tests, 15 .NET Arelle execution tests, and 15
  dependency-inventory tests. Repository hygiene and SPDX generation passed.
- Real taxonomy rights and qualification remain owner-gated by GAT-01/GAT-02.

## XBR-04 BCP 47 language aspects

Fact validation checks the well-formed BCP 47 tag structure, including
extensions, private-use tags and the fixed grandfathered-tag forms. It rejects
empty, malformed, duplicate-variant and duplicate-extension tags. JSON
`dimensions.language` and XML `xml:lang` are also syntax-checked on import before
profile selection, so malformed tags cannot enter the report model through a
profile-free read. Supported language values from concept metadata are matched
case-insensitively. The xBRL-JSON importer rejects well-formed but uppercase tags,
as the [xBRL-JSON 1.0 specification](https://www.xbrl.org/Specification/xbrl-json/REC-2021-10-13/xbrl-json-REC-2021-10-13.html)
requires its language dimension to be lowercase. IANA registry membership is not
checked, and exact language support still depends on the selected profile metadata.

Verification:

- The XBRL Release suite passed 132/132, including profile-free JSON/XML import
  rejection cases for malformed `en--US` and `en_US`, uppercase `EN` and `en-US`
  on xBRL-JSON input, valid lowercase tags, and profile-language case matching.
- The Engine Release suite passed 125/125; the malformed-tag FactConstruct case
  blocks the fact and reports the original source location.
- Exact-head CI [42431859845](https://github.com/nirzaf/XBRLMappingStudio/actions/runs/42431859845)
  passed on commit `5bbd94dd86b27d8b374de8d56e326ecce91b55cd`. Windows and Ubuntu
  each passed 886 tests with 6 environment-gated Arelle skips; the pinned job ran
  74 Python worker tests, 15 .NET Arelle execution tests, and 15
  dependency-inventory tests. Repository hygiene and SPDX generation passed.
- Typed dimensions, concept-to-hypercube applicability, registry membership,
  and real-profile language-list qualification remain open.
