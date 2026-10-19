# 023 source-comparative canary results

Run date: 2026-10-19. Both experiments used the frozen source snapshot and were
qualification-only. No production code path, heading, hierarchy edge, or Gold
reference was changed. The page-level source review cited here is local
adjudication, not Strict Gold.

## Boundary oscillation cohort

The predeclared three-case cohort completed with three valid responses, all
`finishReason=stop`, and no protocol errors. Total provider cost was USD
0.00154324. Safe report:
`.dhx-evidence/eval/023-boundary-oscillation-canary-20261019/report.json`.

| Case | Model choice | Local source review | Qualification reading |
|---|---|---|---|
| P026 oscillation | B (split) | B is correct | Agrees on the target, but not enough to qualify the adjudicator |
| V17 control | B (merges Mục 1 and Điều 16) | A is correct | Control failure |
| V24 control | B (merges Mục 3 and Điều 23) | A is correct | Control failure |

The model selected B for every case. Since it failed both predeclared controls,
its matching choice on P026 is not reliable evidence that a comparative
adjudicator resolves oscillation. The result does not support an automatic
“reject returned A” policy, nor does it support replacing grouper repair with
the current comparative prompt/model. It does show the current canary prompt
does not pass its source-adjudicated controls.

## Hierarchy comparison

The joint comparison completed in one valid response, `finishReason=stop`,
with no protocol error, at USD 0.00061695. Safe report:
`.dhx-evidence/eval/023-hierarchy-comparative-canary-20261019/report.json`.

| Child | Model parent | Local source review |
|---|---|---|
| P008-L018 (Điều 16) | P008-L013 (Chương II) | P008-L016 (Mục 1) |
| P008-L024 (Điều 17) | P008-L013 (Chương II) | P008-L016 (Mục 1) |

The model chose one parent consistently for both children, but that parent
disagrees with the local review for both. Thus the canary does not support a
whole-graph semantic correction path with this prompt/model. The observed
consistency metric alone would have hidden the shared false parent.

Both source/case hashes matched the pinned a7c0c35 snapshot. Full provider
requests and completions remain in the respective local `private` output
directories; no raw model reasoning is copied into this note. The results are
small-cohort evidence only and must not be generalized to other documents.
