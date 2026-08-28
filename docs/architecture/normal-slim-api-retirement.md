# ARCH-4I Normal-Path Slim API Retirement

ARCH-4I adds `DocxSlimExtractor.ExtractForAuthority`, whose result contains
only `SourceDocument Source` and `SlimCompatibilityContext Compatibility`.
`AuthorityExtractionPipeline` now calls this normal authority entry point.
Neither the result nor the normal pipeline signature exposes
`SlimDocument`/`SlimParagraph`.

The Slim document remains an internal implementation detail behind the
compatibility boundary so the existing PDF authority consumer can keep its
current contract while the three demotion operations remain unchanged. This
is an explicit internal escape hatch, not a second source authority. The
legacy `Extract` API and legacy/eval/repair/writeback consumers remain active
and unchanged.

Source identity/facts and candidate/policy output deltas are zero; demotion
order is unchanged. The normal path does not use `SlimSourceFactsAdapter` as
an authority source. Focused API tests pass `3/3`, F regression remains `2/2`,
Release build passes, and no provider was called.
