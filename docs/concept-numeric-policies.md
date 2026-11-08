# Concept numeric policies (MAP04)

Installed manifest schema 6 supports an optional `numericPolicy` on each numeric concept. Policies are collector configuration, not evidence of filing-authority approval or regulatory qualification. Existing profiles without policies retain their previous value validation and canonical digest format.

Example concept fragment:

```json
"numericPolicy": {
  "maximumSignificantDigits": 6,
  "roundingDecimals": -3,
  "roundingMode": "reject",
  "minimumDecimals": -3,
  "maximumDecimals": -3,
  "allowInfiniteAccuracy": false
}
```

- `maximumSignificantDigits`: optional integer 1–29. Counts significant digits of the value, ignoring leading and trailing zeroes; zero counts as one. This is a value constraint, not an XBRL `precision` attribute.
- `roundingDecimals`: optional integer −28–28. Requires the supplied value to be an exact multiple of 10 raised to minus this value. For example, −3 requires whole thousands; 2 requires whole hundredths.
- `roundingMode`: only `reject` is supported and is the default. Neither the engine nor worker silently rounds, truncates or changes values. A mapping must explicitly prepare a conforming value upstream.
- `minimumDecimals` / `maximumDecimals`: optional integers −30–30 constraining the finite XBRL decimals accuracy assertion. Bounds must be ordered. These are independent of significant digits and the rounding grid.
- `allowInfiniteAccuracy`: defaults to true. When false, a non-nil numeric fact must declare finite decimals. INF is otherwise permitted independently of finite bounds.

Nil facts bypass value and accuracy constraints, but all existing nil/aspect checks still apply. Policies on non-numeric concepts, invalid limits, unsupported rounding modes, and policies in pre-6 manifests are rejected. Policies participate in profile identity hashing and are checked both at engine fact construction and against loaded Arelle facts using namespace/local-name identity. Worker facts with an XBRL precision assertion are rejected for policy-bearing concepts; use decimals assertions.

The shared synthetic financial fixture `tests/XBRLMappingStudio.XBRL.Tests/fixtures/concept-numeric-policies.json` is executed by .NET and Python tests. It covers thousands, EPS, negative midpoints, zero, trailing zeroes, significant precision, finite/INF accuracy and the largest .NET decimal magnitude. It is a software regression fixture, not regulatory certification.
