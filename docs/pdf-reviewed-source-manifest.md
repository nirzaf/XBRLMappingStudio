# Reviewed digital PDF source manifest

`PdfSourceAdapter` reads a reviewed table from a sidecar named `<source.pdf>.xms-review.json` by default. A project may set `reviewManifestPath` in the PDF connection configuration to select another path. The sidecar is a strict versioned JSON contract; property names are camel case and unknown or duplicate properties are rejected.

The `sourceSha256` must identify the exact PDF bytes. `templateRevision` must be the digest of the reviewed table template, and every referenced evidence row must have been captured with that same revision. Each label and numeric cell references a persisted evidence ID and the exact one-based page plus normalized page region. Numeric cell `rawText` must match the text saved with its evidence reference. The evidence store also verifies the current PDF and its private managed snapshot against the source digest.

```json
{
  "formatVersion": 1,
  "sourceSha256": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
  "templateRevision": "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
  "reviewer": "reviewer-id",
  "reviewedUtc": "2026-11-05T12:00:00Z",
  "isApproved": true,
  "rowLabelHeader": "Description",
  "numericPolicy": {
    "decimalSeparator": ".",
    "groupSeparator": ",",
    "allowGrouping": true,
    "scaleFactor": 1000,
    "currencyCode": "QAR",
    "allowLeadingSign": true,
    "allowParenthesizedNegative": true,
    "allowTrailingMinus": false,
    "basis": "explicitTemplate"
  },
  "periodColumns": [
    { "headerText": "2024", "reportingYear": 2024, "centerOnColumnAxisNormalized": 0.55 },
    { "headerText": "2025", "reportingYear": 2025, "centerOnColumnAxisNormalized": 0.75 }
  ],
  "rows": [
    {
      "occurrence": 1,
      "labelText": "Revenue",
      "labelEvidenceId": "0123456789abcdef0123456789abcdef",
      "labelEvidence": {
        "pageNumber": 1,
        "leftNormalized": 0.05,
        "bottomNormalized": 0.60,
        "rightNormalized": 0.25,
        "topNormalized": 0.63
      },
      "cells": [
        {
          "reportingYear": 2024,
          "rawText": "1,250",
          "canonicalValue": "1250000",
          "evidenceId": "1123456789abcdef0123456789abcdef",
          "evidence": {
            "pageNumber": 1,
            "leftNormalized": 0.56,
            "bottomNormalized": 0.60,
            "rightNormalized": 0.66,
            "topNormalized": 0.63
          },
          "isApproved": true,
          "correctionReason": null
        },
        {
          "reportingYear": 2025,
          "rawText": "1,500",
          "canonicalValue": "1500000",
          "evidenceId": "2123456789abcdef0123456789abcdef",
          "evidence": {
            "pageNumber": 1,
            "leftNormalized": 0.76,
            "bottomNormalized": 0.60,
            "rightNormalized": 0.86,
            "topNormalized": 0.63
          },
          "isApproved": true,
          "correctionReason": null
        }
      ]
    }
  ]
}
```

`canonicalValue` is an invariant decimal and represents the accepted, scaled value. If it differs from the exact value parsed from `rawText`, `correctionReason` is required. Every row must contain at least one approved cell and refer only to declared reporting years. The adapter returns a stable long-form schema (`row_label`, `reporting_year`, `value`, `currency`, `scale_factor`) so tables with different years retain the same schema fingerprint. Each output record represents one row occurrence and reporting year.

The adapter enforces a 32 MiB manifest limit, a maximum of 100,000 rows and 100,000 numeric cells, 2–30 declared reporting periods, bounded text fields, strict decimal parsing, unique evidence IDs, and the maximum source PDF size used by digital extraction. A missing or unapproved manifest, ambiguous numeric text, changed PDF, or unresolved evidence reference blocks mapping.

This sidecar is a local review assertion, not authenticated organizational approval. The reviewer value is not independently authorized, and correction history is not yet durable; PDF-07 and PDF-08 cover the interactive review surface and governed correction audit.
