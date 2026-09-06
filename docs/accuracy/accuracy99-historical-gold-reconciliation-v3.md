# Accuracy-99 Historical Gold Reconciliation V3

This checkpoint applies `USER_FINALIZED_STRICT_GOLD_V3` to historical reviewed references without changing extraction behavior. Model assistance is retained as truthful provenance and is not, by itself, a reason to reject user-finalized Gold.

## Result

The frozen DEV cohort remains 15 documents. Six documents have an exhaustive semantic reference and are ready for a semantic baseline. Nine documents still require a human reference. The baseline is therefore not started.

| Gate | Result |
| --- | --- |
| Historical census | PASS |
| User-finalized policy V3 | PASS |
| Active cohort membership changed | NO |
| Holdout touched | NO |
| Provider calls | 0 |
| Semantic baseline ready | NO |
| Remaining blocker | `HUMAN_REFERENCE_REQUIRED` |

The complete normalized records are in:

- `eval/a99-closed-loop/historical-reference-census.v3.json`
- `eval/a99-closed-loop/strict-cohort-reference-reconciliation.v3.json`
- `eval/a99-closed-loop/strict-gold-capability-matrix.v3.json`
- `eval/a99-closed-loop/strict-review-remaining.v3.json`
- `eval/a99-closed-loop/strict-gold-authority-policy.v3.json`

## Canonical Strict Gold V3 materialization

Five of the six promoted references now have immutable per-document artifacts under
`eval/a99-closed-loop/strict-gold-v3/`, with complete `headings[]` arrays. `DOC-0258`
uses the explicitly approved 24-heading projection: the historical 27-entry key is
retained as separate evidence, its four `DAY` navigation entries are excluded, and
the reviewed document title is included from the committed exact-byte packet.

`DOC-0264` is intentionally not materialized. Its approved total and taxonomy are
known, but the exact approved 158-heading list is not present in committed evidence;
promoting source facts or production predictions would violate the fail-closed rule.
The blocker is recorded as
`CANONICAL_GOLD_MATERIALIZATION_REQUIRED=DOC-0264` in the reconciliation and
capability manifests.

The key-derived artifacts preserve semantic, role, and level evaluation only.
`occurrenceEvaluable`, character-span, parent, and hierarchy capabilities remain
false unless the committed reference carries the corresponding exact identity.

## Promoted cohort references

The following six active-cohort references are Strict Gold for semantic evaluation:

- `DOC-0205`: 71 legal content headings from the retained full legal key.
- `DOC-0264`: 158 user-approved headings: 1 title, 10 chapters, 12 sections, and 135 articles.
- `DOC-0001`: 7 exhaustive headings from the deterministic style benchmark, finalized by the user.
- `DOC-0258`: 24 user-approved content headings. The historical exact-byte sibling key has 27 entries because it uses a different taxonomy projection; that difference is preserved, not silently collapsed.
- `DOC-0252`: 27/27 headings from the directly reviewed 15-page PDF key, reused across the exact-byte group.
- `DOC-0256`: 24 headings from the rebased key whose source SHA matches the current document.

These references are semantic-baseline-ready. They do not automatically open character-span, parent, or hierarchy denominators where the retained artifact does not contain those fields.

## Historical model-assisted bundle

The explicitly approved N1.2 bundle is normalized as user-finalized Strict Gold while preserving its original files and metadata:

- `003_Luat_Doanh_nghiep_59-2020-QH14`: 230
- `029_WB_RFP_Works_DesignBuild_2021`: 160
- `042_IDA_Financial_Statements_June_2025`: 159
- `057_Quantitative_Methods_in_Finance_Lecture_Notes`: 777

Their original `MODEL_ASSISTED_SILVER` labels and manifest claims remain unchanged. The normalized authority is `FINAL_AUTHORITY=USER`, provenance is `HUMAN_WITH_MODEL_ASSISTANCE`, and the capabilities remain limited to what the artifacts actually contain. N3.2 silver artifacts remain reference-only pending separate finalization.

## Remaining review queue

Only these nine documents remain in the review queue, ordered by source occurrence count:

`DOC-0201`, `DOC-0185`, `DOC-0265`, `DOC-0116`, `DOC-0219`, `DOC-0216`, `DOC-0122`, `DOC-0200`, `DOC-0243`.

Partial TOC, source-structural, and exact-byte-sibling evidence was retained as diagnostic evidence but was not promoted to exhaustive semantic Gold. No headings, spans, parents, or negatives were invented.

Next action: `HUMAN_REVIEW_DOC-0201`.

## Safety and verification

No holdout labels were inspected or changed. No provider was called. The frozen N15 failure remains untouched. This checkpoint does not run the production baseline, model optimization, or holdout evaluation because the active cohort denominator is not yet complete.
