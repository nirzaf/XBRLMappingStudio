# OPEN — Owner-Gated Decisions

Each item below blocks one or more atomic IDs in the architecture guide. None of
these can be closed by the engineering team alone. The synthetic offline
development profile unblocks engineering work but confers no production
certification.

## Jurisdiction / Filing Profile

- **Blocks:** XBR09, EXT01–04 (conditional), UI07 (installer targets a specific
  regulator packaging contract).
- **Decision needed:** Name the first customer reporting jurisdiction and its
  authoritative filing rules (IFRS/ESEF, ACRA, FERC, other).
- **Rationale:** Taxonomy rights, packaging format, iXBRL requirement, and
  extension-taxonomy rules each depend on the regulator.

## Production Taxonomy Package

- **Blocks:** XBR09, EXT03 (extension authoring), PERF04 (real taxonomy size).
- **Decision needed:** Which versioned taxonomy package is the first target?
  Are redistribution rights cleared?
- **Rationale:** The synthetic profile exercises the architecture but does not
  qualify any real concept set. A relase claim requires the actual package
  hash, dependency closure, and offline-resolution fixture.

## Model Provider

- **Blocks:** AI01 (managed mode), AI03 (concrete adapter), AI07, AI08.
- **Decision needed:** Which of offline-only, Azure OpenAI, or local-model
  (e.g., Ollama) is approved for the first deployment? Endpoint, deployment
  identity, retention, and token budget.
- **Rationale:** Provider differs on structured-output support, residency,
  cost, and privacy terms.

## Target Hardware Floor

- **Blocks:** PERF01–07, UI07, OPS06.
- **Decision needed:** Confirm minimum CPU, RAM, storage, OS version, and GPU
  for the first customer release. Benchmarks in `PERF` require this to be
  meaningful.

## Data Residency and Retention

- **Blocks:** IO07–09 (production database), AI01 (managed), OPS04 (outbound
  network policy enforcement), OPS06–07.
- **Decision needed:** Where may the product store customer workbooks, run
  manifests, and derived caches? Which jurisdiction governs? Are logs retained
  locally or shipped to a customer SOC?

## Production Database Family

- **Blocks:** IO07–09, OPS05 (idempotent DB receipts).
- **Decision needed:** Which vendor (SQL Server / Azure SQL, PostgreSQL, MySQL/
  MariaDB, Oracle, Db2, SAP HANA, other) is qualified first? Provider version
  and connector authentication mode.
- **Rationale:** Each provider has independent metadata, numeric precision,
  transaction semantics, and rollback behavior. SQLite-only tests cannot
  support a SQL Server claim (per [IO07] acceptance).

## PDF Fixture Rights

- **Blocks:** IO10–12 (digital, scanned, OCR).
- **Decision needed:** Which customer-provided PDFs are permitted as fixtures?
  Are OCR language packs redistributed? Which rasterizer is chosen?

## Installer Signing and Rollback

- **Blocks:** UI07, OPS06, OPS07.
- **Decision needed:** Code-signing certificate authority and revocation
  workflow. Backup retention policy across upgrade.

## Extension Gates (Conditional)

- **EXT01 (native-XML preservation):** Required if the profile mandates OIM-
  incompatible constructs (tuples, fractional, non-dimensional segments).
- **EXT02 (iXBRL authoring):** Required if the regulator accepts only XHTML
  Inline XBRL deliverables.
- **EXT03 (extension taxonomy authoring):** Required if the customer authors
  private extensions anchored under a regulator base taxonomy.
- **EXT04 (regulator report package):** Required if the collector accepts a
  specific archive layout (e.g., ESEF report package).

None are mandatory under the synthetic OIM-only profile. Bring forward before
committing to a delivery scope once a jurisdiction is named.
