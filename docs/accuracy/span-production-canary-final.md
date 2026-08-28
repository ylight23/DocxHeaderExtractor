# Span Canary Final Offline Closure

Date: 2026-08-28

This closure reads the existing canary evidence only. It made no provider call,
production change, V1-V3 change, or remediation.

## Artifact Gap

`PDF_STAGE_EVAL_PERSISTS_SPAN_LANE = false`.

Proposal-resolution items existed in memory but were not persisted because
`ShowRawOutput=false`:

- `PROPOSAL_RESOLUTION_ITEMS_IN_MEMORY = true`
- `PROPOSAL_RESOLUTION_ITEMS_PERSISTED = false`
- ID-level post-conflict mapping: `NOT_OBSERVABLE`

## Role Ledger

| Stage | HeadingTopic | BodySentence | Uncertain |
|---|---:|---:|---:|
| Raw | 144 | 16 | 0 |
| Post-conflict | 136 | 16 | 8 |

`MARKER_ONLY_NEEDS_VISUAL = 8`.

## Span Workload

The workload required 136 inputs at batch size 4, or 34 batches. The
checkpoint contains 21 batches / 84 inputs, with 81 resolved and 3 null.
Therefore `SPAN_FULL_COMPLETION = REFUTED`.

The normal value path is impossible because 34 required batches do not equal
21 recorded batches. The fault-fallback path is also impossible: role analysis
without spans cannot explain 79 validated and 69 product headings. The evidence
therefore proves the `SPAN_TIMEOUT_PATH` for this run.

## Partial Preservation

After timeout, 81 checkpoint resolutions remain available and 79 inputs remain
unresolved. Route audit reports `completed=81` and `timedOut=79`.
These are `POST_SPAN_PRESERVATION_COUNTERS`, not semantic role-execution
counters.

The preserved-result path produced:

- 81 preserved spans
- 79 validated
- 78 grounded
- 69 product headings

Thus `H1_ARCHITECTURE_GAP` and `H1_OPERATIONAL_IMPACT` are both **PROVEN**.

## Hypothesis Status

- `CANARY_H2_BUDGET`: `PROVEN_FOR_THIS_RUN`
- `CANARY_H3_PROVIDER`: `UNRESOLVED`
- `CANARY_H4_SEQUENTIAL`: `UNRESOLVED`
- Frozen 004 H2/H3/H4: `UNRESOLVED`, because exact selection identity differs.

Final status: `SPAN_CANARY_FINAL_OFFLINE_CLOSED`.
