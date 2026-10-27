# PDF request optimization (geometry, image, completion cap, concurrency)

**Date:** 2026-10-26
**Status:** frozen as the native PDF default by the owner's explicit decision on 2026-10-26. This is a configuration freeze, not a claim that Qwen/OpenRouter quality or a five-minute SLA has been qualified.

## What was built

| Item | Option (PipelineOptions) | Default | Selector id suffix |
|---|---|---|---|
| 1. Column encoding of raster geometry (lossless; same bbox, peers, frames, gaps) | `PdfCompactGeometry` | on | `:geometry-columns-v1` |
| 2. Smaller grayscale page image | `PdfVisualMaxWidth` / `PdfVisualQuality` / `PdfVisualGrayscale` | 960 / 50 / on | `:visual-w960-q50:visual-gray-v1` |
| 3. Completion cap sized by unit count; a truncated reply is re-sent with a larger cap (at most twice, up to `MaxCompletionTokens`); never partial decisions | `PdfDynamicCompletionBudget` (V14 only) | on | `:unit-completion-adaptive-v1` |
| 4. SELECT concurrency | `PdfJobs` (plus the process-wide `ProviderRequestGate`) | 8 | — |

Instruction, response protocol (`PDF_UNIT_SELECT_SOURCE_BOUND_V2`) and DOCX/scan selectors are unchanged
(`PdfRequestOptimizationTests.Application_tuning_is_pdf_only_and_versioned_with_unchanged_response_and_instruction`).

## Offline measurement (0 provider calls, Gold not read)

| | 072 baseline | 072 all three | 095 baseline | 095 all three |
|---|---:|---:|---:|---:|
| SELECT requests | 8 | 8 | 29 | 29 |
| Total bytes | 4,720,444 | 3,656,483 (−22.5%) | 15,829,356 | 12,314,111 (−22.2%) |
| Image bytes | 3,524,049 | 2,695,117 (−23.5%) | 10,698,019 | 8,318,447 (−22.2%) |
| Geometry bytes | 416,919 | 204,132 (−51%) | 2,089,809 | 1,050,502 (−50%) |
| Completion cap (mean) | 32,768 | 19,968 | 32,768 | 19,174 |

Source: `artifacts/qualification/pdf-request-optimization-{072,095}-20261026/summary.json`. The geometry round-trip
audit (`evidence-roundtrip-audit.json`) and `Column_geometry_roundtrips_every_measurement_null_peer_and_unavailable_page`
show the column encoding decodes to the original evidence.

## Live attempts (bridge responder, not the provider)

The `pdf-optimized-*-luna-*` runs used the local response bridge, not OpenRouter. None completed:
072 c8 and c6-recovery stopped at `MODEL_CALL_FAILED` because a bridge-authored reply was malformed JSON
(raw newline in a string); 072 complete and 095 c8 hit the lane deadline while waiting for bridge replies.
These are responder failures, not pipeline failures, and give no quality or latency evidence.

## Haiku bridge runs (2026-10-26)

Same native route (`PdfHandler.ExtractAsync`, V14 units, adaptive visual, concurrency 6) through the SHA-bound bridge.
Every request was answered by a **new Claude Haiku agent that saw only that request** (system text, packet, page images
and earlier turns of the same conversation), with no session history, no Gold and no other requests. Optional tools were
not offered to the responder. The finalize step checked only that the reply was one JSON object; protocol checks stayed
with the pipeline. No request needed a protocol correction turn. Gold was read only after each run finished.

| Run | Requests | Request bytes | Alias TP / FP / FN | Precision / recall | Exact Gold groups |
|---|---:|---:|---:|---:|---:|
| 072 baseline (1200/Q58, plain geometry, cap 32768) | 12 | 4,921,423 | 45 / 17 / 0 | 72.6% / 100% | 41 / 42 |
| 072 optimized (960/Q50, column geometry, unit cap) | 13 | 3,862,957 (−21.5%) | 45 / 25 / 0 | 64.3% / 100% | 42 / 42 |
| 095 optimized | 35 | 12,637,463 | 104 / 1 / 5 | 99.0% / 95.4% | 97 / 103 |

- 072: every false positive in both arms is an item of the annex agenda table (session titles with times or presenters).
  The optimized arm added 8 more of them (`L0522`–`L0532`); the baseline arm split the two-line title. With one sample
  per arm this is within responder variance and is not evidence either way about the smaller image.
- 095: the one FP is "RFC 9114" grouped with "HTTP/3"; the misses are five index letter labels (C, D, G, R, S).
  For reference, the earlier Luna run with full-size requests scored 97 / 0 / 12 and 91 / 103 exact groups (different responder).
- Wall times (393 s, 397 s, 822 s) are dominated by agent dispatch and say nothing about provider latency.
- Dynamic caps were applied (16,384–22,528 for SELECT); no reply was truncated, so the retry path was not exercised live.

Artifacts: `artifacts/qualification/pdf-{optimized,baseline}-072-haiku-20261026/`, `pdf-optimized-095-haiku-20261026/`
(requests, responses, `run-identity.json`, `native-run-result.json`, `gold-score.json`).

## V12 vs V14 cohort, Haiku bridge (2026-10-26)

Production configurations through the runner modes `production-v12-bridge` (V12 segments, no page images) and
`production-v14-bridge` (V14 units, V12 instruction, page images, default placement batch). One new Haiku agent per
request, as above. DOC-0133 (048) is not in the cohort: it routes to the scan lane.

| Source | V12 TP / FP / FN | V12 exact groups | V14 TP / FP / FN | V14 exact groups |
|---|---:|---:|---:|---:|
| 072 (DOC-0252) | 45 / 2 / 0 | 42 / 42 | 45 / 17 / 0 | 41 / 42 |
| 076 (DOC-0256) | 26 / 0 / 9 | 25 / 34 | 35 / 24 / 0 | 34 / 34 |
| 089 (SRC-089) | 50 / 0 / 7 | 31 / 36 | 57 / 0 / 0 | 36 / 36 |
| **Pooled** | **121 / 2 / 16** (P 98.4%, R 88.3%) | **98 / 112** | **137 / 41 / 0** (P 77.0%, R 100%) | **111 / 112** |

- V14 finds every Gold heading in all three files; V12 misses the organisation names of 076 and the title and numbered
  items of 089.
- Every V14 false positive is an item of a meeting agenda table (072 annex, 076 programme). Codex's Luna runs of the same
  V14 route had no such false positives on 072 and 076, so this is at least partly responder behaviour; it needs the Qwen
  check before any prompt change.

## ShapeReview (task 3), Haiku replay on 095

`ShapeReview` (HeadingAgent/Grouping, option `PdfShapeReview`, default off) groups lines with one measured look (font,
size, weight, slant, left edge, alone in its block) whose decisions disagree across requests, at most 24 members per
group and 8 groups per document, and asks the model once per group; the answer applies only when it names every member.
The 095 optimized run was replayed byte for byte with it on: one group was asked (the eight index letters plus three
body-line tails), and the model rejected all eight letters.

| 095 | Alias TP / FP / FN | Exact groups |
|---|---:|---:|
| Without ShapeReview | 104 / 1 / 5 | 97 / 103 |
| With ShapeReview | 101 / 1 / 8 | 94 / 103 |

The step does what it is for: the decisions are now consistent. But it settled on the side Gold disagrees with (Gold
counts the index letters as headings; the earlier Luna run also rejected all of them), so on this file it lowers recall.
It stays off. Whether index letters are headings is a Gold policy question for the owner, not something to tune the
prompt toward.

## Qualification caveat retained after the configuration freeze

The owner explicitly froze this profile despite the open real-provider A/B gate. A future Qwen/OpenRouter run must be
reported as validation evidence, not retroactively implied by this freeze. In particular, the Haiku result with 8 added
agenda false positives and the local-Luna wall time are retained as evidence; neither is treated as Qwen quality or SLA.

## Page-image margin crop (A), offline audit

`PdfVisualCropMargins` (default off; the frozen profile is unchanged) keeps, per page, the band from the first to the last inked row
or parser line box, plus a 24-pixel bleed at 120 DPI, at full page width. The SELECT packet gains `pageImages` with each band in
full-page PDF coordinates (`topPt`, `bottomPt`, `pageHeightPt`). A page whose line boxes fall outside the rendered page is sent
whole. Audit: `artifacts/qualification/pdf-visual-crop-audit-20261026/{072,089}/visual-crop-audit.json` (no provider call, Gold not read).

| | 072 | 089 |
|---|---:|---:|
| Pages verified / cropped | 15 / 15 | 18 / 18 |
| Line boxes outside the kept band | 0 | 0 |
| Pages with ink outside the kept band | 0 | 0 |
| Mean kept height | 89.1% | 82.8% |
| Image JPEG bytes | 1,959,613 → 1,945,886 (−0.7%) | 1,841,910 → 1,804,256 (−2.0%) |
| SELECT request bytes | 3,574,551 → 3,559,366 (−0.4%) | 3,268,506 → 3,221,873 (−1.4%) |

Blank margins cost almost nothing in JPEG, so the byte gain is negligible. The only possible gain is in image tokens, which
follow pixel count (11–17% fewer pixels here). The live Qwen arm (`production-live` mode, 072 and 089, plain vs crop) could
not run: every request returned HTTP 402 PAYMENT_REQUIRED from OpenRouter, so no tokens, latency or quality were measured.
Not promoted.

### Crop A/B, Haiku bridge (frozen production profile, `production-bridge` mode)

One new Haiku agent per request, no session history. Same requests in both arms except the page images and `pageImages`.

| | Request bytes (SELECT) | Alias TP / FP / FN | Exact Gold groups |
|---|---:|---:|---:|
| 072 plain | 3,574,551 | 45 / 25 / 0 | 41 / 42 |
| 072 crop | 3,559,366 (−0.4%) | 45 / 23 / 0 | 41 / 42 |
| 089 plain | 3,268,506 | 55 / 1 / 2 | 34 / 36 |
| 089 crop | 3,221,873 (−1.4%) | 57 / 0 / 0 | 36 / 36 |

Cropping did not lower quality on either file (one sample per arm; the differences are within the responder's run-to-run
variation). The 072 false positives are again agenda-table items in both arms. The byte saving stays negligible; the
token saving needs the provider. Not promoted.

**Production defect found during this run.** The frozen profile sets `PdfJobs = 8`, but the process-wide provider gate
(`ProviderRequestGate`) admits 6 requests per account unless `DHX_OPENROUTER_MAX_CONCURRENCY` is set. Requests 7 and 8
wait at the gate, and that wait is charged to the 30-second `ProductionRetryWaitBudget`
(`AgentModel.Turn`, `requestGate?.Enter(retryWaitBudget - waited, …)`). When the first six replies take longer than 30
seconds, the waiting requests fail with `PROVIDER_WAIT_LIMIT` and the PDF lane fails the whole document
(`RequireCompleteRun`). All four first attempts here failed this way on PDFs with 8–9 SELECT requests; the reruns used
`DHX_OPENROUTER_MAX_CONCURRENCY=8` for this experiment only and replayed every request already answered.

### Visual cache and provider gate (2026-10-26)

- **Visual cache.** Measured geometry and rendered page images (including crops) are stored as MessagePack under the source
  cache (`PdfVisualCache`), keyed by file bytes, path, build, render options and a fingerprint of every parser line the
  measurements read. A damaged or mismatched entry is rebuilt. Timings with the frozen render options (960/Q50, grayscale,
  crop) and no provider calls: 072 no cache 16.0 s, cold 12.0 s, warm 1.1 s; 095 no cache 45.3 s, cold 55.8 s, warm 5.2 s.
  The cold run pays the write; that cost is one-time per file and options.
- **Provider gate.** `ProviderRequestGate` default raised from 6 to 8 (`DHX_OPENROUTER_MAX_CONCURRENCY` still overrides).
  The frozen profile's `PdfJobs = 8` now runs without queueing, so no request waits at the gate for the 30-second budget.
  **Side effect:** the gate is per account, so the scan lane's requests are also limited to 8 instead of 6. Whether the
  provider accepts 8 concurrent requests is not yet measured with Qwen (no credit).
- Test note: `Transport_gate_admits_configured_count…` is timing-sensitive; it failed once in a full run and passed on the next
  run and alone. Not changed.

### 1-bit page images (`PdfVisualBilevel`), Haiku bridge

Pages rendered at 960 px, thresholded at mid-grey (140) to black and white, sent as PNG. Same requests and profile as the
grey baseline except the image. One new Haiku agent per request; Gold read after each run finished.

| | Image base64 bytes | Alias TP / FP / FN | Exact Gold groups |
|---|---:|---:|---:|
| 072 grey (plain) | 2,613,185 | 45 / 25 / 0 | 41 / 42 |
| 072 1-bit | 777,233 (−70%) | 45 / 0 / 0 | **42 / 42** |
| 089 grey (plain) | 2,456,318 | 55 / 1 / 2 | 34 / 36 |
| 089 1-bit | 766,342 (−69%) | 55 / 1 / 2 | 34 / 36 |

- One sample per arm. On 072 the 1-bit run removed all 25 agenda false positives; on 089 the results are identical to grey.
  Not yet evidence of a quality gain: repeat before drawing a conclusion.
- Visual tokens were **not** measured (bridge reports 0 cached and no provider usage). Pixel count is unchanged, so
  1-bit is expected to reduce wire bytes only, not image tokens.
- Defect found and fixed during this trial: the image data URL was hard-coded as `image/jpeg` even when the bytes were
  PNG. Both 1-bit runs above were sent with that mislabel; the label now follows the real format. Test suite 593/593.
- Shading is lost in 1-bit: grey table-header bands turn white (text kept, band removed).
