# PDF V14 block-bounded cohort qualification (Luna)

**Run date:** 2026-10-26  
**Route:** public `PdfHandler.ExtractAsync`; V14 block-bounded structural unitizer; selection instruction V12 unchanged; adaptive page-image evidence; concurrency 6. No production defaults or prompts were changed.

## Results

| Authority | Result | Wall time | Requests (SELECT / GROUP / PLACE) | SELECT packets with images | Alias TP / FP / FN | Precision / recall | Exact groups TP / FP / FN |
|---|---|---:|---:|---:|---:|---:|---:|
| DOC-0133 | Failed before provider request | — | 0 / 0 / 0 | 0 | Not scored | — | Not scored |
| DOC-0256 | Completed | 166.33 s | 14 / 1 / 3 | 14/14 | 34 / 0 / 1 | 1.000 / 0.971 | 33 / 0 / 1 |
| SRC-089 | Completed | 134.48 s | 18 / 3 / 2 | 18/18 | 53 / 0 / 4 | 1.000 / 0.930 | 32 / 0 / 4 |
| SRC-095 (supplement) | Completed | 1052.41 s | 57 / 2 / 7 (+1 chain repair) | 57/57 | 97 / 1 / 12 | 0.990 / 0.890 | 90 / 1 / 13 |
| **Pooled evaluable runs** | **3 completed** | **1353.22 s summed** | **89 / 6 / 12 (+1 chain repair)** | **89/89** | **184 / 1 / 17** | **0.995 / 0.915** | **155 / 1 / 18** |

FN aliases:

- DOC-0256: `L0312:S0`
- SRC-089: `L0109:S0`, `L0117:S0`, `L0245:S0`, `L0248:S0`
- SRC-095: `L0015:S0`, `L0023:S0`, `L0034:S0`, `L1914:S0`, `L1940:S0`, `L1947:S0`, `L1961:S0`, `L2012:S0`, `L2023:S0`, `L2056:S0`, `L2063:S0`, `L2076:S0`

SRC-095's false-positive alias is `L0007:S0`; its false-positive exact group is `L0007:S0 + L0008:S0`. The pooled score uses the three completed, Gold-evaluable files (201 Gold aliases and 173 Gold groups). The cohort has four attempted files; DOC-0133 is excluded because its route failed before provider dispatch and no extraction output exists to score. SRC-095 materially lowers pooled recall and exceeds five minutes by a wide margin.

## Integrity and limitations

- Source SHA-256 matched the authoritative registry for all four attempted inputs, including the SRC-095 supplement.
- For DOC-0256, all 18 request/response pairs were SHA-bound and frozen before Gold was opened. For SRC-089, all 23 pairs were independently rechecked: request hash equals filename, response `requestSha256` points to that request, and response hash equals the freeze inventory; Gold was opened only afterward.
- For SRC-095, source SHA-256 was `ecc538dc3d8e9cb3f8d73c3c10189f91bdc7b81f80c570ce658760e5d6f5db84`, matching the authority registry. The freeze manifest records 67 distinct request/response pairs; every request hash matched its filename, every response `requestSha256` matched its request, and response hashes matched the freeze inventory before Gold was opened. There were 68 bridge calls because one identical `HIERARCHY_PLACE` request SHA was called twice. All 57 SELECT packets contained page images. A parent-chain repair payload was issued; no SELECT/GROUP correction payloads were needed.
- DOC-0133 dispatched to `ScanLane` and failed with `SCAN_HEADING_SELECTOR_REQUIRED` before any provider request. It was not substituted, and its Gold was not opened.
- The SRC-089 and SRC-095 Gold support occurrence and exact source-part grouping evaluation, but not hierarchy evaluation. No hierarchy-accuracy claim is made.
- These are local Luna bridge wall times, including response-file waits. They are not external-provider latency or SLA measurements. SRC-095 took 1052.41 seconds (17m32s); the cohort therefore does not meet a five-minute target consistently. This small cohort does not establish production-wide accuracy or stability.
- No production promotion was performed.

## Artifacts

- DOC-0133 failure/freeze: [`pre-gold-freeze.json`](../../artifacts/qualification/pdf-v14-block-bounded-cohort-luna-20261026/DOC-0133/pre-gold-freeze.json)
- DOC-0256 freeze and score: [`pre-gold-freeze.json`](../../artifacts/qualification/pdf-v14-block-bounded-cohort-luna-20261026/DOC-0256/pre-gold-freeze.json), [`gold-score.json`](../../artifacts/qualification/pdf-v14-block-bounded-cohort-luna-20261026/DOC-0256/gold-score.json)
- SRC-089 freeze and score: [`pre-gold-freeze.json`](../../artifacts/qualification/pdf-v14-block-bounded-cohort-luna-20261026/SRC-089/pre-gold-freeze.json), [`gold-score.json`](../../artifacts/qualification/pdf-v14-block-bounded-cohort-luna-20261026/SRC-089/gold-score.json)
- SRC-095 supplement freeze and score: [`pre-gold-freeze.json`](../../artifacts/qualification/pdf-v14-block-bounded-cohort-luna-20261026/SRC-095/pre-gold-freeze.json), [`gold-score.json`](../../artifacts/qualification/pdf-v14-block-bounded-cohort-luna-20261026/SRC-095/gold-score.json)
