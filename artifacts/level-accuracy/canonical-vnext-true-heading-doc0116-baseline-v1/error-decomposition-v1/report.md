# DOC-0116 TRUE_HEADING Error Decomposition

Status: `DIAGNOSTIC_COMPLETE`

This is an offline forensic decomposition of the frozen official score. It does
not mutate the prediction, Gold, or official score. Categories are
source-derived diagnostic heuristics; they are not new semantic labels and must
not be promoted directly into production rules.

## Frozen inputs

- Prediction SHA: `0e22e6f83d98694850805988e6805a027976f08f87dc91681e2b28cc40a8805c`
- Gold authority SHA: `83ec5942171bca438f08f58341a5c4cb0f0c92dc46976d13069e784a7d3141b0`
- Official score: TP 85, FP 154, FN 35, F1 47.35%
- Provider calls in this lane: 0

## False positives (154)

| Diagnostic category | Count |
|---|---:|
| OTHER_STRUCTURAL_LABEL | 63 |
| TABLE_STRUCTURAL_LABEL | 50 |
| NUMBERING_OR_STYLE_SIGNAL | 34 |
| FORM_OR_FIELD_LABEL | 4 |
| TOC_LIKE_ENTRY | 2 |
| BODY_PARAGRAPH_FALSE_SIGNAL | 1 |

The dominant pattern is not exact binding failure. It is semantic over-selection
of structural-looking items, especially table labels and numbered/formatting-led
items.

## False negatives (35)

| Diagnostic category | Count |
|---|---:|
| OTHER_TRUE_HEADING | 16 |
| TABLE_STRUCTURAL_LABEL | 7 |
| TOC_LIKE_ENTRY | 6 |
| TABLE_FORM_LABEL | 2 |
| BODY_PARAGRAPH_FALSE_SIGNAL | 1 |
| DUPLICATE_OR_REPEATED_HEADING_OCCURRENCE | 1 |
| FORM_OR_FIELD_LABEL | 1 |
| NUMBERING_OR_STYLE_SIGNAL | 1 |

All 35 are missed Gold occurrences; the categories describe source evidence
around the missed occurrence, not a claim that the Gold item is semantically a
label or TOC entry. In particular, TOC/table context is useful for diagnosing
context-boundary and structural-scope misses.

## Interpretation boundary

The decomposition supports prioritizing precision work around structural labels,
tables, and numbering/style signals. It does not by itself authorize a hard
filter, prompt rewrite, or Gold change. Any intervention should be tested in a
new baseline or holdout lane, while this B0 prediction and score remain frozen.
