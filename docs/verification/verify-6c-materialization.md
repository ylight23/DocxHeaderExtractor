# VERIFY-6C-MAT — Materialize canonical artifact set

## Result

`VERIFY_6C_MAT_PASS = true`

The execution environment is the fresh worktree at
`92cd2d6d3cba29986858d30a91d5da0468044cff`. All 683 manifest rows were
verified against canonical Git content. The materialized path and canonical
content SHA-256 match for every row.

- `MANIFEST_ENTRIES = 683`
- `AUTHORITY_RESOLVED = 683`
- `MISSING_BEFORE = 226`
- `MATERIALIZED_FROM_AUTHORITY = 226`
- `MISSING_AFTER = 0`
- `PRESENT = 683`
- `READABLE = 683`
- `AUTHORITY_HASH_MATCH = 683`
- `HASH_MISMATCHES_AFTER = 0`
- `PROVENANCE_UNRESOLVED = 0`
- `STALE_MANIFEST_HASH_ROWS = 12`
- `CANONICAL_CONTENT_REPLACED = 0`

The 12 stale manifest rows were already canonical in ENV2; they were not
overwritten. Verification uses the canonical hashes from PROV2 rather than
the stale manifest values.

## Guards and gates

The focused post-materialization run passed `16/16` tests, including RFC
`5/5`, architecture `2/2`, F `2/2`, and MCP `7/7`. RFC-2 invariants remain
`67/67/0/1.0`. Release build passed with `0` warnings and `0` errors. Temp
root and disk preflight passed, and unexplained worktree changes are `0`.

No artifact was copied from a prior run, no replacement was generated, no
provider was called, and no production code was changed. Full suite execution
was intentionally not performed in MAT.

`VERIFY-6C = CLOSED / READY`

The next permitted task is VERIFY-6B, the canonical integrated full suite.
