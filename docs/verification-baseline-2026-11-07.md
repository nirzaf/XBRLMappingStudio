# Verification baseline — 2026-11-07

This records local verification evidence for the XBR-04 set-collected text-fact
slice on `main`. It does not qualify a customer taxonomy or a filing profile.

## Set-collected language-tagged text facts

`FactConstruct` accepts a `textValues` string collection alongside the existing
scalar `textValue`. Each selected item follows the standard concept, period,
language and entity validation path. Missing and blank entries do not emit facts;
explicit nil and repeated text occurrences are retained. Per-item source
references and transformation paths reach accepted facts, and a rejected language
reports the selected item source. Scalar and set-collected text values present in
the same row are rejected as an ambiguous fact value.

Verification:

- Focused `CollectTextValues` engine tests passed 2/2, including repeated values,
  missing/blank entries, explicit nil, per-item provenance and unsupported-language
  diagnostics, plus rejection of simultaneous scalar and set-collected values.
- The final Release Engine suite passed 117/117; the focused tests passed 3/3.
- The Release Desktop suite passed 78/78 with
  `BaseOutputPath=artifacts/qa/desktop-bin/` because the open desktop process held
  the checkout's shared Release DLLs. The first shared-output attempt failed on
  those file locks; the running desktop process was left untouched.
- `git diff --check` passed after the implementation and documentation changes.

Remaining XBR-04/UX-06 work includes FactConstruct compound-unit creation, typed
dimensions and default-member handling, complete concept-specific dimension and
BCP-47 metadata, other non-monetary value types, automatic source-drift
revalidation, and installed real-profile/native workflow acceptance. Real taxonomy
qualification remains gated by GAT-01/GAT-02.
