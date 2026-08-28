# ARCH-4K Public Slim API Deprecation Contract

ARCH-4K formalizes Slim as a legacy compatibility API without deprecating the
`SlimDocument` or `SlimParagraph` types themselves. The supported normal entry
point is `DocxSlimExtractor.ExtractForAuthority(string)`, whose result exposes
`SourceDocument` and the bounded compatibility context. The two older
Slim-exposing extraction entrypoints now carry a non-error `Obsolete` message:

- `Extract(string) -> SlimDocument`
- `ExtractWithSourceFacts(string) -> DocxSourceExtractionResult` containing Slim

This is deliberately an entrypoint-level boundary. Slim remains valid internal
state for the three demotion operations, legacy runtime, repair/evaluation,
writeback, and existing test fixtures. The ARCH-4J lifecycle inventory records
170 intentional compatibility caller files (75 production and 95 tests); they
are allowlisted by lifecycle class and have migration targets rather than being
mass-migrated in this milestone.

The normal authority call graph has zero legacy Slim API calls and zero direct
Slim references. `SlimDocument` and `SlimParagraph` remain active types, and
the three demotion blockers remain unchanged. Characterization reports zero
source, candidate, role, score, level, route, and demotion deltas. Focused API
tests pass `4/4`; Release build passes with zero errors. No provider was called.

ARCH-4K is therefore closed as a deprecation contract, not as Slim retirement.
Full retirement remains blocked by the legacy runtime, repair/writeback
contracts, and demotion state boundary.
