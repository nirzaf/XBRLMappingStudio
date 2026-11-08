# OPEN — Selected Priorities and Owner-Gated Decisions

The owner has selected the implementation priorities below. They guide
engineering scope but do not close regulator, customer, licensing, hardware, or
production-qualification gates. The synthetic offline development profile
continues to support implementation tests and confers no production
certification.

## Selected implementation priorities (confirmed 2026-11-08)

- **Standards baseline:** Prioritize IFRS-based financial reporting with the
  IFRS Accounting Taxonomy 2025. Treat the product as a standards-based mapping
  and validation tool, not as certified submission software for any regulator.
  The IFRS Foundation's current guidance says the 2025 taxonomy remains current
  for 2026 reporting. The initial non-early-application profile uses the Full
  IFRS Accounting Standards Entry Point (IAS 1); use the separate IFRS 18 early
  application entry point only when a profile explicitly declares early
  adoption. See the [official 2025 taxonomy page](https://www.ifrs.org/issued-standards/ifrs-taxonomy/ifrs-accounting-taxonomy-2025/)
  and [2026 current-version guidance](https://www.ifrs.org/news-and-events/news/2026/02/ifrs-accounting-taxonomy-2025-to-remain-current-for-2026/).
- **First jurisdiction priority:** Qatar, initially general non-financial listed
  companies, before banking and insurance. Use IFRS Accounting Taxonomy 2025 as
  the technical foundation for mapping and reporting preparation. This is not
  Q-Disclosure certification or evidence that the base IFRS entry point is accepted
  by Qatar. The official QSE sector split distinguishes **Other** (Consumer,
  Industrials, Telecoms and Transportation) from **Real Estate**; the initial
  customer/sector template must therefore be confirmed rather than treating all
  non-financial companies as one official taxonomy category.
- **Approval rule:** Require a recorded human approval for the current semantic
  revision, graph digest, and taxonomy profile digest. Material graph or profile
  changes invalidate that approval. Production publication requires successful
  validation and explicit confirmation. Unattended publication may proceed only
  when a valid approval matches all current revisions and digests. Preserve
  existing authorization gates.
- **Taxonomy evaluation and distribution:** Limit any local technical evaluation
  to uses permitted by the applicable terms. Do not bundle, redistribute, or
  commercially distribute taxonomy materials unless the required written rights
  are documented. If those rights are not confirmed, customers must obtain the
  package independently where legally permitted.

## Decisions still open

Each item below continues to block one or more atomic IDs in the architecture
guide. The selected priorities above do not close these gates.

## Jurisdiction / Filing Profile

- **Blocks:** XBR09, EXT01–04 (conditional), UI07 (installer targets a specific
  regulator packaging contract).
- **Selected scope:** IFRS-based standards mapping and validation is the first
  implementation target, with Qatar/QFMA/QSE Q-Disclosure the first jurisdiction
  priority; no regulator submission certification is claimed.
- **Still open:** Identify the first customer and precise QSE sector template,
  obtain the authorized current Qatar taxonomy, authoritative business/validation
  rules and submission package contract, then qualify representative filings.
  Jurisdiction selection does not authorize guessing the technical package or rules.
- **Rationale:** Taxonomy rights, packaging format, iXBRL requirement, and
  extension-taxonomy rules each depend on the regulator. The selected IFRS
  baseline alone does not establish Qatar's local filing contract.

### Qatar source evidence and remaining technical inputs

Verified against public official sources on 2026-11-08:

- [QSE Q-Disclosure](https://www.qe.com.qa/q-disclosure) describes a joint QSE/QFMA
  initiative, mandatory for listed companies since 1 October 2020. It lists
  annual audited, semi-annual reviewed and quarterly reporting, sector taxonomies,
  five face financial statements, XBRL and additional business-rule validation,
  and an auditor workflow. These public descriptions are not a complete technical
  filing specification.
- The [QSE-linked Q&A](https://www.qe.com.qa/documents/20181/979666/QBRL%2BQA.pdf/52722723-b0eb-74ba-62c9-278fe68cf45d?t=1601196743806)
  says company users download taxonomy templates after login and obtain dedicated
  manuals. It describes Super User approval/submission and no individual taxonomy
  customization. This is rollout-era 2020/2021 guidance; current applicability,
  deadlines and extension restrictions still need confirmation.
- No authoritative publicly downloadable, versioned Qatar package has been
  established here. Obtain its publisher/version/release date, applicable periods,
  SHA-256 hashes, namespaces, entry points and full dependency closure. Also obtain
  the current severity catalogue, mandatory facts/dimensions, identifiers,
  unit/precision rules, language rules, accepted format/container, extension policy,
  submission manual and authorized positive/negative filings. No Q-Disclosure
  compliance or submission compatibility claim is allowed before that evidence.
- [Official IFRS 2026 guidance](https://www.ifrs.org/content/dam/ifrs/standards/taxonomy/2026/guidance-on-use-of-2025-ifrs-taxonomy.pdf)
  retains the 2025 taxonomy and entry points subject to regulator filing rules.
  Element-reference effective/expiry dates still matter for the reporting period;
  unchanged package version does not qualify every element for every period.

No forms were submitted, accounts registered or taxonomy packages downloaded for
this source review. Publication, human approval and licensing gates remain unchanged.

## Production Taxonomy Package

- **Blocks:** XBR09, EXT03 (extension authoring), PERF04 (real taxonomy size).
- **Selected target:** IFRS Accounting Taxonomy 2025, using the Full IFRS
  Accounting Standards (IAS 1) Entry Point as the initial non-early-adoption
  profile. The official [taxonomy page](https://www.ifrs.org/issued-standards/ifrs-taxonomy/ifrs-accounting-taxonomy-2025/)
  identifies the package and entry points; official [2026 guidance](https://www.ifrs.org/news-and-events/news/2026/02/ifrs-accounting-taxonomy-2025-to-remain-current-for-2026/)
  says the 2025 version remains current for 2026 reporting.
- **Still open:** Obtain and record the exact package artifact URL/filename and
  SHA-256 hashes, dependency closure, and offline-resolution evidence after the
  applicable evaluation rights are confirmed. The official [package download
  form](https://392942.hs-sites-eu1.com/ifrs-accounting-taxonomy-2025-download-page)
  requires a form submission. No form was submitted, and no package artifact or
  hash has been verified in this repository.
- **Rights gate:** The current [IFRS Taxonomy terms](https://www.ifrs.org/content/dam/ifrs/about-us/legal-and-governance/legal-docs/taxonomy/taxonomy-terms-and-conditions.pdf)
  restrict use and prohibit commercial use without permission. Engineering will
  not make a legal determination that product evaluation or distribution is
  permitted; obtain written clarification or licensing before integrating the
  package into a product build or distributing it.
- **Rationale:** The synthetic profile exercises the architecture but does not
  qualify any real concept set. A production or conformance claim requires
  verified package identity, rights, dependency closure, and offline-resolution
  evidence.

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
