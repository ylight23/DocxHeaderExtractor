# Production generic PDF + OpenCV Luna page-14 end-to-end qualification

## Result

Ran the promoted PDF defaults on PDF072 page 14 through selection, native pairwise grouping, and a qualification-only semantic verification pass. The current application defaults represented in this run are membership instruction V7, source-bound selection, OpenCV raster geometry, the generic boundary instruction, and pairwise grouping. The fresh SELECT wire SHA-256 was `cfb81af631a796ed7f8eb79f6a47952092c92e97c106ee9bb6ee1fa397eeb26d`; it matches the independently exported production `SEG-013` request.

The frozen SELECT chose 19 of 33 source aliases. Native GROUP used one request for five adjacency runs, joined the two wrapped presentation titles, and returned 17 groups without protocol failures. Qualification VERIFY returned verdicts for 13 candidates and all 19 selected aliases, with no unresolved aliases. Its semantic judgments did not match registered Gold: it retained 15 false positive headings. The full qualification pipeline therefore returned 19 aliases in 17 groups, including those 15 false positives.

| Measure on the page-14 scope | TP | FP | FN |
| --- | ---: | ---: | ---: |
| SELECT membership, 33 owned aliases | 4 | 15 | 0 |
| Final membership after VERIFY | 4 | 15 | 0 |
| Exact heading boundaries | 4 | 13 | 0 |

The verifier completed its protocol successfully; this is not a semantic accuracy pass. It labeled all 15 initial false positives as headings and retained them. See [gold-score.json](../../artifacts/qualification/pdf-generic-production-luna-e2e-20261025/03/gold-score.json) for the aliases and groups scored against the registered authority.

## Run artifacts

The initial output `01` contains the frozen fresh selection request and reply. Its first grouping bridge attempt failed with a generic `MODEL_CALL_FAILED`; that path did not retain the bridge's inner exception, so the precise initial cause is unknown. The frozen selection was loaded unchanged for continuations; SELECT was not rerun.

Continuation `02` successfully parsed the identical GROUP reply but the qualification VERIFY wrapper failed before writing verifier requests because it assumed message content was a string. The verifier request format used content arrays. The wrapper was repaired to support both array and string content. Continuation `03` then completed GROUP and VERIFY. Its group request has the same SHA and bytes as the frozen GROUP request; the frozen reply was reused only after exact SHA equality. Details, request hashes, and the intentionally unused typo response file are recorded in [continuation-manifest.json](../artifacts/qualification/pdf-generic-production-luna-e2e-20261025/03/continuation-manifest.json).

The completed continuation is [03](../../artifacts/qualification/pdf-generic-production-luna-e2e-20261025/03/). It preserves exact request bodies in `luna-requests/`, request-bound replies in `luna-responses/`, per-phase JSON, model reply trace, the final source-bound headings, and the registered-Gold score. SELECT is linked to `../01/selection.json` in the continuation identity record.

One qualification-wrapper issue remains in the frozen VERIFY requests: their top-level `pageGeometryEvidence.version` is `PDF_PAGE_GEOMETRY_V1`, while each attached measurement page retains the production `PDF_RASTER_GEOMETRY_V1` version. The wrapper now takes the version directly from `PdfGeometryMeasurements.Version`; the raw 03 requests and their hashes were preserved and not rewritten. VERIFY is an experimental extension, so this mismatch does not change the production SELECT or GROUP builders, but it is a limitation of this verifier observation.

## Scope and limits

- The whole PDF was parsed and supplied as source context, but model selection was limited to page 14's production segment `SEG-013`; this is not a whole-document extraction or hierarchy run.
- The result is one session authored by Luna through the local in-session file bridge. No OpenRouter calls or provider timing claims were made.
- Pairwise GROUP is the native production request builder and prompt. Semantic VERIFY is qualification-only and is not enabled in production.
- The Gold score is scoped to the 33 owned page-14 aliases. It cannot measure recall for headings outside the scope or validate hierarchy placement.
- The result is a single-session diagnostic, not a stability estimate or evidence about other models.

## Validation

- `dotnet build tools/qualification/TargetRepresentationTrial/TargetRepresentationTrial.csproj --no-restore` succeeded with zero warnings and errors after the continuation-wrapper changes.
- The three 03 request body SHA-256 values were recomputed and matched their filenames; each matching response's `requestSha256` matched the request.
- The initial SELECT request was independently confirmed against the previously exported production `SEG-013` wire.
- Gold was read and scored only after SELECT, GROUP, and VERIFY replies were frozen.
