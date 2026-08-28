# ARCH-4P Numbering / Style Feature Boundary

ARCH-4P introduces an immutable feature projection from `SourceDocument` for
the numbering and style facts that are already source-owned. The contract is
occurrence-safe through `DocumentId + SourceId` and contains no candidate,
role, score, guessed-level, or demotion decision.

The bounded consumer cutover is the numbering/style portion of
`RepairCorpusAudit.StructureSourceAudit`. Its outline/style and numbering
counters now read `NumberingStyleFeatures` rather than reading those fields
from mutable Slim state. The rest of the repair workflow remains compatibility
aware because it still needs TOC/legacy and mutable state. This is a partial
unblock, not a claim that all repair callers migrated.

`HasBuiltInHeadingStyle`, table role, TOC state, and ordered demotion state are
not silently reclassified as generic style features. They remain explicit
compatibility/deferred inputs. Consequently `DemoteInlineEmphasis` input
coverage is `partial`, so ARCH-4Q should not move that operation yet.

Characterization reports zero numbering/style, source, candidate, role, score,
level, demotion, and route deltas. Focused tests pass `34/34`; Release build
passes with zero errors; provider calls are zero. No production heading
behavior changed.
