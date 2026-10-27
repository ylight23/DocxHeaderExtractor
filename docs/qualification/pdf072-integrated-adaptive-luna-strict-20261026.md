# PDF072 integrated adaptive visual route: strict local-Luna validation

## Run and freeze

The run used the public `PdfHandler.ExtractAsync` path on `072_ICP_TAG_Minutes_Mar_2025.pdf` (source SHA-256 `a005f25e3bb9754cd6c8c7000682d00eb68238fb8937d3475fe807ffbbd94b61`). It enabled adaptive visual selection with a 786,432-byte request cap, source-bound V12 selection, raster geometry, separate pairwise grouping, and current production placement. The test-only local Luna bridge consumed the exact production requests; no external provider was called. The 15 page-owned SELECT units ran with plan concurrency 6.

Fresh text-only GROUP and PLACE requests followed SELECT. The first PLACE reply proposed 39 placements where the request asked for 16, so production rejected it and issued a repair request; the subsequent three batches were answered for their exact requested aliases. The completed run returned 39 headings.

Before reading DOC-0252 Gold, I froze 20 request/response pairs: 15 SELECT, 1 GROUP, and 4 PLACE. All response `requestSha256` values match the UTF-8 SHA-256 of their saved request. Request and authored response JSON files are read-only. The manifest is [pre-gold-freeze.json](../../artifacts/qualification/pdf072-integrated-adaptive-luna-strict-20261026/pre-gold-freeze.json). Gold scoring was performed only after this freeze.

## DOC-0252 scoring

| Measure | Result |
|---|---:|
| Gold source aliases | 45 |
| Predicted source aliases | 39 |
| Alias membership precision | 100.0% (39/39) |
| Alias membership recall | 86.7% (39/45) |
| Alias membership F1 | 92.9% |
| Gold heading groups | 42 |
| Predicted groups | 39 |
| Exact group matches | 38/42 |
| Exact group precision / recall / F1 | 97.4% / 90.5% / 93.8% |

There were no false-positive source aliases. The six missed source aliases were `L0000:S0`, `L0001:S0`, `L0315:S0`, `L0360:S0`, `L0513:S0`, and `L0514:S0`. Three Gold groups were absent: the composite title on page 1 (`L0000:S0` + `L0001:S0`), the ICP-classification task-force item (`L0315:S0`), and the composite agenda title (`L0513:S0` + `L0514:S0`). The selected `L0359:S0` was only part of the Gold composite heading `L0359:S0` + `L0360:S0`; it formed the one predicted group that did not match a complete Gold group. All eight explicit pairwise GROUP boundaries were returned as SPLIT.

Exact-group metrics treat a group as its ordered source-part alias set. The predicted groups were singletons because all explicit boundaries split and all other selected aliases were nonadjacent. Gold declares hierarchy evaluation unavailable, so this report does not claim parent-placement accuracy.

## Artifacts

- [Score JSON](../../artifacts/qualification/pdf072-integrated-adaptive-luna-strict-20261026/gold-score.json)
- [Run identity and request log](../../artifacts/qualification/pdf072-integrated-adaptive-luna-strict-20261026/run-identity.json)
- [Production run result](../../artifacts/qualification/pdf072-integrated-adaptive-luna-strict-20261026/native-run-result.json)
- [Frozen request/response manifest](../../artifacts/qualification/pdf072-integrated-adaptive-luna-strict-20261026/pre-gold-freeze.json)

No production source or defaults were changed. No tests were run.
