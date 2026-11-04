# XBRL Mapping Studio

An offline-capable desktop designer, a portable deterministic execution engine, and a
headless CLI for turning workbooks, files and databases into validated XBRL (Open
Information Model) reports — with every emitted fact traceable to its source.

---

## Table of contents

- [What it does](#what-it-does)
- [Architecture](#architecture)
- [Technology stack](#technology-stack)
- [Repository layout](#repository-layout)
- [Getting started](#getting-started)
- [Building and testing](#building-and-testing)
- [Command-line reference](#command-line-reference)
- [Testing](#testing)
- [CI/CD](#cicd)
- [Implementation status](#implementation-status)
- [Owner-gated decisions](#owner-gated-decisions)
- [Design principles](#design-principles)
- [Contributing](#contributing)

---

## What it does

A reporting analyst's workflow starts with a source (Excel workbook, CSV, JSON, XML, or a
saved XBRL report) and ends with a validated deliverable whose individual facts can be
traced back to their origin. The **same saved mapping** runs interactively in the designer
and unattended in the CLI, producing semantically equivalent output.

Core capabilities implemented here:

- **Visual mapping** — drag-and-drop node graph with typed input/output ports, multi-select,
  reconnection, delete, and undo/redo. Layout changes never bump the semantic revision.
- **Deterministic transformation** — a topologically-sorted, cancellable execution plan with
  provenance recorded on every fact.
- **XBRL generation & validation** — serialization to xBRL-JSON and xBRL 2.1 XML, taxonomy
  concept search, dimensions, compound units, multilingual labels, and filing-rule validation.
  Filing-profile identity fails closed: a project whose `ProfileId`/`ProfileDigest` does not
  match the resolved profile is rejected with a blocking `XBR015` diagnostic before any
  source read or worker call. Each profile declares its required checks; a check that never
  fires (for example, an empty fact set) is recorded as `XBR014` and resolves the run to
  incomplete rather than a silent success.
- **Durable AI06 audit trail** — every accept/edit/reject/batch/undo decision persists to
  the versioned `suggestion_audit` table with governed actor, method, prompt-template
  revision and bounded original/approved payloads; a broken sink is captured on
  `AuditLog.PersistenceFaults` and cannot block the analyst.
- **Report re-import** — an XBRL instance can be read back as records and remapped.
- **Adapters** — Excel (read/write), CSV, JSON (read/write), XML (read/write), and XBRL
  report input, each behind a capability-gated, direction-checked interface. `MappingJobService`
  surfaces target and source suitability (IO01) so the designer can disable a read-only
  connector before a user starts mapping.
- **SQLite input** — table discovery, schema inspection, bounded preview, and shared-engine
  execution. Provenance uses a stable SHA-256 row identity derived from declared single or
  composite primary keys; keyless tables fall back to positional provenance with an `IO015`
  warning. Transactional upsert exists as an adapter API but is not a general publishing flow.
- **Local operational state** — a versioned SQLite application database holds the run
  journal and publication receipts, with non-overwriting online-backup support.
- **AI-assisted mapping (offline-safe)** — deterministic local retrieval proposes concept
  candidates; an analyst accepts/edits/rejects them in a draft before anything becomes
  executable. Under the `Offline` data policy the service is structurally incapable of any
  outbound network call.
- **Unattended operations** — destination locking with atomic replace, private staging, run
  journal with crash recovery, worker supervision (flood bounding, watchdog kill, orphan
  check) with distinct terminal outcomes for cancellation, timeout and crash (`WKR004` /
  `WKR003`), workspace path guard, and redacted support bundles.

---

## Architecture

Portable logic is separated from I/O, UI, and the Python worker so preview, full execution,
and scheduled execution share one implementation.

```mermaid
graph TD
    subgraph Clients
        CLI[CLI - xbrl-studio]
        DESKTOP[Desktop - Avalonia designer]
    end

    APP[Application - MappingJobService]
    CLI --> APP
    DESKTOP --> APP

    subgraph Portable core
        ENGINE[Engine - graph, validator, plan, provenance]
        PROJECT[Project - schema, serializer, revision]
        DIAG[Diagnostics - codes, manifest]
        XBRL[XBRL - taxonomy, OIM/XML, filing rules]
        ADAPTERS[Adapters - Excel/CSV/JSON/XML/XBRL, PathGuard]
    end

    APP --> ENGINE
    APP --> ADAPTERS
    APP --> XBRL
    ENGINE --> PROJECT
    XBRL --> PROJECT
    ENGINE --> DIAG

    subgraph Isolated boundaries
        AI[AI - policy, ranker, provider, draft validation]
        WORKER[Worker - protocol + supervision]
        PY[[Arelle Python process]]
    end

    DESKTOP -.draft proposals.-> AI
    AI --> XBRL
    AI --> ENGINE
    APP --> WORKER
    WORKER <--> PY
```

**Trust boundaries**

| Boundary | Rule |
|---|---|
| `Engine` | Portable C#. No UI, Python, or disk dependencies. |
| Adapters | Reached only through `ISourceAdapter` / `ITargetAdapter` with declared capabilities and a read/write direction gate. |
| Worker | Reached only through `IWorkerClient`; Arelle runs as a separate Python process, isolated for crashes/cancellation. |
| Application | `MappingJobService` is the single job pipeline consumed by both CLI and Desktop. |
| AI | Suggestions mutate only a draft; offline mode replaces the provider with a null object so no transport can be reached. |

---

## Technology stack

| Component | Choice | Notes |
|---|---|---|
| Runtime / SDK | **.NET 10 LTS** (`net10.0`, SDK 10.0.300) | Solution uses the `.slnx` format. |
| Language | C# 14 (`LangVersion` pinned in `Directory.Build.props`), nullable enabled | |
| Desktop UI | **Avalonia 11.1** | The guide's 12.1.5 + Nodify.Avalonia baseline does not restore in this environment; a custom `GraphCanvas` is used. Revisit at hardware/installer qualification. |
| Excel | ClosedXML 0.104.2 | |
| Data formats | `System.Text.Json`, `System.Xml` | XML parsed with `DtdProcessing.Prohibit` and `XmlResolver = null`. |
| XBRL | Synthetic OIM profile (`urn:synthetic:finance:1.0`) | No real regulator taxonomy is available offline. |
| Local state | `Microsoft.Data.Sqlite` | Versioned app database (`state.db`): run journal, publication receipts, backups. |
| XBRL validation worker | Python 3.12 + Arelle (`tools/worker/`) | Hash-locked requirements; offline CI validation against a checked-in synthetic taxonomy. |
| Tests | xUnit 2.9.3 | 284 tests across 8 projects. |
| CI/CD | GitHub Actions | See [CI/CD](#cicd). |

---

## Repository layout

```
XBRLMappingStudio.slnx
src/
  XBRLMappingStudio.Engine/        # portable graph, validation, plan, provenance
  XBRLMappingStudio.Project/       # project schema, serializer, semantic revision, manager
  XBRLMappingStudio.Diagnostics/   # diagnostic contract, stable codes, run manifest
  XBRLMappingStudio.XBRL/          # taxonomy index, OIM/XML serialization, filing rules
  XBRLMappingStudio.Adapters/      # Excel/CSV/JSON/XML/XBRL adapters, PathGuard
  XBRLMappingStudio.Application/   # MappingJobService, locking, publication, local state DB, support bundle
  XBRLMappingStudio.AI/            # policy, retrieval ranker, providers, draft validation
  XBRLMappingStudio.Worker/        # worker protocol, Arelle client, supervision
  XBRLMappingStudio.Desktop/       # Avalonia designer
  XBRLMappingStudio.CLI/           # xbrl-studio headless runner
tests/
  XBRLMappingStudio.*.Tests/       # per-subsystem suites
  XBRLMappingStudio.Fixtures/      # shared test fixtures (workbooks, projects)
docs/
  implementation-status.md         # capability matrix with per-item evidence and limits
  sqlite-state.md                  # local SQLite state, source and target behavior
  decisions/
    BASE01-decision-record.md      # dated decision record (synthetic column filled)
    OPEN.md                        # owner-gated decision register
tools/worker/
  arelle_worker.py                 # Arelle validation worker (protocol + offline tests)
  requirements.lock                # hash-locked Python runtime
```

---

## Getting started

**Prerequisites**

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (10.0.300 or later)
- Git

**Clone & build**

```bash
git clone https://github.com/nirzaf/XBRLMappingStudio.git
cd XBRLMappingStudio
dotnet build XBRLMappingStudio.slnx -c Release
```

**Run the designer**

```bash
dotnet run --project src/XBRLMappingStudio.Desktop
```

**Run the CLI**

```bash
dotnet run --project src/XBRLMappingStudio.CLI -- --help
```

---

## Building and testing

```bash
# Full solution, Release
dotnet build XBRLMappingStudio.slnx -c Release

# All tests
dotnet test XBRLMappingStudio.slnx -c Release

# One subsystem
dotnet test tests/XBRLMappingStudio.Engine.Tests -c Release
```

Publish a self-contained CLI (no separate .NET install required):

```bash
dotnet publish src/XBRLMappingStudio.CLI -c Release -r win-x64  --self-contained true -o publish/cli
dotnet publish src/XBRLMappingStudio.CLI -c Release -r linux-x64 --self-contained true -o publish/cli
dotnet publish src/XBRLMappingStudio.CLI -c Release -r osx-x64  --self-contained true -o publish/cli
```

---

## Command-line reference

`xbrl-studio` shares the exact engine, adapters, and project format the designer uses.
Standard output carries machine-readable results; standard error carries diagnostics.

| Command | Purpose |
|---|---|
| `inspect <project.xmap>` | Print the project's bound sources and schemas. |
| `validate <project.xmap>` | Run graph validation; non-zero exit on blocking diagnostics. |
| `preview <project.xmap> --binding <id>=<path> --rows <n>` | Sampled, visibly-partial execution. |
| `execute <project.xmap> --binding <id>=<path> --output <path> [--workspace-root <dir>]` | Full run producing xBRL-JSON; all source, output and manifest paths stay inside the workspace root (defaults to the project directory). |
| `taxonomy-search [--query <text>] [--lang <tag>] [--page <n>] [--size <n>]` | Paged concept search. |
| `validate-artifact <file.xbrl.json>` | Filing-rule validation of the exported artifact — synthetic validator by default; add `--arelle --python <exe> --taxonomy-root <dir>` (optionally `--package <zip>`, `--plugin <name>`, `--disclosure-system <name>`) for independent offline Arelle validation. Install the pinned worker runtime from `tools/worker/requirements.lock`. |
| `inspect-run-result <runId>` | Look up a run's journal result. |
| `support-bundle <project.xmap> <runId> [--out <path>] [--log <file>]` | Redacted diagnostic bundle. |

**Exit codes**

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | Success with warnings |
| 2 | Invalid mapping |
| 3 | Data error |
| 4 | Incomplete profile / configuration |
| 5 | Authorization failure |
| 6 | Cancelled |
| 7 | Operational failure |

Example:

```bash
dotnet run --project src/XBRLMappingStudio.CLI -- \
  execute my.xmap --binding wb=Quarterly.xlsx --output out.json
echo $?   # 0 on success, 2 on invalid mapping, ...
```

---

## Testing

284 tests across 8 xUnit projects, all green in Release:

| Suite | Count | Covers |
|---|---:|---|
| Engine.Tests | 33 | Validation, plan compilation, collections/joins/functions, preview reconciliation, determinism, cancellation |
| XBRL.Tests | 49 | Taxonomy index, dimensions/compound units, xBRL-JSON↔XML equivalence, report re-import, filing rules, conversion resilience, XBR07 required-check completeness |
| AI.Tests | 47 | Zero-outbound policy, deterministic retrieval, provider contract, draft validation, analyst workflow, in-memory and durable audit sink (AI06) |
| Adapters.Tests | 45 | XLSX input/output with worksheet selection, CSV/JSON/XML round-trips and hardening, JSON and XML writers with staging/reconciliation, SQLite source (primary-key row identity) and target upsert |
| Ops.Tests | 47 | Destination locking/publication/journal, local app database migrations and backup, SuggestionAuditStore v1→v2, IO01 capability surfacing, worker supervision, path guard, redaction |
| Integration.Tests | 40 | CLI parity, exit-code contract, fail-closed profile identity, publication gate, XBR08 worker timeout/crash/deadline, schema drift, acceptance scenarios |
| Desktop.Tests | 19 | Editing session: multi-select, reconnect, undo/redo, layout-vs-semantics, SQLite bindings and key disclosure |
| Project.Tests | 6 | Atomic save, revision conflict, semantic digest |

```bash
dotnet test XBRLMappingStudio.slnx -c Release
```

---

## CI/CD

Two GitHub Actions workflows under `.github/workflows/`:

- **[`ci.yml`](./.github/workflows/ci.yml)** — on pull requests and pushes to `main`:
  Release build + full test suite on **ubuntu-latest** and **windows-latest**, an
  **Arelle worker** job that installs the hash-locked Python runtime and runs the worker's
  protocol/offline-taxonomy tests, plus a hygiene job that rejects tracked `bin/`/`obj/`
  output and any edit to the architecture guide.
- **[`release.yml`](./.github/workflows/release.yml)** — on `vX.Y.Z` tags (or manual
  dispatch): gates on build + test, publishes self-contained CLI binaries
  (win/linux/mac) and portable desktop bundles, then opens a **draft GitHub Release**.

> **Release scope:** signed Windows installers and locked/signed release bundles are
> owner-gated (guide IDs `UI07`, `OPS06`/`OPS07`). The pipeline produces **unsigned,
> portable** artifacts only and does not claim code-signing.

To cut a release:

```bash
git tag v1.0.0 && git push origin v1.0.0
```

---

## Implementation status

Capabilities are tracked by stable IDs and delivery gates (G0–G5; full matrix in
[`docs/implementation-status.md`](./docs/implementation-status.md)). The
following IDs have implementation code and repository tests; qualification limits still apply:

| Area | Implemented IDs |
|---|---|
| Foundation | `BASE02`–`BASE04`, `BASE01` (synthetic column) |
| Mapping engine | `MAP01`–`MAP08` |
| Adapters | `IO01`–`IO06` (JSON/XML writers closed Wave 2), `IO07`–`IO08` (SQLite source and target, partial) |
| XBRL | `XBR01`–`XBR08` (synthetic profile; Arelle `validate-artifact` path; `XBR07` required-check completeness; `XBR08` distinct timeout/crash terminal outcomes) |
| Desktop | `UI01` (development build), `UI02` (canvas prototype), `UI03`–`UI04`, `UI06` (headless workflow tests) |
| AI baseline | `AI01`–`AI04`, `AI06` (offline baseline with durable `suggestion_audit` persistence); `AI05` (single-suggestion apply/undo path) |
| Operations | `OPS01`–`OPS05`, `OPS08` (`OPS05` partial) |

SQLite source discovery/read and transactional target upsert have repository tests.
The target publisher remains a callable adapter rather than an end-to-end desktop/CLI
workflow, and mixed-destination receipt reconciliation remains incomplete (`IO07`–`IO08`, `OPS05`).
SQLite source rows with declared single or composite primary keys receive deterministic
SHA-256 hashed row identities used for provenance; the schema inspector exposes
declared primary-key order. A table without a primary key keeps positional provenance and
produces an `IO015` warning; SQLite does not guarantee row ordering between reads. The
run manifest records which row-identity strategy was used, and the desktop binding UI
discloses the key configuration.

Filing-profile identity is fail-closed: `validate`, `preview` and `execute` compare the
project's `ProfileId`/`ProfileDigest` against the resolved profile and stop with a blocking
`XBR015` diagnostic — before any graph compilation, source read, or worker call — on
mismatch (`tests/XBRLMappingStudio.Integration.Tests/ProfileIdentityTests.cs`).

Local operational state lives in a versioned SQLite application database (`app_settings`,
`project_registry`, `run_journal`, `publication_receipts`, `suggestion_audit`); `.xmap`
remains the portable source of truth for executable graphs. See
[`docs/sqlite-state.md`](./docs/sqlite-state.md).

Per-ID evidence lives in [`docs/decisions/BASE01-decision-record.md`](./docs/decisions/BASE01-decision-record.md).
Items marked "synthetic" (e.g. the XBRL filing profile) are honestly scoped to the offline
synthetic OIM profile — they are **not** qualified against a real regulator package.
Independent Arelle validation is available for existing artifacts through the CLI;
desktop and full `execute` runs still use the local synthetic validation worker.
Desktop tests exercise the view model and canvas session without a native Windows session;
keyboard-only use, screen-reader behavior, native dialogs and installer behavior remain
unqualified. AI review does not yet persist a governed reviewer identity and complete
accept/edit/reject/batch audit trail.

---

## Owner-gated decisions

The following require commitments outside this development environment and are documented,
not silently re-scoped. See [`docs/decisions/OPEN.md`](./docs/decisions/OPEN.md):

- First filing **jurisdiction**, target **production taxonomy**, **model provider**, and
  **data-residency** agreement (`BASE01`).
- Production database family qualification (`IO07`–`IO09`) and PDF/OCR ingestion (`IO10`–`IO12`).
- Real regulator profile + taxonomy upgrade simulation (`XBR09`).
- Model-provider qualification and rollout (`AI07`–`AI08`).
- Keyboard/screen-reader qualification (`UI05`), signed Windows installer (`UI07`) and
  real Windows performance benchmarks (`PERF01`–`PERF07`).
- Signed release bundles and upgrade/backup/rollback (`OPS06`–`OPS07`), and
  regulator-mandated extensions (`EXT01`–`EXT04`).

See [`docs/sqlite-state.md`](./docs/sqlite-state.md) for SQLite behavior and limits, and
[`docs/implementation-status.md`](./docs/implementation-status.md) for the full status matrix.

---

## Design principles

These are held invariant across the codebase:

- The **portable `Engine`** has no UI, Python, or disk dependencies.
- Adapters are isolated behind `ISourceAdapter` / `ITargetAdapter`; the worker behind
  `IWorkerClient`.
- A single **`Application.MappingJobService`** is consumed by both CLI and Desktop.
- **Stable diagnostic codes** — never reused — namespaced by prefix (`MAP`/`PRJ`/`IO`/`XBR`/`WKR`/`PUB`/`AI`/`UI`).
- **Semantic revision is independent of layout**: moving a node on the canvas must not change
  the project's semantic digest.
- Execution is **deterministic and cancellable**, with **provenance on every emitted fact**.
- Safety gates **fail closed**: stale schemas, mismatched filing profiles, and unproven
  output digests stop the run before any side effect.
- No `bin/`/`obj/` output is committed.

---

## Contributing

1. Create a feature branch from `main`.
2. Add or update tests before changing shared behavior — CI runs the whole suite on Linux
   and Windows.
3. Keep the architecture boundaries and stable-diagnostic-code rules intact.
4. Record behavior- and scope-changing decisions as dated entries in `docs/decisions/`.
5. Open a pull request; CI must be green before review.

---

## License

**No `LICENSE` file has been declared in this repository yet.** Until one is added, the
source should be treated as proprietary and all rights reserved by the project owner.

Third-party components remain under their own licenses — notably Avalonia (MIT),
ClosedXML (MIT), and, where used, Arelle (Apache 2.0). Consult the pinned dependency
baseline for licensing and redistribution obligations before shipping a release.
