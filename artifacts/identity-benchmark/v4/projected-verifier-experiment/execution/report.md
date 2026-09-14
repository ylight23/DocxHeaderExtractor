# A99 V4F-E — paired OLD vs PROJECTED verifier execution

Status: **RESPONSE_FREEZE_COMPLETE_WITH_RAW_PERSISTENCE_GAP**

Frozen sample: **128** candidates / **256** interleaved provider attempts. Model: `qwen/qwen3.7-flash` via `OpenRouter`. The only changed variable is evidence representation.

## Execution

Execution order SHA: `0a3c75d296d8966a70c8545db9e4ec9819d80ba700186406ed8626366a8f2492`. Recorded attempts: **256**; non-transport outcomes: **254**; raw response hashes: **221**. No retries were used.

## Parsing

- OLD valid: **109/128**
- PROJECTED valid: **63/128**
- Valid paired outputs: **58/128**

## Behavioral preservation

- Agreement: **35/58** (60.34%)
- Behavioral changes among valid pairs: **23**

See `agreement-matrix.json`. Agreement is behavioral agreement with OLD, not semantic accuracy; OLD is not Gold.

## Integrity note

See `execution-integrity.v1.json`. The two initial HTTP 200 response bodies were not persisted because of a local runner directory-creation defect; they were retained as immutable attempt records and were not rerun. Provider-error attempts have no raw response body.

## Firewall

Gold reads before response freeze: **0**; V4F-B reads: **0**; sample/request/projection/parser/model config changed: **NO**. Raw responses were frozen before paired comparison.

## Cost and usage

See `usage-and-latency.json` and `cost-report.json`; actual provider usage is reported only when returned.

## Semantic accuracy

**UNAVAILABLE**. No Gold was opened in this experiment.
