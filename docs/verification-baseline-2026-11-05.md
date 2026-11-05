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
