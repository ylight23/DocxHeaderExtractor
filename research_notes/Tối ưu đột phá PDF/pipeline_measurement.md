# Tối ưu pipeline multimodal document extraction

## Which optimizations can plausibly produce a 2x effect for frozen V14/V12/V2?

### Takeaway

A measured 2x is plausible only when an intervention removes or overlaps a dominant component of the current critical path: high-detail vision input, serial per-page calls, or locally hosted autoregressive decoding. Output-schema trimming and prompt tidying are normally percentage-level unless output decoding is demonstrably at least half of end-to-end time. No source supports claiming a universal 2x for cropping, tiling, or JSON Schema; those require a paired experiment on the frozen corpus.

### Cited Findings

- OpenAI documents image-token accounting that can make resolution/tiling a first-order cost lever: at high detail its tile-accounted models charge a base plus one charge per 512px tile; GPT-4o/4.1 use 85 base tokens plus 170 per tile, while `detail: low` costs only the base irrespective of image dimensions. The same guide gives different accounting for newer models (patch budget rather than the legacy tile table), so a token calculator must be model-specific. — [OpenAI Images and vision](https://developers.openai.com/api/docs/guides/images-vision?api-mode=responses)
- Gemini’s PDF documentation says each PDF page is equivalent to 258 tokens in the documented mode, pages are rendered within bounded resolutions, and smaller page sizes do not reduce cost except bandwidth; its newer media-resolution guide explicitly recommends `medium` for PDFs and says higher resolution rarely improves OCR for standard documents. Thus an optimization that merely downsizes PDFs can have zero token/cost effect on this provider. — [Gemini document understanding](https://ai.google.dev/gemini-api/docs/document-processing), [Gemini media resolution](https://ai.google.dev/gemini-api/docs/media-resolution)
- Anthropic’s vision guide says image tokens are approximately pixels/750 for images that are not resized, and recommends no more than 1.15 megapixels / 1568px per dimension to avoid server-side resizing and first-token latency without extra model performance. It also warns that tiny or unclear text harms performance. — [Anthropic vision guide](https://docs.anthropic.com/zh-CN/docs/build-with-claude/vision)
- A document-parsing model report describes a coarse-to-fine strategy: global layout first, then native-resolution crops of the original image for detailed content. This is evidence that targeted crops can be effective, not evidence that arbitrary grid tiling preserves a generic extractor’s correctness. — [MinerU2.5 report](https://yuhangzang.github.io/papers/arxiv-2509.22186.html)
- Anthropic reports a consistent uplift in image evaluations when a model has a crop/zoom tool. The documentation does not quantify the uplift or establish a cost reduction, so it justifies a crop experiment rather than a performance claim. — [Anthropic prompting best practices](https://docs.anthropic.com/en/docs/build-with-claude/prompt-engineering/prompt-templates-and-variables)
- OpenAI recommends parallelizing calls only when stages are not strictly sequential; it also describes speculative execution for a likely classification outcome, followed by cancellation/retry if the prediction is wrong. — [OpenAI latency optimization](https://developers.openai.com/api/docs/guides/latency-optimization)
- Exact speculative sampling has published 2–2.5x decoding speedup on a 70B model while preserving the target distribution in the reported hardware setup. This is an inference-server optimization, not a reason to substitute a smaller model’s answer in an extraction pipeline. — [Chen et al., speculative sampling](https://arxiv.org/abs/2302.01318)
- Further controlled speculative-decoding reports show 2–3x acceleration with identical outputs in a T5 setup and 3.16x single-batch latency reduction in a GPT-2-L setup. These are architecture/workload-specific, so they establish plausibility only for self-hosted decoding, not guaranteed API speedups. — [Leviathan et al.](https://arxiv.org/abs/2211.17192), [Spector & Re](https://arxiv.org/abs/2308.04623)
- Amdahl-style analysis bounds parallel gains by the serial component and workload imbalance; therefore doubling workers cannot double total latency when rendering, merging, validation, or a serial final call dominates. — [NASA report on Amdahl’s law and imbalance](https://ntrs.nasa.gov/archive/nasa/casi.ntrs.nasa.gov/19890017081.pdf)

### Inferences

- **Plausible 2x candidates, conditional on measurements:**
  - **Parallel pages / independent extraction branches:** credible for throughput, and for document latency only if per-page inference currently runs serially, quota/concurrency is available, and the final merge is short. Measure both documents/hour and P50/P95 document latency; do not call throughput a latency win.
  - **Adaptive vision budget:** run an inexpensive global/layout pass and request native/high-resolution crops only for dense tables, small fonts, multi-column regions, or low-confidence pages. A >=2x token/cost reduction is plausible only if the baseline’s *billed input vision tokens* are cut by >=50% under the actual provider token rule. Retain the global page (or its layout evidence) so headers, reading order, and cross-region associations are not silently lost.
  - **Server-side speculative decoding:** plausible only if V14/V12/V2 own the inference serving stack and output-token decoding is a material share of latency. Verify byte/AST equivalence under fixed decoding settings before calling it lossless for the pipeline.
- **Likely percentage-only candidates:** shorter instructions, compact field names, removing prose around a JSON object, connection reuse, upload/file reuse, and lower image resolution on a provider with fixed per-page billing. They can be worthwhile operationally but are not 2x hypotheses until the stage breakdown says otherwise.
- **Seductive but invalid optimization: naïve tile everything.** Grid tiling can raise the number of image inputs/tokens, remove global layout and cross-tile table/reading-order evidence, and create duplicate boundary text. It is not an optimization until compared against the unmodified full page at identical extraction criteria.
- **Seductive but invalid optimization: always use a thumbnail/low detail.** It may reduce tokens enormously but can erase precisely the small text, footnotes, table rules, and hierarchy that heading extraction depends on. It is a routing option, not a replacement baseline.
- **Experiment A — adaptive render/crop, highest expected effect:** hold model, prompt, schema, document IDs, and outputs’ semantic scoring constant. Compare: (A0) current render; (A1) provider-supported medium/low page input; (A2) global page plus deterministic layout-selected high-res crops. Record provider-reported input/output tokens, wall time, request count, per-page failure and exact Gold score. Predefine the crop selector and thresholds before opening the blind score.
- **Experiment B — bounded page concurrency:** compare concurrency 1, 2, 4 (or the verified quota ceiling), keeping one request per page and the same merge algorithm. Report P50/P95 latency, throughput, 429/retry rate, token counts, and exact output equality/Gold score. Stop if throttling or ordering repairs change either runtime or output.
- **Experiment C — self-hosted speculative decoding only:** A/A exact-output test first; then measure output tok/s and document end-to-end time separately. Reject the experiment if it changes generated bytes at the chosen deterministic configuration or if input vision/prefill, rather than decoding, dominates.

### Gaps

- The frozen V14/V12/V2 stage timings, providers, model versions, page distribution, quotas, and existing renderer parameters were intentionally not inspected. Without them, no credible ranking can state that any candidate *will* deliver 2x; the classifications above are conditional hypotheses.
- No provider-neutral study found establishes a universal accuracy-preserving crop/tile policy for heading extraction. The necessary evidence is a blinded, paired result on the project’s own frozen documents.

## How should token accounting and output schemas be optimized without confusing syntactic validity with extraction correctness?

### Takeaway

Use provider-reported token usage and stage-level timings, not image byte size, prompt character count, or JSON validity as proxies. A minimal schema should remove unconsumed output and constrain transport shape, but preserve every field needed for the canonical scorer and provenance; a valid JSON object can still contain a semantically wrong extraction.

### Cited Findings

- Gemini documents that all input modalities are tokenized and provides an API token-count mechanism; its document documentation also says PDF image processing is reported under the `IMAGE` modality in current usage metadata. — [Gemini token counting](https://ai.google.dev/gemini-api/docs/tokens), [Gemini document understanding](https://ai.google.dev/gemini-api/docs/document-processing)
- OpenAI’s vision guide provides explicit model-dependent image-token formulas and advises that `auto` may select high-quality rendering on later models, increasing input usage. — [OpenAI Images and vision](https://developers.openai.com/api/docs/guides/images-vision?api-mode=responses)
- JSONSchemaBench was designed to evaluate constrained generation on efficiency, schema-feature coverage, and generated quality separately; it evaluates multiple constrained-decoding frameworks on 10K real-world schemas. That separation is direct evidence that format conformance is not an adequate quality metric. — [JSONSchemaBench paper](https://arxiv.org/abs/2501.10868)
- A study of 9,558 real-world schemas found constrained approaches can have coverage limitations; external validation plus feedback reached 95% coverage but added typically 1–4 seconds per schema. Its result is about structured-generation coverage/latency, not semantic document-extraction accuracy. — [Good-Enough Structured Generation](https://openreview.net/pdf?id=p84kZ3ZFux)
- A separate structured-output study reports that hard schema constraints can improve validity while lowering answer accuracy, producing more wrong-but-valid outputs in its small-model setting. This demonstrates the direction of risk, though its magnitude is not transferable to a different model or extraction task. — [Constraint Tax study](https://arxiv.org/abs/2605.26128)
- OpenAI’s Responses reference describes strict-schema adherence as an option and recommends `json_schema` for supporting models; this specifies an output-format mechanism, not truth verification. — [OpenAI Responses streaming reference](https://platform.openai.com/docs/api-reference/responses-streaming/response/mcp_call/failed?lang=node.js)

### Inferences

- Build a per-call ledger: document/page ID, renderer/crop coordinates, model snapshot, request start/end, provider input tokens by modality, output/reasoning tokens if exposed, HTTP/retry data, response bytes, validation result, and downstream Gold score. Aggregate by page class (clean digital, scan, small type, table, multi-column, rotated) rather than only global means.
- **Minimal safe schema:** use short stable keys, enums for finite labels, and only the canonical extraction fields plus evidence/provenance needed to score/review them. Request no explanations or duplicated text. Keep all canonical fields required; if a rich diagnostic representation is valuable, generate it in a separate non-scored audit run rather than redefining the scored artifact.
- **Do not count these as correctness:** JSON parse success, schema validation, non-empty output, matching number of headings, or an LLM-as-judge score alone. They are guardrails/telemetry. Correctness must remain comparison to Gold (and, when relevant, page/region evidence) under the existing blinded adjudication rules.
- **Schema-minimization experiment:** compare only a semantically equivalent “transport schema” against the current schema. Freeze prompt/model/images and map both to the same canonical record before scoring. Gate on exact/semantic Gold parity plus parse/repair rate; measure output-token and latency savings. Treat it as 2x-plausible only if output generation is >=50% of baseline elapsed time and tokens are reduced comparably.
- **Invalid shortcut:** deleting hierarchical, page, or evidence fields because a dashboard does not presently display them. That changes the task and may make a heading list look correct while losing level/parent/page correctness. It is not a neutral compression.
- **Invalid shortcut:** replacing Gold with “strict structured output.” Strict decoding addresses syntax; it cannot establish that the chosen heading, level, or omission is true.

### Gaps

- The providers/models behind V14/V12/V2 and whether their APIs expose modality-level usage are unknown. Where a provider omits it, controlled request-level A/B plus measured wall time is necessary; estimates must be labelled estimates.
- There is no task-specific evidence here quantifying how much V14/V12/V2 output schema contributes to their latency. That must be profiled before schema work is prioritized.

## How can effects be isolated while preserving Gold and blindness?

### Takeaway

Use a locked, paired, one-factor-at-a-time protocol: each fixed document is processed by baseline and one variant; the evaluator/scorer receives canonicalized outputs and Gold but the experiment runner/optimizer does not. Optimize on a disjoint development set, perform a single locked blind holdout readout, and report accuracy together with tokens, cost proxy, latency, failures, and uncertainty.

### Cited Findings

- The DUDE benchmark was designed for multi-page document understanding, visual layout/reasoning, and generalization across document types/domains; it highlights the need to evaluate more than plain text extraction for document understanding. — [DUDE paper](https://openaccess.thecvf.com/content/ICCV2023/papers/Van_Landeghem_Document_Understanding_Dataset_and_Evaluation_DUDE_ICCV_2023_paper.pdf)
- The DUE benchmark covers visual QA, key information extraction, and machine reading comprehension over layouts that include tables, graphs, lists, and infographics, reflecting that document systems must preserve text-positioning, formatting, and graphical information. — [DUE benchmark paper](https://datasets-benchmarks-proceedings.neurips.cc/paper/2021/file/069059b7ef840f0c74a814ec9237b6ec-Paper-round2.pdf)
- OmniDocBench provides end-to-end and task/attribute-level evaluation over diverse document sources and layout categories. This supports stratified diagnostics rather than a single aggregate number. — [OmniDocBench overview](https://ieeexplore.ieee.org/document/11091844/)
- A controlled study of duplicate train-test leakage reports that duplicate samples can inflate evaluation because a flexible classifier can echo the previously seen decision rather than generalize. Though it is not a document-extraction paper, it supports keeping tuning examples and final evaluation examples separated. — [duplicate-leakage study](https://jepcs.org/index.php/jepcs/article/view/61?articlesBySimilarityPage=2)
- Amdahl-style analysis recognizes serial sections and load imbalance, so measuring only mean per-page time is insufficient for a parallel document pipeline’s end-to-end latency. — [NASA report on Amdahl’s law and imbalance](https://ntrs.nasa.gov/archive/nasa/casi.ntrs.nasa.gov/19890017081.pdf)

### Inferences

- **Blindness-preserving harness:** lock Gold files and scorer behind an evaluation boundary. The variant generator receives only document inputs and a public contract; it writes outputs keyed by immutable document IDs. A separate evaluator canonicalizes, validates, scores, and releases only aggregate/per-stratum metrics. Do not expose per-document blind errors, Gold diffs, or error examples during tuning.
- **One-factor paired design:** for each experiment, change one declared factor only (for example crop routing, concurrency, or schema). Use identical document IDs, renderer source unless rendering is the factor, model version, decoding settings, system prompt, retry policy, and canonicalizer. Pairing sharply distinguishes a variant effect from document difficulty.
- **Two datasets / two gates:** use a non-blind development set to choose thresholds and repair logic; then freeze code/config/hash and run the untouched blind set once. If a blind readout causes another tweak, declare that set no longer blind and reserve another holdout.
- **Pre-register success/failure:** e.g., “accept only if Gold metric is non-inferior within a stated tolerance in every safety-critical stratum, no new catastrophic omission class, and median input tokens or end-to-end P50 improves by >=X%; report P95 and cost too.” Choose X and non-inferiority tolerance before the blind run, not after seeing it.
- **Report the whole causal chain:** input tokens/pages/crops -> request count/retries -> per-page and merge timings -> canonical outputs -> Gold metric. Include raw counts and paired deltas, not only averages. Separate throughput, P50 latency, P95 latency, provider cost, and accuracy; a variant may help one and harm another.
- **Seductive but invalid evaluation practices:** tuning crop thresholds on blinded failures; changing Gold or canonicalization while comparing variants; allowing an LLM judge to replace Gold; comparing different document mixes; comparing a faster run with weaker retry/validation; or tallying only successfully parsed outputs and dropping failures.
- **Decision order for current frozen versions:** (1) collect baseline stage ledger, (2) test page concurrency because it changes no perception semantics, (3) test provider-specific vision resolution/crop routing with global-context retention, (4) only then test output-schema compression, and (5) consider speculative decoding only if self-hosting and profiling show decode dominance. This order maximizes the chance of a material effect while keeping the Gold contract unchanged.

### Gaps

- The project’s exact Gold schema, blindness mechanism, scoring unit, and safety-critical strata were not inspected by assignment. The proposed harness therefore states invariants, not a replacement for the project’s existing protocol.
- No sample size or confidence interval can be responsibly prescribed without the number of documents, baseline variance, and the metric’s unit. The evaluator should compute uncertainty from the paired document-level deltas appropriate to its existing metric.
