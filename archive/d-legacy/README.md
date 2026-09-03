# D legacy diagnostic evidence archive

Source worktree: `D:\DocxHeaderExtractor`
Source branch: `diagnosis/span-production-canary`
Source HEAD: `c6059270dadf47d9a432ab7409a3f1bbbe4585c1`
Salvage base: `5a61dc0`
Salvage branch: `integration/d-salvage`

This directory preserves historical diagnostic evidence from the
pre-DocumentProcessing pipeline worktree.

The files under `original/` preserve their Git blob contents exactly.
See `blob-map.tsv` for source blob identity.

These artifacts are historical evidence only.

They MUST NOT be interpreted as current production contracts.

Not restored:

- `DocxHeaderExtractor.Core.Pipeline` compatibility surface
- legacy diagnostic probe source
- `Generate-004AuthorityFirstLoss*.ps1` generators
- stale TODO handoff changes
- `docs/handoff-2026-08-28.md`

The current `DocumentProcessing` pipeline remains authoritative.
