# SRC-095 full V14/V12 rerun with rejection reasons

Date: 2026-10-26  
Source: `095_RFC9114_HTTP_3.pdf`, SHA-256 `ecc538dc3d8e9cb3f8d73c3c10189f91bdc7b81f80c570ce658760e5d6f5db84`  
Gold: canonical SRC-095 Gold, SHA-256 `8c7cea0ada3d3af2a2fe9f7f48375575f40e987442a1d4586462ad7706f58ef5`

## Run configuration

- Native `PdfHandler.ExtractAsync` path, V14 structural geometry units and unchanged V12 semantic instruction.
- Source-bound V2 response contract, including a concise evidence-grounded reason for every `NOT_SELECT`.
- Adaptive visual page packing, 786,432-byte request cap, parallelism 6, hierarchy placement batch 32.
- No Gold content was included in request packets. Request/reply artifacts were frozen before scoring.
- This uses Luna-agent-authored replies through a local bridge. It is not an external Qwen/OpenRouter execution.

## First attempt and timeout diagnosis

The first full attempt took 1,118.49 seconds and returned 90 headings. One packet, `SEG-017` (pages 35–36; request SHA-256 `74dbd09df0a7eac37973acd20d3f06d8d08331e9a416424db8a7bd6aa40dd019`), exceeded the transport's 300-second per-call timeout. Its response file was authored after 327 seconds, so the selector did not consume it. The incomplete output therefore missed these five selected-in-the-packet headings:

- `L1265:S0` — `9. Extensions to HTTP/3`
- `L1294:S0` — `10. Security Considerations`
- `L1298:S0` — `10.1. Server Authority`
- `L1301:S0` — `10.2. Cross-Protocol Attacks`
- `L1308:S0` — `10.3. Intermediary-Encapsulation Attacks`

The incomplete attempt scored TP 96 / FP 1 / FN 13. This was a transport timeout, not five `NOT_SELECT` judgments. Its diagnostic score is preserved as `attempt-incomplete-gold-score.json` beside the run artifacts.

## Completed replay and Gold result

The exact frozen SELECT and GROUP replies were replayed through the full native pipeline. Three placement packets had new request hashes because the recovered selection changed the heading set; fresh Luna agents authored those placement replies. The replay completed 29 SELECT, 2 GROUP, and 3 PLACE calls with 95 returned headings in 183.68 seconds.

| Metric | Result |
| --- | ---: |
| Gold heading aliases | 109 |
| Predicted heading aliases | 102 |
| TP / FP / FN | **101 / 1 / 8** |
| Precision / recall / F1 | **99.02% / 92.66% / 95.73%** |
| Exact Gold groups | 94 / 103 |
| Pages 47–48 Appendix subsection FN | **0** |

The remaining eight FN are the index section labels `C`, `D`, `G`, `H`, `M`, `P`, `R`, and `S` (`L1914`, `L1940`, `L1947`, `L1961`, `L2012`, `L2023`, `L2056`, `L2063`). Their V2 reasons say they sort alphabetic-index entries rather than divide document content. Gold classifies them as index group headings, so this is a remaining semantic disagreement. The one FP is `L0007:S0`, `RFC 9114`; the model selects it together with `HTTP/3`, while Gold includes only the latter alias.

## Interpretation and limits

The page 47–48 Appendix misses do not recur in the full-file replay. All three subsection headings are present in the final prediction. The first attempt's five new misses were caused by the expired `SEG-017` call; replaying its saved response recovered all five.

The 183.68-second figure measures a local response replay and placement authoring; it is not model-generation time or a five-minute provider SLA. The run did not call Qwen/OpenRouter, and the 31 replayed selection/group replies are not a new independent inference. No production defaults or production route were promoted by this qualification run.

## Frozen artifacts

- [Replay pre-Gold freeze manifest](../../artifacts/qualification/pdf-v14-src095-reason-v2-replay-20261026/pre-gold-freeze.json) — SHA-256 `264172254095ab6256f631dbd431275fa919532c367def3f638cc61c7a2ac2d6`
- [Replay Gold score](../../artifacts/qualification/pdf-v14-src095-reason-v2-replay-20261026/gold-score.json)
- [First-attempt pre-Gold freeze](../../artifacts/qualification/pdf-v14-src095-reason-v2-rerun-20261026/pre-gold-freeze.json) — SHA-256 `92eba85e0b54bd4028d71e447e4f4954886c6a5d4bae2fe12d29865bf590c2f4`
- [First-attempt incomplete score](../../artifacts/qualification/pdf-v14-src095-reason-v2-rerun-20261026/attempt-incomplete-gold-score.json)
