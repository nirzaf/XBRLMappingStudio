# Implementation status

This matrix records repository evidence as of 2026-11-04. “Verified” means code and tests in this checkout cover the stated subset; it does not imply regulator acceptance, production qualification, or a complete end-to-end feature where explicitly narrowed below. The authoritative architecture guide is unchanged.

| Guide IDs | Status | Evidence and limitation |
|---|---|---|
| BASE01 | Partially implemented | `docs/decisions/BASE01-decision-record.md`; external source-data rights remain open. |
| BASE02 | Implemented and verified | `XBRLMappingStudio.slnx`, pinned project dependencies, Release solution build. |
| BASE03 | Implemented and verified | `src/XBRLMappingStudio.Project/ProjectSerializer.cs`, `ProjectManager.cs`, tests in `tests/XBRLMappingStudio.Project.Tests`. |
| BASE04 | Implemented and verified | `src/XBRLMappingStudio.Diagnostics/`; adapter and integration tests. |
| MAP01 | Implemented and verified | `src/XBRLMappingStudio.Engine/MappingGraph.cs`, `PlanCompiler.cs`; engine tests. |
| MAP02 | Implemented and verified | `src/XBRLMappingStudio.Engine/` function and collection execution; engine tests. |
| MAP03 | Partially implemented | Deterministic plan compilation/execution and cancellation are covered by `tests/XBRLMappingStudio.Engine.Tests/EngineTests.cs` and `tests/XBRLMappingStudio.Integration.Tests/AcceptanceScenarios.cs`; provenance tracking exists in `src/XBRLMappingStudio.Engine/ProvenanceTracker.cs`, but broader qualification and connector coverage remain open under IO07–IO12. |
| MAP04 | Partially implemented | `src/XBRLMappingStudio.Engine/` and `src/XBRLMappingStudio.XBRL/`; exact decimal domain is CLR decimal, not the full guide domain. |
| MAP05 | Partially implemented | Graph validation and deterministic execution exist; remaining constructs/profile constraints are not fully qualified. |
| MAP06 | Partially implemented | `src/XBRLMappingStudio.Engine/ProvenanceTracker.cs`; supported record/fact lineage only. |
| MAP07 | Partially implemented | Project reusable function metadata exists; graphical authoring and full reuse lifecycle are incomplete. |
| MAP08 | Implemented and verified | Bounded preview and partial totals in `src/XBRLMappingStudio.Application/MappingJobService.cs`; integration tests. |
| IO01 | Partially implemented | `src/XBRLMappingStudio.Adapters/ISourceAdapter.cs`, `MappingJobService.cs`; adapter capability contract and SQLite shared-service execution verified, but UI connector coverage remains partial. |
| IO02 | Implemented and verified for supported XLSX input subset | `src/XBRLMappingStudio.Adapters/Excel/` and focused Excel source tests under `tests/XBRLMappingStudio.Adapters.Tests/`. |
| IO03 | Partially implemented | `src/XBRLMappingStudio.Adapters/`; schema fingerprints and drift checks; not every format has a fully qualified round-trip. |
| IO04 | Partially implemented | Excel/CSV exports exist; report output goes through staged publication; target capability matrix is incomplete. |
| IO05 | Implemented and verified for file outputs | `src/XBRLMappingStudio.Application/PublicationService.cs`, file target adapters and publication tests. |
| IO06 | Partially implemented | SQLite source discovery/read and target upsert implemented; target publisher is not wired into generic desktop/CLI execution. |
| IO07 | Partially implemented | SQLite read/discovery/preview code exists; SQLite integration tests pass. SQL Server/PostgreSQL/MySQL/Oracle live qualification is missing. |
| IO08 | Partially implemented | Transactional SQLite upsert and intent-bound receipt tests; no CLI/desktop lifecycle integration or external DB qualification. |
| IO09 | Missing | No qualified SQL Server, PostgreSQL, MySQL/MariaDB, or Oracle adapter. |
| IO10 | Missing | No PDF extraction adapter or evidence review workflow. |
| IO11 | Blocked by native/runtime qualification | No PDF renderer or OCR path is implemented or qualified. |
| IO12 | Missing | No PDF evidence retention, reviewer decision, and extraction-to-mapping lifecycle. |
| XBR01 | Implemented and verified for supported subset | `src/XBRLMappingStudio.XBRL/OimSerializer.cs`; pinned Arelle worker fixtures and serializer tests pass; full OIM conformance remains open. |
| XBR02 | Partially implemented | Local taxonomy index and profile metadata exist; no authorized production taxonomy package is included. |
| XBR03 | Partially implemented | `src/XBRLMappingStudio.XBRL/TaxonomyIndex.cs`; synthetic profile search is implemented, real large taxonomy indexing is not. |
| XBR04 | Partially implemented | Fact construction and source provenance exist; no real filing-profile qualification. |
| XBR05 | Implemented for supported constructs | `OimSerializer`, XBRL serialization tests and checked-in positive/negative fixtures; full OIM conformance remains open. |
| XBR06 | Partially implemented | `tools/worker/arelle_worker.py` and worker client exist; CLI artifact validation supports Arelle, but full profile-driven execution integration is incomplete. |
| XBR07 | Partially implemented | Fail-closed validation/publication gates exist; required plugin/check completeness and full desktop execution validation remain incomplete. |
| XBR08 | Partially implemented | `src/XBRLMappingStudio.XBRL/XmlSerializer.cs` supports a bounded path; broader OIM-to-XML and package handling are not qualified. |
| XBR09 | Blocked by release dependencies | Signed Windows packaging, clean-machine Windows acceptance, licensing and filing-profile sign-off require external credentials, hardware, rights and reviewer evidence. |
| AI01 | Implemented and verified for offline mode | `src/XBRLMappingStudio.AI/AiPolicy.cs`; offline tests assert no outbound provider calls. |
| AI02 | Implemented and verified for local synthetic index | `LocalRetrievalRanker.cs` and deterministic retrieval tests; real taxonomy scale not qualified. |
| AI03 | Partially implemented | `src/XBRLMappingStudio.AI/Providers.cs` bridges `Microsoft.Extensions.AI`; provider configuration and production qualification remain incomplete. |
| AI04 | Partially implemented | `DraftValidator.cs` plus project digest/source identity checks; no full profile-aware model workflow. |
| AI05 | Partially implemented | Desktop offline suggestions and undo tests exist; batch review and complete graph workflow remain incomplete. |
| AI06 | Partially implemented | Audit types exist, but SQLite audit events are not yet wired through suggestion decisions and privacy evaluation is incomplete. |
| AI07 | Missing | No independently adjudicated held-out dataset or measured evaluation. |
| AI08 | Missing | No controlled model-backed rollout and deployment configuration lifecycle. |
| UI01 | Partially implemented | Avalonia desktop shell and project open/save/validate workflows exist; complete lifecycle is not yet available. |
| UI02 | Partially implemented | `DesignerSession.cs` provides graph edits, selection, undo/redo; scale and full port interaction qualification remain open. |
| UI03 | Partially implemented | Taxonomy explorer exists against synthetic profile; real taxonomy/package search absent. |
| UI04 | Implemented for current diagnostics | Diagnostic rows and node navigation in `MainWindowViewModel.cs`; accessibility/native UI not fully qualified. |
| UI05 | Partially implemented | Accessible names and keyboard commands exist; Windows Narrator and native input certification not run. |
| UI06 | Partially implemented | Local AI suggestion review exists; full edit/reject/batch review and persistent audit trail are incomplete. |
| UI07 | Missing | No signed/self-contained Windows installer or clean-machine installation verification. |
| OPS01 | Implemented and verified for supported inputs | Shared `MappingJobService`, CLI commands and parity tests in `tests/XBRLMappingStudio.Integration.Tests/`. |
| OPS02 | Implemented and verified for file publication | `DestinationLock.cs`, SQLite-backed run journal, publication and recovery tests. |
| OPS03 | Partially implemented | Worker process supervision and cancellation exist; no full desktop Arelle lifecycle. |
| OPS04 | Partially implemented | `PublicationGate.cs` and path checks exist; centralized authorization and immutable trusted reviewer authority remain incomplete. |
| OPS05 | Partially implemented | Diagnostics and run manifests exist; retention and complete terminal job history are not wired. |
| OPS06 | Partially implemented | `PathGuard.cs` exists; workspace-root enforcement is not universal across all entry points. |
| OPS07 | Partially implemented | `tools/worker/` uses pinned requirements and tests; packaged runtime installation and verification are incomplete. |
| OPS08 | Partially implemented | Redacted support bundle code exists; scheduled unattended runner and operational recovery qualification are absent. |
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

## Current verification

- `dotnet build XBRLMappingStudio.slnx -c Release --no-restore -m:1` — passed with 0 warnings and 0 errors for this revision.
- `dotnet test XBRLMappingStudio.slnx -c Release --no-restore -m:1` — passed, 241 tests, 0 failed, 0 skipped for this revision.
- `PYTHONPATH=/tmp/xms-worker-venv/lib/python3.12/site-packages python3 -m pytest tools/worker/tests -q` — passed, 5 tests, using the exact pinned `tools/worker/requirements.lock` installed into a temporary Python 3.12 environment with hashes enforced.
- `git diff --check` — passed for this revision.

No performance result is claimed. No interactive desktop, Windows, installer, or real regulator profile was exercised. The SQLite target publisher remains a callable adapter API rather than a completed end-to-end target workflow.
