# Bounded explicit hypercube validation

This increment advances P1-07 for the local fact model. It does **not** qualify
a real IFRS profile or any jurisdiction-specific filing profile, implement typed
dimensions, or replace Arelle's DTS/schema and regulator-rule validation.

## Supported contract

`FilingRuleValidator` evaluates `xbrl:dimension-hypercube-valid` using the selected
profile's existing definition relationships. Installed manifest schema 5/6 already
binds those relationships and their attributes into the profile digest. No new
schema or profile identity is introduced by this change. The supplied arcs must
be the effective, verified taxonomy relationships, not unresolved raw linkbase
arcs requiring priority/prohibition resolution.

The supported subset is explicit dimensions in `segment`, matching the current
XML report model. It evaluates:

- `all` and `notAll`, with conjunction within each originating definition role
  and alternatives across roles. `notAll` negates the complete cube predicate.
- Primary-item inheritance through `domain-member`, honoring `targetRole` when
  walking back to applicable has-hypercube relationships.
- `targetRole` transitions on has-hypercube, hypercube-dimension,
  dimension-domain and domain-member relationships.
- Member domains specific to the axis and effective domain role. An unusable
  member remains excluded while its usable descendants may be valid. An unusable
  occurrence overrides a usable occurrence in that domain network.
- Required axes, globally declared defaults inferred when an axis is omitted,
  and checking inferred defaults against the cube's usable domain. Explicitly
  reporting a default remains invalid under the separate dimension rule.
- Closed cubes rejecting additional reported axes, open cubes allowing them,
  and empty cubes. A primary item with no applicable cubes has no cube restriction.

The pre-existing `DimensionDomains` rule remains a **separate profile restriction**
on reported explicit members. It is not used as a substitute for role-specific
hypercube membership. Profile authors must supply its complete permitted union
across roles, including extra axes permitted in open cubes, if those reports are
to pass that additional restriction.

## Fail-closed limits

Applicable scenario or missing context metadata, typed values, missing explicit
axis/domain metadata, conflicting defaults, directed cycles and exhausted
traversal budgets yield `Incomplete`, never a successful negated cube. Unsupported
applicable metadata also prevents accepting another role as a fallback. This is
intentionally more conservative than an evaluator with complete context/DTS
metadata. Unrelated scenario cubes do not affect a fact with another primary item.

The evaluator uses iterative traversal, the existing one-million-relationship
profile limit, and a default maximum of 100,000 traversal steps per fact. A caller
can supply a smaller positive `maximumDimensionalRelationshipVisits` to
`FilingRuleValidator`. Cancellation is checked during traversal. Indexes depend
on profile size; no cache grows with input row count. This does not establish
large real-taxonomy latency or total-process-memory acceptance.

The model still cannot represent scenario placement, non-dimensional context
content, typed domains, or taxonomy substitution-group details. The local
checker does not certify those features, full DTS consistency, or the completeness
of a real taxonomy package. Existing installed-profile and production publication
gates remain unchanged.

## Worker and publication behavior

Conditional dimensional findings now reach the local synthetic worker even when
the rule is not listed among unconditional required checks. Each finding carries
the exact fact occurrence locator, allowing the shared application service to
recover source evidence. Any failed conditional dimension rule produces domain
failure; any incomplete dimensional rule produces incomplete validation. The
same behavior applies to streamed local validation, so neither path can silently
publish a report after discarding a conditional dimension finding.

## Evidence

- `HypercubeValidationTests` covers direct/inherited cube constraints, role
  combinations, all targetRole transitions, default/member semantics, malformed
  and cyclic networks, traversal budget, cancellation and streamed parity.
- Twenty hand-authored cases in `fixtures/explicit-hypercubes.json` are consumed by
  .NET and independently by pinned Arelle's dimensional algorithm. The Python
  oracle adapts only its concept type guard to lightweight relationship objects;
  it is **not** a real DTS, report-conversion or regulator-conformance test.
- `DimensionalPublicationTests` covers conditional errors and unavailable context
  metadata, source-linked exact fact findings, multiple streamed occurrences,
  legacy member restrictions and preservation of an existing destination when
  publication is blocked.

Official IFRS package rights, metadata completeness, typed/scenario support and
permitted financial conformance fixtures remain open acceptance work.

The upstream feature commit `e0a7062` passed exact-head CI run
[42459177665](https://github.com/nirzaf/XBRLMappingStudio/actions/runs/42459177665),
including locked restore, separate 100,000-row memory qualifications, enabled
Arelle integration (15/15), Python worker tests (78/78), dependency/performance
tool tests (15/15), and the taxonomy benchmark self-test. After the local
publication outcome-order correction, the Windows Release solution suite passed
1,097 tests with six environment-gated Arelle skips; `DimensionalPublicationTests`
passed 5/5. These results do not close any open real-profile acceptance gate.
