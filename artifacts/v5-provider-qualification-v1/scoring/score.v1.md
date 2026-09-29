# V5 Qwen Flash qualification — offline Gold score

Status: `V5_SCORE_COMPLETE_ZERO_CONTRACT_VALID_RESPONSES`

Gold was opened only after `provider/freeze-manifest.v1.json` was committed. No provider call occurred after freeze. All 31 completed responses were rejected by the frozen V5 claim codec (30 missing `subject`/`claimId`; 1 truncated JSON at `finish_reason=length`), so the exact empty-prediction score below is an execution-gate result, not a claim that a valid V5 semantic cohort scored zero.

| document | Gold | TP | FP | FN | precision | recall | F1 |
|---|---:|---:|---:|---:|---:|---:|---:|
| SRC-089 | 36 | 0 | 0 | 36 | 0.0000 | 0.0000 | 0.0000 |
| SRC-095 | 103 | 0 | 0 | 103 | 0.0000 | 0.0000 | 0.0000 |
| aggregate | 139 | 0 | 0 | 139 | 0.0000 | 0.0000 | 0.0000 |

Execution: 31 provider attempts, 31 initial semantic calls, 0 refinements, 0 transport retries, 0 layout/retrieval/visual calls, 2,025,269 prompt tokens, 30,996 completion tokens, 0 reasoning tokens, wall-clock p50 8,943.5 ms / p95 17,352.3 ms, max 54,343.2 ms. Finish reasons: 30 `stop`, 1 `length`. Contract codec invalid: 31; binding refusals: 0; graph conflicts: 0.

Frozen V4 comparison: 118 TP / 56 FP / 21 FN / F1 0.7540 aggregate; SRC-089 F1 0.7887; SRC-095 F1 0.7438. V5 does not support a semantic comparison because no response reached contract-valid prediction status.
