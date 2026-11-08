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


## Desktop package preservation

Desktop import validates every packaged revision against its actual named ports
and the complete imported dependency library. An isolated compiler scaffold
supplies typed input ports, including collection inputs, and an iteration source
for zero-input functions. This is structural validation, not execution evidence
for generalized typed functions. Existing marker, wiring, type, dependency,
recursion, catalog and expansion limits still apply. Compatible single-input,
single-output Decimal functions additionally retain synthetic runtime validation;
invalid numeric parameters are not accepted merely because a graph compiles.

Import, export and pinning preserve port names, ordering, shape, cardinality,
requiredness and descriptions, as well as exact dependency revisions. Import only
rewrites the requested root identity/name and namespaced dependency identities.
Import remains one undoable project edit. It does not create or renew approval.
Project changes during a pending package dialog or validation refuse application
rather than transferring a package into a different active project.

The visual editor still authors only one Single Decimal input and one Single
Decimal output. Loading a compatible revision retains its actual public names,
port metadata and catalog rather than replacing them with the template signature.
General typed revisions show their signature and remain available for inspection,
export and exact-revision pinning. Numeric editing, preview and saving are disabled
for those revisions, including direct command invocation and canvas mutation.
Starting a new numeric draft restores the authoring controls.

Headless Desktop regression coverage exercises package round trips, malformed
contracts, immutable revisions, undo/redo and interrupted dialog flows. It is not
native Windows keyboard, mouse, visual-layout or screen-reader acceptance. General
visual signature authoring, real-profile validation and release qualification
remain open.
