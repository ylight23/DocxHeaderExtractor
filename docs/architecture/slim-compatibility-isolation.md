# ARCH-4H Slim Compatibility Boundary

ARCH-4H isolates the concrete Slim representation from normal authority
orchestration. `SlimCompatibilityBoundary` captures a narrow transitional
context containing only policy, TOC, numbering/style compatibility state and
the existing marker parser bridge. It does not expose source text, style facts,
numbering facts, source segments, or source identity as an alternative
authority.

`AuthorityExtractionPipeline` creates the boundary after the dual DOCX source
extraction. `DocxAuthorityPipeline` receives `SourceDocument` plus the opaque
compatibility context; it no longer has a normal-path signature containing
`SlimDocument` or `SlimParagraph`. The three `Demote*` methods remain in their
original implementation and order. Legacy, repair, eval, writeback, and test
callers are intentionally unchanged.

The direct normal Slim type reference count is reduced from `2` to `0`, with
all remaining compatibility dependencies documented. Source/policy identity
and behavior deltas remain zero. This is encapsulation, not Slim retirement.
