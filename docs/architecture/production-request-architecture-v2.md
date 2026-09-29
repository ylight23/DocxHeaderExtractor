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

## Open

- **Re-baseline required.** Style facts changed shape after T3B, so production requests are no longer
  the T3B bytes and the T3B score (F1 0.7451) does not carry over.
  `pdf-v4-production-rebaseline-preflight.v1.json` freezes the plan; a provider run under it is needed
  before any claim about production quality or any sliding-window comparison.
- Placement is on in production but was off in every V4 measurement.
- Ownership is by a claim's first part (`InferAsync`): a claim that continues into the right halo
  belongs to the leaf owning its start, and the neighbour sees that start in its left halo and is
  refused - no loss, no duplicate. The T4/T5 offline scorer required every part owned, so it was
  stricter than production for boundary-crossing claims; the re-baseline scores the production output.
- Replay bundles still record the legacy contract hash for every lane.
