# PDF072 V14 structural-unit run with Luna

## Run integrity

Ran the public `PdfHandler.ExtractAsync` route on PDF072 using V14 geometry-based structural units, the unchanged V12 selection instruction, adaptive visual evidence, source-bound selection, and concurrency 6. The selector used 15 final segments. All SELECT packets contained an inline page image.

The run produced 23 request/response pairs: 16 SELECT turns, 3 GROUP turns, and 4 PLACE batches. All 23 request hashes matched their response bindings. The freeze manifest records `goldOpenedBeforeFreeze: false`; scoring occurred only after that freeze.

SELECT needed one coverage repair on SEG-014 because the initial answer named a unit outside the request. GROUP needed one protocol repair because several `selectedText` values did not exactly match source text. The accepted run returned 38 headings.

## Score and comparison

| Metric | V12 baseline | V14 + V12 instruction | Change |
|---|---:|---:|---:|
| True positives | 39 | 41 | +2 |
| False positives | 0 | 0 | 0 |
| False negatives | 6 | 4 | -2 |
| Exact Gold groups | 38/42 | 38/42 | 0 |
| Predicted partial/wrong groups | 1 | 0 | -1 |
| End-to-end wall time | 395.43 s | 420.43 s | +25.00 s |

V14 membership precision was 100%, recall 91.1%, and F1 95.3%. Its four missed aliases are `L0123:S0`, `L0137:S0`, `L0150:S0`, and `L0288:S0`. All 38 predicted groups match Gold exactly; four Gold singleton groups are missing. Gold marks hierarchy as non-evaluable, so this run makes no parent-accuracy claim.

Wall time uses `durationSeconds` from each frozen `run-identity.json`, which includes Luna bridge waits. The older V12 report text says approximately 303 seconds, but the frozen V12 identity reports 395.429 seconds and its bridge-call span is 385.751 seconds. This comparison uses the identity duration consistently. Both runs exceed five minutes; V14 is about 25 seconds slower by that measure.

This is a local Luna-authored run, not a live OpenRouter provider qualification or a production latency SLA.

## Artifacts

- [Pre-Gold freeze manifest](../../artifacts/qualification/pdf072-v14-structural-luna-20261026/pre-gold-freeze.json)
- [Gold score](../../artifacts/qualification/pdf072-v14-structural-luna-20261026/gold-score.json)
- [Run identity](../../artifacts/qualification/pdf072-v14-structural-luna-20261026/run-identity.json)
- [V12 baseline report](pdf072-integrated-adaptive-luna-strict-20261026.md)
