# Verification baseline — 2026-11-09

Checkout: `main` at `e74337bd6e46c76cd2250de43c7c0a5f990c54c9` before this evidence-only documentation update.

## Release verification

- The complete .NET solution Release suite passed **1,856 tests** with **6 environment-gated Arelle skips** using an isolated artifact root at `C:\Users\DELL\AppData\Local\Temp\xms-full-qa-20261109`.
- The standard shared Release artifact directory could not be used because a running XBRLMappingStudio Desktop process held generated files open; the isolated run completed successfully without closing or changing that process.
- The Python worker support suite passed **15/15** tests.
- The performance harness self-test passed.
- Exact-head GitHub Actions run [42525985348](https://github.com/nirzaf/XBRLMappingStudio/actions/runs/42525985348) passed Windows, Ubuntu, pinned Arelle integration, and repository hygiene jobs on the checkout SHA above.

## DST-08 additions

The checkout supports mixed file/SQLite publication for xBRL-JSON/XML, CSV v1/v2, XLSX v1/v2, record-JSON v1, and record-XML v1. Real child-process termination tests cover the durable file-confirmed/SQLite-in-progress and SQLite-commit/local-confirmation-loss boundaries for the six generic record contracts and xBRL-XML. Adapter round trips cover escaped text, embedded quotes and newlines, leading-zero identifiers, invariant decimal lexemes, and namespaced XML values. These are synthetic-fixture results. Adapter-internal process loss, lexical edge values through the mixed CLI journey, exhaustive reconciliation, and filesystem power-loss durability remain open.

## Native desktop boundary

A fresh native screenshot on 2026-11-09 showed the Windows lock screen. The app accessibility tree was readable, but the project loaded behind it was a stale `sample-main` mapping with no valid end-to-end publication fixture. No further native interaction was attempted. Prior disposable-fixture native evidence remains applicable only to its recorded narrow journeys; DST-04 native acceptance is not extended by this observation.

## External qualification boundaries

No official IFRS package was downloaded or bundled. Package artifact identity, dependency closure, offline resolution, applicable evaluation rights, and redistribution rights remain unverified. No jurisdiction-specific filing profile, signed installer, production database provider, customer hardware floor, or production taxonomy authorization is claimed.
