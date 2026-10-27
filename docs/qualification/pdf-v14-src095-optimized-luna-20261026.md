# Optimized V14 SRC-095 Luna qualification

**Run date:** 2026-10-26  
**Route:** public `PdfHandler.ExtractAsync`; V14 structural units V2; unchanged V12 selection instruction; adaptive visual evidence; concurrency 6; placement batch 32 (experimental; production default remains 16).

## Integrity and execution

The PDF SHA-256 is `ecc538dc3d8e9cb3f8d73c3c10189f91bdc7b81f80c570ce658760e5d6f5db84`, matching the SRC-095 source registry. The final run2 was frozen before Gold was opened. All 34 request filenames matched their serialized request hashes, all 34 responses bound to those request hashes, and 116 non-manifest output files are hash-listed in the freeze manifest. The freeze manifest SHA-256 is `60f198e6fca3561306ca4cb71bb05b9807f5e7c18e54846c363abbf26c0be024`.

The serialized selection packets identify the unitizer as `PDF_STRUCTURAL_GEOMETRY_SELECTION_UNITS_V2`. The run identity's `SelectorId` string still said `...units-v1`; this was a stale diagnostic label, not the packet contract. I corrected that label and its regression assertion after freezing the run. Frozen run artifacts were not edited.

Adaptive packing used 28 requests containing two complete adjacent pages and one request containing a single page. Every one of the 29 SELECT requests included page image data: 57 images total. SELECT request bodies ranged from 319,104 to 675,400 bytes (median 558,490), within the 786,432-byte request ceiling; all requests passed the configured resource admission check. Total serialized request bytes were 16,115,894.

| Request stage | Calls | Summed local bridge wait | Repairs |
|---|---:|---:|---:|
| UNIT_SEGMENT | 29 | 507.892 s | 0 |
| GROUP | 2 | 40.327 s | 0 |
| HIERARCHY_PLACE | 3 | 66.752 s | 0 |
| **Total** | **34** | **614.971 s** | **0** |

Bridge wait sums overlap because SELECT calls ran concurrently; they are not elapsed stage durations and do not add to the 253.74-second end-to-end wall time. The bridge log's text heuristic labeled one UNIT_SEGMENT packet (SEG-006, request SHA `ade80609…`) as GROUP because its content matched the heuristic. The explicit request stage is UNIT_SEGMENT; the phase counts above use the packet stage. There were no SELECT or GROUP correction requests and no parent-chain repair request.

End-to-end local Luna bridge time was **253.74 seconds** (4m14s), including response-file waits. This is not an external provider latency or SLA measurement.

## Results and prior-error changes

Scoring used the registry-authorized canonical SRC-095 Gold at `eval/a99-closed-loop/gold-current/documents/SRC-095.gold.v1.json` (SHA-256 `8c7cea0ada3d3af2a2fe9f7f48375575f40e987442a1d4586462ad7706f58ef5`). Its 103 Gold groups contain 109 source-part aliases. The legacy root copy at `eval/a99-closed-loop/gold/SRC-095.gold.json` has a different hash and is not the scoring authority. The final score records the registry and Gold hashes. The earlier frozen V14 SRC-095 prediction was independently rescored from its frozen run output against this same canonical Gold; its old `gold-score.json` was not used as score evidence.

| Metric | Prior frozen V14 run | Optimized run2 |
|---|---:|---:|
| Alias TP / FP / FN | 97 / 1 / 12 | **97 / 0 / 12** |
| Alias precision / recall | 98.98% / 88.99% | **100% / 88.99%** |
| Exact Gold groups | 90 / 103 | **91 / 103** |
| Wrong predicted groups | 1 | **0** |
| Wall time | 1,052.41 s | **253.74 s** |
| SELECT / GROUP / PLACE calls | 57 / 2 / 7, plus 1 parent-chain repair | **29 / 2 / 3, no repairs** |

The prior false positive `L0007:S0` (“RFC 9114”) is gone; `HTTP/3` is now a correctly grouped singleton, removing the old false-positive group and recovering one exact Gold group. Three old misses are now selected: `L0015:S0` (“Status of This Memo”), `L0023:S0` (“Copyright Notice”), and `L0034:S0` (“Table of Contents”). Three new misses appear in Appendix A: `L1688:S0` (“A.1. Streams”), `L1710:S0` (“A.2. HTTP Frame Types”), and `L1729:S0` (“A.2.1. Prioritization Differences”). Nine unchanged misses remain: eight index group labels (`L1914:S0`, `L1940:S0`, `L1947:S0`, `L1961:S0`, `L2012:S0`, `L2023:S0`, `L2056:S0`, and `L2063:S0`) and `L2076:S0` (“Author's Address”).

### FN cause audit

All 12 missed aliases were present as selectable members of valid structural units. Their packets included page images; none was removed by source binding, table-region construction, or placement.

| Misses | Packet and unit evidence | Observed decision | Diagnosis |
|---|---|---|---|
| `L1688`, `L1710`, `L1729` | `SEG-023`, two complete pages (47–48), 44 target units, 591,696 request bytes. Each missed heading is its own one-row paragraph-region unit. The same response selected the parent “Appendix A” heading. | `NOT_SELECT` for all three | Model selection omission. The units and aliases were available; the response did not identify these lower-level headings. Because they shared a two-page packet, packet density may have contributed, but this run alone cannot establish that as the cause. |
| `L1914`, `L1940`, `L1947` | `SEG-026`, pages 53–54, 54 target units; `L1961`, `L2012`, `L2023` in `SEG-027`, pages 55–56, 51 target units. Each alias is a singleton paragraph-region unit in the index. The measured text is a regular 10 pt letter label at the left indent, followed by indented index entries. | `NOT_SELECT` | Model did not treat the index letter labels as outline/group headings. Unitization preserved them separately; this is a semantic classification miss, not a parser omission. |
| `L2056`, `L2063` | `SEG-028`, page 57, 23 target units. Singleton paragraph-region units for regular 10 pt `R` and `S` index labels. | `NOT_SELECT` | Same index-label classification miss. |
| `L2076` | `SEG-028`, page 57, singleton paragraph-region for “Author's Address”; measured 14.4 pt bold, followed by author/address content. | `NOT_SELECT` | Model classification miss despite strong typography and local context; no code filter removed it. |

The responses contain no per-decision rationale that would establish why Luna chose `NOT_SELECT`. The immediate failure is in the model decision because the source units, text, context and images reached it. A controlled page-47/48 split-versus-pair run would be needed to determine whether packing caused the three Appendix omissions; changing the generic prompt or adding a case-specific rule is not justified by this run.

Gold does not evaluate hierarchy for this source, so the run makes no parent-accuracy claim. No production promotion occurred. Two aborted response-authoring attempts are preserved in the sibling `...-aborted` and `...-aborted2` directories and were excluded from scoring.

## Artifacts

- [Pre-Gold freeze manifest](../../artifacts/qualification/pdf-v14-src095-tablefix-pagepair-place32-luna-run2-20261026/pre-gold-freeze.json)
- [Canonical Gold score and prior-run rescore](../../artifacts/qualification/pdf-v14-src095-tablefix-pagepair-place32-luna-run2-20261026/gold-score.json)
- [Run identity](../../artifacts/qualification/pdf-v14-src095-tablefix-pagepair-place32-luna-run2-20261026/run-identity.json)
- [Native run result](../../artifacts/qualification/pdf-v14-src095-tablefix-pagepair-place32-luna-run2-20261026/native-run-result.json)
