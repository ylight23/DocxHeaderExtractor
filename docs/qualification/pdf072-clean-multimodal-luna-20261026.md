# PDF072 clean multimodal SELECT contract run — 2026-10-26

## Result

The production `PdfHandler.ExtractAsync` path completed for PDF072 using V12 selection instruction text unchanged. A test-only `HttpMessageHandler` adapter appended the PNG page(s) referenced by the request aliases as OpenAI-style `image_url` content blocks on each SELECT user message. The captured final request bodies prove image data was in every SELECT packet: **15/15 SELECT packets contained image blocks**, spanning all 15 rendered pages and all 650 target aliases. The run produced 42 headings. The production GROUP and PLACE prompts remained text-only; there was one GROUP packet and six PLACE packets, with zero image blocks.

The adapter captured the actual final body passed by production `HttpClient` into the handler, before network transmission. It did not send requests to OpenRouter or another provider. Responses were fresh local bridge files authored independently from the captured text and page images, without cross-request conversation history. The route envelope identifies `qwen/qwen3.7-flash` / Alibaba, but there was **no Qwen/Luna inference call**. The local response bridge is therefore a contract/payload experiment, not a provider model-quality run.

## DOC-0252 score (after the pre-Gold freeze)

Gold was opened only after the complete run and its request/response artifacts were frozen. DOC-0252 Gold has 45 aliases in 42 groups; the captured output selected 42 aliases in 42 groups.

| Measure | TP | FP | FN | Precision | Recall |
|---|---:|---:|---:|---:|---:|
| Alias membership | 42 | 0 | 3 | 1.000 | 0.933 |
| Exact groups | 39 | 3 | 3 | 0.929 | 0.929 |

The omitted aliases were `L0360:S0`, `L0513:S0`, and `L0514:S0`. The three exact-group misses were the two-line document title (`L0000:S0` + `L0001:S0`), the wrapped research topic (`L0359:S0` + `L0360:S0`), and the two-line appendix title (`L0513:S0` + `L0514:S0`). The first title was split into two one-alias groups. The latter two were not fully selected. Gold does not make hierarchy accuracy evaluable; placement is reported only as production output.

These metrics score the locally authored response set. They do not measure Qwen/Luna performance or establish a general model-quality claim.

## Scope and wire evidence

- Source: `072_ICP_TAG_Minutes_Mar_2025.pdf`, SHA-256 `a005f25e3bb9754cd6c8c7000682d00eb68238fb8937d3475fe807ffbbd94b61`.
- Rendered all 15 pages as PNG using the existing production PDF raster renderer at 200 DPI; each page is 1699 × 2200. See [image-manifest.json](../../artifacts/qualification/pdf072-clean-multimodal-luna-20261026/attempt-02/image-manifest.json).
- Evaluated 15 production selection units, 650/650 uniquely owned aliases. Per-alias unit and page mapping is in [alias-unit-page-map.json](../../artifacts/qualification/pdf072-clean-multimodal-luna-20261026/attempt-02/alias-unit-page-map.json).
- Captured 22 final request bodies and 22 bound response files: 15 SELECT, 1 GROUP, 6 PLACE. The exact request JSONs are under `artifacts/qualification/pdf072-clean-multimodal-luna-20261026/attempt-02/luna-requests/`; responses are under `.../luna-responses/`.
- SELECT body sizes were 5,773,523–10,261,070 bytes (mean 7,901,811; total 118,527,168). They carried 122 inline PNG image blocks across all requests. Raw PNG bytes represented by those repeated blocks total 80,844,565 bytes. Each image block used `data:image/png;base64,...`; [wire-integrity-audit.json](../../artifacts/qualification/pdf072-clean-multimodal-luna-20261026/attempt-02/wire-integrity-audit.json) verifies each block against the rendered page SHA-256 and byte length.
- SHA audit passed: each final request file hash matches its request filename; each response `requestSha256` binds to that final request hash; the production text-only request hashes reported by the route match the original-request hashes recorded at the adapter boundary. The final multimodal hash is distinct from that original text-only hash.
- GROUP and PLACE packets were captured with text blocks only. Every captured request contained only a system and user message; no request inherited conversational history from another unit.

## Model identity and timing

The request envelope selected `qwen/qwen3.7-flash`, provider `alibaba`, with route marker `LOCAL_LUNA_IN_SESSION_ONLY`. This was a local bridge-only run: no external HTTP send occurred, and locally authored response bodies supplied the model replies. The timings in [bridge-calls.json](../../artifacts/qualification/pdf072-clean-multimodal-luna-20261026/attempt-02/bridge-calls.json) include time waiting for those files and manual evaluation. They are not model generation latency. Per-request timestamps, packet sizes, image page references, and response bindings are retained in the wire audit.

## Freeze and comparison limits

The pre-Gold run is frozen at [pre-gold-freeze.json](../../artifacts/qualification/pdf072-clean-multimodal-luna-20261026/attempt-02/pre-gold-freeze.json); frozen decision SHA-256 is recorded in [run-manifest.json](../../artifacts/qualification/pdf072-clean-multimodal-luna-20261026/attempt-02/run-manifest.json). The post-freeze, separate DOC-0252 result is [post-freeze-doc-0252-score.json](../../artifacts/qualification/pdf072-clean-multimodal-luna-20261026/attempt-02/post-freeze-doc-0252-score.json).

This establishes that the test-only adapter put real image bytes inside every production-style SELECT HTTP body. It does not establish that OpenRouter accepted those bodies, that Qwen/Luna processed them, or that production defaults should change. No production source/default was modified; only the qualification harness was extended.

### Artifact note

The files directly under `artifacts/qualification/pdf072-clean-multimodal-luna-20261026/` (outside `attempt-02/`) are an initial adapter bring-up diagnostic. That attempt failed before producing any captured HTTP request body because the adapter initially assumed the user content block was a plain string. It is not included in the pre-Gold freeze or score. `attempt-02/` is the only completed and scored run.
