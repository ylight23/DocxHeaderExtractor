# A99 semantic control ceiling study

Status: R1 provider execution, 30-attempt continuation, and 10-attempt final retry stopped incomplete before Gold/scoring. No R1 accuracy or promotion claim is made.

## 1. V4 frozen baseline

The immutable V4 baseline remains `qwen/qwen3.7-flash`, `V3_RobustGlyphStatistics`, `FIXED_OWNED_COUNT_120`, the existing source-part canonicalizer/binder, and the single-authority contract `sourceParts + semanticFunction`.

- SRC-089 F1: 0.8406; Article exact 26/26; Chapter exact 3/5.
- SRC-095 F1: 0.8111; TOC false positives 0; INDEX_ENTRY false positives 20; wrong-extent false positives 6.
- Production default remains V2. Frozen V4 raw/run/score artifacts were not modified.

## 2. Actual reasoning transport state

The frozen V4 execution wrote `reasoning: { effort: "none" }` on the OpenRouter boundary route. The optional legacy `OpenRouterReasoningEnabledOverride` was not consumed by that body. The current transport exposes `OpenRouterReasoningEffort` for a separately authorized arm while preserving the default `none`. Across 25 successful frozen V4 responses, observed telemetry had `reasoning_tokens=0` and no `message.reasoning` channel.

Conclusion: `V4_BASELINE_REASONING_MODE = EXPLICITLY_DISABLED`.

Evidence: [reasoning-transport-audit.v1.json](reasoning-transport-audit.v1.json).

## 3. Provider execution fingerprint

`ProviderSemanticExecutionFingerprint` now hashes model, endpoint, sampler fields, max tokens, reasoning field/value, response format/schema, provider routing, and tool configuration. It excludes API keys, request IDs, timestamps, and telemetry. Tests prove same-config equality, reasoning-change inequality, and API-key invariance.

## 4. Residual causal taxonomy

All 52 frozen residuals are classified:

- `MODEL_SEMANTIC_MISCLASSIFICATION`: 34
- `CLAIM_EXTENT_ERROR`: 18
- `ONTOLOGY_GAP`: 0
- `CONTEXT_INSUFFICIENT`: 0 as a proved primary class
- `SOURCE_REPRESENTATION_ERROR`: 0
- `BINDER_ERROR`: 0
- `GOLD_OR_DEFINITION_AMBIGUITY`: 0

The 20 SRC-095 index-entry false positives are model `REGION_STRUCTURE` decisions where the current ontology already provides `NAVIGATION`. They are not evidence for a new membership function. Evidence: [residual-causal-audit.v1.json](residual-causal-audit.v1.json).

## 5. Ontology expressiveness findings

The nine functions have normative definitions, necessary/exclusion conditions, counterexamples, and six boundary pairs. `REGION_STRUCTURE` versus `NAVIGATION` is occurrence-sensitive; `DOCUMENT_IDENTITY` versus `METADATA` is identity-sensitive; captions and table rows remain object/table-scoped non-members.

## 6. Is the nine-function ontology sufficient?

Yes for the current membership decision. Index entries are `NAVIGATION`; an index-group letter can be `REGION_STRUCTURE`. A later `structuralKind=INDEX_GROUP` descriptive axis may improve reporting, but it must not become a second membership authority. No new `semanticFunction` is justified by the frozen residuals.

## 7. Is explicit reasoning a causal bottleneck?

The authorized R1 run changed only the OpenRouter reasoning envelope from `none` to `medium`, but did not reach 25/25 successful predictions. The immutable attempt-1 artifact records 19 attempts and 7 successful predictions (SRC-089: 5/5; SRC-095: 2/20). A separately authorized execution-reliability continuation increased only the client timeout from 90s to 300s, preserved the semantic request bytes, and consumed its full incremental 30-attempt budget; it added 17/18 pending SRC-095 predictions, leaving 24/25 cumulative successful hashes. A final authorization allowed 10 more attempts for the last hash; all 10 responses lacked `choices[0].message.content`, so the hash remained pending. Therefore the causal question remains unscored and the medium-reasoning route is not operationally complete under these budgets.

Evidence: [r1-run.v1.json](r1-run.v1.json), [r1-continuation.v1.json](r1-continuation.v1.json), [r1-final-continuation.v1.json](r1-final-continuation.v1.json). Gold remained closed and no score or combined manifest was produced.

## 8. Is context/extent now the dominant limitation?

The current evidence points to semantic classification plus claim extent. The six wrong-extent SRC-095 false positives and partial Chapter/title occurrences are extent failures; absent index-group and clause/title occurrences are classification/omission failures. A reasoning-only counterfactual is required before ranking reasoning versus context.

## 9. Held-out generalization result

Not run. It remains blocked until a complete authorized R1 causal result is frozen. No promotion claim is made.

## 10. Recommended production architecture

Keep V2 as production default. Keep V4 experimental. Preserve open private reasoning with a closed typed commitment: exact source parts, one closed semantic function, fail-closed validation, deterministic membership, exact binding, identity, then hierarchy.

## 11. What not to change

Do not alter frozen V4 raw/score artifacts, production default, model, facts, packing, binder, Gold, or semantic taxonomy based on these residuals. Do not add post-filters, document-specific rules, generic `heading`, `isHeading`, or a new index-specific membership function.

## 12. Promotion decision evidence

No promotion. R1 preflight is frozen at 25 planned requests (SRC-089: 5, SRC-095: 20), identical semantic request hashes, and a reasoning-only fingerprint delta. The first attempt stopped at its output cap; the authorized 300-second execution-reliability continuation stopped at its 30-attempt cap with one SRC-095 hash still pending; the final 10-attempt retry also failed the response contract. Gold remained unopened and no score was generated. Under this route and budget, medium-reasoning R1 is not operationally viable.

Evidence: [r1-reasoning-preflight.v1.json](r1-reasoning-preflight.v1.json), [r1-run.v1.json](r1-run.v1.json), [r1-continuation.v1.json](r1-continuation.v1.json), [r1-final-continuation.v1.json](r1-final-continuation.v1.json).

## 13. Frozen R1 wording and next work

The R1 freeze is explicit: semantic completion `24/25`; accuracy evaluation `BLOCKED`; reasoning execution `CONFIRMED`; the 300-second client timeout materially improved completion reliability; the final unresolved request consumed 10 additional attempts and produced 10 provider responses but zero contract-valid predictions. No further R1 calls are authorized.

The next work is provider-free `A99_REQUEST_ARCHITECTURE_V2`: request census and decomposition, resource-aware packing, exact-once ownership with halo, deterministic adaptive split, content-addressed resume, and response-shape handling. Its preflight and simulator artifacts are frozen under `../request-architecture-v2/`; they preserve the V4 semantic contract and production defaults, make zero provider calls, and read no Gold.
