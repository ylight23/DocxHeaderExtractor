# Hierarchy Accuracy Audit

Status: `NOT_OBSERVABLE`

This audit measures structure after a heading occurrence has already been identified. It does
not measure provider behavior, candidate generation, ranking, production remediation, or gold and
silver label quality. Retrieval loss is not classified as a hierarchy failure.

## Authority

The comparison order is deterministic and fail-closed:

1. marker deterministic fact
2. reviewed hierarchy fact
3. validated structure
4. model proposal

A model proposal cannot override a marker fact. An occurrence is comparable only when its predicted
structure is joined to the authoritative occurrence. Missing joins and missing downstream output
are `NOT_OBSERVABLE`, not failures.

## Result

The offline retained population contains 193 hierarchy fact items across four measured documents.
It contains source scope facts and partial deterministic level/parent facts, but it does not retain
per-occurrence emitted structure or a joinable reviewed hierarchy authority. The comparable
accuracy denominator is therefore zero.

| Classification | Count |
| --- | ---: |
| `FULLY_CORRECT` | 0 |
| `TEXT_CORRECT_TYPE_WRONG` | 0 |
| `TEXT_CORRECT_LEVEL_WRONG` | 0 |
| `TEXT_CORRECT_PARENT_WRONG` | 0 |
| `TEXT_CORRECT_SCOPE_WRONG` | 0 |
| `NOT_OBSERVABLE` | 193 |

## Required Metrics

| Metric | Result | Correct / denominator |
| --- | --- | ---: |
| `LEVEL_ACCURACY` | `NOT_OBSERVABLE` | 0 / 0 |
| `PARENT_ACCURACY` | `NOT_OBSERVABLE` | 0 / 0 |
| `SCOPE_ACCURACY` | `NOT_OBSERVABLE` | 0 / 0 |
| `TYPE_ACCURACY` | `NOT_OBSERVABLE` | 0 / 0 |
| `FULL_PATH_ACCURACY` | `NOT_OBSERVABLE` | 0 / 0 |

The source facts are evidence availability, not accuracy claims: 193 scope facts, 26
marker-deterministic level facts, and 14 partial marker-deterministic parent facts were retained.
They cannot be scored without a joinable predicted structure for the same occurrences.

## Provenance And Scope

Primary input: `eval/accuracy-round6/k160-semantic-run.v1.json`, with its retained role/span lane.
The standalone `keys/hierarchy/076_ICP_IACG08_Minutes_2023.hierarchy.json` is not joined to that
run, so it is not used as a title-based or positional fallback. The regression contract remains
the identity rule: document hash plus source line IDs plus occurrence ID.

Output artifact: `eval/accuracy/hierarchy-accuracy-audit.v1.json`.

`PROVIDER_CALLS = 0` and `PRODUCTION_CODE_CHANGED = false`.
