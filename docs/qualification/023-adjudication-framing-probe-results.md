# 023 adjudication-framing probe results

Run date: 2026-10-19. Both probes used the exact source, alias-universe and
evidence-store hashes pinned in the preceding qualification runs. No production
or Gold data changed. These observations use local source adjudication as a
comparison reference; that review is not Strict Gold.

## Boundary label swap without trajectory

Six fresh calls completed with valid responses, at USD 0.002557992. Safe report:
`.dhx-evidence/eval/023-boundary-label-swap-canary-20261019/report.json`.

| Case | Normal labels | Swapped labels, normalized to physical partition | Stable? | Local review |
|---|---|---|---|---|
| P026 | ORIGINAL_A | ORIGINAL_A | Yes | ORIGINAL_B (split) |
| V17 | ORIGINAL_B | ORIGINAL_A | No | ORIGINAL_A |
| V24 | ORIGINAL_A | ORIGINAL_A | Yes | ORIGINAL_A |

P026's response no longer follows the earlier all-B pattern once the trajectory
and identifying case label are removed. Its physical choice is stable under
label swap but still disagrees with the local source review. V17 is label
sensitive; V24 is stable and agrees with local review. The evidence suggests
framing affected the earlier cohort response, but this probe changed both the
history/trajectory and the case blinding/prompt together, so it cannot isolate
which framing element caused the shift. It also does not establish that the
adjudicator's P026 choice is source-correct.

## Separate single-child hierarchy calls

Two fresh calls completed with valid responses, at USD 0.000828976. Safe report:
`.dhx-evidence/eval/023-hierarchy-single-child-canary-20261019/report.json`.

| Child | Independent parent |
|---|---|
| P008-L018 (Điều 16) | P008-L016 (Mục 1) |
| P008-L024 (Điều 17) | P008-L016 (Mục 1) |

Both separate judgments match the local source review. This differs from the
previous joint-call result, which selected Chương II for both children. It
supports the concern that presenting both decisions jointly with a consistency
field can alter the judgment. The probe also withheld the old proposed parent,
so the evidence does not isolate the effect of joint scope from the effect of
proposal visibility or consistency framing.

These results justify keeping the qualification probes separate from any
production correction path. They do not prove a general model capability, do
not authorize a graph repair, and do not make the local source review Strict
Gold.
