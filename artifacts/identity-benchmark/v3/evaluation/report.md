# A99 Identity Benchmark v3B — Frozen shortlist retrieval evaluation

Status: **`V3_RETRIEVAL_RECALL_FAILURE`**

This is an offline retrieval evaluation. V3A integrity was verified before opening the user-reviewed Gold; no provider or model call was made, and no V3A artifact was modified.

## Firewall and authority

- Gold authority: `USER_REVIEWED_IDENTITY_GOLD_FROZEN`; `independentABGold=false`.
- Gold SHA256: `1c50754951b89ec48a0b0016db3e6079afd088bd0fb20a08ac60e10c41e6fae2`.
- V3A integrity: `{"manifestSha256":"824b17c125ec59a54e590dd4063ab5d734f601dc10aa1d183db499abb9219fd8","broadCandidateCount":95999,"afterDominanceCount":7659,"shortlistedCount":7659,"broadCandidateSetSha256":"d9718a6fa67ad7b2dea68959517605751d9b2dfd207ed3036d3d1e0a111e2b4d","shortlistSha256":"b73700755a6ebb4110d43b4b815c64b23a4f1ffa7be284288da60c826e51adda","candidateRankingConfigSha256":"e5df9b2d1868d98957825984c80b039956eb53ca21db63fca8d9704c74e30e06","featureInventorySha256":"3479723189addb2469e69acb05e25e318c9ca85df0ade2a8f18ac33601a5092d","requestManifestSha256":"494500ba054af983128d48dd58bf2f6164f449f584e57649d90f4b65c887474e","integrity":"PASS","goldReadCountBeforeCheck":0,"providerCallsBeforeCheck":0}`.
- Gold was evaluation-only; it was not used to generate, rank, prune, or request candidates.
- `MODEL_CALLS=0`; `PROVIDER_CALLS=0`.

## Retrieval metric

- Positive denominator: `3` (IR-019 continuation + IR-020/IR-021 repeat).
- Positive retrieved: `1/3`; positive recall: `33.33%`.
- Distinct cases IR-018 and IR-022 are reported as diagnostics only; no retrieval precision is calculated from five cases.

## Case outcomes

| Case | Relation | Broad | Shortlist | Rank | Outcome | Attribution |
|---|---|---:|---:|---:|---|---|
| IR-018 | DISTINCT_SEMANTIC_NODE | yes | yes | 62235 | RETRIEVED | NONE |
| IR-019 | CONTINUATION_OF | no | no | - | BROAD_RETRIEVAL_MISS | BROAD_RETRIEVAL |
| IR-020 | SAME_SEMANTIC_REPEAT | no | no | - | BROAD_RETRIEVAL_MISS | BROAD_RETRIEVAL |
| IR-021 | SAME_SEMANTIC_REPEAT | yes | yes | 3219 | RETRIEVED | NONE |
| IR-022 | DISTINCT_SEMANTIC_NODE | yes | yes | 3485 | RETRIEVED | NONE |

## Gate

`V3_RETRIEVAL_RECALL_FAILURE`

No provider benchmark is authorized by this artifact when positive retrieval recall is incomplete. V3A remains immutable and any failure is attributed to source-only retrieval/pruning lineage, not to a model decision.
