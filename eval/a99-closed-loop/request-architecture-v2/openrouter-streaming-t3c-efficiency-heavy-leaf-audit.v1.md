# T3C efficiency / heavy-leaf audit

- status: `T3C_PROVIDER_FREE_AUDIT_COMPLETE`
- providerCalls: `0`
- goldRead: `false`
- request/packing changes: `false`
- audited ordinals: `31`
- total completion tokens: `46956`
- total reasoning tokens: `0`
- total emitted claims: `443`
- observed transport retry events in persisted logs: `3`
- corrected first-attempt contract-valid from persisted retry logs: `28/31`
- corrected observed provider attempts including transport errors: `34`

## Heavy-six observation

The preselected heavy ordinals 31, 10, 30, 11, 26, 8 account for 32131 / 46956 completion tokens (0.68) and 257 / 443 emitted claims (0.58). This supports treating them as output-heavy leaves with substantial decision volume, not automatic split targets.

## OUTPUT_HEAVY top 6

| rank | ordinal | doc | completion | claims | comp/owned | wall ms |
|---:|---:|---|---:|---:|---:|---:|
| 1 | 31 | SRC-095 | 6024 | 38 | 64.09 | 46425 |
| 2 | 10 | SRC-095 | 5797 | 60 | 60.39 | 45748 |
| 3 | 30 | SRC-095 | 5761 | 40 | 60.01 | 47246 |
| 4 | 11 | SRC-095 | 5273 | 15 | 54.93 | 43403 |
| 5 | 26 | SRC-095 | 4803 | 34 | 50.03 | 42432 |
| 6 | 8 | SRC-095 | 4473 | 70 | 46.59 | 35263 |

## LATENCY_HEAVY top 6

| rank | ordinal | doc | wall ms | completion | claims | wall/owned |
|---:|---:|---|---:|---:|---:|---:|
| 1 | 30 | SRC-095 | 47246 | 5761 | 40 | 492.15 |
| 2 | 31 | SRC-095 | 46425 | 6024 | 38 | 493.88 |
| 3 | 10 | SRC-095 | 45748 | 5797 | 60 | 476.54 |
| 4 | 11 | SRC-095 | 43403 | 5273 | 15 | 452.11 |
| 5 | 26 | SRC-095 | 42432 | 4803 | 34 | 442 |
| 6 | 8 | SRC-095 | 35263 | 4473 | 70 | 367.32 |

## INPUT_HEAVY_BY_BYTES top 6

| rank | ordinal | doc | input bytes | prompt tokens | visible atoms | completion |
|---:|---:|---|---:|---:|---:|---:|
| 1 | 30 | SRC-095 | 71592 | 21787 | 112 | 5761 |
| 2 | 4 | SRC-089 | 71267 | 20156 | 112 | 655 |
| 3 | 31 | SRC-095 | 71158 | 21588 | 102 | 6024 |
| 4 | 5 | SRC-089 | 70788 | 20109 | 112 | 491 |
| 5 | 6 | SRC-089 | 70731 | 20396 | 110 | 678 |
| 6 | 9 | SRC-095 | 70717 | 20488 | 112 | 2576 |

## Interpretation

Do not change packing before T4. The current evidence says `reasoning=none + streaming` is operationally clean, and the heavy leaves mostly look output/decision-volume heavy. T4 should open Gold on the frozen T3B lineage before any cost-aware packing challenger.

Note: persisted retry logs show transport retry events for ordinals 3, 15, 24; this audit records that evidence without mutating the earlier combined summary artifact.
