# PDF072 page-owned adaptive multimodal Luna run — 2026-10-26

## Status

This qualification attempt is **partial and not scored**. The page-owned packetizer generated all 15 SELECT requests, but the provider returned HTTP 402 Payment Required for remaining requests after six succeeded. Those six replies cover 257/650 source aliases. The task was stopped before GROUP, PLACE, pre-Gold freeze, or Gold access. There are no TP/FP/FN or group metrics from this run.

The agent initially inspected the previous clean-run qualification note for protocol context; that note included a post-freeze score section. Therefore this attempt cannot claim the entire workflow was blind to prior score information. No prior raw replies or Gold files were opened. Fresh model requests/responses below are independent of previous model replies.

## Packet measurements

| Measure | Result |
|---|---:|
| SELECT requests captured | 15 |
| Provider responses returned | 6 / 15 |
| Source aliases in successful replies | 257 / 650 |
| Serialized request cap | 512 KiB (524,288 bytes) |
| Requests over cap | 0 |
| Request body sizes | min 161,493; median 415,714; p95 472,209; max 472,209 bytes |
| Total serialized SELECT bodies | 5,603,016 bytes |
| Unique JPEG page image bytes | 3,653,997 |
| Image bytes transmitted | 3,653,997 |
| Image mapping | One full-page image per page-owned request, 15 pages total |
| Wall time | Not recorded reliably after provider failure |
| Provider failure | HTTP 402 Payment Required |

The earlier raw-PNG experiment's SELECT bodies totaled 118,527,168 bytes. The current captured request bodies total 5,603,016 bytes, a 95.3% reduction in serialized SELECT body volume. This is a transport-size comparison only; the incomplete run has no quality comparison.

## Packet mapping and artifacts

`page-owned-run/pre-call-packet-manifest.json` records each page, part, alias list, layout-block list, request hash, serialized size, and image byte count. Exact inline-image request JSONs are in `page-owned-run/requests/`. Six provider response bodies, bound response records, and the partial audit are in `page-owned-run/responses/` and `page-owned-run/partial-run-audit.json`.

All 15 packets include the actual JPEG data URI inline in the final JSON body and passed the 512 KiB measurement after serialization. The scheduler owns one page at a time and splits only at parser layout-block boundaries; aliases are assigned to one packet only.

## Contract and parity audit

This is a qualification-only Python runner under `tools/qualification/PageOwnedAdaptive/`; no production source or defaults changed. It uses the same PDF parser cache, source hash, and V12 instruction text. It binds each response record to the exact serialized request SHA-256 and preserves the raw provider response SHA-256.

The only intended scheduler/transport change is page-owned request partitioning and compact evidence transport. Because the public `PdfHandler` has no page-owned scheduler seam, this runner does not claim exact public `PdfHandler` parity. The full-page JPEG is repeated if a page is split into more than one request; each source alias itself is transmitted once. The measurement facts include parser page, layout block, bounding box, width/height, text length, and exact-text repetition count; semantic labels are not included. Style evidence was not reconstructed in this partial runner.

The request system wrapper and JSON schema were assembled in the qualification script rather than invoked through the production `PdfSelector`/`SelectionAuthority` implementation. Source text is requested verbatim for SELECT and responses are checked only by saved model output, not passed through the production parser. GROUP and PLACE were not run. These are material deviations from the requested exact grouping, placement, and SelectionAuthority-compatible validation contracts.

## Conclusion

The packet-size requirement is demonstrated for the captured SELECT packets. The adaptive multimodal experiment is **not complete** because nine SELECT calls did not return, and no grouping, placement, freeze, or DOC-0252 scoring occurred. Continue only after provider access is restored or a supported local Luna response bridge is supplied. The runner is [run_page_owned.py](../../tools/qualification/PageOwnedAdaptive/run_page_owned.py).
