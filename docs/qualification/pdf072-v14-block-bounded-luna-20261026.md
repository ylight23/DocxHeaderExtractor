# PDF072 block-bounded V14 run with Luna

## Run setup and freeze

Ran the public `PdfHandler.ExtractAsync` route with the updated V14 structural unitizer, which keeps paragraph regions within parser layout-block boundaries and uses measured geometry. The V12 selection instruction remained unchanged. Adaptive visual evidence and concurrency 6 were enabled.

The run completed in 223.22 seconds and returned 42 headings. It used 15 SELECT segments, 2 GROUP calls, and 3 PLACE batches. All 15 SELECT packets included page-image data. All 20 request/response pairs passed SHA binding validation; no protocol corrections or retries were needed. The complete request, response, and output hashes were frozen before Gold was opened.

## Score against Gold

| Metric | Previous V14 | Block-bounded V14 | Change |
|---|---:|---:|---:|
| True positives | 41 | 45 | +4 |
| False positives | 0 | 0 | 0 |
| False negatives | 4 | 0 | -4 |
| Exact Gold groups | 38/42 | 42/42 | +4 |
| Wall time | 420.43 s | 223.22 s | -197.21 s |

All four previous false negatives were selected and match Gold: `L0123:S0` (“Eurostat–OECD PPP Program”), `L0137:S0` (“Latin America and the Caribbean”), `L0150:S0` (“Western Asia”), and `L0288:S0` (“2. Terms of Reference for a Task Force on Annual PPP Production”). The new run has 0 FP and 0 FN, with all 42 Gold groups matched exactly.

The observed run finished under five minutes. This is a local Luna bridge measurement that includes bridge waits; it does not establish an external provider latency SLA. Gold hierarchy is not marked evaluable, so no parent-placement accuracy is claimed. No production default or prompt was changed.

## Artifacts

- [Pre-Gold freeze manifest](../../artifacts/qualification/pdf072-v14-block-bounded-luna-20261026/pre-gold-freeze.json)
- [Gold score](../../artifacts/qualification/pdf072-v14-block-bounded-luna-20261026/gold-score.json)
- [Run identity](../../artifacts/qualification/pdf072-v14-block-bounded-luna-20261026/run-identity.json)
- [Native run result](../../artifacts/qualification/pdf072-v14-block-bounded-luna-20261026/native-run-result.json)
