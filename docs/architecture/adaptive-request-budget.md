# Adaptive request packing and budgets

## Implemented boundary

The shared resource envelope and attempt accounting apply to the built-in agent transport. PDF selection is the first structure-aware packing adapter. DOCX/scan, grouping, placement and preflight keep their existing task-specific boundaries while using the shared admission/accounting layer. This change does not introduce semantic verification, change membership/boundary authority, or modify Gold.

`RequestPolicy` is an immutable, versioned profile for the currently locked OpenRouter/Alibaba route. It separates HTTP bytes, estimated prompt tokens, context, enforced completion cap, pricing and continuation headroom. This is a checked-in snapshot with operator overrides, **not automatic endpoint discovery** or an exact tokenizer. Input text uses a labeled UTF-8/framing proxy; images use a configurable per-image token allowance (16,384 by default), never base64 bytes as tokens. Reported native usage remains distinct in the audit. Estimates can be wrong; actual cost above its reservation halts further admissions.

## PDF plan

The production `PdfSelector` tries a complete page using the same builder, instruction, facts, styles, tools, output cap and context that `SelectionTask` sends. It accepts the page only when the measured request and estimated tokens/output fit its envelope, with continuation headroom. It no longer imposes 48 targets. Oversized pages descend through parser layout blocks, visual rows and finally atoms. An atom that cannot fit is refused as `UNSPLITTABLE_REQUIRED_SCOPE`; required source evidence is never truncated to manufacture a fit.

The ordered planner examines legal ends; it does not assume packet size is monotonic, reorder source units, or interpret source text. Every target has exactly one owner and read-only context can repeat. Local facts collection uses legal tool windows even when a page has more than 400 targets. Selection completion demand scales with target cardinality plus a configured reasoning allowance; the historic 32,768 cap is a preferred floor, bounded by the profile ceiling. If the estimated per-target demand itself exceeds the endpoint allowance, the scope must split.

Default HTTP ceiling is 256 KiB for PDF. Continuation byte/token reserve targets are 64 KiB/65,536 tokens, bounded by a configurable fraction (default one quarter) of the respective envelope. These are headroom estimates, not a guarantee that any possible tool history fits. Every continuation and protocol/transport retry is admitted again using its **complete actual body**. An oversized continuation fails closed; no source text, tool messages or required facts are silently dropped. Paging/restarting a child conversation requires a future task-specific preservation/composition contract.

`ResourcePlan` records the plan ID, policy version, alias ranges, target counts, structural boundaries, first-body sizes and request hashes. The ID includes source/universe/evidence hashes, policy, instruction and the ordered first-request hashes. Segment indexes alone are not stable identity after repacking. Historical V6 parity fixtures explicitly use legacy packing; production and the PDF072 regression exercise the new planner.

## Accounting

`AgentBudget` reserves each admitted attempt using its estimated input and **the enforced completion cap of that request**. Confirmed billing replaces its reservation; settlement is idempotent. Missing usage, cancellation, timeout and broken streams retain the reservation provisionally, because aborting a connection need not stop upstream billing. A later confirmed bill can settle that entry. A 429 is confirmed as no generation charge, but every retry still consumes an attempt and must be admitted. Internal retries cannot consume another caller's pending admission for an identical body.

`DocumentRunner.ExtractAsync` opens one isolated `RunResourceBudget` scope. The context flows into preflights, selection/grouping and placement Tasks; each built-in `AgentBudget` automatically joins it. A later extraction on the same runner gets a new ledger. Parallel extractions do not share one. Confirmed spend plus provisional/outstanding reservations must fit the parent ceiling before another attempt is admitted. Stage ceilings and call limits still apply, so one stage cannot borrow unlimited resources from the whole run.

`PipelineOptions` exposes `MaxProviderCostUsd` (default USD 5), `MaxProviderAttempts` (default 2,000) and optional `ProviderBudgetDuration`. The duration is an **admission deadline**, not a promise to cancel upstream billing. Nullable `MaxModelRequestBytes`, `ModelContextTokens`, `MaxModelPromptTokens`, `MaxModelOutputTokens`, `ContinuationReserveBytes` and `ImageTokenAllowance` override the versioned profile. Null means that profile's configured default, not unlimited resources. A custom `PdfSelector` may also receive a `RequestPolicy` explicitly.

The root `RunResult.ResourceBudget` reports confirmed, provisional and outstanding amounts for **default agent transport admissions**. It does not claim to meter arbitrary custom `IModelProvider` implementations. Safe Web audit includes this summary, selection plan, per-attempt resource/usage observations and placement accounting. No source text, prompts, crop bytes or credentials are in these observations. Existing raw evidence controls remain separate.

## Subsequent migrations

This foundation is resource-adaptive, not a learned quality optimizer. It does not claim native exact preflight token counting, live metadata refresh, learned quantiles or adaptive provider concurrency. The next adapters must respect their own composition rules: grouping cannot arbitrarily split a selected run; hierarchy must retain its ancestor catalog; OCR crops need coordinate ownership and overlap reconciliation. These boundaries should not be changed by a generic byte splitter.

Before learned calibration, collect native token counts, enforced cap, output truncation, tool growth, actual/provisional costs, route identity and latency. Freeze profile/calibration revisions within a plan. `finish=length` remains a failure and never authorizes a parseable prefix. A valid semantic answer is not retried merely because its decision is inconvenient.

## Verification

Regression coverage includes complete PDF072 page 15 (76 atoms in one request), a smaller envelope forcing legal splits, exact target/facts coverage and first-body hash parity with runtime; distinct output/context/byte constraints; image accounting independent of base64 length; idempotent unknown/confirmed settlement; concurrent reservations; isolated parent run scopes; and per-attempt retry admission. These tests use recorded source files or simulated providers, not paid inference.
