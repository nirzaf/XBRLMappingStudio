# Bounded CSV source execution

CSV schema inspection reads only the first logical record. Sampling reads the requested
records plus one lookahead record to report truncation. Full source iteration retains
one logical record at a time, including quoted multiline fields and escaped quotes.
The configured encoding remains authoritative; only a leading UTF-8 BOM is stripped,
including when another encoding was declared. Row IDs and cell locators count logical
records, including the header, not physical lines. Short rows retain missing cells;
rows wider than the inspected schema fail rather than silently discarding values.

Source limits default to 1 GiB per file and 4 MiB per logical record. The latter is a
conservative allocation budget accounting for UTF-16 syntax characters and per-cell
overhead, not an exact encoded-byte limit. Execution supplies its configured row budget.
Limits are checked before constructing a complete oversized record, cancellation is
checked during scanning, and unterminated quotes fail when reached. Schema inspection
and samples intentionally do not validate unread tails. Destination formatting is not
subject to these source limits.

Full runs can use the disk-backed large-run path for one CSV binding with a row-scoped
plan and no configured destination. Multiple bindings and set-phase plans remain
unsupported in large mode and return a structured configuration outcome. Existing
profile validation and publication approval gates still apply.

CSV identities retain file SHA-256 digests with `csv-immutable-file` snapshot strategy.
This means a hash-verified unchanged-file expectation, not a retained immutable extract
or guaranteed replay snapshot. Bounded streaming hashes are compared before execution
results are accepted and again at staged artifact validation/publication. A changed,
missing, unreadable or oversized source is refused. No promise is made that a source
modified and restored between observations can be detected; use an immutable extract
when external writers cannot be controlled.

Qualification tests compare small and large serialization byte-for-byte and source
digests for 100,000 synthetic CSV rows, with a 64 MiB retained-heap variation ceiling
during steady-state batches. They also exercise source mutation and staged-validation refusal, cancellation,
malformed/oversized records, encoding, multiline provenance and bounded sampling.

CI runs the CSV and SQLite 100,000-row heap qualifications in separate fresh test
processes on Windows and Linux, in addition to the remaining full suite. Process-wide
GC measurements must not include another test's materialized comparison oracle or
its later release. Both retain the same 64 MiB variation budget and minimum sample
count. The CSV comparison oracle is created only after the bounded-run measurement;
a nonparallel xUnit collection also prevents unrelated local tests from allocating
concurrently. On a failure the ordered heap samples are included in the assertion.
