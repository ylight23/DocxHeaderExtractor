# A2 Candidate Boundary Repair Feasibility

Status: `REMEDIATION_NOT_JUSTIFIED`

This was a model-free, test-only counterfactual. Production candidate grouping, ranking, source
authority, gold/silver labels, and provider configuration were unchanged. Provider calls: `0`.

## Frozen Classes

The 004 first-loss authority was kept separate:

- `LINE_GROUP_BOUNDARY_SPLIT`: 9 occurrences.
- `LINE_GROUP_ABSORBED_OR_TRUNCATED`: 1 occurrence (`004/section/5`).

The absorbed/truncated occurrence was audited separately and was not fed into the split repair.

## Counterfactual Policy

The replay retained every existing grouping predicate and added one test-only continuation condition:
a marker-led block may continue into an adjacent uppercase/title-shaped line when source order and
visual proximity are compatible. It retained the production four-line limit and had no
document-, page-, or text-specific exception.

The replay then rebuilt the existing wide candidate producer, supplement producer, deduplication,
candidate contexts, and deterministic ranker. Scores and ranks were recomputed; observed ranks were
not copied from the earlier upper-bound simulation.

## Result

| Document | Baseline candidates | Counterfactual | Delta | Reviewed present | Counterfactual present | Selected @160 | Counterfactual @160 |
|---|---:|---:|---:|---:|---:|---:|---:|
| 004 | 2653 | 2662 | +9 | 83 | 88 | 55 | 59 |
| 030 | 3457 | 3462 | +5 | 209 | 209 | 9 | 9 |
| 043 | 2038 | 2044 | +6 | 42 | 42 | 3 | 3 |
| 058 | 1884 | 1903 | +19 | 41 | 41 | 13 | 13 |
| **Total** | **10032** | **10071** | **+39** | **375** | **380** | **80** | **84** |

The policy recovered 5 of the 47 reviewed candidate misses in this replay. It did not recover all
9 004 split cases, and it introduced 39 candidates across the four documents. No reviewed heading
was lost from the measured candidate population or selected-at-160 cohort, but the candidate-cost
increase means the frozen promotion gate is not met. This is not a production remediation result.

The prior 18-case boundary lineage remains useful recurrence evidence across `004`, `030`, `043`,
and `058`; A2 does not reinterpret that artifact as proof that this particular policy is safe.

## Decision

`REMEDIATION_JUSTIFIED = NO`

The repair remains a feasibility diagnosis only. Do not implement it in
`PdfSemanticBlockGrouper.Build`, do not combine it with the absorbed/truncated case, and do not
open a prompt, ranker, or provider experiment from this result.

Artifact: `eval/accuracy/candidate-boundary-repair-feasibility.v1.json`.
