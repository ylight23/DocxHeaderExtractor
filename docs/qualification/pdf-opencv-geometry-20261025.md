# PDF text OpenCV geometry experiment — 2026-10-25

## Implementation and scope

Qualification implementation: `tools/qualification/TargetRepresentationTrial/PdfRasterGeometry.cs`, invoked by `geometry` (prepare only) or `geometry-live` (one page-14 pair). Production defaults and PDF/DOCX/scan source code are unchanged.

PDF parser text, aliases, ownership and selection response contract are retained. The experiment reuses the scan PDF renderer (200 DPI) and `ScanFrames.Find`. OpenCV thresholds the page using Otsu, measures enclosing drawn frames, horizontal blank bands, ink coverage and horizontally separated ink runs inside each source alias box. Vertical-overlap peers and left-alignment peers are computed from parser boxes transformed into raster coordinates; they are explicitly distinguished from OpenCV image measurements. No OCR, title vocabulary, document-specific alias rule, Gold label or semantic table/heading label enters this preprocessing.

This first increment does **not** implement a complete table/paragraph region recognizer. It exports local peer relations and raster measurements for testing their usefulness. A full table-region claim would exceed what was implemented and measured.

## Mapping verification

Unrotated pages with an origin-zero CropBox matching page dimensions are supported. Other transforms fail explicitly rather than emitting guessed coordinates. Conversion uses `x=pdfX*widthPx/widthPt`, `y=(heightPt-pdfY)*heightPx/heightPt`. Out-of-bounds or duplicate alias boxes are refused. This is a qualification limitation, not support for arbitrary production PDFs.

Synthetic checks verify scaling, y inversion, same-row overlap, left alignment, separate image ink runs within a single alias, and rejection of out-of-bounds boxes. The tool builds without warnings or errors. Page-14 overlay was visually inspected: parser rectangles align with the rendered text. All 33 aliases map to boxes containing image ink. Ink presence is a sanity check; it alone is not proof of exact character alignment.

The page has no detected drawn frame and 23 exported blank horizontal bands. The two merged source aliases `L0550:S0` and `L0554:S0` each have two widely separated raster ink runs. Their original aliases and text are not split or rewritten. This observation is measured uniformly for all aliases and was not used to force a membership result.

## Controlled pair

Artifacts: `artifacts/qualification/pdf-opencv-geometry-20261025/02/`. `01/` is the earlier prepare-only inspection before adding intra-alias ink runs.

Both arms use flat alias targets, the common generic structural instruction and source-bound system prompt, the same context facts, extended block evidence, styles, tools, completion budget and parser. Both receive the same optional-field description in the system prompt. Only the treatment receives `pageGeometryEvidence`. Complete wire parity is asserted after removing that one field. Thirty packets are checked across 15 pages; image evidence is generated for page 14 only. Other pages are packet checks, not image-preprocessed pages.

| Page 14 | Control | Geometry |
|---|---:|---:|
| Owned aliases | 33 | 33 |
| Request bytes | 111,476 | 124,179 |
| Input token estimate | 100,017 | 111,644 |
| Completion cap | 32,768 | 32,768 |
| Admission with continuation reserve | PASS | PASS |
| TP | 4 | 4 |
| FP | 0 | 1 |
| FN | 0 | 0 |
| Posts | 1 | 1 |
| Model conversation seconds | 53.904 | 48.422 |

Conversation durations exclude raster preprocessing and must not be reported as document extraction latency. The existing renderer renders pages up to page 14 to obtain the requested page; this is suitable for this isolated experiment but not an optimized single-page preprocessing implementation.

The geometry arm's false positive is `L0550:S0`. Its visible response reason treats the occurrence as a section heading introducing a topic block with following papers, citing boldness and left alignment. That is a semantic interpretation that disagrees with registered Gold even though its text/alias source binding is valid. No parser repair, semantic veto or hand-edited output was applied.

## Result

**No demonstrated accuracy improvement; do not promote.** Geometry preprocessing and request integration work on this page, but one controlled pair does not establish stability, a causal regression, or usefulness across other pages/documents. The measured raster relations did not eliminate the outstanding semantic ambiguity in this sample. More geometry or a table-region implementation cannot be claimed as a proven fix from this result. Registered Gold was read only by the offline scorer after model execution.

Reproduce preparation with `dotnet run --project tools/qualification/TargetRepresentationTrial -- <pdf> <new-output-directory> geometry`; use `geometry-live` for the bounded pair. Score using `python tools/qualification/audit_target_representation.py <output-directory>`. The audit retains SHA identities for actual first requests and historical reconstructions.
