# P7-D2.3 source-only review preparation

Qualification only. No inference, replay output, interpretation record or existing Gold is read.
One minimum content-SHA PDF per source-pool category (eight categories including the reference)
is selected before parsing. Selection is metadata-only, not a shape/adversarial/Gold filter;
parser failure aborts and does not replace the document. This is a review candidate pool,
not an approved qualification cohort. Category diversity is not verified shape diversity.

The private pack contains hash-pinned copies of all original PDFs, a complete parser snapshot
(all atoms, aliases, catalog, occurrence evidence, native line geometry and glyph span maps),
parser code/dependency hashes, evidence store, every-page HTML source index and blank review drafts.
There is no truncation to model-visible or predicted sources. Empty extraction pages remain in
the PDF-page inventory. HTML bbox diagrams are not PDF renders. Review the original PDF too.
Public receipts contain hashes/counts, not document excerpts. Keep full review packs private.

The user approved scope on 2026-10-09: include titles/subtitles, adjudicate each unit separately.
This does not approve any occurrence's membership or extent. No heading is prefilled as Gold.
The reference is known development; the other candidates have unscreened exposure. No held-out claim.

## Review and approval

Copy `review.draft.json` to a new reviewed document; never mutate the pinned source files.
Review all original PDF pages and all extracted occurrences, including non-heading regions.
Record every page in `reviewedPdfPages`. For each occurrence select independently:
`IN_SCOPE/ADJUDICATED`, `OUT_OF_SCOPE/ADJUDICATED`, or `UNADJUDICATED/NOT_EVALUABLE`.
Units carry `DOCUMENT_TITLE`, `DOCUMENT_SUBTITLE` or `STRUCTURAL_HEADING` and exact alias/span
parts. Multipart source lines are allowed; sharing a subject does not make them one unit.
Review table/region labels by document semantic role, not by a geometric exclusion rule.
An unresolved extent remains `NOT_EVALUABLE`; do not infer it from prediction or explanation.
Record source-only evidence and any dispute in a separate independently reviewed adjudication
record. A label alone is not proof. Final user approval is required before Gold freeze.

`P7SourceOnlyReview.ApprovalGaps` checks completeness, snapshot/policy alignment and span bounds.
It cannot prove review independence, annotation truth, or visual accuracy. Attestations must
correspond to a real review; do not manufacture an approval hash/status just to close a gate.
Reviewing the already-exposed reference cannot make it newly blinded.

Freeze approved Gold and scorer separately from source and requests. Existing Gold stays untouched.
`FACT_VERIFIED` refers to physical evidence only, never semantic interpretation. Review packages
must not contain control/treatment responses. P7 provider execution and production promotion
stay locked until the full existing cohort/scoring gate and separately approved budget close.

Run (new output paths only):

```powershell
dotnet run --project scripts/P7SourceReviewFreeze -c Release -- <repo> <reference-pdf> <new-private-pack-dir> <new-public-receipt>
```

Source copies/snapshots are created before any review. Reproduction uses the same pool, parser
code, dependencies and source PDFs; generated manifests exclude clocks and machine paths.
Assembly hashes intentionally change if the parser/dependency binaries change. A public receipt
is not an immutable archive by itself: retain the hash-addressed private pack to reparse/review.
