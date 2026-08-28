# ARCH-4M Slim Blocker Boundary Resolution

ARCH-4M resolves the first blocking dependency for the six deferred logical
components from ARCH-4L. It is an audit/proof milestone only; no caller was
migrated and no production behavior changed.

The first blockers are intentionally distinct:

- `HeaderExtractionPipeline`: `LEGACY_OUTPUT_CONTRACT`
- Repair workflows: `WRITEBACK_MAPPING_BOUNDARY`
- Evaluation/replay: `TOC_BOUNDARY`
- Outline/product writeback: `WRITEBACK_MAPPING_BOUNDARY`
- Three `Demote*` operations: `DEMOTION_BOUNDARY`
- Test fixtures/probes: `MULTIPLE_COUPLED_BLOCKERS`

Secondary blockers are retained separately so solving one boundary is not
mistaken for making the whole component migratable. No component has an
unexplained blocker.

The dependency order observed in the current writers/readers is source
identity and numbering/style facts first, then TOC state where consumed,
ordered demotion compatibility state, writeback mapping, and finally legacy
output contract retirement. The three demotions remain unchanged and are not
reclassified from ARCH-4E5.

The next boundary is `WRITEBACK_MAPPING`. It has the clearest independent
concept and directly unblocks two logical production components, repair and
outline/product writeback. It is a design target only; ARCH-4M does not
introduce `IWriteback`, `ISlimState`, or another naming-only abstraction.

Normal authority Slim references remain zero. Provider calls are zero.
