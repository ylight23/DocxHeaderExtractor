# VERIFY-6C-PROV — Artifact Authority Reconstruction

Status: **CLOSED**.

The `683` manifest entries were checked against the exact canonical execution revision `92cd2d6d3cba29986858d30a91d5da0468044cff`. All `683/683` are present in the Git tree and are classified `GIT_TRACKED_AT_REVISION`. SHA-256 values were computed from clean revision content, not from the dirty source worktree.

The `226` filesystem-missing entries are therefore:

- `MISSING_BUT_AUTHORITY_LOCATED`: 226
- `MISSING_AND_REGENERATABLE_FROM_FROZEN_INPUT`: 0
- `MISSING_PROVENANCE_UNRESOLVED`: 0

No artifact was copied, generated, or used to mutate the canonical execution tree. Materialization remains a separate VERIFY-6C-MAT task and must copy only the approved exact revision bytes.

## Mismatch Reconciliation

The earlier audit reported 12 mismatches because it compared present replay files with hashes from a dirty source worktree. The ENV2 report covered one selected artifact. The canonical revision comparison reports zero mismatches. Thus the difference is explained by filter and run-revision scope, not by silently choosing one manifest as authority.

- `ENV2_ARTIFACT_HASH_MISMATCHES`: 1
- `ARTIFACT_AUDIT_HASH_MISMATCHES`: 12
- canonical revision mismatches: 0
- `ARTIFACT_REPORTS_RECONCILED`: true
- regeneration justified: false
- provider calls: 0
- production code changed: false

Full path-level manifest and hashes: `eval/verification/verify-6c-provenance-reconstruction.v1.json`.
