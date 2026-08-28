# ARCH-4E4 Pure Post-Classification Policy

ARCH-4E4 extracts the policy mutation that remained in the former
`DocxSlimExtractor.PostProcess` operation. `PostClassificationPolicy` receives
immutable source identity, the initial `CandidateDecision`, TOC structural
features, and the already-ordered neighboring context. It returns a
`PostClassificationDecision`; the OpenXML compatibility layer only projects
that decision back into `SlimParagraph`.

The execution order is unchanged:

`initial classification` -> the three existing demotions -> TOC recognition
and feature derivation -> `PostClassificationPolicy`.

`DemoteCoverPageBlock`, `DemoteInlineEmphasis`, and
`DemoteRunsWithoutOwnProse` remain in `DocxSlimExtractor` and are explicitly
deferred. They are not owned by this policy. The policy has no OpenXML,
extractor, legacy fallback, model, validator, hierarchy, or provider
dependency, and it does not mutate `SourceDocument` or
`TocStructuralFeatures`.

The focused policy characterization covers the old rule behavior, TOC
promotion, ordered context scoring, duplicate-text source identity, immutable
facts, and dependency guards. ARCH-4E4 records zero candidate, role, score, or
level delta, with F regression `2/2`, Release build `PASS`, and zero provider
calls.
