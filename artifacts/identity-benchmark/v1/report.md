# A99 Identity Promotion Benchmark v1

Status: `READY_FOR_PROVIDER_EXECUTION`

This is a Gold-blind candidate and request freeze. No provider/model call was made.

## Frozen input

- Machine-evaluable identity Gold: `5/5` (evaluation-only; relation labels were not loaded for preparation).
- Source universe: `6538` source occurrences across `3` documents.
- Candidate generator: `hdsa-deterministic-identity-candidate-generator-v1`.
- Candidate pairs: `95999`.
- Candidate SHA256: `d9718a6fa67ad7b2dea68959517605751d9b2dfd207ed3036d3d1e0a111e2b4d`.
- Verifier requests: `95999`.
- Request manifest SHA256: `b94524b99c1162d09849a1e5fde27589be548e43f3eb91e096acbc030e67341d`.
- Exact request bytes: per-request SHA256 and byte length frozen; bodies are reconstructible from the frozen source catalog.
- Source catalog: `source-catalog.json`; relation Gold is not loaded in this phase.
- Provider/model: OpenRouter endpoint / `qwen/qwen3.7-flash` from the current live runner configuration.
- Planned provider calls: `95999`; new calls: `0`.

## Firewall

`GoldReadCountBeforePredictionFreeze=0`, `GoldConsumedBeforePredictionFreeze=false`.
Candidate generation and request construction use source facts only. Relation labels, Gold confidence, model outputs, and residual hints are absent. The five cases were not injected as candidate pairs; natural retrieval is measured by joining after freeze.

## Gate

`READY_FOR_PROVIDER_EXECUTION`

This task stops before provider execution because the current turn does not explicitly authorize a new external benchmark call. Existing frozen raw responses will be replayed only where request hashes are compatible; otherwise each request requires a new call. No production behavior, promotion policy, prompt, or model configuration was changed.
