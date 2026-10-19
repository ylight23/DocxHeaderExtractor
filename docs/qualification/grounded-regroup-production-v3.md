# Production V3 grounded REGROUP promotion

Date: 2026-10-19. The production verifier now uses `HEADING_GROUP_VERIFICATION_V3`, with selector/provenance version `verified-groups-v3-grounded-regroup`.

REGROUP must provide an exact current partition echo and a different valid suggested partition. Membership coverage/order, evidence grounding and all-HEADING requirements remain mandatory. Self-contradiction is refused with `BOUNDARY_VERDICT_SELF_CONTRADICTORY` and repaired in the same verifier conversation within its existing three-response budget. An ACCEPT after repair follows ordinary acceptance. A valid current echo is also allowed on ACCEPT; a suggestion is not accepted on non-REGROUP decisions.

This is the minimal contract promotion, not the experimental replacement architecture. Suggestions are retained only as alias partitions in `grouping.boundaryVerdicts`, always `applied=false`. The structured suggestion is excluded from grouper feedback; the existing reason, evidence references and rejected current proposal retain their previous feedback role. There is no automatic suggestion application, new independent-verifier chain, extra semantic cycle, ancestor recovery or hierarchy change. `BOUNDARY_REPAIR_NO_PROGRESS` and the two-cycle cap are preserved.

Production Ask calls the strict parser, which has no grounding-bypass flag. Qualification reuses the separate membership-field validator and applies its own frozen protocol checks. The V2 prompt is frozen in qualification so historical E request hashes remain reproducible after the production version change.

## Validation

- Final core regression selection: **302 passed, 0 failed**, plus **25 passed, 0 failed** in the disjoint DOCX selector/flavor/tools/source selection (**327 related tests** total). This includes new production tests for self-contradiction → same-conversation ACCEPT, bounded repeated contradiction, mismatched current graph, missing/duplicate/reordered/unknown/empty partitions, membership inconsistency, partial batch acceptance, optional exact current echo on ACCEPT, audit privacy and suggestion exclusion from grouper feedback. Existing no-progress, two-cycle, survivor refinement, budget/concurrency, scan/PDF/DOCX-path and hierarchy/transport regressions remain covered.
- The broader all-project run was deliberately stopped after **7m42s**. It reported 1,195 completed tests passed and no failures, then `Test Run Aborted`. This is not a full-suite pass and must not be represented as one. The final focused selection above was rebuilt and completed after the last parser change.
- Offline E `--validate` matched both original independent request hashes after the prompt was frozen; no provider call was made.
- Source PDF and page-raster hashes matched the retained OCR evidence for the [V17/V24 source review](023-v17-v24-source-adjudication.md). Both local adjudications favor A and identify B as a false merge. No Gold references were altered.

No new live model experiment, deployment or backend restart was performed for this change. Production code is updated; live recall and backend activation have not been measured in this offline task.
