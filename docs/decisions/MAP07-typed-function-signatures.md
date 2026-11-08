# MAP07 typed function boundaries

Date: 2026-11-08

Reusable function revisions now support zero or more named inputs and one or more
named outputs, with every existing port shape and single/collection cardinality.
One `functionInput` marker exposes the input signature and one `functionOutput`
marker consumes the output signature. Multiple ports bind by exact, case-sensitive
name, never list position. A single-port marker may retain its historical local
name (for example public `input` and marker `value`). This preserves old projects.

Calls must declare the exact pinned revision's names, shapes, and cardinalities.
Required input connectivity comes from that revision, not the editable call node's
requiredness flag. Each output marker port needs exactly one producer. Boundary
edges are type-checked before expansion, including unused inputs and pass-through
outputs, so removing markers cannot conceal incompatible wiring. Duplicate names,
invalid enum values, recursion, graph cycles, and ambiguous output wiring fail
closed. Existing catalog-version, nesting, expansion-size, and ID-collision gates
remain in place. Optional disconnected inputs retain normal missing-value behavior
and still must satisfy required inputs of ordinary executable body nodes.

Signatures are shared by library creation, revision, import, and package validation.
Library drafts may still have incomplete bodies; execution validates their complete
graph. No scripts, arbitrary code, or new runtime evaluator is introduced. The
existing desktop decimal-template editor remains available; generalized signatures
are represented in the existing project/package schema and library APIs.
