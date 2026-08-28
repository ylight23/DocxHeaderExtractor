# ARCH-4E2 Post-classification Policy Ownership

ARCH-4E2 audits the four operations that run after initial candidate
classification. All four now have explicit ownership decisions without moving
behavior prematurely.

`DemoteCoverPageBlock`, `DemoteInlineEmphasis`, and
`DemoteRunsWithoutOwnProse` remain deferred. They are policy operations, but
their current inputs include Slim-derived numbering/style state and ordered
whole-document run state. A new component cannot safely accept those inputs
until the corresponding boundaries are stable.

`PostProcess` is explicitly split: it writes the derived TOC-adjacency fact
`PrecedesTableOfContents` and also mutates role/score. Moving the method intact
would mix feature derivation with policy, so no `PostClassificationPolicy` is
introduced in this milestone.

No candidate, score, role, level, validator, hierarchy, model, route, or output
semantics changed. Existing operation order remains authoritative. Verification
is recorded as ARCH-4E1 `30/30`, F regression `2/2`, Release build `PASS`, and
provider calls `0`.
