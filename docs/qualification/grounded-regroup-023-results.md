# Grounded REGROUP cohort: 023, 2026-10-19

The predeclared cohort demonstrates two additional self-contradictory REGROUP verdicts beyond the excluded P021 pilot. Both became grounded ACCEPT of the unchanged partition after one protocol-consistency repair. It does not demonstrate successful changed-partition repair: neither of the two changed suggestions was accepted by the independent verifier.

## Declaration and denominators

Code and declaration were committed and pushed as `eb2d806` before provider calls. Related regression suite: **279 passed**. Declaration SHA256: `819365673be0eb01efce835774671775e343ef8c9232824c517d0744942a4312`.

The fixed universe was all 25 multi-alias VERIFY/C0 scopes from the hash-matched e2bb0f5 audit after excluding the P021-L010..L012 pilot. Single-group scopes were included when they had multiple aliases. No scope was selected by a D outcome. New C0 conversations used historical partitions with default source reading/full extent; they were not exact replays of historical packets or historical selection choices.

| C0 outcome, all declared scopes | Count |
|---|---:|
| Valid REGROUP, all HEADING, no requested dynamic evidence: eligible | 6 |
| ACCEPT: retained but ineligible | 12 |
| MEMBERSHIP_DISPUTE: retained but ineligible | 7 |
| UNRESOLVED / invalid / runtime failure / dynamic-evidence exclusion | 0 |
| Total | 25 |

All 25 C0 conversations completed before a durable cohort lock was written. Every eligible seed packet and private seed hash was fixed before the first D call. No eligible sample-size target, replacement, pilot inclusion, further scope search or second D sample was used. The six eligible seeds all completed D.

## D outcomes: denominator 6

| Predeclared category | Count |
|---|---:|
| REGROUP self-contradiction → protocol repair → ACCEPT current | 2 |
| Valid changed suggestion → independent ACCEPT | 0 |
| Valid changed suggestion → independent reject/unresolved | 2 |
| Persistent self-contradiction at round bound | 0 |
| Membership dispute/unresolved | 0 |
| Direct ACCEPT current at D round one | 2 |
| ACCEPT current after other protocol repair | 0 |
| Other protocol/transport/runtime/budget failure | 0 |

| Seed | Ordered alias scope | D trajectory | Terminal result |
|---|---|---|---|
| V1 | P001-L013..L015 | REGROUP with suggestion equal to input → BOUNDARY_VERDICT_SELF_CONTRADICTORY → ACCEPT at round 2 | Qualified current |
| V17 | P008-L013..L018 | Valid changed suggestion at round 1 → independent REGROUP | Unresolved |
| V19 | P009-L009..L012 | REGROUP with suggestion equal to input → BOUNDARY_VERDICT_SELF_CONTRADICTORY → ACCEPT at round 2 | Qualified current |
| V24 | P011-L001..L004 | Valid changed suggestion at round 1 → independent REGROUP | Unresolved |
| V42 | P017-L031..L034 | ACCEPT at D round 1 | Qualified current; no observed consistency repair |
| V54 | P023-L009..L010 | ACCEPT at D round 1 | Qualified current; no observed consistency repair |

The corrected current partitions were `[[P001-L013,P001-L014],[P001-L015]]` and `[[P009-L009,P009-L010],[P009-L011,P009-L012]]`. Both contradictions were detected by structural comparison, without reading the reason. Each correction happened in the same verifier conversation; neither opened a semantic boundary cycle or called the grouper.

V17 changed `[[P008-L013,P008-L014,P008-L015],[P008-L016,P008-L017],[P008-L018]]` to `[[P008-L013,P008-L014,P008-L015],[P008-L016,P008-L017,P008-L018]]`. V24 proposed merging its two two-line groups into one four-line group. Both had valid structural coverage/order/contiguity but were not independently authorized. The independent verifier retained its existing production contract; its REGROUP is a non-authorization result, not proof that the proposal is Gold-incorrect or free of the old boundary-grounding limitation.

All eight D protocol responses finished with `stop`; all validated D alias memberships remained HEADING. There was no membership-change recovery. The two direct ACCEPT cases are not credited to consistency feedback: D uses a new conversation and grounded prompt, and the cohort provides no causal control separating those differences from model variance.

## Integrity, cost and interpretation

Offline checks matched all six retained private seed hashes and result packet hashes against the pre-D cohort lock. Every D round's input partition matched the frozen C0 proposal. Provider admission counts reconcile exactly: **28 C0 POSTs + 8 D verifier POSTs + 2 independent verifier POSTs = 38**. Actual total cost was **USD 0.019670096** (C0 USD 0.01404534; D plus independent USD 0.005624756). No scope was omitted from the final denominator.

This supports grounded REGROUP as a generic protocol consistency invariant: the previously observed failure recurred in two additional scopes and was repaired within the fixed bound. It does not justify increasing semantic cycles or claim that a stronger grouper is required. It also does not establish Gold correctness, a production recall increase, or generalization beyond one document. In particular, the changed-proposal branch has 0 independent acceptances in 2 attempts.

Production behavior remains unchanged. Qualification's four accepted current partitions emitted no headings, and the two rejected changed partitions stayed unresolved. No hierarchy, ancestor recovery or resolver changes were made.

Safe runtime artifacts are retained locally under `.dhx-evidence/eval/023-grounded-cohort-eb2d806-20261019/`: `declaration.json`, `preflight.json`, `cohort-lock.json`, and `report.json`. Source-bearing packets, reasons and model completions remain in the separate `private/` directory.
