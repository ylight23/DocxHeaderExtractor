# Semantic-function membership replay v1

This is an offline measurement over the immutable raw replies of `LLM_SEMANTIC_PILOT_V1` (V2) and `LLM_SEMANTIC_EXCLUSION_ARM_V1` (V3). It made zero provider calls and changed neither production code nor a request contract.

The complete, hash-pinned result is [replay-score.v1.json](../../eval/a99-closed-loop/semantic-function-membership-replay-v1/replay-score.v1.json).

## Frozen projection

Before any Gold is opened, every raw `semanticRole` is mapped to one of four functions:

- `STRUCTURAL_UNIT` and `DOCUMENT_IDENTITY` derive membership.
- `NON_STRUCTURAL` and `UNMAPPABLE` derive non-membership.
- Generic free-form `heading` is deliberately `UNMAPPABLE`, not structure. Treating it as structure would retain the escape hatch the proposed closed vocabulary must remove.

The replay only changes `semanticRole -> semanticFunction -> derived membership`. It reuses the existing canonicalizer and exact binder with untouched `sourceParts`, verbatim text, aliases, spans, claim boundaries, and binder identity. Gold is opened only after that candidate set is fixed.

## Scores

| Lane | Document | Baseline TP / FP / FN / F1 | Replay TP / FP / FN / F1 | F1 delta |
| --- | --- | --- | --- | ---: |
| V2 raw | SRC-089 | 28 / 10 / 8 / 0.7567 | 12 / 5 / 24 / 0.4528 | -0.3039 |
| V2 raw | SRC-095 | 94 / 160 / 9 / 0.5266 | 66 / 91 / 37 / 0.5077 | -0.0189 |
| V3 raw | SRC-089 | 25 / 28 / 11 / 0.5618 | 7 / 2 / 29 / 0.3111 | -0.2507 |
| V3 raw | SRC-095 | 94 / 143 / 9 / 0.5529 | 79 / 90 / 24 / 0.5809 | +0.0280 |

Every mapped `NON_STRUCTURAL` contradiction is eliminated by construction: V2 goes 4 -> 0 on SRC-089 and 50 -> 0 on SRC-095; V3 goes 0 -> 0 and 51 -> 0. This establishes the representational benefit, but not a safe legacy-role bridge.

## What remains on SRC-095

The source-review breakdown is reporting only; it does not participate in projection. The decisive result is that **87 contents-entry false positives remain in both V2 and V3 replays**. The raw model called them structural roles, so role projection has no information with which to reject them.

| Lane | Furniture / caption / table-header false positives removed | Index entries retained | Contents entries retained |
| --- | ---: | ---: | ---: |
| V2 raw | 43 | 0 | 87 |
| V3 raw | 29 | 2 | 87 |

Thus the V3 gain on SRC-095 comes from removing some correctly-labelled non-structural items. It cannot cure occurrences already semantically classified as `section` or `section-heading`.

## SRC-089 assembly checks

The replay never generates a new claim: replay identities are a subset of baseline identities, and the title-block part fingerprint is unchanged in every lane. The four colon-clause labels remain zero exact matches.

However, the strict legacy bridge drops raw generic `heading` entries and therefore damages assembly:

| Lane | Articles exact | Chapter labels over titles exact |
| --- | --- | --- |
| V2 raw baseline -> replay | 23/26 -> 10/26 | 5/5 -> 2/5 |
| V3 raw baseline -> replay | 21/26 -> 6/26 | 4/5 -> 1/5 |

## Decision gate

1. **V2 semantic roles do not carry enough information for a safe membership projection.** Precision improves, but recall and SRC-089 multipart assembly regress sharply.
2. **Projection alone is not sufficient.** The retained 87 contents claims are already labelled as structural by the model.
3. **A single authority is still necessary.** It removes the independent `isHeading`/non-structural-role contradiction by construction, instead of trusting a free-text role name.

Do not add this mapping as a production post-filter and do not alter V2. If a new contract version is authorized, it must supply a closed semantic-function vocabulary, derive membership from that vocabulary, and omit both `isHeading` and a generic `heading` escape value. That future contract still needs a provider-backed experiment; this replay neither simulates nor authorizes one.
