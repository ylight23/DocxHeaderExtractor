# P7-D3 F1 execution readiness

Provider-free planning only. No HTTP transport, credentials, inference, Gold decision
reads, runtime changes, or authorization grant. New manifest output uses `CreateNew`.

```powershell
dotnet run --project scripts/P7F1ExecutionReadiness -- <frozen-F1-directory> <new-execution-manifest.json>
```

The plan contains exactly the 14 frozen requests (7 Control, 7 B) in the registered
order. Validate all body/message/issued-universe hashes before any later attempt.
Use frozen bytes directly, not a new serialization. Historical captures are not
adopted and cannot subtract from the planned fresh calls. F1 results cannot silently
fan out G2A/H2-C: downstream generation/capture authorization is separate.

## Limits and authorization

Planned primary/HTTP cap 14, concurrency 1, deadline 300 seconds per streamed attempt,
transport retries 0, repair false, fallback false. Freeze `TransientRequestRetries=0`
explicitly: the transport default is 2 and must not be inherited. The carrier requests
32768 completion tokens per call, total ceiling 458752; this is not observed usage,
an input-token bound, or a hard USD/billing cap. Response parser limits are 49152
UTF-8 bytes for Control and 262144 for B, after raw capture; not raw SSE download caps.

Approved USD/input caps are null. Exact tokenizer mapping is UNVERIFIED, endpoint
usage NOT_MEASURED, and provider execution LOCKED. The existing endpoint snapshot
is historical metadata, not a new endpoint availability/pricing check. Before approval,
bind a current endpoint/pricing observation and a conservative per-call reservation
or explicitly approved exposure policy. A cumulative stop after reported cost alone
cannot guarantee a hard spend cap: a failed/timed-out call may still be billed and a
single in-flight call may overshoot. Missing usage/cost must remain unknown, not zero.

## Capture and failures

Reservation binds plan hash + call identity, before a single HTTP attempt. Preserve
provider body, raw response, raw SSE, observation (including raw usage), hashes and
available failure evidence. Freeze raw first, then strict parse and freeze the ledger.
Successful raw receipt uses the existing upstream schema and validator. The new
attempt schema covers NOT_ATTEMPTED and quarantine outcomes too. A failed/incomplete
call is not a valid empty ledger, is never converted to OTHER and is not retried.
Failure receipts may legitimately lack response/SSE/usage; preserve that absence.

Budget exhaustion, unknown cost exposure, cancellation, endpoint drift or raw storage
failure halt further starts; preserve remaining slots as NOT_ATTEMPTED. Individual
parser/reference failures are quarantined; any continuation is within independently
approved remaining budget/call caps. No substitute upstream or automatic fifth/eighth
call per arm. Never report an incomplete capture as 14/14 successful.

## Scoring boundary

Keep F1 isolated (`F1_ISOLATED_COMPOSITE_B`), controlled downstream and natural E2E
metrics separate. Freeze both arms' raw and parsed receipts before reading Gold for
scoring. Scoring reads the pinned Gold/scorer; interpretation is never Gold.

Report 7 planned per arm, attempts, status counts, valid total-ledger coverage,
JSON/schema/missing-decision/reference/assertion errors, finish reason, observed usage
and reported cost (null when unknown). Score the three F1 functions and establishes
membership separately on the 213 adjudicated rows per arm. Never infer anchor/extent
accuracy from F1. The 430 out-of-scope occurrences stay UNKNOWN, not negatives.
Valid-only scores must disclose coverage and cannot claim full-population accuracy;
paired semantic comparisons require both accepted outputs on the same reviewed rows.
Reasoning tokens are a reported subcomponent, not added again to completion totals.

This manifest has no numeric model score or measured cost. Synthetic tests verify
accounting contracts only. No inference is run merely to finish readiness.
