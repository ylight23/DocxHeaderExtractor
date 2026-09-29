# PDF Lane Removal

Date: 2026-09-18. Commit: `45e007c`.

## What was removed

The legacy PDF authority lane: `PdfLayoutEvidenceOutline`, `PdfTextbookOutline`,
`PdfBookmarkOutline`, `PdfBoldLabelOutline`, `PdfFinancialReportOutline`,
`PdfTaggedEvidenceOutline`, `PdfTocDictionaryOutline`, `PdfLegalTitleGrounder`,
`CorruptParagraphVisualVerifier`, `PdfSemanticRecoverySelector`, `PdfVisualTextRecovery`,
`PdfProposalConflictResolver`, `PdfVisualBlockAnalyst`, `PdfVisualRelationAnalyst` and
`PdfRegionRasterizer`. 51 files, 11,458 lines, with 33 test files that tested only that lane.

## Why

It had no production caller left. Its only entry was a DOCX run discovering a same-named PDF on the
filesystem through `PdfTextbookOutline.FindSiblingPdf`, which looked beside the input, rewrote an
eval corpus path segment, and then walked up from the process working directory searching a dataset
directory by filename. That let a file nobody uploaded take authority away from the file that was
uploaded, and made the winner depend on the working directory. Routing by the uploaded file closed
that entry, and nothing else opened it.

Leaving it would have left two contrary architectures in the tree with no way to tell which was
live. It is also where `ResolveHeadingSpansAsync` asked the model for span boundaries, which the
canonical contract forbids, and where three components still treated a domain heuristic as a veto
over the model.

## What replaced it

A PDF upload is extracted by `CanonicalSemanticPdfAuthorityAdapter` from that PDF alone. Everything
after source occurrences is `CanonicalSemanticEngine`, shared with the DOCX lane, so the two differ
only in how the source is read.

## Reading the older documents

These record what was true when they were written and have not been rewritten, because a record
that is edited to match the present stops being a record:

- `docs/architecture/source-tree-hygiene.md` — the MOVE/KEEP table from the strategy
  reorganisation. Rows for the files above describe moves that happened and were later deleted.
- `docs/architecture/r5-3e-pdf-headingrecord-producer-retirement.md` — a count taken at that
  retirement.
- `docs/consolidation/accuracy-r1-merge-ledger.md` — a merge ledger row.
- `handoff.md` — a dated work journal.

`docs/architecture/legacy-reachability.md` and `docs/architecture/clean-architecture-review.md`
were corrected in place instead, because they describe the current tree rather than a past event,
and a current-state document that contradicts the runtime is the more dangerous of the two kinds.

## Recovering it

`git show 45e007c^:<path>` for any file above. The rasterizer in particular is a general utility a
future cross-modal path would want; it was removed because an uncalled utility kept in case is the
debt this cleanup existed to remove, not because rendering PDF regions is a bad idea.
