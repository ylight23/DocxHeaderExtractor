# canonical-manifest-hash merge ledger

Source: `infra/canonical-benchmark-manifest-hash` @ `cd273a91`

| Responsibility | Classification | Disposition |
|---|---|---|
| canonical manifest hashing and line-ending normalization | SHARED_EVAL | IMPORT after conflict/test review |
| hash tests and remediation artifact | TEST / FROZEN_ARTIFACT | IMPORT together |
| `lane-i-full.err`, `lane-i-full.out` | DIAGNOSTIC | REJECT from git; preserve untracked locally |

This commit is not reachable from canonical `b7854b5` and is ahead of an older base; cherry-pick/merge must be explicit.
