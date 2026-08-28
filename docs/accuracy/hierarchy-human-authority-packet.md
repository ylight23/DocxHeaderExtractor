# Hierarchy Human Authority Packet

Status: `READY_FOR_HUMAN_ANNOTATION`

The packet preserves 422 occurrence-level seeds across 4 documents. Identity is
`documentSha256 + sourceLineIds + occurrenceId`; duplicate text is not an identity key.

The seeds come from model-assisted silver only and are explicitly `PENDING_HUMAN_ANNOTATION`. No
level, parent, scope, type, or path value is inferred. Every unannotated field is `NOT_OBSERVABLE`.
Consequently, `joinableHumanAuthorityOccurrences=0` and hierarchy accuracy remains
`NOT_OBSERVABLE` until a human annotates the packet.

`PROVIDER_CALLS=0` and `PRODUCTION_CODE_CHANGED=false`.

Output artifact: `eval/accuracy/hierarchy-human-authority-packet.v1.json`.
