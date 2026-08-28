# Ranking Inversion Diagnosis

Offline replay over frozen N3 candidate/ranking snapshots. Provider calls and production changes are both zero.

- `K=160`; `REMEDIATION_JUSTIFIED=NO`
- `SAME_RANKING_INVERSION_CAUSE=NOT_PROVEN`
- Pairwise competitors are selected above each lost heading with score >= heading score - 0.10, capped at eight.

## 004

`candidateCount=2653`, `reviewed=83`, `reviewedRecoveredAt160=55`, `losses=28`, `owner=UNRESOLVED`

| signal ablation | reviewedRecoveredAt160 | reviewedDisplacedAt160 | netReviewedGain | candidateRankDelta | collateralRankChanges |
|---|---:|---:|---:|---:|---:|
| `labelled_numbering_marker` | 0 | 54 | -54 | 785 | 2653 |
| `unlabelled_numbering_prefix` | 0 | 0 | 0 | -22 | 2494 |
| `standalone` | 2 | 0 | 2 | -29 | 2653 |
| `marker_title_composite` | 0 | 0 | 0 | -4 | 213 |
| `canonical_marker_title` | 0 | 1 | -1 | 2 | 2652 |
| `layout_prominence` | 0 | 0 | 0 | 0 | 1037 |
| `opens_content` | 0 | 0 | 0 | 0 | 2632 |
| `table_scope` | 2 | 0 | 2 | -47 | 2582 |
| `running_page_scope` | 0 | 0 | 0 | 0 | 0 |
| `header_footer_zone` | 2 | 0 | 2 | -14 | 2591 |
| `long_marker_body_window` | 8 | 0 | 8 | -369 | 2652 |

Lost headings and pairwise inversion evidence are serialized in the JSON artifact.

## 030

`candidateCount=3457`, `reviewed=209`, `reviewedRecoveredAt160=9`, `losses=200`, `owner=UNRESOLVED`

| signal ablation | reviewedRecoveredAt160 | reviewedDisplacedAt160 | netReviewedGain | candidateRankDelta | collateralRankChanges |
|---|---:|---:|---:|---:|---:|
| `labelled_numbering_marker` | 11 | 9 | 2 | -119 | 3457 |
| `unlabelled_numbering_prefix` | 0 | 0 | 0 | 165 | 3354 |
| `standalone` | 2 | 0 | 2 | 198 | 3457 |
| `marker_title_composite` | 0 | 0 | 0 | 5 | 1981 |
| `canonical_marker_title` | 0 | 0 | 0 | 7 | 3451 |
| `layout_prominence` | 0 | 0 | 0 | -2 | 935 |
| `opens_content` | 1 | 0 | 1 | -89 | 3457 |
| `table_scope` | 0 | 0 | 0 | 5 | 3023 |
| `running_page_scope` | 0 | 0 | 0 | 0 | 0 |
| `header_footer_zone` | 1 | 2 | -1 | 19 | 3403 |
| `long_marker_body_window` | 2 | 0 | 2 | -26 | 3457 |

Lost headings and pairwise inversion evidence are serialized in the JSON artifact.

## 043

`candidateCount=2038`, `reviewed=42`, `reviewedRecoveredAt160=3`, `losses=39`, `owner=UNRESOLVED`

| signal ablation | reviewedRecoveredAt160 | reviewedDisplacedAt160 | netReviewedGain | candidateRankDelta | collateralRankChanges |
|---|---:|---:|---:|---:|---:|
| `labelled_numbering_marker` | 2 | 3 | -1 | 332 | 2037 |
| `unlabelled_numbering_prefix` | 0 | 0 | 0 | -22 | 1389 |
| `standalone` | 0 | 0 | 0 | 180 | 2030 |
| `marker_title_composite` | 0 | 0 | 0 | -12 | 1999 |
| `canonical_marker_title` | 0 | 0 | 0 | -18 | 2037 |
| `layout_prominence` | 0 | 0 | 0 | -2 | 1513 |
| `opens_content` | 0 | 0 | 0 | -33 | 2032 |
| `table_scope` | 0 | 0 | 0 | 32 | 1900 |
| `running_page_scope` | 0 | 0 | 0 | 0 | 0 |
| `header_footer_zone` | 2 | 0 | 2 | -249 | 2020 |
| `long_marker_body_window` | 0 | 0 | 0 | 96 | 2004 |

Lost headings and pairwise inversion evidence are serialized in the JSON artifact.

## 058

`candidateCount=1884`, `reviewed=41`, `reviewedRecoveredAt160=13`, `losses=28`, `owner=UNRESOLVED`

| signal ablation | reviewedRecoveredAt160 | reviewedDisplacedAt160 | netReviewedGain | candidateRankDelta | collateralRankChanges |
|---|---:|---:|---:|---:|---:|
| `labelled_numbering_marker` | 2 | 0 | 2 | -21 | 1884 |
| `unlabelled_numbering_prefix` | 0 | 2 | -2 | 5 | 1755 |
| `standalone` | 0 | 10 | -10 | 87 | 1884 |
| `marker_title_composite` | 1 | 0 | 1 | -2 | 1542 |
| `canonical_marker_title` | 2 | 1 | 1 | 18 | 1572 |
| `layout_prominence` | 2 | 2 | 0 | 14 | 1840 |
| `opens_content` | 2 | 0 | 2 | -31 | 1881 |
| `table_scope` | 3 | 10 | -7 | -461 | 1882 |
| `running_page_scope` | 0 | 0 | 0 | 0 | 0 |
| `header_footer_zone` | 0 | 10 | -10 | 4 | 1872 |
| `long_marker_body_window` | 2 | 7 | -5 | -75 | 1881 |

Lost headings and pairwise inversion evidence are serialized in the JSON artifact.

## Cross-document recurrence

| document | reviewed headings | reviewed at 160 | ranking losses | budget cutoff | same inversion cause |
|---|---:|---:|---:|---|---|
| `004` | 83 | 55 | 28 | PROVEN | NOT_PROVEN |
| `030` | 209 | 9 | 200 | PROVEN | NOT_PROVEN |
| `043` | 42 | 3 | 39 | PROVEN | NOT_PROVEN |
| `058` | 41 | 13 | 28 | PROVEN | NOT_PROVEN |

## Conclusion

Budget-cutoff recurrence is present, but the evidence does not prove one recurring ranking-inversion cause; ranking remediation is not justified.
