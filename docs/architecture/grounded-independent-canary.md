# Qualification E: grounded independent verification of D's frozen changed proposals

The predeclared inputs are exactly V17 and V24 from the eb2d806 cohort, both classified as valid changed suggestions followed by legacy independent REGROUP. No other seed, new C0, alternate changed proposal or replacement is admitted. The declaration is `docs/qualification/grounded-independent-023-e.json` and must be committed before live calls.

```text
boundary-grounded-e <declaration.json> <new-output-directory> --validate
boundary-grounded-e <declaration.json> <new-output-directory> --live
```

`--validate` reads and verifies local artifacts without model/network calls. Both cases must pass validation before any live call. The loader verifies the prior cohort report and lock hashes, each private seed hash and old independent-check hash, the eligibility lock's seed packet hashes, unchanged ordered alias scope, and that the proposed graph actually differs from the original C0 graph. It requires the old independent verdict to be REGROUP with all HEADING and no dynamic evidence or extra POST rounds.

The full old independent request can be reconstructed from the retained C0 packet by changing only candidate id to V1 and proposedGroups to D's changed proposal. The original candidate scope, static facts, selected line representations, context and styles remain identical. Rebuilding that request with the original production verifier prompt must match the actual request SHA256 stored in its response audit. If it does not match, E refuses to run rather than approximating evidence. Source document, alias-universe and evidence-store hashes must also match the cohort.

E starts a fresh conversation with that packet and the SAME grounded verdict schema/parser and complete template used by D. It includes neither D's reason/complaint nor the old independent REGROUP verdict or history. It exposes no tools/crops. The original request hash comparison proves input reconstruction; E's actual prompt, stage and removed tool declarations are deliberately different from the legacy request.

E permits at most three protocol-consistency rounds in the SAME conversation. currentGroups must echo the input graph; REGROUP must supply a different valid ordered partition. Equal suggestion/input triggers BOUNDARY_VERDICT_SELF_CONTRADICTORY and consistency feedback. Malformed membership, evidence or coverage remains invalid. Incomplete output fails closed, without salvage.

Terminal handling is explicit:

| Grounded independent outcome | Action |
|---|---|
| ACCEPT current | Qualify the original changed proposal, with direct versus post-contradiction acceptance distinguished |
| REGROUP + valid different suggestion | GROUNDED_REGROUP_REJECT_CURRENT; remain unresolved; record the counterproposal only |
| Persistent contradiction, dispute/unresolved, format/transport failure or budget exhaustion | Remain unresolved |

No second suggestion is applied, no further verifier is spawned, and no grouper or semantic boundary cycle is invoked. The two cases share a six-POST/USD-1 admission cap. There are no sample retries after the fixed protocol bound. Safe output retains source aliases, partitions, round/error metadata, validated membership statuses, hashes and cost; source packets and full replies remain under private.

The shared qualification engine was factored to expose terminal independent mode; D's previous frozen-complaint path and changed-proposal independent behavior remain unchanged. Production is untouched. Qualification emits no headings even after ACCEPT. A repaired ACCEPT is evidence for the contract failure mechanism on that frozen proposal, not proof of Gold correctness or a causal estimate from a controlled repeated experiment.
