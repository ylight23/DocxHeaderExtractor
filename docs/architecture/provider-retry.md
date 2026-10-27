# Provider retry ownership (2026-10-24)

Production `AgentModel` owns the allowlisted transient retries: HTTP 429, server/network failures,
incomplete streams and SSE errors. All of these share **three retries** (four sends maximum per
logical turn), with identical request bytes. The former five inner 429 retries could be restarted
by conversation/grouping/placement retries; managed failures now carry `TransportRetriesHandled`
so those callers cannot restart the sequence. Protocol repairs and the scan task's explicit
timeout/reasoning-exhaustion retry remain task-specific. Single-attempt transports and synthetic
model delegates retain the caller retry contract used by offline qualification.

The default cumulative **waiting** allowance is 30 seconds per logical turn. It includes account
admission/cooldown and retry backoff, not time spent receiving provider responses. The existing
300-second HTTP attempt timeout and the run's resource/attempt ceilings still apply. This is not
a 300-second document deadline or a proven latency improvement.

`Retry-After` delta and HTTP-date forms are respected. A delay longer than the remaining waiting
allowance stops the turn; it is never shortened to send early. Without a hint, retries use jittered
exponential delays beginning at 4 seconds. A 429 publishes a shared cooldown even on the final
attempt. Waiting and queued admission are cancellable, and no concurrency permit is held during
backoff. Response headers are read before the body so HTTP status and Retry-After survive body-read
errors; a linked timeout covers both headers and body. SSE is still assembled after receiving the
body: this change does not measure first-token latency.

An in-process gate shared by API-key hash limits the locked provider/account to eight in-flight HTTP
requests (matching the eight concurrent PDF SELECT requests) across model instances and document runs. `DHX_OPENROUTER_MAX_CONCURRENCY` overrides that
limit when the account gate is first created. It does not limit other processes/machines or estimate
provider TPM. No raw API keys are stored in the registry. The limit is fixed, not a learned controller.

Every retry has its own resource admission. Only HTTP 429 and requests known not to have been sent
are settled as zero cost. Server errors, interrupted streams and dispatched cancellations retain
provisional reservations until billing is confirmed. Known pre-dispatch queue failure/cancellation
releases its reservation. Terminal failures remain failed/unresolved; no answer or source evidence is
truncated to satisfy the retry limit.

Selection and placement resource audits expose safe per-attempt `Transport` observations: unit,
turn, retry index, HTTP status, outcome, request seconds, queue/cooldown seconds, Retry-After seconds,
actual backoff seconds and numeric provider error code where supplied. Provider error messages,
response bodies and credentials are excluded. Billing `Status` is separate from transport `Outcome`.

Verification uses simulated providers for repeated/mixed failures, exact request preservation,
Retry-After, cancellation, shared concurrency and resource settlement, plus selection/grouping/
placement regressions proving that managed retry sequences do not multiply. A live PDF072 run and
quality/latency benchmark are separate from these offline checks.
