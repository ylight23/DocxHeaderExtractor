# V4R2 P05 boundary / ownership attribution audit

Provider-free audit. Gold and production artifacts were read-only; providerCalls = 0.

Accepted execution leaves were checked against original parent ownership sets. Ordinal 030 remains two independent adaptive child leaves; their claims were not merged.

## Attribution counts

- `FN_OWNER_OMISSION`: 13
- `FN_PRESENT_ONLY_IN_NEIGHBOR_HALO`: 1
- `FN_CROSSES_PRIMARY_BOUNDARY`: 0
- `FN_CROSSES_ADAPTIVE_SPLIT_BOUNDARY`: 0
- `FP_NEAR_BOUNDARY`: 20
- `FP_INTERIOR`: 41
- `PARTIAL_BOUNDARY_RELATED`: 1
- `PARTIAL_INTERIOR`: 4

## OOS rejects

- SRC-089: 8 (expected 8)
- SRC-095: 23 (expected 23)
- SRC-089: duplicate-owner=6, Gold-exact=4, Gold-overlap=8, non-Gold=0, score-changing-if-reowned=8.
- SRC-095: duplicate-owner=16, Gold-exact=8, Gold-overlap=9, non-Gold=14, score-changing-if-reowned=13.

No CONTEXT_INSUFFICIENT label was inferred. Split-boundary proximity is reported as execution evidence only.

A matched FIXED_OWNED_COUNT_120 + reasoning=medium control remains a separate future experiment; this audit does not alter or promote P05.
