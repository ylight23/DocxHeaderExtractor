# P7-D3 financial and endpoint binding qualification

Provider-free qualification, not an inference runner or authorization. The only network
operation is an unauthenticated public metadata GET (no redirects, no retries, 30-second
timeout, 2 MiB cap). No source data or API key is sent. Output is create-new only.

```
dotnet run --project scripts/P7FinancialQualification -- snapshot <new-directory>
dotnet run --project scripts/P7FinancialQualification -- qualify <snapshot-directory> <frozen-F1-directory> <new-manifest.json>
```

The second command is entirely offline. Reuse the same immutable snapshot to reproduce
the manifest; a second live GET is a new observation, not a determinism check.

Pin model, Alibaba endpoint name/tag, advertised limits, all pricing tiers, supported
parameters, caching and quantization. Raw metadata has its own hash, timestamp and receipt.
Volatile uptime/latency does not change the semantic binding. Before each future request,
require a fresh observation (at most 300 seconds old) with the same binding. Pricing,
identity, support or limit drift halts. `order=[alibaba]`, `allow_fallbacks=false` and
`require_parameters=true` remain in the frozen carrier; do not add `max_price`, change
headers, or assume transport's default retries (2) are disabled. Explicit execution retries
must be zero. Public metadata does not pin hidden weights or prove max_tokens enforcement.

The $1.00 total is CANDIDATE ONLY. No budget or call authorization is granted. No key is
created, inspected, or used. A separate limited key needs a redacted, hash-bound receipt
and proof of exclusive use, matching limits and remaining credit before execution.
Never store a key or bearer header in artifacts. A key limit is defense in depth, not
a claim that an in-flight request cannot overshoot. Reset policy/external users must not
replenish or consume the experiment budget unnoticed.

Offline stress sizing uses advertised maximum prompt tokens and sums maximum prompt,
cache-read and cache-write rates, plus 32768 at maximum completion price. This intentionally
avoids assuming mutually exclusive cache charges. It is NOT a verified billing upper bound
or a tokenizer measurement. Bytes are not converted into a token bound. A separate
validated exposure/billing policy must establish token enforcement and all charge categories;
otherwise pre-send is denied. Metadata support alone does not prove all completion,
including reasoning, is capped at the requested value. No whole-14 cost guarantee is made.

Fixture ledger reserves before a start, admits one outstanding call, enforces 14 unique
frozen calls/no retries, then records usage.cost (account charge), not upstream cost.
Missing prompt/completion/cost or invalid receipt halts. Reasoning usage is optional and
not added again to completion. Failed or unknown-charge calls retain unresolved exposure,
never become free calls; known costs are retained even with missing token counts.
An overrun records actual cost without clamping and stops future calls. Ledger simulation
is not integrated into live transport and cannot independently prove a hard USD cap.

Official docs are pinned as references in the manifest. Their guidance on in-flight holds
excludes non-token charges and is not substituted for this experiment's budget policy.
Live execution needs separate approval, billing-bound proof, dedicated-key receipt and
transport integration. Gold, production, historical receipts and frozen request bytes remain
unchanged. Synthetic accounting tests are not semantic accuracy or measured provider usage.
