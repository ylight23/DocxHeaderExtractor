# ARCH-4E5 Demotion Dependency Resolution

ARCH-4E5 resolves ownership for the three demotion operations that remain in
`DocxSlimExtractor`. None is moved in this milestone. Each operation mutates
`SlimParagraph.Role` and `SlimParagraph.Score` after candidate classification,
and each relies on an ordered, document-wide view of compatibility state.

`DemoteCoverPageBlock` remains temporarily at the Slim compatibility boundary.
It identifies the first body-prose boundary from mutable Slim candidate state
and uses Slim numbering/style exemptions. `DemoteInlineEmphasis` is deferred to
a numbering/style boundary because its structural-marker gate and exemptions
depend on numbering, outline, and style facts. `DemoteRunsWithoutOwnProse` is
split: it combines those numbering/style facts with a whole-document run and
the role mutations produced by the preceding demotion.

The current order is frozen and unchanged:

`HeadingCandidatePolicy` -> `DemoteCoverPageBlock` -> `DemoteInlineEmphasis`
-> `DemoteRunsWithoutOwnProse` -> `TocStructuralFeatureDeriver`
-> `PostClassificationPolicy`.

No source, derived, TOC, candidate, ranking, validator, hierarchy, route, or
provider semantics were changed. The ownership artifact records explicit
defer/split boundaries, zero candidate/role/score/level delta, F regression
`2/2`, Release build `PASS`, and zero provider calls. This is an intentional
ownership result, not a requirement to empty `DocxSlimExtractor` immediately.
