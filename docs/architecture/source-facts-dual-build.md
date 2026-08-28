# ARCH-4C Source Facts Dual Build

ARCH-4C adds `DocxSlimExtractor.ExtractWithSourceFacts`, which returns a
`DocxSourceExtractionResult` containing both the existing `SlimDocument`
compatibility result and a source-only `SourceDocument`.

The producer opens and parses the DOCX once. `DocxSourceFactsBuilder` maps the
already parsed source paragraph state directly; it does not run a second parser
or perform I/O. The existing `Extract` API delegates to this method and returns
only `SlimDocument`, so callers are not cut over in this milestone.

The source contract retains source identity, text spans, source segments,
style, numbering, and layout facts. It does not copy role, score, candidate,
guessed-level, model, validation, or hierarchy policy fields. The adapter
remains a compatibility oracle, and field-by-field serialization equivalence is
tested against it.

Status: CLOSED. The focused dual-build contract tests pass 5/5; the combined
route/source-facts/regression selection passes 22/22; and the Release solution
build passes with zero errors. Existing analyzer/compiler warnings are unchanged
and are outside ARCH-4C.

Provider calls: 0. Production behavior change: false.
