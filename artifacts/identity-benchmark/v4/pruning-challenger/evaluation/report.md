# A99 Identity Retrieval V4E-B - frozen pruning retention evaluation

Status: **READY_FOR_V4_PROVIDER_SCALE_DECISION**

V4E-A integrity passed before the user-reviewed Gold was opened. This phase is evaluation-only: no retriever, pruner, ranking, request, promotion, model, or provider execution was changed.

## Freeze integrity

- V4E manifest SHA256: f7fc8328f0a3287d6d6d2a8977fbd55fc94b51fb4d43dfb1dddb38f2a3e5ebe6.
- V4E shortlist SHA256: 41036e1f4eea349fdc68dcac13932b00b377774f04b2c58ab184e5e29061ae12; count: 7702 (expected 7702).
- V3 compatibility: PASS; frozen V3 shortlist reproduced byte/SHA-identically.
- Gold SHA256: 1c50754951b89ec48a0b0016db3e6079afd088bd0fb20a08ac60e10c41e6fae2; Gold opened after integrity: true.

## Pipeline comparison

| Lane | Candidate volume | Positive result |
| --- | ---: | ---: |
| V3 broad | 95,999 | 1/3 recall |
| V4 broad | 96,069 | 3/3 recall |
| V4E shortlist | 7,702 | 3/3 retention |

## Cases

| Case | Relation | V3 broad | V4 broad | V4-only | Disposition | Shortlisted | Outcome |
| --- | --- | --- | --- | --- | --- | --- | --- |
| IR-018 | DISTINCT_SEMANTIC_NODE | True | True | False | SHORTLISTED | True | RETAINED_V3_EXISTING |
| IR-019 | CONTINUATION_OF | False | True | True | SHORTLISTED | True | RETAINED_V4_ONLY |
| IR-020 | SAME_SEMANTIC_REPEAT | False | True | True | SHORTLISTED | True | RETAINED_V4_ONLY |
| IR-021 | SAME_SEMANTIC_REPEAT | True | True | False | SHORTLISTED | True | RETAINED_V3_EXISTING |
| IR-022 | DISTINCT_SEMANTIC_NODE | True | True | False | SHORTLISTED | True | RETAINED_V3_EXISTING |

IR-018 and IR-022 are DISTINCT_DIAGNOSTIC_COVERAGE only; they are excluded from the positive denominator and are not treated as negatives.

## Firewall

GoldOpenedAfterFreeze=true; MODEL_CALLS=0; PROVIDER_CALLS=0; V4E artifacts mutated=false.

## Gate

READY_FOR_V4_PROVIDER_SCALE_DECISION
