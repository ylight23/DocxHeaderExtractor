# VERIFY-6C-PROV2 — Canonical hash mismatch resolution

## Result

All 12 mismatch rows were audited against clean Git authority at
`92cd2d6d3cba29986858d30a91d5da0468044cff`. For every row:

- canonical Git blob identity was resolved;
- canonical content SHA-256 was computed from the clean authority checkout;
- canonical content matched the ENV2 observed hash;
- the manifest hash differed from canonical content.

The causal classification is `STALE_MANIFEST_HASH` for all 12 rows. There is
no canonical hash conflict and no unresolved provenance:

`MISMATCH_ROWS = 12`

`CAUSALLY_CLASSIFIED = 12`

`UNKNOWN = 0`

`CANONICAL_HASH_CONFLICTS = 0`

`PROVENANCE_UNRESOLVED = 0`

`CANONICAL_ARTIFACT_SET_RECONSTRUCTABLE = true`

## 12 versus 1

The original artifact audit reports 12 present-row mismatches. ENV2's
preflight reports one mismatch because its filter covered the 004 silver
artifact, not these 12 rows. All 12 PROV2 rows therefore have
`includedInArtifactAudit = true` and `includedInEnv2Audit = false`; the count
difference is a documented filter-scope difference, not an unexplained hash
conflict.

No artifact was copied, no replacement was generated, no tests or production
code were changed, and no provider was called. VERIFY-6C-MAT may now be opened.
