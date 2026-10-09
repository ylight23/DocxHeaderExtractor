# Authorized F1 V2 execution

Separately approved 14 frozen F1 Control/B logical requests, at most one identical-byte retry
per failed request (28 HTTP attempts). Transport retries remain zero; no repair, fallback,
G2A/H2-C fan-out, Gold mutation or production promotion. Retry only transport/strict protocol
failures, never a semantically incorrect but valid decision.

This explicitly supersedes V1's execution lock and pre-reservation USD gate for this run only.
User accepts post-accounting. No approved USD cap, no dedicated-key limit verification, no
verified per-request billing bound. Missing charges remain UNKNOWN, not zero. Frozen V1 receipts
are preserved. Uses the production OpenRouter SSE engine and OPENROUTER_API_KEY from environment.

1. `prepare <frozen-request-dir> <new-public-plan.json>` validates bodies and freezes authorization.
2. Commit preflight before execution.
3. `execute <frozen-request-dir> <full-source-dir> <plan.json> <plan-sha256> <new-private-capture-dir> <new-public-receipt.json>`
4. Freeze/commit capture receipt before opening Gold.
5. `score <frozen-request-dir> <capture-dir> <public-capture-receipt> <gold-dir> <new-public-score>`

Source snapshots and store reconstructed before network; Gold/reviewer sidecars not loaded.
Fresh anonymous endpoint metadata checked before every attempt; endpoint/body drift fails closed.
Each primary/retry has a durable separate reservation, exact body, raw response/SSE hashes,
usage, strict parsing result and error classification. Received raw output freezes before parsing.
Failed transport may not expose raw/usage through the production engine; that limitation and
unknown billed exposure are recorded rather than invented. Private exception detail is never pushed.
Private local raw retention is not remotely reparseable; public receipts preserve hashes.

Scoring is F1-isolated on 213 adjudicated rows; 430 other rows per arm remain UNKNOWN. No
extent accuracy or end-to-end claims. Missing/failed requests are not converted to OTHER;
retry-inclusive and zero-retry status are reported separately.
