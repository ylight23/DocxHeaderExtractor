# Marker-Only Policy Audit

Offline audit of the retained semantic run. The source keeps aggregate proposal resolutions only; it does not keep ID-level raw/post-conflict transitions.

- `MARKER_ONLY_TOTAL=13`; `MARKER_ONLY_NEEDS_VISUAL=13`
- `MARKER_ONLY_HEADING_TOPIC=NOT_OBSERVABLE`; `FIRST_LOSS_CONFLICT_RESOLUTION=NOT_OBSERVABLE`
- `TRUE_HEADING_LOWERED=0`; `NOT_JOINABLE=13`
- `CROSS_DOCUMENT_VALID_HEADING_LOSS=NOT_PROVEN`; `REMEDIATION_JUSTIFIED=NO`

## Inventory

| document | regime | marker-only candidates | marker-only HeadingTopic | marker-only-needs-visual | raw role | post-conflict role | class |
|---|---|---:|---|---:|---|---|---|
| `004` | legal | 8 | NOT_OBSERVABLE | 8 | NOT_OBSERVABLE | NOT_OBSERVABLE | CLASS_2 |
| `030` | procurement/contract | 5 | NOT_OBSERVABLE | 5 | NOT_OBSERVABLE | NOT_OBSERVABLE | CLASS_2 |
| `043` | financial | 0 | NOT_OBSERVABLE | 0 | NOT_OBSERVABLE | NOT_OBSERVABLE | CLASS_3 |
| `058` | textbook/book | 0 | NOT_OBSERVABLE | 0 | NOT_OBSERVABLE | NOT_OBSERVABLE | CLASS_3 |

## Authority and safety

No occurrence was called `UNREVIEWED` or false positive. All 13 aggregate-only rows are `NOT_JOINABLE`; occurrenceId, sourceLineIds, candidateId, validator, grounding, and output transitions are unavailable.

The requested keep-HeadingTopic counterfactual cannot be replayed deterministically because the retained artifact lacks the affected IDs and downstream per-occurrence transition evidence. Validator, grounding, and output safety are therefore `NOT_OBSERVABLE`, not assumed safe.

## Conclusion

The evidence does not satisfy the E decision gate: there is no reviewed-proven valid heading loss, no first-loss attribution, no material downstream counterfactual recovery, and no CLASS_1 recurrence. `REMEDIATION_JUSTIFIED=NO`.
