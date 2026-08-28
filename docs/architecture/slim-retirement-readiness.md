# ARCH-4G Slim Facade Retirement Readiness

ARCH-4G audits the remaining `SlimDocument` and `SlimParagraph` callers after
the ARCH-4F source-authority cutover. The repository contains 66 production
and 66 test files with direct Slim references at the file level. This is a
caller inventory, not a claim that every reference is on the normal authority
route.

The normal route is now source-authoritative. `AuthorityExtractionPipeline`
uses Slim for result compatibility (`Mode` and paragraph count), and
`DocxAuthorityPipeline` uses the sidecar for policy and deferred demotion/TOC
state. No unexplained normal source-fact mirror read remains. Source text,
style, numbering/layout facts and identity are read from `SourceDocument`.

Slim cannot be retired yet. The three demotion operations remain ordered,
mutable compatibility policy; the legacy `HeaderExtractionPipeline` still has
Slim-shaped consumers; and evaluation, repair, writeback, and PDF alignment
components retain explicit compatibility contracts. Test-only callers are
migration cost, not production blockers.

The correct readiness result is `READY_FOR_PARTIAL_DEPRECATION`: the facade can
be documented and bounded as compatibility-only, but deletion or normal-path
retirement requires the exit criteria in the artifact, especially a new
boundary for demotion state and owned migration of legacy/eval callers.

No production behavior changed and no provider was called.
