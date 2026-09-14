# A99 Identity Promotion Benchmark v1 — Provider Execution Readiness Audit

Recommended gate: **`BLOCKED_ON_BENCHMARK_PIPELINE_MISMATCH`**

This audit is source/request-manifest-only. It made **0 model calls**, **0 provider calls**, and opened **0 semantic Gold files**. Frozen v1 artifacts were not modified.

## Frozen workload

- Source units: `6,538`
- Candidate pairs / frozen requests: `95,999` / `95,999`
- Estimated input workload: `15,349,632,526` tokens (UTF-8 bytes / 4, ceiling; estimated)
- Provider/model: `OpenRouter / qwen/qwen3.7-flash`

## Request and context validity

Request-byte and estimated-token distributions are in the JSON artifact (min, p50, p90, p95, p99, max, mean, total).
- Configured context limit: `1,000,000` tokens; provider-advertised value was not verified because no call was allowed.
- Estimated classifications: within `95,999`, near `0`, exceeds `0`.
- Context status: `WITHIN_CONFIGURED_LIMIT_NOT_PROVIDER_VERIFIED`.

## Workload / cost / latency

- Output projection uses explicitly labeled planning assumptions: minimum 64, typical 256, configured maximum 16,000 tokens per request.
- Pricing: `COST_UNKNOWN`; no authoritative local price metadata was available without external execution.
- Theoretical completion times for 95,999 requests: 1 rps ≈ 26h 40m; 5 rps ≈ 5h 20m; 10 rps ≈ 2h 40m; 25 rps ≈ 1h 4m; 50 rps ≈ 32m. These are throughput scenarios, not provider guarantees.
- Current runner config says max parallelism 1 and transient retries 0; provider throttling/concurrency remains unknown.

## Redundancy and candidate density

- Candidate reasons and per-document density are recorded in JSON.
- The generator is high-recall pair retrieval: adjacency and normalized-text affinity are evidence only; no Gold reduction was performed.
- Top-50 occurrence fan-out is recorded in JSON to expose pathological degree without tuning it.

## Pipeline intent / compatibility

- Frozen v1 request construction is intent **A**: one verifier request per generated candidate.
- Current checked-in runner evidence: `src/DocxHeaderExtractor.Eval/ReasoningRetention/HdsaDeterministicCandidatePairVerificationLiveRunner.cs:111 foreach candidateInput.Candidates; src/DocxHeaderExtractor.Eval/ReasoningRetention/HdsaDeterministicCandidatePairVerificationLiveRunner.cs:115 new HdsaIdentityPairVerificationRequest; src/DocxHeaderExtractor.Eval/ReasoningRetention/HdsaDeterministicCandidatePairVerificationLiveRunner.cs:116 JsonSerializer.Serialize(request, JsonOptions); src/DocxHeaderExtractor.Eval/ReasoningRetention/HdsaDeterministicCandidatePairVerificationLiveRunner.cs:96-99 ContextSize=1,000,000; MaxOutputTokens=16,000; MaxParallelRequests=1`.
- Existing pruning stage: `NO_PRE_VERIFIER_PRUNING_STAGE`.
- Current serialization sample status: `SERIALIZATION_MISMATCH_ON_SAMPLES` (0/3 samples match).
- The current live runner is DOC-0205-specific and does not consume the three-document v1 request manifest. This is retained as a compatibility/integration finding; v1 was not rewritten.

## Decision

`BLOCKED_ON_BENCHMARK_PIPELINE_MISMATCH`. The audit does not authorize provider execution. Resolve the frozen-request/current-runner compatibility issue first; if the v1 contract is confirmed compatible, the remaining blocker is the unbounded one-request-per-candidate workload with no pre-verifier pruning/ranking stage.

## Reproducibility

Run the audit tool from the repository root with no network dependency. It reads only `artifacts/identity-benchmark/v1/{manifest,request-manifest,candidate-set,source-catalog}.json` and the checked-in verifier runner source.
