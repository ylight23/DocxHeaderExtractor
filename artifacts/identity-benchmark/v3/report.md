# A99 Identity Benchmark v3A — Source-only shortlist freeze

Status: **`READY_FOR_V3_RETRIEVAL_EVALUATION`**

This phase is source-only. It read the frozen v2 broad candidate lineage, made 0 provider calls, opened 0 Gold files, and did not change request context or verifier semantics.

## Input

- Source units: `6,538` across `3` documents.
- Broad candidates: `95,999`; SHA256 `d9718a6fa67ad7b2dea68959517605751d9b2dfd207ed3036d3d1e0a111e2b4d` (same as v2).
- Candidate generation/configuration: unchanged; no Gold/model output was read.

## Source-only ranking contract

- Lexicographic order: evidence tier descending, source document-order distance ascending, stable pair ID ascending.
- Tier 3: adjacency plus normalized-text affinity; tier 2: adjacency; tier 1: normalized-text affinity.
- `NORMALIZED_TEXT_AFFINITY` is retained as a signal, never used as a blanket veto.
- Dominance is source-only Pareto dominance within shared occurrence dimensions; pair-level pruning requires domination at both endpoints.
- Fixed operational budgets: `8` per occurrence, `12,000` per document, `18,000` total.
- Canonical pair ordering is retained; symmetric identity pairs are requested once and continuation direction remains a verifier response field.

## Reduction

- After dominance: `7,659`; dominated: `88,340` (pair-dominated: `88,340`).
- Shortlisted: `7,659`; reduction: `92.0218%`.
- Logical request bytes: `7,026,603,795`; estimated input tokens at 4 bytes/token: `1,756,650,948`.
- Manifest dry-run: `7,659` entries, rejected `0`, provider calls `0`.
- Detailed degree/reason/per-document statistics are in `scalability-report.json`.

## Request freeze

- Shortlist SHA256: `b73700755a6ebb4110d43b4b815c64b23a4f1ffa7be284288da60c826e51adda`; request manifest SHA256: `494500ba054af983128d48dd58bf2f6164f449f584e57649d90f4b65c887474e`.
- Requests use `HdsaCanonicalPairVerifierRequestBuilder` from v2; exact bodies are not persisted and are deterministically reconstructible.
- Reconstructed SHA and byte length are checked before any hypothetical transport.

## Firewall

- Gold: not loaded; relation labels/confidence/diagnostics are not inputs.
- Model: not used for features or ranking.
- Context projection: unchanged.
- Every broad candidate has an explicit disposition: `SHORTLISTED`, `PRUNED_DOMINATED`, or `PRUNED_BUDGET`.

## Gate

`READY_FOR_V3_RETRIEVAL_EVALUATION`

Next step: open the frozen user-reviewed identity Gold only in a separate v3B task and score candidate recall. Do not tune this frozen v3A artifact during that evaluation.
