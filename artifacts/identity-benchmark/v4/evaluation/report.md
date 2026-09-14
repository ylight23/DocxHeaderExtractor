# A99 Identity Retrieval V4C — frozen V4 broad evaluation

Status: **`READY_FOR_V4_PRUNING_EVALUATION`**

This is evaluation-only. V4B freeze integrity passed before the user-reviewed Gold was opened. No V3 pruning, request, prediction, model, or provider execution was used.

## Freeze integrity

- Integrity before Gold: `PASS`.
- V4 manifest SHA256: `d8a0ce04130328141dcf75569c8fb646596624e328e326db6f52bf06abc78cdb`.
- V4 broad SHA256: `a9aad1afabb0645112f6aefae743d91b8dfb6f8681231881fcf791538053c9a0`.
- V4 broad count: `96069`; V4 additions: `70`.
- Gold SHA256: `1c50754951b89ec48a0b0016db3e6079afd088bd0fb20a08ac60e10c41e6fae2`.

## Retrieval comparison

| Lane | Retrieved | Denominator | Recall |
| --- | ---: | ---: | ---: |
| V3 broad | 1 | 3 | 33.33% |
| V4 broad | 3 | 3 | 100.00% |

Broad volume: `95,999 → 96,069` (`+70`, `0.0729%`). No retrieval precision is calculated because unlabeled candidates are not negatives.

## Cases

| Case | Relation | V3 broad | V4 broad | V4-only | Outcome | Reasons |
| --- | --- | --- | --- | --- | --- | --- |
| IR-018 | DISTINCT_SEMANTIC_NODE | True | True | False | RETRIEVED_V3_EXISTING | NORMALIZED_TEXT_AFFINITY |
| IR-019 | CONTINUATION_OF | False | True | True | RETRIEVED_V4_MULTI_SIGNAL | EXPLICIT_CONTINUATION_VARIANT, SHARED_STRUCTURAL_HEADING_KEY |
| IR-020 | SAME_SEMANTIC_REPEAT | False | True | True | RETRIEVED_V4_MULTI_SIGNAL | SHARED_STRUCTURAL_HEADING_KEY, TERMINAL_ACRONYM_VARIANT |
| IR-021 | SAME_SEMANTIC_REPEAT | True | True | False | RETRIEVED_V3_EXISTING | NORMALIZED_TEXT_AFFINITY |
| IR-022 | DISTINCT_SEMANTIC_NODE | True | True | False | RETRIEVED_V3_EXISTING | NORMALIZED_TEXT_AFFINITY |

IR-018 and IR-022 are DISTINCT diagnostics only; they are excluded from positive recall and no precision is inferred.

## Signal contribution

Counts are descriptive participation of frozen reasons and are not causal ablations. Overlapping reasons are not double-counted as recovered cases.

- Structural key: 2 positive case(s).
- Terminal acronym: 1 positive case(s).
- Continuation marker: 1 positive case(s).

## Firewall

- `GoldOpenedAfterFreeze=true`.
- `MODEL_CALLS=0`; `PROVIDER_CALLS=0`.
- No pruning, request, prediction, threshold, or V4B artifact mutation.

## Gate

`READY_FOR_V4_PRUNING_EVALUATION`
