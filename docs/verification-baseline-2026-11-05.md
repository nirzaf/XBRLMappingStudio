# Verification baseline — 2026-11-05

Read-only snapshot after the UX-03 graph-editor implementation. This records the
checkout and evidence that were inspected; it does not update or replace the
2026-11-04 pre-implementation baseline.

| Item | Evidence |
|---|---|
| Branch and local commit | `main` at `0f0e9be2987023aa1e746280b217e43aff4e9a90` |
| Configured remote head | `origin/main` at `c68cc816f8acd157a1afc3080ccd4d06c3b029c2`; local `main` was three commits ahead |
| Worktree | Clean when captured; no prior changes were discarded |
| .NET solution and test projects | `XBRLMappingStudio.slnx`; 8 xUnit projects |
| Local Release test result | `dotnet test XBRLMappingStudio.slnx -c Release --no-restore -m:1`: 360 passed, 0 failed, 2 skipped, 362 total |
| Skipped tests | `ArelleExecutionTests.ExcelToXbrlMappingIsAdjudicatedByOfflineArelleWorker` and `ArelleExecutionTests.CliPreviewUsesArelleToValidateExcelMappingOutput`; both require the gated Arelle environment |
| Worker test gate | `.github/workflows/ci.yml` separately installs Python 3.12 dependencies from `tools/worker/requirements.lock` with `--require-hashes`, runs `python -m unittest discover -s tools/worker/tests -v`, then enables the two .NET cases with `XMS_ARELLE_E2E=1` and `XMS_ARELLE_PYTHON=python` |
| Current pushed-head CI | Workflow run [41937172454](https://github.com/nirzaf/XBRLMappingStudio/actions/runs/41937172454) targets `c68cc816f8acd157a1afc3080ccd4d06c3b029c2`; Windows, Ubuntu, repository hygiene and the pinned worker job all completed successfully. The archived Windows and Ubuntu TRX artifacts were present. |
| Build configuration | .NET SDK `10.0.401`, target framework `net10.0`, Release; pinned worker job uses Python 3.12 |
| Authoritative repository guide | `XBRL_Mapping_Studio.md` remains read-only |
| Active user-supplied work specification | `XBRLMappingStudio_Pending_Features_Atomic_Implementation_Spec_2026-11-04.md` in the user's Downloads folder |
| Push state | An ordinary `git push origin main` was rejected by GitHub with `fatal error in commit_refs`; local commits are preserved and no history rewrite or protection bypass was attempted |

The local Release run exercised the current checkout on Windows. The workflow run
is evidence for its exact remote SHA only; it does not qualify the current local
commits or establish real filing-profile, customer-data rights, signed-release,
accessibility-certification or customer-hardware performance acceptance.

## Follow-up verification for profile worker preflight

This follow-up records the local `main` head after the TAX-05 worker handshake and
taxonomy preflight change. It supplements the earlier snapshot above.

| Item | Evidence |
|---|---|
| Local commit | `c37d9337111fe6df741401e36cfe747bb0a43a7d` |
| Remote head after push attempt | Still `c68cc816f8acd157a1afc3080ccd4d06c3b029c2`; ordinary push rejected with `fatal error in commit_refs` |
| Local Release test result | 375 passed, 0 failed, 2 gated Arelle subprocess tests skipped (377 total) |
| Worker unit tests | Python 3.14.8: 15 passed; four Arelle-runtime tests skipped because `arelle-release` is unavailable, one symlink test skipped because Windows denied symlink creation |
| Focused profile/worker tests | 29 passed, 0 failed |
| Static and whitespace checks | `ruff check --select E4,E7,E9,F` and `git diff --check` passed |

The Python suite ran protocol, profile metadata, offline path/hash, plugin-option
and taxonomy-load fixtures. The pinned Python 3.12/Arelle integration suite was
not available in this local environment, so its runtime cases remain pending CI.

## Follow-up verification for the offline suggestion review workflow

| Item | Evidence |
|---|---|
| Base checkout | `main` at `d1a6048a575b9b57f7d3460690465f32eb24ac2c`; `origin/main` at `c68cc816f8acd157a1afc3080ccd4d06c3b029c2`; seven local commits were ahead before this worktree change. |
| Local Release solution run | `dotnet test XBRLMappingStudio.slnx -c Release --no-restore -m:1 --verbosity minimal`: 380 passed, 0 failed, 2 skipped (382 total). Suite totals: AI 47, Project 8, Engine 33, Ops 51, Adapters 55, XBRL 49, Desktop 45, Integration 92 passed plus 2 skipped. |
| Skipped tests | `ArelleExecutionTests.ExcelToXbrlMappingIsAdjudicatedByOfflineArelleWorker` and `ArelleExecutionTests.CliPreviewUsesArelleToValidateExcelMappingOutput`; both require the gated Arelle environment. |
| Focused graph-widget run | `GraphCanvasWidgetTests`: 5 passed, including node selection after zoom and pan using the inverse of the rendered graph transform. |
| Native Windows smoke | Opened the saved CSV/mapping fixture, verified the `Revenue` source field and offline `synthetic:Revenue` pending candidate, and confirmed the `XBRL Revenue` fact node updates Properties after graph pan (unit `USD`, period `duration`, decimals `0`). Candidate status stated no source data was sent over the network. No accept/reject action was made in the smoke app. |
| Implementation evidence | Desktop suggestion edit/reject/accept/undo/redo now uses `SuggestionSession` with durable local audit persistence and fail-closed rollback. View-bound changing collections notify the desktop, and graph selection synchronizes the selected fact context. |
| Remaining gates | The local Arelle runtime, real filing-profile approval, organizational reviewer authority, batch suggestion workflow, large-graph performance and Narrator qualification remain open. |

The full solution run verifies the local Windows checkout only. It does not
qualify a production filing profile or replace the gated worker CI job.

## Previous exact-head baseline after Arelle path correction

This records the pushed code baseline before the staged-publication slice; the
preceding entries remain historical snapshots of their respective commits.

| Item | Evidence |
|---|---|
| Branch and worktree | `main`, clean; `HEAD`, `origin/main`, and remote `refs/heads/main` all equal `c1557d3b46208bff7e1db00088afaebd43dfab45` |
| Exact-head CI | [Run 41971061473](https://github.com/nirzaf/XBRLMappingStudio/actions/runs/41971061473), completed successfully on the exact SHA |
| Release builds | Ubuntu and Windows Release builds passed with 0 warnings and 0 errors; repository hygiene passed |
| .NET test matrix | Each OS passed 380 tests and skipped 2 gated Arelle tests (382 total); eight suites: AI 47, Project 8, Engine 33, Ops 51, Adapters 55, XBRL 49, Desktop 45, Integration 92 passed plus 2 skipped |
| Pinned worker job | All 20 Python worker tests and both gated .NET Arelle integration tests passed |
| Local regression reproduction | Pinned Python 3.12 worker suite passed 19 tests; the symlink test was skipped because Windows denied symlink creation. Both gated .NET Arelle integration tests passed locally. |
| Root cause corrected | The worker resolved a relative taxonomy entry point against the process working directory instead of the configured taxonomy root. Relative entries are now joined to the root before canonicalization and containment checks; the integration assertions include failure diagnostics. |

These checks verify the supported synthetic-profile worker path and the repository
test matrix. They do not close the external filing-authority/taxonomy-rights,
reviewer-authority, Windows signing/hardware, PDF/OCR-rights or production database
gates recorded in `docs/decisions/OPEN.md`.

## Exact-head staged-publication verification

| Item | Evidence |
|---|---|
| Verified implementation commit | `main` at `6f6bf6d08aa2665fedebb22a3ffb8fdff258487a`; `origin/main` matched and the worktree was clean after push |
| Exact-head CI | [Run 41976559279](https://github.com/nirzaf/XBRLMappingStudio/actions/runs/41976559279) completed successfully on the verified commit |
| Release builds and hygiene | Ubuntu and Windows Release builds passed; repository hygiene passed and `XBRL_Mapping_Studio.md` is unchanged |
| .NET test matrix | Ubuntu and Windows each passed 388 tests and skipped 3 gated Arelle cases (391 total) across 8 projects |
| Pinned worker job | All 20 Python tests and all 3 gated .NET Arelle integration cases passed |
| Local pinned worker run | 19 passed; Windows symlink-privilege case skipped; all 3 gated .NET Arelle tests passed |
| Scope | Exact staged xBRL-JSON validation, digest rechecks, terminal abort/recovery, durable validated-digest columns, CLI/Desktop publication, and README/status reconciliation |
