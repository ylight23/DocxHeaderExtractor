# A99 V4F-D — paired old-context vs projected-context verifier experiment

Status: **READY_FOR_V4F_PAIRED_PROVIDER_EXECUTION**

Population: **7,702** frozen candidates. Source-only deterministic sample: **128** candidates across **3** documents and both packet classes.

## Paired design

Each sampled candidate has ARM_A (frozen old full-context request) and ARM_B (frozen projected request). Candidate identity, model (`qwen/qwen3.7-flash`), provider (`OpenRouter`), decision options, response schema and configuration are held constant. The only changed variable is evidence representation. Request bodies are not persisted; SHA/length/builder metadata are frozen and reconstructible.

Future calls: **256**; old estimated input tokens: **30,212,453**; projected estimated input tokens: **304,123**; combined estimated input tokens: **30,516,576**. Pricing: **COST_UNKNOWN**.

## Sampling

Selection seed: `a99-v4f-d-source-only-stratified-sha256-v1`. Strata use document, packet class, natural retrieval-reason configuration and source-only distance bucket; stable SHA ranking makes the sample reproducible. Population/sample counts are in `population-strata.json`. No Gold, IR, V4E-B outcome or model output was read.

## Metrics frozen

Behavioral preservation is separate from semantic accuracy: pairwise relation agreement, projected change rate, parse validity, invalid response and abstention rates. Semantic accuracy requires independent labels; ARM_A is not an oracle.

## Firewall

`ProviderCalls=0`; `ModelCalls=0`; `GoldReadCountForSampling=0`; `V4EBEvaluationReadCountForSampling=0`; `KnownIRUsedForSelection=false`; candidate universe/ranking/projection unchanged.

**Next gate:** provider execution requires separate authorization.
