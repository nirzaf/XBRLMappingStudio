# xBRL report reverse mapping

The read-only `xbrl-report` source exposes each supported report fact as a typed
record with an occurrence ID, concept, entity, period, unit, value, accuracy,
language, nil state, dimensions, and source evidence. Repeated facts remain
separate occurrences. Every selection setting is conjunctive.

## Pin a selected occurrence to the report bytes

Occurrence IDs use the selected source binding ID and the fact's one-based
position, such as `report#f2`. The position is stable only for the exact report
version. To prevent a changed report from silently selecting a different fact,
include its SHA-256 digest in the binding configuration:

```json
{
  "bindingId": "report",
  "kind": "xbrl-report",
  "locator": "reports/current.json",
  "configuration": {
    "occurrenceId": "report#f2",
    "reportDigest": "sha256:<64-lowercase-hex-digits>"
  }
}
```

Compute the prefix and digest over the exact file bytes, for example in
PowerShell:

```powershell
$reportDigest = "sha256:$((Get-FileHash -LiteralPath .\reports\current.json -Algorithm SHA256).Hash.ToLowerInvariant())"
```

If the report changes, the digest assertion matches no fact and the adapter
returns `IO001`. The digest is an optional source-level assertion so existing
projects and their `sourceId#fN` occurrence values retain the same schema and
behavior. Changing the report does not update an existing selection
automatically; review the new report and explicitly update its digest.

## Supported selection keys

- `occurrenceId`, `concept`, `entityScheme`, `entityIdentifier`
- `period` as the exact ISO period, or `periodKind` plus `instantDate` or
  `periodStart`/`periodEnd` in `yyyy-MM-dd` form
- `unit`, `lang`, and `nil` (`true` or `false`)
- `dimensionFilters`: a JSON object of explicit axis to member QNames
- `typedDimensionFilters`: a JSON object of axis QNames to exact canonical typed
  value evidence
- `reportDigest` as described above

For example, `dimensionFilters` may be
`{"ex:RegionAxis":"ex:EMEA","ex:ProductAxis":"ex:Cloud"}`. The adapter
requires every configured filter to match the same fact occurrence. Typed
dimension evidence stays opaque unless the selected profile verifies a supported
typed-domain declaration; unsupported typed domains are not guessed or flattened.

The adapter does not create or infer ledger rows. `targetRowId` is rejected
because the source adapter has no bound target dataset. Tuple mapping, tuple
flattening, and complex typed-domain flattening are unsupported and fail closed.
This reverse-mapping path does not certify a report for regulator submission.
