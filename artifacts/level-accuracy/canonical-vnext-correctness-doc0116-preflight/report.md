# Canonical vNext correctness preflight — DOC-0116

Status: `READY_FOR_CORRECTNESS_PROVIDER_AUTHORIZATION`

- source paragraphs: `3590`
- non-empty source paragraphs: `1921`
- canonical source catalog units / model-visible aliases: `1921`
- candidate hints: `1921` (attention-only)
- V2A subset used: `false`
- provider calls: `0`
- Gold reads: `0`
- scoring: `false`

The preflight materializes the same source alias packet used by the canonical semantic adapter.
Every alias remains visible regardless of its candidate hint. Prediction and evaluation artifacts
are intentionally not created; a fresh provider authorization is required for the correctness run.
