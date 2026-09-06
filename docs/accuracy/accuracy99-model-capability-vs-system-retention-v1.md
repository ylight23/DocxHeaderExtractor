# Accuracy-99 Model Capability vs System Retention

This checkpoint adds an evaluation-only reasoning-retention harness on top of
a7db1b8b163c0061fe636656c7c938c9b60e14ab.

The comparison is deliberately split into three routes:

- MODEL_CAPABILITY_CEILING: every non-empty parser-owned source occurrence is visible to the model; candidate policy is serialized only as optional evidence.
- PRODUCTION_SYSTEM: the current AuthorityExtractionPipeline remains the production oracle and is not changed by this checkpoint.
- REASONING_PRESERVING_SHADOW: full-context visibility plus deterministic evidence and hard source/span validation, still evaluation-only.

The harness does not receive Gold at runtime. Gold joins only after route execution through
exact source/span identity. It does not log private chain-of-thought; it records compact
decision evidence codes and stage observations.

## Current Status

DIAGNOSTIC_DEV_SUBSET is not yet measured. Six active documents are exhaustive semantic
references in V4, but the committed capability flags currently authorize zero exact
occurrence/span denominators. DOC-0258 contains one span-shaped row, but its artifact still
declares occurrence and character-span evaluation false; that row is not promoted into a
benchmark denominator. DOC-0264 has a user-approved semantic total but no committed occurrence
list. All six therefore remain NOT_EVALUABLE until Gold v4 is enriched with complete exact spans.

The evaluator derives this cohort from the Gold capability flags and exact span rows rather than
from heading totals. No provider run is justified while that denominator is empty.

Provider calls: 0
Holdout touched: false
Production semantic delta: 0
Claim: A99_NOT_MEASURED

The committed information-flow artifact identifies candidate pruning, request membership,
validation/resolution, and task projection as the places where information can be deleted or
semantically mutated. No production gate is relaxed here. Any intervention waits for paired
measurement.
