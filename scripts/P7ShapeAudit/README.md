# P7-D2.3 source-only shape audit

This is qualification tooling, not production semantics. It reads the immutable eight-document
source pack and original PDFs, not provider decisions, interpretations or existing Gold.
No provider transport is linked. Old source freeze artifacts and production requests stay unchanged.

## Two distinct levels

1. **Physical screening:** visit every PDF page; inventory native paths/orientations and frozen
   atom geometry. Rules select review candidates (parallel x regions, nearby bold lines,
   literal reference words, repeated margin strings). Rules do not label tables or headings.
   Path bounding rectangles only detect some long thin strokes; no matched stroke is not proof
   that a table has no borders. No cue is not evidence of absence or parser completeness.
2. **Visual source review:** actual PDFium PNGs, original PDF page links and all source IDs/bboxes
   for each observed case. Human/source-review notes are separate inputs, not model predictions.
   Geometry forms can be observed without adjudicating heading membership or exact extent.

All 647 pages are physically screened. The deterministic page sample has 43 renders;
19 of those pages were inspected visually in this checkpoint, across all eight documents.
That is not a complete visual review of 647 pages or semantic Gold.

The public receipts hold hashes, counts and document-stratified coverage. Private dossiers hold
the source text, native observations and PNGs. Every selected case references the pinned PDF,
page, complete page source IDs/bboxes and image hash. The case region is the whole page;
the audit does not fabricate precise semantic unit boundaries from geometry.

## Coverage interpretation

- Multi-column tables/front matter are not evidence of two-column **body** text.
- A partially ruled numeric table is not automatically a strictly borderless table.
- A centered structural-looking label inside a spanning table cell is a useful challenge candidate,
  not approved proof of document heading membership.
- `NOT_COVERED` means no confirmed example in this reviewed subset, not proven absence from the corpus.
- Shape counts are **reviewed examples**, not corpus prevalence. Document count is reported separately.
- No-table **document** coverage requires a whole-document check; a single table-free page is insufficient.
- Cross-page heading coverage requires inspecting both pages and adjudicating that the text is one unit.
- The known development reference remains exposed; source-only inputs in this pass do not make it blinded.

Remaining coverage gaps: cross-page heading, two-column body text, strictly unruled table,
no-table whole document, and semantic confirmation of the table-contained heading candidate.
Do not silently add or replace a PDF in the frozen selection to fill these gaps; use a new
versioned source-only cohort extension if source review shows it is needed.

## Evaluation scope is not the source universe

`P7EvaluationUniverse` is a qualification-only **gate**, not a semantic scorer or authority.
It currently supports explicit whole-page subsets, not arbitrary clipped regions. The subset
must be versioned and pinned to source/universe/snapshot identity before experiment execution.
No subset has been selected or approved in this checkpoint; empty draft scopes deliberately fail readiness.

Every source occurrence must be partitioned explicitly:

- `ADJUDICATED`: semantic function, heading membership and extent parts all recorded independently.
- `PENDING`: no scorable labels; outstanding disputes/evidence can live in a separate review record.
- `OUT_OF_EVALUATION_SCOPE`: not a semantic negative; no labels are converted to OTHER.

Readiness requires every in-scope occurrence to be adjudicated, including non-predicted regions.
If a Gold unit crosses the selected pages, expand and re-review the scope rather than truncating Gold.
Every prediction touching the evaluation scope is retained **whole**, whether its start lies inside
or outside. Cross-scope predictions are not clipped, dropped, or silently excluded. Unknown IDs are
invalid references; unfinished scope makes boundary metrics `NOT_EVALUABLE` while retaining diagnostics.
The eventual scorer must freeze how these observations contribute to FP/FN and exact boundary;
this gate does not itself compute accuracy or grant provider authorization.

## Title/subtitle policy remains a separate adjudication step

User-approved scope includes titles and subtitles. Still open: whether a descriptive line is part
of the same lexical title, an independent subtitle, or metadata. Require source-role evidence,
not H2-C's answer. Multipart is allowed for fragments of one independently reviewed unit; visual
proximity, same font, or shared topic does not decide this. `GIẤY MỜI` and `CHƯƠNG TRÌNH` case notes
describe their appearance only; they do not select Gold parts or approve combined extents.

## Commands (new output locations only)

```powershell
dotnet run --project scripts/P7ShapeAudit -c Release -- <frozen-source-pack> <new-private-shape-pack> <new-public-screening-receipt>
dotnet run --project scripts/P7ShapeReviewReceipt -c Release -- <frozen-source-pack> <shape-pack> <source-only-notes> <new-private-review-dir> <new-public-shape-audit>
```

The renderer is pinned to PDFtoImage 5.4.0 at 120 DPI; the rendering executable is Windows-only.
The review receipt verifies pinned original PDF/snapshot bytes and image hashes, then rebuilds
source references from the original snapshot rather than trusting an intermediate cue file.
The pinned source manifest and screening receipt must match; altered inputs fail closed.
No review approval, Gold, request manifest, production change or provider permission is created.
