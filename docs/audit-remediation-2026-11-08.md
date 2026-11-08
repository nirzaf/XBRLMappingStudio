# November 8 completeness-audit remediation

This ledger reconciles the audit of `40cb9bf` with the later baseline `ea274f7`
and the engineering changes in this increment. It is not a production release
certificate. An implemented contract or passing synthetic fixture does not close
the real-taxonomy, installed-Windows, identity-enrollment or customer-data gates.

The owner's subsequent decision selects IFRS Accounting Taxonomy 2025 for
standards-based mapping and validation. The priorities and unresolved rights are
recorded in [OPEN.md](decisions/OPEN.md), preserved from upstream `4a6ab3b`.
Regulator-specific compliance is deferred until a named customer/authority is
confirmed. Local evaluation is conditional on the applicable terms; no taxonomy
bundling, redistribution or commercial distribution is authorized without
documented rights. Synthetic fixtures and production fail-closed gates remain.

## Delivered in this increment

- **P0-01:** The PID-file readiness fix was already delivered by `17fa2db`; baseline
  [CI 42452574577](https://github.com/nirzaf/XBRLMappingStudio/actions/runs/42452574577)
  passed on `ea274f7`. Windows CI now repeats the cancellation regression five
  times in addition to the full test suite on every run.
- **P0-04:** Host-enrolled reviewer keys verify ECDSA P-256 signatures binding the
  project, semantic revision/digest, profile, reviewer, authority and validity
  window. Non-synthetic publication fails closed without this policy. Authority
  is rechecked at file replacement and SQLite commit, including receipt replay.
  Expired, withdrawn, forged or modified approvals cannot authorize publication.
  Explicit synthetic development opt-in retains legacy behavior when no host
  trust policy is supplied. See [the trust contract](trusted-publication-approvals.md).
- **P1-03:** Optional concept numeric policies constrain significant digits,
  permitted decimal grids and the accuracy assertion. Validation does not silently
  round values. Schema 6 binds policies into the profile digest; earlier schemas
  and absent policies preserve legacy behavior. C# and Arelle use shared financial
  fixtures. See [numeric policies](concept-numeric-policies.md).
- **P1-04:** Engine/library/package APIs support named typed multiple inputs and
  outputs, collections, zero-input functions and nested reuse. Pinning a function
  in the desktop copies its actual signature. Invalid boundaries, cycles and
  mismatched pinned contracts fail closed. The visual authoring template remains
  numeric. See [typed function boundaries](decisions/MAP07-typed-function-signatures.md).
- **P1-14:** One CLI lifetime handles Ctrl+C and Unix SIGTERM, propagates cancellation
  to workers and publication, and maps argument, access and operational failures to
  stable exit codes. The [operations runbook](unattended-operations.md) documents
  no-overlap systemd/Windows scheduling, persistent state, reconciliation and safe
  retry limits. This is not an automatic durable job queue or immutable-source replay.
- Build telemetry is explicitly disabled in CI/release and documented for local builds.

## Verification

Release solution build succeeded with zero warnings and zero errors under SDK
10.0.300 on Linux. The locked restore completed with NuGet vulnerability warnings
treated as errors; the direct/transitive vulnerability report returned no findings.
Full-solution Release tests passed **1,016**, with **6** environment-gated Arelle
skips. Those Arelle cases passed in the separate enabled selection below. Exact
pushed-commit Windows CI must still run; local Linux results are not Windows
evidence. Additional checks cover:

- Full Engine suite: 155 passed; full Project suite: 32 passed.
- Full XBRL suite: 166 passed, including shared numeric fixtures; profile-policy
  integration cases: 3 passed.
- CLI regression suite: 48 passed, including actual Linux SIGINT/SIGTERM subprocess
  tests during a pending worker handshake.
- Approval/boundary integration selection: 38 passed; adapter precommit denial
  rollback: 1 passed.
- Desktop function editor selection: 11 passed.
- Pinned Arelle Python worker suite: 77 passed; Arelle-backed .NET selection:
  15 passed. Dependency/performance Python tools: 15 passed.
- Synthetic taxonomy/graph performance fixture self-test passed. This is not
  target-hardware latency acceptance.

## Remaining acceptance ledger

The following items are deliberately open. Do not remove fail-closed restrictions
or represent these rows as completed to make a release appear ready.

| Audit ID | Remaining acceptance |
|---|---|
| P0-01 | Final pushed-commit Windows/Ubuntu/Arelle CI must pass, including repeated Windows cancellation. |
| P0-02 | IFRS Accounting Taxonomy 2025 is selected for standards mapping/validation. Applicable evaluation rights, exact official artifact/hash/dependency closure, offline resolution and permitted financial fixtures remain open. Regulator certification is deferred, not inferred. |
| P0-03 | Real-profile mandatory rule/plugin completion evidence. Existing synthetic evidence and missing-check refusal do not qualify a real authority. |
| P0-04 | Authorized reviewer enrollment/revocation, protected host trust configuration, external signing workflow and CLI/Desktop deployment integration. The application API contract is implemented; production identity governance is not. |
| P0-05 | Clean Windows installer, native/Python dependencies, signing, license clearance and installed upgrade/backup/rollback proof. |
| P1-01 | Multiple large bindings and qualified large-file adapters under declared end-to-end resource budgets. |
| P1-02 | Disk-backed grouping/joins, spill recovery and provenance qualification. |
| P1-03 | Selected IFRS profile precision/rounding/accuracy values and permitted financial acceptance fixtures. The optional policy mechanism is implemented. |
| P1-04 | Generalized visual authoring beyond the numeric-template editor and native accessibility acceptance. Engine/library signatures and desktop pinning are implemented. |
| P1-05 | Remaining derived-record lineage, exact Arelle finding locators, complete input identity and native preview/full-run evidence. |
| P1-06 | Retained immutable database extracts and replay from those extracts. Live SQLite content rechecks are not replay. |
| P1-07 | Typed dimensions, concept-to-hypercube applicability and profile-qualified dimensional validation. |
| P1-08 | Independent JSON/XML conformance fixtures for the selected IFRS taxonomy after the rights/evaluation gate. Existing synthetic equivalence is narrower. |
| P1-09 | Existing-target-row authorization/update semantics. Aspect and occurrence source selection landed before this increment. |
| P1-10 | Approved managed/local model provider, endpoint, secure credential deployment and governed desktop workflow. |
| P1-11 | Permitted held-out financial benchmarks, abstention/quality thresholds, approved model-version rollout and rollback. |
| P1-12 | Native Windows keyboard/mouse/screen-reader journeys. Headless Avalonia tests are not native acceptance. |
| P1-13 | Exhaustive interrupted/ambiguous publication reconciliation for every supported destination. Late approval denial and rollback cases are now covered. |
| P1-14 | Deployment-specific scheduler/recovery drills and any durable queued-job/replay requirement. CLI lifetime and scheduler guidance are implemented. |
| P2-01 | Qualified SQL Server/Azure SQL adapter, provider/authentication decision and live-system fixtures. |
| P2-02 | Qualified PostgreSQL/MySQL/MariaDB/Oracle adapters and provider-specific semantics. |
| P2-03 | Authorized scanned PDFs, chosen renderer/OCR runtime, language-pack rights and OCR review evidence. |
| P2-04 | Separately qualified Excel templates and unsupported XML/JSON constructs. |
| P2-05 | Accepted minimum hardware and measured startup/memory/graph results. |
| P2-06 | Permitted real taxonomy and native UI/validation-under-load measurements. |
| P2-07 | Installed-Windows recovery and clean-install measurements. |
| P2-08 | Dependency redistribution review and installed-release migration/rollback evidence. |
| EXT01–04 | Native XML preservation, iXBRL, taxonomy extension/anchoring and collector package assembly only after the selected filing authority establishes the required scope. |

Selected priorities and unresolved decisions remain in [OPEN.md](decisions/OPEN.md). The original architecture
guide remains unchanged. No real profile is enabled, model credentials enrolled,
release deployed or regulatory sign-off inferred by this increment.
