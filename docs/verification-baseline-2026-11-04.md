# Verification baseline — 2026-11-04

This is the read-only evidence snapshot captured before implementation of the
pending-features specification.

| Item | Evidence |
|---|---|
| Branch and commit | `main` at `d8ac4afea3e915ce7724962a0bc4bd752fbc62b8` |
| Worktree | Clean before implementation changes |
| .NET test suites | 8 xUnit suites; 288 total test cases |
| Local Windows .NET result | 286 passed, 0 failed, 2 skipped (Arelle subprocess cases) |
| Exact-head Ubuntu CI | 286 passed, 0 failed, 2 skipped |
| Exact-head Windows CI | 286 passed, 0 failed, 2 skipped |
| Dedicated worker job | Python 3.12 with hash-locked requirements; 6 Python tests and 2 .NET Arelle execution tests passed |
| Local pinned worker verification | Python 3.12.11 with `--require-hashes`; all 6 Python tests and both gated .NET Arelle tests passed |
| Build configuration | .NET 10, Release; solution file `XBRLMappingStudio.slnx` |
| Worker test condition | `XMS_ARELLE_E2E=1` and `XMS_ARELLE_PYTHON=python` enable the two gated .NET worker subprocess cases |
| Authoritative implementation guide | `XBRL_Mapping_Studio.md` (read-only) |

The CI evidence is available at
[GitHub Actions run 41892559012](https://github.com/nirzaf/XBRLMappingStudio/actions/runs/41892559012).
It establishes the checks executed by CI on this commit only; it does not
qualify a real filing profile, release package, Windows GUI journey, or
customer-hardware performance target.
