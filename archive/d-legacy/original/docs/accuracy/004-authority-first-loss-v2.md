# Document 004 occurrence-safe authority first-loss trace V2

Binding: `EXACT_SOURCE_IDENTITY`; occurrenceSafe: `false` because 38 occurrence bindings remain unresolved.
Reference: 93 model-assisted silver structural occurrences; not independent human gold.

| class | expected | exact source | selected exact | model timeout |
|---|---:|---:|---:|---:|
| DOCUMENT_TITLE | 1 | 1 | 1 | 1 |
| CHAPTER | 7 | 5 | 5 | 5 |
| SECTION | 8 | 0 | 0 | 0 |
| ARTICLE | 77 | 49 | 49 | 49 |

OpenRouter Qwen9B run: `pdf-authority-v1`, candidate pool 2653, selected 160.
Semantic: scheduled 160, completed 0, timed out 160. Span: scheduled 160, completed 0, timed out 160. Semantic batches: 40.
Product emitted: 0. Provider calls: OpenRouter; VLM calls: 0.
Exact selected source identities: 55. Those exact occurrences first lose at `MODEL_EXECUTION_TIMEOUT`. The remaining 38 have no exact source-line lineage in the frozen audit and remain `UNRESOLVED_BINDING`.
No SOURCE_FACT_LOSS, CANDIDATE_GENERATION_LOSS, or CANDIDATE_SELECTION_LOSS is proven.
Article 6 and Article 7 collision check: distinct.
PRIMARY_FIRST_LOSS_OWNER: `UNRESOLVED_TRACE`
PRIMARY_STATUS: `UNRESOLVED`
PROVIDER_CALLS_THIS_TASK: 0
PRODUCTION_CODE_CHANGED: false
REMEDIATION_PERFORMED: false
