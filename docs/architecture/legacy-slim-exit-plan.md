# ARCH-4J Legacy Slim Exit Plan

ARCH-4J is an audit-only closure based on `c5309a5`. The repository-wide
static census found 75 production files and 95 test files referencing
`DocxSlimExtractor`, `SlimDocument`, `SlimParagraph`, or the compatibility
boundary. These are file-level counts; a file can contain several callers and
one caller can belong to more than one operational route.

The important boundary is already clean: `AuthorityExtractionPipeline` has
zero dependency on the legacy `Extract()` result and uses `ExtractForAuthority`.
The remaining Slim references belong to legacy runtime, repair/evaluation,
writeback, OpenXML implementation internals, or tests. No normal-authority
Slim caller remains and no unexplained normal Slim read remains.

## Exit Map

- Source-only evaluation consumers should migrate to `SourceDocument`.
- Mutable demotion and compatibility consumers should migrate to a bounded
  `SlimCompatibilityContext` or a narrower demotion-state boundary.
- Repair and writeback should receive an explicit `SourceIdentity` plus
  `WritebackMapping`, rather than importing mutable Slim state into normal
  application code.
- Legacy `HeaderExtractionPipeline` remains temporarily because it still owns
  a Slim-shaped runtime contract.
- Tests that construct Slim only as a convenient fixture can move to a source
  or compatibility fixture builder; tests that verify legacy behavior remain
  compatibility tests.

## Blockers and Levels

`DemoteCoverPageBlock`, `DemoteInlineEmphasis`, and
`DemoteRunsWithoutOwnProse` remain explicit compatibility blockers. ARCH-4J
does not move or rewrite them. This means Level 1, normal-path retirement, is
complete; Level 2 runtime retirement and Level 3 full retirement are not.

The appropriate readiness decision is `READY_FOR_PUBLIC_API_DEPRECATION`.
Slim can now be documented as compatibility-only and its public legacy entry
point can be deprecated with migration ownership. It is not ready for removal.

No production behavior changed and no provider was called.
