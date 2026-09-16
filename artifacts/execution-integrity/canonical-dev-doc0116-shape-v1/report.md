# DOC-0116 EXECUTION SHAPE FORENSIC V1

Status: `DOC0116_EXECUTION_SHAPE_DIAGNOSED`

## Finding

EXEC_V3 enforced a 300-second **per-document parent watchdog**. DOC-0116 started at `2026-09-16T10:02:06.6614488+00:00` and was terminated at `2026-09-16T10:07:06.7047020+00:00` with `PROCESS_TREE_KILL`. DOC-0001 completed in `23.756` seconds and made `5` provider calls.

The exact blocking logical call and transport phase are **UNKNOWN**. EXEC_V3 persisted the worker job before entering the child, but did not persist per-logical-call request/attempt telemetry before timeout. No request bytes, response headers, stream events, parse event, or binding event survived for DOC-0116. This is an `OBSERVABILITY_GAP`, not evidence of a provider stall or oversized request.

## Time budget

| Layer | Value / observation |
|---|---|
| Parent document watchdog | 300 seconds |
| Provider request timeout | 90 seconds |
| Provider configured retries | 2 |
| Actual terminating layer | parent watchdog, process-tree kill |
| DOC-0116 wall time | 300.043 seconds |

The run proves the document watchdog is coarse relative to a multi-call document, but cannot distinguish request construction, provider wait, response parse, or binding.

## Production-input comparison

| Metric | DOC-0001 | DOC-0116 |
|---|---:|---:|
| Source bytes | 2259 | 438079 |
| Parser paragraphs | 14 | 3590 |
| Candidate/heading inputs before model | 7 | 404 |
| Logical provider calls | 5 | unknown |
| Persisted request contracts | 5 | 0 |
| Largest persisted contract bytes | 14166 | unknown |
| Largest estimated input tokens | 4167 | unknown |

DOC-0001 measurements are UTF-8 bytes and `ReasoningTokenBudget` estimates of persisted model-input contracts. They are not full provider HTTP-envelope sizes. DOC-0116 has no persisted request contract to measure.

## Decision

Primary execution blocker: `OBSERVABILITY_GAP`.

Recommended next experiment: **D. OBSERVABILITY_INSTRUMENTATION**. Add append-only per-logical-call events before transport and at request-built, headers-received, body-progress, response-complete, parse-start, parse-complete, bind-start, and bind-complete. This changes execution observability only; it does not change production semantic behavior.

No EXEC_V4, provider call, Gold read, scoring, truncation, or request repair was performed.
