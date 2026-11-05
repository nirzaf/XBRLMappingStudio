# FND-02 cross-format oracle verification

**Scope:** Hand-authored synthetic fixtures only. This evidence does not establish
acceptance for a licensed filing taxonomy, customer data, a regulator, or a production
environment.

## Oracle contract

The fixture manifest now has separate expected facts for the two different flows:

- **Workbook mapping:** `Financials!B4` is the `Current` field with numeric value
  `1250.0000` and displayed evidence lexeme `1,250.0000`. The authored graph scales by
  `1000` and constructs `synthetic:Revenue` for entity `ABC-Corp`, period
  `2025-01-01/2026-01-01`, unit `QAR`, decimals `-3`, and output lexical value
  `1250000`.
- **Imported report:** JSON and XML inputs contain `synthetic:Revenue` for entity
  `ABC123` with source value lexical `1250000.0000`, the same period, unit, and
  decimals. Tests compare this lexeme directly with the authored JSON value token and
  the namespaced XML `Revenue` element text, in addition to comparing parsed facts.

The manifest scopes row counts by input (`workbook=2`, `csv=2`, `sqlite=2`, nested
JSON/XML `=1`, and imported report JSON/XML `=1`). It declares the XML same-local-name
case using two expanded field paths (`[urn:xms:acceptance:a]value` and
`[urn:xms:acceptance:b]value`) and expected values `1` and `2`. The duplicate JSON key
case expects diagnostic `IO012`, matching `DiagnosticCodes.IoDuplicateKey`.

`IndependentAcceptanceFixtures.VerifyIntegrity` checks the manifest pin and all nine
fixture digests. Its directory overload is exercised against a copied fixture set:
the intact copy passes, then a changed `source.csv` is rejected by the same verifier.
The synthetic profile ID/digest and taxonomy digest are also checked. The manifest
pin is `sha256:1040bceb1bd12510c10c611e41c37d124a31eaaa5881a3953a5d964575a9da39`.

The workbook mapping oracle is applied to in-process facts and OIM, CLI facts and OIM,
Desktop preview semantic facts plus visible display/evidence, and the gated Arelle
execution/publication tests. Imported JSON/XML report facts and exact value lexemes
are asserted independently.

## Review and verification

A second read-only review inspected the revised manifest, fixture hashes, source
lexemes, all fact aspects, row counts, negative/ambiguous cases, tamper verification,
and CLI/Desktop/Arelle coverage. It found no remaining fixture-contract gaps.

On 2026-11-06, the Windows Release solution run passed **505 tests** and skipped **5**
environment-gated Arelle subprocess tests. No local `XMS_ARELLE_PYTHON` runtime was
configured, so those five cases were not executed in this local run. The updated
integration tests were run again after adding direct source JSON/XML lexical checks:
**3 passed**. The Desktop oracle test passed **1/1**. The solution used
`-p:BaseOutputPath=bin-codex\` to keep the build away from a running Desktop process
that held the normal Release output files open.

The pinned CI worker job remains the required execution evidence for the Arelle-gated
cases on the pushed implementation head. `git diff --check` is recorded separately
with the implementation commit.
