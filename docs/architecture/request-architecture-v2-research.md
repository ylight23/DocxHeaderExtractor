# Request Architecture V2: research basis

Status: provider-free preflight. No provider inference calls were made and no Gold was read.

## Evidence labels

This document deliberately separates what was observed, what external work reports, and what V2 concludes. An inference is not a provider capability claim.

### SOURCE FACT

- The frozen V4/R1 execution lineage records that `FIXED_OWNED_COUNT_120` was incomplete: 25 planned requests, 19 attempts, 7 successful attempts, 18 pending leaves, 1,187,381 input tokens and 239,017 output tokens. SRC-089 alone had 14 attempts and five successes; SRC-095 had five attempts and two successes. This is execution evidence, not an accuracy result.
- The current production packing default remains `FIXED_OWNED_COUNT_120`; V2 is an explicit opt-in policy and does not change the default.
- Provider-free endpoint metadata observed on 2026-09-27 for `qwen/qwen3.7-flash` reported a 1,000,000-token context window, a 65,536 maximum completion allowance, and `response_format` in supported parameters. Its Alibaba endpoint reported implicit caching support. The metadata did not prove explicit cache control nor `json_schema` with `strict: true` for this exact route.
- The current canonical transport uses JSON-object output. Until strict-schema capability is positively proved for the selected endpoint, V2 retains that response format.

### RESEARCH FINDING

- [Lost in the Middle](https://arxiv.org/abs/2307.03172) reports position-sensitive long-context retrieval: relevant information in the middle may be used less reliably than information near the beginning or end.
- [RULER](https://arxiv.org/abs/2404.06654) measures a gap between advertised context capacity and effective performance as length and task complexity rise.
- [OpenRouter prompt-caching guidance](https://openrouter.ai/docs/guides/best-practices/prompt-caching) documents stable prompt prefixes and `session_id` sticky routing as cache-readiness mechanisms. [Alibaba context cache documentation](https://docs.modelstudio.console.alibabacloud.com/en/model-studio/context-cache) likewise describes common-prefix reuse.
- [OpenRouter structured outputs](https://openrouter.ai/docs/guides/features/structured-outputs) describes `response_format` JSON Schema and strict mode, but provider/endpoint support must be checked.
- [OpenRouter message transforms](https://openrouter.ai/docs/guides/features/message-transforms) documents prompt compression that can remove or truncate prompt content. That conflicts with exact-source extraction.
- [LLMLingua](https://aclanthology.org/2023.emnlp-main.825/), [LLMLingua-2](https://aclanthology.org/2024.findings-acl.57/), and [LongLLMLingua](https://aclanthology.org/2024.acl-long.91/) are useful prompt-compression research, not evidence that token removal is lossless for this binding task.

### ARCHITECTURE INFERENCE

- A request should be bounded by observable execution resources, not merely a count of owned atoms. Count does not capture source-text length, context duplication, envelope overhead, or reserved completion capacity.
- The suitable target is *minimum sufficient raw source context*, never the largest prompt that fits a nominal context window. V2 keeps text verbatim and makes no Gold- or semantic-based selection.
- Stable semantic bytes can be prepared for caching, while cache support remains optional. A cache miss must produce identical model-visible semantic bytes.
- Because the exact endpoint has not proven strict JSON Schema support, V2 fails closed: it records `strictJsonSchemaSupported = UNKNOWN` and keeps `json_object`.
- Lossy transforms, summaries, truncation, and token-dropping are outside canonical V2. Compression research can be evaluated only in a separately authorized, independently losslessness-proven branch.
