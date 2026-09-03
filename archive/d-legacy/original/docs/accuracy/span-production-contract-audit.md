# Span Production Contract Audit

Phase A was offline-only. No provider was called and production code was not changed.

## Reachability

The normal `AuthorityExtractionPipeline` call omits `checkpointPath` and `resume` when it invokes `TryBuildBroadAuditWithAnalystAsync`. Therefore `NORMAL_AUTHORITY_CHECKPOINT = DISABLED`. R1 partial-span preservation code exists and requires a checkpoint, but is not reachable from the normal authority route.

## Frozen 004 workload

The frozen role responses cover 160 unique candidate IDs: 96 heading-like, 64 non-heading, and 0 uncertain. With span batch size 4, the observed workload requires 24 batches. This is workload arithmetic only; it is not an HTTP request count.

Semantic budget is request 90 seconds, batch 120 seconds, lane 5 minutes, concurrency 1. Span batches are sequential. Span uses the outer `RemainingOr(RequestTimeout)` lane policy; there is no separate per-batch request timeout in `ResolveHeadingSpansAsync`.

Actual span HTTP requests, batches started/completed, per-batch latency, and time remaining at span start are not observable from the frozen counters.

## Hypotheses

- H1 checkpoint reachability: **PROVEN**.
- H2 total budget: **UNRESOLVED**.
- H3 provider latency/transient behavior: **UNRESOLVED**.
- H4 sequential batching as causal owner: **UNRESOLVED**.

Historical evidence supports recurrence of the partial-timeout lane across 001, 003, and 004, but the discard mechanism is only **PARTIAL** because 003 has durable completed checkpoint batches while 004 has no normal-route checkpoint. `LIVE_CANARY_JUSTIFIED = true`; it was not executed in Phase A.

