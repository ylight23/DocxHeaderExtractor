# P7-D2.3 scoring and cohort freeze gate

Status: **SCAFFOLD IMPLEMENTED; REAL COHORT NOT FROZEN**. This gate is independent
of exact tokenizer mapping. It grants no inference authorization or production
promotion. The 84-document source pool is not an approved cohort or benchmark.

`P7CohortScoringFreeze` rejects incomplete manifests and returns explicit readiness
gaps. Complete manifests are defensively copied, canonically ordered and hashed.
Its tests use synthetic identities/approvals; they do not certify real annotations.
Hash checks establish identity syntax and cross-record consistency, not truth of
an approval. Independently approved source review, policy and Gold records must
exist and their bytes must be pinned before populating a real manifest.

## Required independent records

- Unique source documents/content hashes; source occurrence universe, store and
  P05 pack hashes; exact source aliases and explicit prior-exposure labels.
- Source-only reviewed cases for administrative document, multicolumn table,
  borderless table, multiline heading, cross-page heading, two-column document,
  no-table document, and genuine document heading inside a table cell.
  Every case binds to the reviewed document, snapshot/universe and source aliases.
  File names, model output and table geometry alone cannot supply these reviews.
- Per-document immutable Gold and independent approval hashes, bound to that
  document's source snapshot/universe. This task does not read or modify Gold.
- Approved title/subtitle policy, scorer version/hash and policy approval hash.
  Inclusion of document titles and grouping of title/subtitle must be adjudicated
  independently before scoring. Unknown boundaries stay NOT_EVALUABLE.
- Control and each treatment's exact prompt/body/user hashes and protocol versions
  for each document/stage. Actual request-byte verification remains the preflight's
  responsibility, not a claim that a syntactically valid hash proves body parity.
  Freeze upstream ledgers and request cardinalities before downstream transport;
  never select a stage cohort from treatment predictions or Gold.
- Every document/stage/arm needs a stage coverage row: frozen issued-universe or
  ledger hash plus exact expected call cardinality. Actual counts must match;
  one surviving H2-C call cannot hide missing anchor calls. Explicit zero-call
  rows are allowed only for downstream G2A/H2-C with a declared issued-universe
  identity; a nonempty source universe requires at least one F1 call. Requests bind to the
  same issued-universe hash and have unique call handles. These are structural
  checks, not proof that the declared cohort was correctly derived upstream.
  In particular, a zero count and hash do not prove that the real upstream ledger
  is empty; preflight must independently reparse and verify that ledger.
- Held-out claims require independently screened exposure plus a review hash for
  **every** document; UNKNOWN/NOT_SCREENED is not evidence of non-exposure.

## Scoring draft (not adjudicated Gold)

| Lane | Frozen policy requirement |
| --- | --- |
| F1 | Membership correctness/FP/FN separately from extent |
| G2A | Anchor correctness/FP/FN separately from membership |
| H2-C | Exact boundary, under/over and signed/absolute distance on true anchors |
| False G2A anchors | Diagnostic lane; no exact-heading denominator |
| Unadjudicated title/subtitle or authority gap | NOT_EVALUABLE; no invented target |
| Overlap / cross-stage disagreement | Observation only; no deterministic pruning |
| Evidence facts / interpretation | Separate verifier statuses; no semantic certification |

Raw captures must freeze before prediction scoring. Keep fixed source/Gold,
Control bytes, semantics and production authority. Provider counts/budget and
retry policy require a later explicit authorization, not this gate.

## Present blockers

No real shape review set, cohort selection, source snapshot/store/pack manifests,
independent title/subtitle approval, scorer/Gold authority freeze or multi-document
treatment request universe has been approved. Exact tokenizer and provider usage
measurement remain separate gates. **Do not label D2.3 COMPLETE or FROZEN.**
