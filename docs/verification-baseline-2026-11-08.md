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
- Native visual layout, screen-reader accessibility, broader function
  signatures/operations, and installed-profile acceptance remain open.
