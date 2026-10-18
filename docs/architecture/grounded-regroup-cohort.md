# Predeclared qualification D cohort

The checked-in declaration is `docs/qualification/grounded-regroup-023-cohort.json`. It must be committed before live API calls. `boundary-grounded-cohort <declaration.json> <new-output-directory> --live` requires existing hash-matched local OCR/transcription artifacts and the API key in the environment. No key enters the declaration or safe report.

## Universe, pilot exclusion and eligibility

Use every `VERIFY` cycle-zero candidate with at least two aliases in the fixed 023/e2bb0f5 audit, excluding the exact P021-L010..L012 pilot scope. This yields 25 unique ordered alias scopes, including both single-group and multi-group partitions. The runner recomputes that exhaustive rule from the hash-matched historical audit and rejects any declaration that omits or adds a proposal. Historical partitions are proposal inputs, not reconstructed historical conversations.

The saved private store contains only one complete historical REGROUP seed (P021, already a pilot). Therefore a new qualification C0 verifier is run once per declared proposal, using default source reading and full text extent. Selection and grouping are not resampled. C0 uses the existing production verifier protocol with its bounded protocol/transport rounds. This does not replay historical selected alternate readings/extents or historical multi-candidate conversations. Both facts limit comparisons with earlier live outcomes.

All C0 outcomes remain in the denominator. A seed is eligible only if C0 returns a valid REGROUP with all alias memberships HEADING and no requested dynamic evidence. D has static evidence only, so tool-informed seeds are prospectively excluded and explicitly counted. ACCEPT, membership dispute, unresolved, invalid C0, runtime errors and evidence exclusions are all retained. There is no retry to obtain a qualifying seed, target eligible sample size, replacement, pilot reuse or additional scope search.

## Frozen barrier and fixed experiment

All baseline conversations finish before D can begin. The synchronous freeze barrier writes `cohort-lock.json` with every baseline status and the packet/private seed SHA256 of every eligible seed. If lock persistence fails, no D call starts. Each eligible seed enters D exactly once, with D's existing prompt and maximum three protocol rounds. A valid changed suggestion receives a fresh production verifier; only independent ACCEPT can qualify the changed partition. No grouper, semantic repair recursion, hierarchy, binding or production heading emission occurs.

Concurrency is four independent scopes. One shared admission budget caps the experiment at 400 POSTs and USD 3 of reserved cost; actual cost is reported. A source cache miss cannot trigger transcription HTTP. The immutable declaration and source hashes are written before the first provider call. Interrupted or failed cohorts are not silently resumed or replaced; their denominator and partial outcomes remain visible.

## Outcomes and denominators

Classify every eligible D seed:

| Category | Definition |
|---|---|
| SELF_CONTRADICTION_TO_ACCEPT_CURRENT | At least one structural self-contradiction followed by grounded ACCEPT of unchanged input |
| CHANGED_INDEPENDENT_ACCEPT | Valid changed suggestion; fresh independent ACCEPT |
| CHANGED_INDEPENDENT_REJECT_OR_UNRESOLVED | Valid changed suggestion; fresh independent verifier does not authorize it |
| PERSISTENT_SELF_CONTRADICTION | Terminal protocol failure is BOUNDARY_VERDICT_SELF_CONTRADICTORY at the fixed round bound |
| MEMBERSHIP_DISPUTE_OR_UNRESOLVED | Valid terminal D membership dispute or unresolved decision |
| DIRECT_ACCEPT_CURRENT | Grounded ACCEPT at D round one; not evidence that consistency feedback fixed anything |
| OTHER_PROTOCOL_REPAIR_TO_ACCEPT_CURRENT | Current ACCEPT after other protocol repairs, with no observed self-contradiction |
| OTHER_PROTOCOL_OR_TRANSPORT_FAILURE | Remaining invalid, transport, runtime or budget failures |

Report the 25-proposal baseline denominator, eligible REGROUP count, every baseline exclusion/status, eligible D denominator, category counts including zeros, and all per-seed rounds. Never pool pilot P021 or the earlier schema-failed D run into this cohort. Safe output contains aliases, partitions, hashes, statuses, counts and costs; packet/reason/model evidence remains under `private`.

D round observations include the validated alias-to-membership-status map. A terminal UNRESOLVED with all memberships still HEADING is an unresolved boundary judgment, not a measured membership change; distinguish it from NOT_HEADING/UNRESOLVED membership when interpreting the combined dispute/unresolved category.

The unit of inference is one source scope in a single document. Even frequent self-contradiction-to-ACCEPT would support a boundary-contract invariant, not establish Gold correctness, broad document-level recall, or statistical independence across scopes. Production behavior remains unchanged pending cohort interpretation.
