# A99 Identity Promotion Benchmark v2 — Request-Path Parity

Status: **`BLOCKED_ON_PRE_VERIFIER_SCALABILITY`**

This phase closes request-path parity only. It made `0` provider calls, read `0` relation Gold files, and did not mutate v1 or retrieval semantics.

## Live request path

- DTO: `HdsaIdentityPairVerificationRequest`.
- Shared builder: `HdsaCanonicalPairVerifierRequestBuilder` (`020d2df158f601af1ee1bdb376764556ea6111de380f5f368caa3dddbd229a25`).
- Serializer: `System.Text.Json`, Web defaults, `WriteIndented=true`; UTF-8 bytes; SHA256 over those exact bytes.
- Provider envelope/prompt/configuration remain owned by the existing live runner; this task does not change them.
- Live mechanics were DOC-0205-specific; v2 keeps them data-driven and records the existing source paths `src/DocxHeaderExtractor.Eval/ReasoningRetention/HdsaDeterministicCandidatePairVerificationLiveRunner.cs` and `src/DocxHeaderExtractor.Eval/ReasoningRetention/HdsaGlobalIdentityRetrieveVerifyLiveRunner.cs`.

## Parity

- Historical DOC-0205 immutable request hash evidence: `11/11` hashes match; exact historical bytes were unavailable, so byte parity is not claimed.
- Cross-document samples (DOC-0133, DOC-0252, DOC-0123): `12/12` shared-builder byte/hash pairs match.
- Manifest reconstruction samples: `True`; every frozen entry was guarded during freeze.

## V2 freeze

- Source units: `6,538` across `3` documents.
- Candidate count unchanged: `95,999`; SHA256 `d9718a6fa67ad7b2dea68959517605751d9b2dfd207ed3036d3d1e0a111e2b4d`.
- Request count: `95,999`; request manifest SHA256 `9d93fcfeca621171499bd3114b9629d10ddb8644984c468e4cbbcbf2fdd59d11`.
- Exact request bodies persisted: `false`; deterministically reconstructible: `true`.
- Candidate set is an immutable v1 reference; no candidate generator/pruning/ranking change was made.

## Execution safety

- Dry-run executor reads v2 manifest, resolves frozen source/candidate inputs, rebuilds through the shared builder, and verifies SHA256 and byte length before transport.
- The current historical live runner is not silently claimed to consume the v2 manifest; provider execution remains prohibited until the scalability design is approved.
- SHA mismatch and byte-length mismatch fail closed before network.
- Dry-run provider calls: `0`.

## Remaining gate

`PIPELINE PARITY = CLOSED` for the shared request construction. `PRE-VERIFIER SCALABILITY = OPEN`: v2 deliberately retains 95,999 candidates/requests and adds no pruning or context reduction.

Next recommendation: design a new source-only v3 deterministic retrieval/ranking stage; do not execute v2 against the provider.
