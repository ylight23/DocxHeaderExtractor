# DOC-0258 — canonical semantic identity v1

Status: **READY_FOR_USER_APPROVAL_CANONICAL_IDENTITY**

This lane assigns semantic identity only to the 44 exact span-aware heading occurrences frozen at 78af9bf. It does not assign parent, hierarchy, or level.

## Source-only identity freeze

- Input occurrences: 44
- Semantic nodes: 44
- PRIMARY: 44
- REPEAT: 0
- CONTINUATION: 0
- Multi-occurrence nodes: 0
- Identity ambiguities: 0
- Provider/model calls: 0

The traversal is occurrence-first and fail-closed. Each occurrence defaults to a new semantic node; no collapse was forced by the historical semantic total.

## Post-freeze diagnostic

- Historical semantic total: 37
- Difference: 7
- Historical level read: FALSE
- Historical parent read: FALSE

The semantic total was read only after assignments were persisted and is not an identity target. No parent edges or levels were created.
