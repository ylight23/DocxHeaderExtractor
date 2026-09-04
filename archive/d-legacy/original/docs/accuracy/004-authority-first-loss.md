# Document 004 authority first-loss trace

Canonical pipeline: `AuthorityExtractionPipeline`
Reference: 93 model-assisted silver structural occurrences; not independent human gold.

| class | expected | emitted | source fact | candidate | selected |
|---|---:|---:|---:|---:|---:|
| DOCUMENT_TITLE | 1 | 0 | 1 | 1 | 1 |
| CHAPTER | 7 | 0 | 5 | 5 | 5 |
| SECTION | 8 | 0 | 0 | 0 | 0 |
| ARTICLE | 77 | 0 | 76 | 76 | 60 |

Current run: OpenRouter Qwen9B, PDF route, 160 selected blocks. Product emitted: 0.
Semantic scheduled/completed/timed out: 160/0/160.
Span scheduled/completed/timed out: 160/0/160.
Provider calls: OpenRouter; VLM calls: 0.
No validator, hierarchy, grounding, or output-policy loss is claimed because model lanes timed out before proposals.
PRIMARY_FIRST_LOSS_OWNER: `UNRESOLVED_TRACE`
STATUS: `UNRESOLVED`
PRODUCTION_CODE_CHANGED: false
REMEDIATION_PERFORMED: false
