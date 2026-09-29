# T5B escalation signal derivation (provider-free)

Status: `T5B_PROVIDER_FREE_SIGNAL_DERIVATION_COMPLETE` · provider calls `0` · Gold used as policy input `false`

Medium arm = already-accepted P05 reasoning-medium responses, spliced per leaf (replay, no new calls).
Gate: aggregate F1 >= full-medium; SRC-089 F1 within 0.02 of medium; SRC-095 F1 within 0.01 of none; escalated <= 30%.

| policy | TP | FP | FN | F1 | SRC-089 F1 | SRC-095 F1 | escalated | extra medium completion tokens | gate |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---|
| P0_NONE_STRICT | 110 | 51 | 29 | 0.7333 | 0.7419 | 0.7311 | 0/31 | 0 | FAIL |
| P1_MEDIUM_STRICT_ALL | 120 | 61 | 19 | 0.7500 | 0.8696 | 0.7171 | 31/31 | 222303 | FAIL |
| P2_NONE_BOUNDARY_WS_REPAIR | 114 | 53 | 25 | 0.7451 | 0.8182 | 0.7250 | 0/31 | 0 | FAIL |
| P3_ESCALATE_ON_ANY_NONE_REFUSAL | 117 | 49 | 22 | 0.7672 | 0.8182 | 0.7531 | 15/31 | 103238 | FAIL |
| P4_WS_REPAIR_THEN_ESCALATE_ON_RESIDUAL_REFUSAL | 117 | 49 | 22 | 0.7672 | 0.8182 | 0.7531 | 15/31 | 103238 | FAIL |
| P5_MEDIUM_WS_REPAIR_ALL | 120 | 61 | 19 | 0.7500 | 0.8696 | 0.7171 | 31/31 | 222303 | FAIL |
| P6_ESCALATE_ON_OWNED_ALIAS_REFUSAL | 117 | 49 | 22 | 0.7672 | 0.8182 | 0.7531 | 6/31 | 40640 | FAIL |
| P7_WS_REPAIR_THEN_ESCALATE_ON_RESIDUAL_OWNED_REFUSAL | 117 | 49 | 22 | 0.7672 | 0.8182 | 0.7531 | 5/31 | 35502 | FAIL |
| X8_EXPLORATORY_P6_PLUS_LOW_EMISSION_LE2_GOLD_INFORMED | 120 | 61 | 19 | 0.7500 | 0.8696 | 0.7171 | 10/31 | 80429 | FAIL |

## Findings

1. Most of the SRC-089 none-vs-medium loss is not missing reasoning. Under reasoning=none the model
   emits the same multipart claims as medium but prefixes continuation parts' `verbatimText` with a
   space; the canonicalizer refuses them as `TextNotInAtom`. SRC-089 ordinal 2: 7 claims emitted,
   4 refused, all 4 recovered by trimming boundary whitespace when the trimmed quote is the whole atom.
2. T5A undercounted per-leaf FN: it assigned only single-alias Gold identities. Multipart-aware
   ownership assigns every Gold identity (0 cross-leaf, 0 unowned).
3. `NOT_OWNED` refusals (model cites a halo atom) fire on neutral leaves and are not a signal.
   Refusal on an owned alias is: P6/P7 escalate 6/5 leaves and exceed full-medium aggregate F1.
4. SRC-089 ordinal 5 (+3 TP under medium) has no refusal signal. The only probe that catches it
   (<= 2 emitted member claims, Gold-informed threshold) also catches SRC-095 ordinal 30, where medium
   adds 12 FP, and cancels the gain. No generic pre-Gold signal for it was found.
5. Boundary-whitespace repair also binds 2 genuine FPs on SRC-095 ordinal 18 that strict refusal had hidden.

## Decision needed before T5C live

P7 is the best generic policy but fails only the SRC-089-closeness gate (ordinal 5). A live T5C rerun
of P7's leaves would re-measure the same replayed outcome, so it is not run until the gate or the
policy is decided. The whitespace repair is a shared-contract canonicalizer change and needs its own
approval and full suite before production.
