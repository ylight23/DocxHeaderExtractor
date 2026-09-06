# Accuracy-99 Strict Gold Promotion V4

This checkpoint applies explicit user blanket approval to the existing reviewed/reference evidence for the exact 15-document active DEV cohort. It does not change cohort membership, extraction behavior, model labels, holdout data, or N15.

## Authority result

All 15 active documents are now `STRICT_GOLD` with `finalAuthority=USER`, `userFinalApproval=true`, and `promotionReason=USER_BLANKET_FINAL_APPROVAL`. Historical provenance remains recorded per document. No document remains in a human-review queue for this promotion task.

Strict Gold status is intentionally independent of coverage. Six documents retain exhaustive reviewed semantic references. Four retain partial historical key evidence. Five retain only the associated historical/source-first packet metadata because no heading list was stored in committed evidence. Missing headings and negatives are not fabricated.

## Capability result

Whole-document baseline readiness remains `false` because the active cohort is not uniformly exhaustive and does not carry character-span or hierarchy capability. This is a capability blocker, not a Gold-authority blocker. The exact per-document counts and flags are in `strict-gold-capability-matrix.v4.json`.

Canonical artifacts are under `eval/a99-closed-loop/strict-gold-v4/`. Each artifact binds document/source/packet hashes, preserves original reference paths and provenance, and records only materialized historical headings. `DOC-0258` remains the approved 24-heading projection; its historical 27-heading key is not replaced.

## Safety

Provider calls: `0`. Holdout touched: `false`. Baseline: `NOT_RUN`. Frozen N15 is unchanged. The untracked `tests/DocxHeaderExtractor.Tests/TestResults/` directory remains untouched and uncommitted.