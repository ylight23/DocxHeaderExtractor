# ARCH-4E1 Candidate Policy Extraction

ARCH-4E1 introduces `HeadingCandidatePolicy` as the application owner of the
initial candidate classification operation. It returns a `CandidateDecision`
containing only `IsCandidate`, `Score`, `Role`, and `GuessedLevel`.

The existing `HeadingHeuristics` implementation is delegated unchanged. This
keeps the pass behavior-neutral while moving orchestration ownership out of
`DocxSlimExtractor`. The policy is invoked in the same two places and in the
same order as before: after source/derived preparation and again after the
style-trust decision.

Cover-page demotion, inline emphasis demotion, own-prose demotion, and
`PostProcess` remain explicitly deferred to a later post-classification policy
boundary. No model proposal, validation, hierarchy, route, ranking, or output
semantics are included.

Characterization: policy tests `3/3`, combined architecture/F regression
verification and Release build are recorded in the artifact. Provider calls: 0.
