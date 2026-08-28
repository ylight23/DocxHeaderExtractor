# ARCH-4N Source Identity / Writeback Mapping Boundary

ARCH-4N introduces a small immutable mapping contract at the source/OpenXML
boundary. `SourceIdentity` uses the existing `DocumentId + SourceId`
authority, while `WritebackLocator` carries only paragraph ordinal, source
text, and source segments required to locate and safely split an OOXML
paragraph.

`OutlineWriteback` and `PdfProductWriteback` now build this mapping from the
`SourceDocument` returned by the normal extraction entrypoint. Their split
check and source-anchor validation no longer consume a `SlimParagraph`, and
their post-write verification reads the source-only result. This reduces the
direct writeback Slim dependency from two logical components to zero.

The mapping contains no role, score, candidate, guessed-level, demotion, or
validated state. It does not mutate `SourceDocument` or compatibility state.
Duplicate source text remains distinct because the mapping key is `SourceId`,
not text. Duplicate identity is rejected by the one-to-one mapping invariant
rather than silently collapsed.

The legacy route, repair workflows, evaluation callers, and three demotion
operations remain deferred. No broad migration or Slim deletion is part of
ARCH-4N. Focused writeback/architecture tests pass, Release build passes, and
provider calls are zero.
