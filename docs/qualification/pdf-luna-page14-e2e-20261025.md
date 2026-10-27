# PDF072 page 14: local Luna end-to-end target representation trial

## Scope and method

This is a bounded, qualification-only local semantic integration run for PDF072 page 14. It compares flat alias targets with flat alias targets plus measured OpenCV raster geometry. Both arms used the same source-bound selection contract, system prompt, grouping and verifier prompts, request settings, facts, context and parser. The treatment arm alone received `pageGeometryEvidence` on the selection, grouping and verification requests. `PACKET_PARITY_PASS: 30` confirms the prepared packet parity checks for this run.

The harness used the new `geometry-e2e-luna` mode in `tools/qualification/TargetRepresentationTrial`. Its in-process file bridge saved each exact prepared request and waited for a Luna-authored response file named by that request's SHA-256. It checked the reply's `requestSha256` before constructing `ModelTurn` directly. No request was sent to OpenRouter; no synthetic SSE, token usage or cost was used. The complete request/reply hash manifest is `artifacts/qualification/pdf-luna-page14-e2e-20261025/01/luna-bridge-manifest.json`; exact request and raw authored response files are under `luna-requests/` and `luna-responses/`. Selection, grouping, verification and materialized final outputs are saved separately for each arm.

The full path completed in both arms: SELECT → production pairwise GROUP → qualification semantic VERIFY (`allowMembershipReview: false`) → source-bound final groups. A first GROUP answer in each arm used the wrong protocol identifier and was refused; one bounded repair per arm supplied the correct `PDF_HEADING_BOUNDARIES_V1` answer. The repaired grouping and verifier calls passed. No aliases were unresolved or withheld. This protocol repair is retained in the raw request/reply artifacts and phase traces.

## Results

After both arms' authored outputs were frozen, `tools/qualification/audit_target_representation.py` read Gold and scored the results. The machine-readable score is `artifacts/qualification/pdf-luna-page14-e2e-20261025/01/offline-score.json`.

| Arm | Selection | Final membership | Exact boundaries | Unresolved |
|---|---:|---:|---:|---:|
| Flat aliases | 4 TP, 0 FP, 0 FN | 4 TP, 0 FP, 0 FN | 4 TP, 0 FP, 0 FN | 0 |
| Flat aliases + OpenCV geometry | 4 TP, 0 FP, 0 FN | 4 TP, 0 FP, 0 FN | 4 TP, 0 FP, 0 FN | 0 |

Both arms selected the same four source occurrences, all explicit schedule structure: `L0536:S0` (SESSION V), `L0539:S0` (DAY 2), `L0540:S0` (SESSION V continued), and `L0553:S0` (SESSION VI). The semantic verifier accepted each as a heading and confirmed the day/session boundary as a split. It returned `HEADING` for every one; no membership state is inferred from an unknown.

OpenCV mapped all 33 page 14 aliases to the rendered raster, with zero mapped aliases lacking ink. The recorded raster SHA-256 is `7ef191afa9457c42e679be7e5f4214a57d3c0b2fe3ab2e57eaf29c68ebb66f74`; the page image, mapped overlay and full geometry evidence are in the run output directory.

## Limits

This tests one page and one set of in-session judgments. The two arms ran in the same Luna session, so shared context and arm-order effects cannot be ruled out; this is not an independent randomized replication or a Qwen quality/speed measurement. No provider-native usage or cost exists for this bridge. The score only covers source aliases owned by the page 14 selection segment and cannot establish recall elsewhere in the document, whole-document hierarchy quality, or production behavior: semantic verification here is qualification-only, not the current production path.
