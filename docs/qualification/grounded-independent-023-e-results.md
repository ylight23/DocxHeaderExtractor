# Canary E: grounded independent checks of V17 and V24

Neither frozen changed proposal passed grounded independent verification. Both received a valid REGROUP with a different structurally valid counterproposal. In both cases that counterproposal equalled the original C0 partition. No second suggestion was applied and both cases remained unresolved.

## Fixed inputs and execution

Harness and declaration were pushed as `fea655b` before live calls. Related regression suite: **290 passed**. The declaration SHA256 was `56ddf4eacb37d13e7ca0634ea4cb701f9d8e29b641f91e591e732e6bfaf7d6d6`. Exactly V17 and V24 were admitted, using their changed proposals from the prior predeclared eb2d806 cohort; no new C0, D proposal or replacement seed was sampled.

Both reconstructed legacy independent requests matched their actual recorded full prepared-request SHA256:

| Case | Original independent request SHA256 |
|---|---|
| V17 | bbd8abe168ba5f52ffeb045a17964da1152a678889ab892a6b6ab4918f057eb7 |
| V24 | ea5904d753369c3fca5215b7f35ac073e83e18bf49acf1c6fcdd45fe61fe4e16 |

This checks the partition, full source packet and original request serialization, not just alias sets. E then used the same grounded schema and consistency-repair implementation as D, in a fresh final independent conversation without the D reason, C0 complaint, old independent verdict or history. Source, alias-universe and evidence-store hashes matched the prior cohort.

## Outcomes

| Case | Grounded independent trajectory | Qualified acceptance | Action |
|---|---|---|---|
| V17 | Round 1: REGROUP with exact current echo and valid different suggestion | No | Stop unresolved |
| V24 | Round 1: invalid overlong reason; protocol repair; round 2: REGROUP with exact current echo and valid different suggestion | No | Stop unresolved |

V24's first reason had 2,103 characters against the 1,000-character bound. It had all required fields but could not authorize a verdict. The second response passed the complete contract. This was format repair, not a self-contradiction-to-ACCEPT outcome.

| Outcome category | Cases |
|---|---:|
| Grounded independent ACCEPT current, direct | 0 |
| Self-contradiction → repair → ACCEPT current | 0 |
| Valid grounded REGROUP + different suggestion | 2 |
| Persistent contradiction / verdict UNRESOLVED / runtime failure | 0 |

Terminal qualification status for both was `GROUNDED_REGROUP_REJECT_CURRENT`. All three responses finished with `stop`; final validated membership remained HEADING for every alias. Counterproposals were recorded only. No grouper, semantic cycle, independent-of-independent verifier or heading emission occurred.

## Exact graphs

V17:

```text
Original C0:
[[P008-L013,P008-L014,P008-L015],[P008-L016,P008-L017],[P008-L018]]

D changed proposal = E current input:
[[P008-L013,P008-L014,P008-L015],[P008-L016,P008-L017,P008-L018]]

E counterproposal (not applied):
[[P008-L013,P008-L014,P008-L015],[P008-L016,P008-L017],[P008-L018]]
```

V24:

```text
Original C0:
[[P011-L001,P011-L002],[P011-L003,P011-L004]]

D changed proposal = E current input:
[[P011-L001,P011-L002,P011-L003,P011-L004]]

E counterproposal (not applied):
[[P011-L001,P011-L002],[P011-L003,P011-L004]]
```

Offline checks matched E input to D's frozen changed proposal for both cases, checked that every protocol round retained that same input, confirmed no complaint was supplied, and matched E's counterproposal to the old C0 graph. The latter is not authorization of C0: E did not independently ACCEPT that graph as its current input.

## Interpretation and cost

The conditional outcome motivating this canary did not occur: neither legacy independent REGROUP became grounded ACCEPT. These results do not support labelling the old independent decisions false negatives caused by the boundary contract. The changed branch instead exhibits structurally coherent disagreement between fresh model passes: D rejects C0 and proposes a merged graph; E rejects the merged graph and proposes C0 again. Grounding exposes this disagreement but does not resolve it or establish which graph is Gold-correct.

The prior cohort's two upstream self-contradiction repairs remain evidence for the invariant. E adds evidence that grounding the independent side can provide a checkable rejection and stop safely. It does not demonstrate changed-branch recall improvement or establish that semantic grouper repair should be removed from production. Production architecture remains unchanged pending a separate decision.

Actual E usage: **3 POSTs, USD 0.00158901**, within the predeclared maximum of six POSTs and USD 1. There were no further E samples or counterproposal verification calls.

Safe runtime artifacts are under `.dhx-evidence/eval/023-grounded-e-fea655b-20261019/`: declaration, preflight request-hash proof and report. Source-bearing packets and completions remain separately under private.
