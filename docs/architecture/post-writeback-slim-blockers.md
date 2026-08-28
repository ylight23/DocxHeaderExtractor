# ARCH-4O Post-Writeback Slim Blocker Re-evaluation

ARCH-4O recomputes the exact six logical components recorded by ARCH-4M at
`d173feb`. No new caller census was introduced. `OutlineWriteback` and
`PdfProductWriteback` are now resolved: their direct writeback Slim
dependency is zero after ARCH-4N.

The remaining graph is not uniformly unlocked. Repair workflows are only
partially resolved because the mapping concept exists but the group still has
numbering/style and ordered mutable-state dependencies. Evaluation/replay
remains TOC-bound. The legacy route remains bound by its output contract and
the three demotion helpers remain bound by the unchanged demotion boundary.
Test fixtures are intentionally mixed and are not a production blocker.

ARCH-4E5 authority is unchanged:

- `DemoteCoverPageBlock` remains on the Slim compatibility boundary.
- `DemoteInlineEmphasis` remains dependent on numbering/style facts.
- `DemoteRunsWithoutOwnProse` remains dependent on ordered Slim state.

The next prerequisite is `BUILD_NUMBERING_STYLE_BOUNDARY`. It is the first
remaining source/derived dependency for repair and demotion sequencing. This
is a design/implementation candidate for a later milestone only; ARCH-4O
performs no migration and does not reopen ARCH-4M or ARCH-4E5.

No provider was called and production behavior did not change.
