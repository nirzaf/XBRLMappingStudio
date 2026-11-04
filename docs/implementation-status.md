# Implementation status

This matrix records repository evidence as of 2026-11-04. The exact checkout and
baseline verification are recorded in [`verification-baseline-2026-11-04.md`](verification-baseline-2026-11-04.md).
“Verified” means code and tests in this checkout cover the stated subset; it does not
imply regulator acceptance, production qualification, or a complete end-to-end feature
where explicitly narrowed below.

| Guide IDs | Status | Evidence and limitation |
|---|---|---|
| BASE01 | Partially implemented | `docs/decisions/BASE01-decision-record.md`; external source-data rights remain open. |
| BASE02 | Implemented and verified | `XBRLMappingStudio.slnx`, pinned project dependencies, Release solution build. |
| BASE03 | Implemented and verified | `src/XBRLMappingStudio.Project/ProjectSerializer.cs`, `ProjectManager.cs`, tests in `tests/XBRLMappingStudio.Project.Tests`. |
| BASE04 | Implemented and verified | `src/XBRLMappingStudio.Diagnostics/`; adapter and integration tests. |
| MAP01 | Implemented and verified | `src/XBRLMappingStudio.Engine/MappingGraph.cs`, `PlanCompiler.cs`; engine tests. |
| MAP02 | Implemented and verified for supported graph shapes | `src/XBRLMappingStudio.Engine/GraphValidator.cs` and `EngineTests.cs` cover stable diagnostics for cycles, dangling required ports, collection-to-single cardinality mismatches, and unsupported nodes. |
| MAP03 | Partially implemented | Deterministic plan compilation/execution and cancellation are covered by `tests/XBRLMappingStudio.Engine.Tests/EngineTests.cs` and `tests/XBRLMappingStudio.Integration.Tests/AcceptanceScenarios.cs`; provenance tracking exists in `src/XBRLMappingStudio.Engine/ProvenanceTracker.cs`, but broader qualification and connector coverage remain open under IO07–IO12. |
| MAP04 | Partially implemented | Scalar and conditional functions exist in `src/XBRLMappingStudio.Engine/`; numeric support is bounded by CLR `decimal` and does not yet qualify a profile-specific precision domain. |
| MAP05 | Partially implemented | `CollectionAndJoinTests.cs` covers collection aggregation and group-by; guide-level memory/spill budgets and failure qualification remain incomplete. |
| MAP06 | Partially implemented | `CollectionAndJoinTests.cs` covers keyed joins and lookup outcomes; only the implemented cardinality and duplicate policies are supported. |
| MAP07 | Partially implemented | Project reusable function metadata exists; graphical authoring and full reuse lifecycle are incomplete. |
| MAP08 | Partially implemented | `PreviewReconciliationTests.cs` verifies an independent full-run oracle and labels previews as partial; interactive drill-down to all contributing records is incomplete. |
| IO01 | Partially implemented | `src/XBRLMappingStudio.Adapters/ISourceAdapter.cs`, `MappingJobService.cs`; adapter capability contract and SQLite shared-service execution verified, but UI connector coverage remains partial. |
| IO02 | Implemented and verified for supported XLSX input subset | `src/XBRLMappingStudio.Adapters/Excel/` and focused Excel source tests under `tests/XBRLMappingStudio.Adapters.Tests/`. |
| IO03 | Implemented and verified for generated XLSX output | `ExcelOutputAdapter` stages, reopens and reconciles output; tests verify row counts and preservation of prior output on failure. Template-copy behavior is not qualified. |
| IO04 | Implemented and verified for the supported CSV subset | `CsvAdapter` and tests cover quoted newlines, embedded quotes, trailing empty fields, BOM policy and formula-injection handling; broader profile-specific encoding/culture qualification remains. |
| IO05 | Implemented for supported shapes | `JsonAdapter` reads bounded top-level objects/arrays and rejects duplicate keys or out-of-budget values. `JsonOutputAdapter` stages, reopens and reconciles output; nested arrays, empty arrays, `null` and missing fields round-trip byte-deterministically. Exotic JSON beyond the 28-digit budget is refused rather than narrowed silently. |
| IO06 | Implemented for supported shapes | `XmlAdapter` provides bounded, namespace-aware input and blocks DTD/external entities. `XmlOutputAdapter` preserves two same-local-name elements under distinct namespaces, refuses mixed-content markup with `IO011`, and stages/publishes with row-count reconciliation. Mixed content is declared unsupported until a preservation fixture is authored. |
| IO07 | Partially implemented | SQLite schema inspection exposes declared primary-key order; row provenance uses a SHA-256 identity derived from single or composite primary keys. Tables without a primary key keep positional IDs and emit IO015 plus a manifest strategy marker. Snapshot/immutable-extract semantics, production-family qualification and live-database evidence remain open. |
| IO08 | Partially implemented | `SqliteTargetPublisher` provides transactional upsert and intent-bound receipts with tests; end-to-end desktop/CLI integration and production qualification remain incomplete. |
| IO09 | Missing | No qualified SQL Server, PostgreSQL, MySQL/MariaDB, or Oracle adapter. |
| IO10 | Missing | No PDF extraction adapter or evidence review workflow. |
| IO11 | Blocked by native/runtime qualification | No PDF renderer or OCR path is implemented or qualified. |
| IO12 | Missing | No PDF evidence retention, reviewer decision, and extraction-to-mapping lifecycle. |
| XBR01 | Partially implemented | `SyntheticProfile` and `FilingProfile` record development profile metadata. `MappingJobService` and `PublicationGate` reject XBR015 profile ID/digest mismatches before graph compilation or source reads; normal CLI/Desktop profile resolution still uses the synthetic profile, and no permissible customer taxonomy/report package is approved. |
| XBR02 | Partially implemented | Versioned worker handshake and pinned Python worker tests exist; standalone Arelle validation is available, while profile-driven Arelle mapping execution is not complete. |
| XBR03 | Partially implemented | `TaxonomyIndex` supports paged synthetic concept, label and relationship search; indexing a permitted production taxonomy at scale is not qualified. |
| XBR04 | Partially implemented | Typed fact construction and source provenance exist for supported facts; real filing-profile aspects and source-linked rejection rules are not qualified. |
| XBR05 | Implemented for supported constructs | `OimSerializer`, XBRL serialization tests and checked-in positive/negative fixtures; full OIM conformance remains open. |
| XBR06 | Partially implemented | `XbrlReportAdapter` imports supported JSON/XML report facts as records and preserves repeated facts; reverse mappings and the complete report construct set remain incomplete. |
| XBR07 | Implemented for the synthetic profile | `FilingRuleValidator` records a completion status per rule; a required check that cannot be evaluated (missing dimension domain) produces an incomplete diagnostic and `RunOutcome.IncompleteConfiguration`. The profile now declares `RequiredChecks`; a rule that never produces an outcome is appended as `Incomplete` with `XBR014` so an empty fact set can no longer resolve to a silent success. Required plugin/check completeness for a real profile and full desktop execution validation remain scoped to XBR09. |
| XBR08 | Implemented for supported constructs | `ConversionResilienceTests.cs` covers cancellation, corrupted intermediates and retry equivalence. `MappingJobService` now distinguishes `WorkerStatus.TimedOut` and `WorkerStatus.Crash` from cancellation and emits a blocking `WKR004`/`WKR003` diagnostic in the run manifest, so a hung worker yields `OperationalFailure` rather than a silent success. Real-profile worker timeout qualification remains at XBR09. |
| XBR09 | Blocked by release dependencies | Signed Windows packaging, clean-machine Windows acceptance, licensing and filing-profile sign-off require external credentials, hardware, rights and reviewer evidence. |
| AI01 | Implemented and verified for offline mode | `src/XBRLMappingStudio.AI/AiPolicy.cs`; offline tests assert no outbound provider calls. |
| AI02 | Implemented and verified for local synthetic index | `LocalRetrievalRanker.cs` and deterministic retrieval tests; real taxonomy scale not qualified. |
| AI03 | Partially implemented | `src/XBRLMappingStudio.AI/Providers.cs` bridges `Microsoft.Extensions.AI`; provider configuration and production qualification remain incomplete. |
| AI04 | Partially implemented | `DraftValidator.cs` plus project digest/source identity checks; no full profile-aware model workflow. |
| AI05 | Partially implemented | Desktop offline suggestions and undo tests exist; batch review and complete graph workflow remain incomplete. |
| AI06 | Implemented for the offline baseline | Audit types exist and every `SuggestionSession` decision (accept, edit, reject, batch-accept, undo) now appends an `AuditRecord` that flows through the optional `IAuditSink`. `SuggestionAuditStore` in `XBRLMappingStudio.Application` persists the record to the versioned `suggestion_audit` table with governed actor, method, prompt-template revision, bounded original/approved payloads and the provider-snapshot limitation disclosure; migration from v1 preserves historical rows. Real multi-user reviewer authority and privacy evaluation remain scoped to AI07–AI08. |
| AI07 | Missing | No independently adjudicated held-out dataset or measured evaluation. |
| AI08 | Missing | No controlled model-backed rollout and deployment configuration lifecycle. |
| UI01 | Partially implemented | Avalonia desktop shell and project open/save/validate workflows exist; complete lifecycle is not yet available. |
| UI02 | Partially implemented | `DesignerSession.cs` provides graph edits, selection, undo/redo; scale and full port interaction qualification remain open. |
| UI03 | Partially implemented | Taxonomy explorer exists against synthetic profile; real taxonomy/package search absent. |
| UI04 | Partially implemented | The desktop exposes source selection, bounded preview and basic diagnostic/source navigation; the complete source, preview and property-pane workflow is not qualified. |
| UI05 | Partially implemented | Accessible names and keyboard commands exist; Windows Narrator and native input certification not run. |
| UI06 | Partially implemented | Local AI suggestion review exists; full edit/reject/batch review and persistent audit trail are incomplete. |
| UI07 | Missing | No signed/self-contained Windows installer or clean-machine installation verification. |
| OPS01 | Implemented and verified for supported inputs | Shared `MappingJobService`, CLI commands and parity tests in `tests/XBRLMappingStudio.Integration.Tests/`. |
| OPS02 | Implemented and verified for file publication | `DestinationLock.cs`, SQLite-backed run journal, publication and recovery tests. |
| OPS03 | Partially implemented | Worker process supervision and cancellation exist; no full desktop Arelle lifecycle. |
| OPS04 | Partially implemented | `PublicationGate.cs` and path checks exist; centralized authorization and immutable trusted reviewer authority remain incomplete. |
| OPS05 | Partially implemented | SQLite publication receipts and intent-bound idempotency are tested; mixed-destination reconciliation after an ambiguous database/file outcome is not implemented. |
| OPS06 | Partially implemented | NuGet and worker dependencies are pinned, but locked self-contained release bundles and per-target dependency/license inventories are absent. |
| OPS07 | Partially implemented | Versioned SQLite migrations and non-overwriting backups are tested; installed-release upgrade, compatible project restore and rollback are not qualified. |
| OPS08 | Partially implemented | Structured diagnostics and redacted support bundles exist; scheduler integration and unattended recovery qualification are absent. |
| PERF01 | Missing | No cold/warm startup measurements. |
| PERF02 | Missing | No idle/peak resource measurements. |
| PERF03 | Missing | No graph scale benchmark at 100–2,000 nodes. |
| PERF04 | Missing | No real taxonomy benchmark at 20,000 concepts. |
| PERF05 | Missing | No interaction latency measurements. |
| PERF06 | Missing | No background validation responsiveness benchmark. |
| PERF07 | Missing | No Windows recovery and clean-install qualification. |
| EXT01 | Missing pending profile selection | No native-XML preservation path for a named OIM-incompatible filing profile. |
| EXT02 | Missing pending profile selection | No XHTML authoring/tagging workflow or regulator transformation rules. |
| EXT03 | Missing pending profile selection | No governed extension taxonomy authoring. |
| EXT04 | Missing pending profile selection | No collector-specific report package assembler. |

## Baseline verification before pending-feature implementation

- Baseline checkout: `d8ac4afea3e915ce7724962a0bc4bd752fbc62b8` (`main`, 2026-11-04); worktree clean before implementation began.
- Local Windows `dotnet test XBRLMappingStudio.slnx -c Release` — 286 passed, 0 failed, 2 skipped (the gated Arelle subprocess cases), 288 total. Suite totals: AI.Tests 47, Adapters.Tests 47, XBRL.Tests 49, Ops.Tests 47, Engine.Tests 33, Desktop.Tests 19, Integration.Tests 40 (38 passed, 2 skipped), Project.Tests 6.
- Exact-head GitHub CI run [41892559012](https://github.com/nirzaf/XBRLMappingStudio/actions/runs/41892559012) on `d8ac4af`: Ubuntu and Windows each reported 286 passed, 0 failed, 2 skipped. The separate pinned Arelle job passed 6 Python worker tests and 2 .NET Arelle execution tests.
- Local Python 3.12.11 virtual environment installed `tools/worker/requirements.lock` with `--require-hashes`; all 6 Python worker tests and both gated .NET Arelle tests passed.
- Wave 1 additions: FilingRuleValidatorTests (+2 required-check completeness), WorkerAdjudicationTests (+3 timeout/crash/deadline), AuditPersistenceTests (+4 AI06 sink), SuggestionAuditStoreTests (+5 SQLite persistence + v1→v2 migration), AdapterCapabilitySurfacingTests (+9 IO01 gate).
- Wave 2 additions: JsonAndXmlOutputAdapterTests (+7 covering IO05/IO06 staging, round-trip, mixed-content refusal, mid-write fault).
- Local SQLite v2 schema migration adds six columns to `suggestion_audit` (method, actor, prompt_template_revision, original_proposal_json, approved_edit_json, provider_snapshot_limitation) with historical v1 rows preserved.

The pending-features specification is being implemented on `main` in bounded groups. External decisions (filing authority, taxonomy and PDF rights, provider policy, hardware/signing and production database vendor) remain open; this status file will be updated only with evidence from the resulting changes and CI.
