# T4 — T3B reasoning-none Gold comparison

- status: `T4_GOLD_SCORE_COMPLETE`
- score artifact: `openrouter-streaming-t4-t3b-reasoning-none-score.v1.json`
- score SHA256: `BA5EB083F6BD7D720412B8AE01B2A3B9B2EB424D55E72DB55491370D8CDC51A2`
- provider calls during scoring: `0`
- production promotion: `false`
- scorer: `SEMANTIC_FUNCTION_V4_EXACT_BIND_SCORER_V1` / `GENERIC_EXACT_SCORER_V1`

## Aggregate delta vs P05-medium

| arm | TP | FP | FN | F1 |
|---|---:|---:|---:|---:|
| T3B P05 stream reasoning-none | 110 | 51 | 29 | 0.7333 |
| P05 reasoning-medium | 120 | 61 | 19 | 0.7500 |
| delta none - medium | -10 | -10 | +10 | -0.0167 |

## By document

| document | arm | TP | FP | FN | precision | recall | F1 | partial-only | OOS refusals |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| SRC-089 | none | 23 | 3 | 13 | 0.8846 | 0.6389 | 0.7419 | 2 | 9 |
| SRC-089 | medium | 30 | 3 | 6 | 0.9091 | 0.8333 | 0.8696 | 2 | — |
| SRC-095 | none | 87 | 48 | 16 | 0.6444 | 0.8447 | 0.7311 | 3 | 25 |
| SRC-095 | medium | 90 | 58 | 13 | 0.6081 | 0.8738 | 0.7171 | 3 | — |

## SRC-095 residual headline

For `reasoning=none`, SRC-095 false positives break down as:

- `CONTENTS_ENTRY`: 33
- `INDEX_ENTRY`: 12
- `GOLD_HEADING_WRONG_EXTENT`: 3
- other counted families: 0

SRC-095 numbered-section recall is `80/87 = 0.9195`; RFC title identifier exact is `0/1`; index group letters exact is `0/8`.

## Interpretation

`reasoning=none` is still the operational winner, but not a semantic-dominance winner over P05-medium. It improves SRC-095 F1 slightly and reduces false positives, but SRC-089 recall drops enough that aggregate F1 is slightly worse.

Do not promote `reasoning=none` as a production semantic default yet. The clean next step is selective escalation: keep none as the cheap first pass, then use medium only for the subset/conditions that explain the SRC-089 recall regression or other localized semantic misses.

## Rescore after canonicalizer CASE B2 (2026-09-28)

The tables above record the score under the byte-exact canonicalizer the run was first scored with.
`SemanticSourcePartCanonicalizer` now treats a quote whose only difference from its atom is edge
whitespace as the whole atom (see `openrouter-streaming-t5b-escalation-signal.v1.md`). The frozen JSON
is regenerated under the current canonicalizer; the same responses, provider calls `0`, now score:

| arm | TP | FP | FN | F1 |
|---|---:|---:|---:|---:|
| T3B P05 stream reasoning-none | 114 | 53 | 25 | 0.7451 |
| P05 reasoning-medium (unchanged) | 120 | 61 | 19 | 0.7500 |

SRC-089 none: TP 27, FN 9, F1 0.8182. SRC-095 none: FP 50, F1 0.7250 (two genuine FPs on one leaf that
strict refusal had hidden). The movement equals the T5B `P2_NONE_BOUNDARY_WS_REPAIR` replay exactly.
