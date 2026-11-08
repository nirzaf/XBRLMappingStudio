# Generic JSON/XML source integrity

This increment hardens the existing generic source adapters. It does not expand
XBRL import, XML row discovery, supported graph shapes or production filing scope.

## Exact numeric values

JSON number tokens must be representable exactly in the CLR decimal domain.
Parsing must not turn a nonzero input into zero, round a fractional value or lose
coefficient digits. Accepted values retain their original numeric lexeme alongside
the exact decimal value. Existing decimal scale/formatting is retained after
exactness verification. The configured mantissa digit budget still
applies, including trailing zeros; normalizing a number for representability does
not relax that budget. Exponent notation is accepted only when its value is exact.

The generic XML adapter keeps its existing limited type inference. A value that
the existing leading-sign/decimal-point parser accepts as a decimal must also be
exact. Numeric-looking text that the original parser does not accept remains
text, including exponent notation, grouped values, surrounding whitespace and
values beyond its range. This distinction avoids reinterpreting textual identifiers
or silently changing the adapter's supported numeric grammar.

An inferred numeric value requiring rounding or underflow is refused with the
existing `IO014` number-budget diagnostic. Exactness checking is bounded by the
input length and the decimal coefficient size, not by an exponent-sized allocation.
Zero and removable trailing zeros do not justify rounding a nonzero value.

## Ambiguous flattened fields

Both adapters keep their existing column names and source-locator format. A
single record may not resolve two source values to the same column. Examples are:

- JSON `{"a.b":1,"a":{"b":2}}`.
- XML `<row a="1"><a>2</a></row>`.
- Dotted XML names that collide with a nested element path.

Such collisions are refused with the existing `IO012` duplicate-key diagnostic,
including collisions involving missing or explicit-nil values. No value or source
reference may silently overwrite an earlier one. The adapters do not introduce
an escaping convention that would rename existing unambiguous columns. Different
records may still use the same flattened column convention; this check does not
redefine cross-record schema identity.

Schema inspection, sampling and full reads use the same parser and refusal rules.
These checks complement the existing duplicate JSON property, depth, byte-budget,
DTD/entity and namespace controls; they do not replace them.

Generic JSON reads remain buffered, so they now have explicit limits before an
input can grow without bound: 16 MiB by default (64 MiB absolute configuration
ceiling), 100,000 top-level array records by default (1,000,000 ceiling), and
1,000,000 UTF-16 code units per property name or string value by default
(16 MiB ceiling). Project connection options may lower or raise the defaults
within those ceilings. Exceeding the byte, row, or string limits reports IO032,
IO006, or IO033 respectively. Cancellation is checked while reading bytes and
parsing tokens. This bounds the adapter's input and token budget; it is not a
streaming or peak-process-memory qualification.

## Verification and limits

Adapter regressions include an independent bounded BigInteger coefficient/scale
oracle, long-token cases, representable boundaries, excess fractional precision,
coefficient overflow, underflow, exponents, zero, trailing zeros, original lexical
evidence and resolved-field collisions. Mapping regressions exercise accepted
exact inputs and fail-closed invalid sources without changing accepted destination
bytes or bypassing approval and input-identity checks.

XML's current rowset selection, repeated-element representation and text inference
remain limited. Existing XML buffering and depth behavior is unchanged; this increment
does not establish streaming or total-process-memory qualification. Generic adapter checks do not qualify arbitrary XML/JSON constructs,
real IFRS artifacts, regulator conformance, native desktop acceptance or a release.
