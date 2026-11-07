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
