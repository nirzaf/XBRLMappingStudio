# BASE01 — Product Boundary Decision Record

**Dated:** 2026-11-03
**Owner:** Product (unassigned — see `OPEN.md` for owner-gated fields)
**Scope:** G1 development on the synthetic offline OIM profile.

This record satisfies [BASE01] by distinguishing what the synthetic development
profile covers today from what requires a production qualification decision from
the named owner.

## Fields

| Field | Value today (synthetic development) | Production qualification status |
|---|---|---|
| First customer OS/CPU | Linux dev host (Ubuntu 24.04, x64) is the current execution environment; Windows x64 remains the first proposed customer release per the guide. | **OPEN — blocks UI07, PERF01–07, OPS06.** Target hardware floor must be ratified by the release lead before benchmarks are meaningful. |
| Development CPU | x64 host, .NET 10 SDK 10.0.300, Python 3 available. | Recorded. |
| Report / filing-profile candidate | `synthetic-oim-v1` (namespace `urn:synthetic:finance:1.0`). Six monetary concepts, three currency units (EUR, USD, QAR). | **OPEN — blocks XBR01 (production) / XBR09.** No named regulator profile selected. IFRS / ACRA / ESEF / FERC remain candidates. |
| Source data rights | Repository contains only synthetic fixtures authored for this project. No customer workbooks. | **OPEN — blocks IO07–12.** Real source data rights (customer workbooks, PDFs, database access) require a data-residency and privacy agreement. |
| Target direction | Emit xBRL-JSON and (from G2) xBRL-XML from a source. Report→record import is supported as an internal operation. | Recorded. |
| Qualified reviewer | None appointed. Financial oracles in this repository are authored by the engineering team and explicitly labeled as internal (see `TestFixtures.GoldenRevenueFacts`). | **OPEN — blocks AI07 evaluation and XBR09 sign-off.** A reporting specialist must adjudicate held-out cases before pilot rollout. |
| Representative fixture plan | Synthetic revenue workbook with current/prior year, zero and blank cells, one instant balance, QAR-thousands heading, and one duration fact. Located at `tests/XBRLMappingStudio.Fixtures/TestFixtures.cs`. | Recorded for synthetic development; a customer fixture must be added once IO07 closes. |
| Model provider | Offline-only development is selected; `AiPolicy.Offline` and `LocalRetrievalRanker` run with synthetic fixtures. No outbound provider is enabled. | **OPEN for deployment — blocks managed AI03, AI07, AI08.** The first production deployment's provider/data policy, endpoint, identity and retention are not approved; Microsoft.Extensions.AI is not wired to a concrete provider in this build. |
| Data residency | Local-only; no outbound network calls in engine, adapters, project, or worker (asserted by tests). | **OPEN — blocks AI01 (managed mode), IO07–09.** Residency agreement required before cloud inference or hosted databases are enabled. |

## What is closed by this record

- Synthetic development profile exists and is exercised by all tests.
- Owner decisions required for a real customer release are named and blocked
  to the specific dependent IDs listed in `OPEN.md`, so engineering can proceed
  on the synthetic path without pretending production readiness.
- No acceptance criterion has been silently loosened. Where a test only covers a
  synthetic subset (e.g., `synthetic:Revenue` period check rather than a full
  OIM conformance suite), the test name and file comment mark it as such.

## What this record does NOT claim

- It does not select a jurisdiction, regulator, production taxonomy, or customer
  hardware floor. Those are owner decisions in `OPEN.md`.
- It does not authorize redistribution of any regulator taxonomy or IFRS material.
  The synthetic profile is entirely self-authored.
- It does not pass XBRL certification. External regulator acceptance is a separate
  evidence packet assembled at XBR09.
