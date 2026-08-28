# VERIFY-6C-ARTIFACT — Benchmark Artifact Manifest

Status: **BLOCKED_ARTIFACT_PROVENANCE**.

The manifest enumerates `683` Git-tracked paths under `bench`, `data`, `eval`, `keys`, and `todo10_8` that may be consumed by the test/evaluation surface. The replay tree contains `457` of them and is missing `226` required paths. Among present paths, `12` do not match the SHA-256 calculated from the current source worktree; the source worktree is dirty, so those hashes are not asserted as commit-authoritative.

Clean-authority reconstruction found all `226` missing rows as Git-tracked
paths at revision `92cd2d6d3cba29986858d30a91d5da0468044cff`. They were not
copied or regenerated. The `12` hash mismatches remain causally `UNKNOWN`:
their observed values came from a dirty source worktree and are not authority.

Required gates:

- `MISSING_AUTHORITY_LOCATED = 226`
- `REGENERATION_JUSTIFIED = false`
- `PROVENANCE_UNRESOLVED = 12`
- `UNKNOWN_REQUIRED_ARTIFACTS = 12`
- `MISSING_REQUIRED_ARTIFACTS = 226`
- `HASH_MISMATCHES = 12`
- `CANONICAL_ARTIFACT_SET_RECONSTRUCTABLE = false`
- `VERIFY_6C_READY = false`
- `noArtifactsCopied = true`
- `noReplacementGenerated = true`
- `providerCalls = 0`

The artifact manifest is path-level and preserves expected/actual hashes where readable. Provenance cannot be reconstructed as a clean revision-level manifest until the artifact-bearing source tree and replay tree are aligned. No tests or production code were changed.

Artifact: `eval/verification/verify-6c-artifact.v1.json`.
