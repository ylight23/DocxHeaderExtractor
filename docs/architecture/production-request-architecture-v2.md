# Production request architecture V2 — single PDF lane (2026-09-28)

Supersedes the packing row and the "production" framing of `production-request-architecture-v1.md`:
V1's evidence was measured on the A99 V4 lane, which ordinary PDF traffic never reached. V2 makes
that lane the only PDF lane and deletes the old one outright — no profile, no fallback.

## The PDF lane

| concern | setting | where |
|---|---|---|
| source authority | structured segment atoms (`L{row}:S{segment}`), facts `PDF_SOURCE_FACTS_V3` | `PdfStructuredSourceAuthorityBuilder` |
| line / block grouping | visual line + corridor segmentation; continuation by the document's own line pitch | `PdfLineExtraction`, `PdfSemanticBlockGrouper` |
| request | `V4_SEMANTIC_FUNCTION_SINGLE_AUTHORITY` | `CanonicalSemanticPdfAuthorityAdapter.Request` |
| contract | `PdfSemanticFunctionMembershipV1` (validate, decode, canonicalize, bind) | `SemanticCoordinateContract` |
| packing | P05 resource-bounded: 90,000 bytes, 28,000 est. tokens, 96 owned, 128 visible, 8 halo. Sliding window evaluated offline and not adopted (`sliding-window-offline-calibration.v1.md`) | `SemanticEvidencePackingPolicies.PdfResourceBoundedP05` |
| owned evidence style | raw measurements and ratios only; no bold/size judgement, neutral attention | `PdfSourceEvidence` |
| placement | one bounded placement pass (hierarchy source for V4 claims) | `CanonicalSemanticPlacementCoordinator` |
| reasoning / streaming / retry | none / on / bounded transient | `RemoteInferenceOptions` |
| edge-whitespace quotes | tolerant but exact (CASE B2) | `SemanticSourcePartCanonicalizer` |

The DOCX lane is a different format, not a fallback: `DocxAliasSpan`, `V2_ATTENTION_FREE`,
`FIXED_OWNED_COUNT_120`. Packing has no shared default; each lane names its policy. Its document
regime is now text-inferred by `DocumentDomainPolicy.InferRegime`, the same as the PDF lane, instead
of coming from `DocumentModeClassifier`. That regime is observation-only and never model-visible.
TOC flags are no longer source facts, so TOC lines reach the model as ordinary body text
(`DOC-0123.structural-audit.v1.json` was regenerated accordingly).

`IHeaderClassifier` has one model call, `BoundaryCutAsync`. The chunk classify/critique/hierarchy
path, together with its prompt, grammar and prefix-cache machinery, is gone from every provider.

## Deleted

Legacy occurrence universe (`PdfCanonicalSourceUniverse*`), `PdfSemanticAuthorityProfile`, request
versions V1 (attention legacy) and V3 (exclusion clause) with `HistoricalContracts`, PDF contracts
`PdfAliasSelection` / `PdfStructuredSourceParts` / `PdfStructuredSourcePartsV2` /
`PdfSemanticMembershipV1` and the Stage-1 membership seam, coherent-region packing, source facts V1/V2,
midpoint line grouping and the 22pt block ceiling, the builder's dead fixed-120 `Packs`/`CallPlanHash`,
and the historical experiment tests that could only run on those. Also deleted: `DocumentModeClassifier`
with the outline's `documentMode` field, the Web `/api/inspect` endpoint and its "Kiểm tra mode" button,
`HeaderPrompt`, `PrefixCachedRunner`, `ChunkResult`/`HierarchyItem`, and the local options
`GrammarMode`, `EnableThinking`, `ReusePromptPrefix`, `Temperature` and `MissingIdRetries` with their
CLI flags. Frozen eval artifacts stay as data.

Second pass:
- **DOCX lane:** reads `SourceDocument` directly. The Policy layer (`DocxPolicyState`, `IPolicyParagraph`),
  `DocumentFeatureDeriver`, `NumberingStyleFeatures`, `StyleTrustAudit` (with `--style-trust`) and
  `HeadingHeuristics` are gone. The only facts the lane used from them, the built-in heading style and
  the numbering-style level, are read straight from `SourceParagraph`.
- **Output chunking:** the chunk budget now only sizes output chunks and is no longer rewritten per backend
  or model. The local model's context comes from the GGUF alone.
- **Calibration:** calibration and critic leftovers are removed from the outline and from the Web and MCP
  output. These are `decisionAudit`, `AutoAcceptedCalibrated`, `modelConfirmed`, `criticConfirmed`,
  `acceptanceSignature`, `calibrationSamples` and `evidence`.
- **Web:** the model-assisted `.key`/training export and the paragraph-index answer-key panel are removed.
  Neither meets the Gold rules.
- **Test-only code:** the `pdf_hierarchy_facts` row artifact, `ProviderSemanticExecutionFingerprint`,
  `DocumentReviewResultMapper`/`ValidatedHeading` and the unused vNext projection and cache-key helpers
  are removed.

Neutral source facts (model-visible payload changed; the frozen request bytes were re-frozen):
- **PDF location:** `pageBand` (TOP/BOTTOM/BODY at 8%/92%) is replaced by `verticalPosition`, the raw
  position within the document's text extent (0 is the lowest text, 1 the highest). The
  `Repeated`/`HeaderFooterZone`/`PageNumber`/`TableLike` line classifications are gone, and layout
  blocks, which are only a label beside per-line atoms, are grouped by geometry alone.
- **Markers:** `markers` (the harness's parse of the numbering prefix) is no longer sent. The model reads
  the symbol in the text itself. DOCX `numbering` (Word's generated label, absent from the run text) stays.
  The parser is used only by the internal hierarchy-facts audit.
- **No-model mode:** with no model (`--no-llm`), the DOCX lane no longer declares headings from style,
  outline level or numbering. Like the PDF lane, it proposes nothing.

`NumberingAudit` is deleted. Its post-model consistency audit was never called; its strict numbering parser
moved unchanged into `PdfMarkerFactsParser`. `PdfOutputDecisionPolicy` is renamed `PdfOutputDecisions` and keeps
only the source-validity checks, without the excluded-scope list. The visual, adjudication and
global-reopen model hooks in the production entry point are kept as reserved control-plane infrastructure.

## Defects the switch exposed and fixed

- The PDF production input bound V4 claims with the v1 structured contract's binder.
- The PDF experiment gate compared manifests against the legacy DOCX-shaped schema hash.
- A manifest without a packing declaration was read as fixed-120; it is now refused.

## Production transport (OpenRouter streaming transport V1)

Production now sends what the re-baseline qualified, and the audit gaps are closed:
- **Streaming:** `stream=true` and `usage.include=true`. The reply is read with `ResponseHeadersRead`
  and parsed from raw SSE bytes. Transport counts as complete only with a terminal `finish_reason`,
  `[DONE]` and a clean EOF.
- **Deadlines:** a 300 s transport-only deadline per attempt (`ProviderTransportTimeoutSeconds`). The
  content is handed to the contract after the deadline, and `HttpClient.Timeout` no longer cuts streams.
- **Retries:** `TransientRequestRetries` (default 2) now applies to 429, 502/503/504, network errors,
  timeouts, incomplete streams and mid-stream provider errors, honouring `Retry-After`. A completed
  stream is never resent, and content failures are never retried as transport.
- **Route:** `OpenRouterProviderRoute` is consumed, pinning `order=[route]` with `allow_fallbacks=false`.
  The default model is the qualified `qwen/qwen3.7-flash`, pinned to `Alibaba`. `OPENROUTER_PROVIDER_ROUTE`
  overrides the route, and a custom model gets no implicit pin.
- **Lane deadline:** the PDF lane deadline was 5 min for a whole document, which would have failed
  SRC-095 (about 327 s of provider time). It is now a 60 min runaway guard, and the unused per-request
  and batch lane timeouts are gone.
- **Verification:** all 31 recorded re-baseline streams, replayed byte for byte through the production
  transport, reassemble to exactly the accepted content (`OpenRouterStreamingReplayTests`).

## Open

- **Re-baseline done (2026-09-29).** `eval/a99-closed-loop/production-rebaseline-v1`: 31/31 leaves
  contract-valid on first attempt; production output F1 0.754 (P 0.678, R 0.849) on SRC-089 + SRC-095.
  A new baseline, not comparable causally with T3B.
- Placement is on in production but was off in every V4 measurement.
- Ownership is by a claim's first part (`InferAsync`): a claim that continues into the right halo
  belongs to the leaf owning its start, and the neighbour sees that start in its left halo and is
  refused - no loss, no duplicate. The T4/T5 offline scorer required every part owned, so it was
  stricter than production for boundary-crossing claims; the re-baseline scores the production output.
- Replay bundles still record the legacy contract hash for every lane.
