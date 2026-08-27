# Span production canary - post-conflict role reconciliation V3

This is an offline audit only. It makes no provider call, changes no
production code, and leaves V1 and V2 unchanged.

## What is observable

The semantic checkpoint contains 160 unique IDs: 144 raw `HeadingTopic` and
16 raw `BodySentence`. The retained result reports zero visual proposals. The
result's aggregate proposal resolution is:

| resolution | count |
|---|---:|
| `marker-only-needs-visual` | 8 |
| `no-visual-proposal` | 152 |

The span checkpoint contains 84 unique inputs, with 81 resolved and 3 null.
The latter arithmetic is internally consistent.

## Evidence boundary

`proposalResolution.items` is `null` in the retained result. It does not retain
`ResolvedRole`, per-decision candidate IDs, or the identity of each transition.
Consequently this artifact cannot prove `HeadingTopic -> Uncertain`, cannot
identify the expected 60 occurrences, and cannot compute the post-conflict
heading cohort. The aggregate value 8 is a resolution bucket, not a proven
role-lowering count.

Therefore:

- `HEADING_RETAINED_AFTER_CONFLICT = NOT_OBSERVABLE`
- `HEADING_LOWERED_AFTER_CONFLICT = NOT_OBSERVABLE`
- `MARKER_ONLY_LOWERED = NOT_OBSERVABLE`
- `POST_CONFLICT_HEADING_TOPIC = NOT_OBSERVABLE`
- `POST_CONFLICT_TO_SPAN_ID_MATCH = UNRESOLVED`
- `SPAN_EXECUTION_COHORT_CONSISTENCY = UNRESOLVED`

The route counters `scheduled=160`, `completed=81`, and `timedOut=79` remain
execution counters and are deliberately not used as role counts. Recovering
the requested reconciliation would require a new diagnostic artifact that
persists each proposal-resolution item and its resolved role; this V3 performs
no new provider run.

Provider calls in this task: `0`. Production code changed: `false`.
Remediation performed: `false`.
