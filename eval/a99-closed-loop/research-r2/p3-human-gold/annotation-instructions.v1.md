# A99-R2-P3A independent human heading annotation

This package prepares source-only material for Human A and Human B. It does not contain model
predictions, residual reports, scores, or another annotator's answers. Review the complete original
document independently; do not annotate from a shortlist or from formatting alone.

## Scope

Only the six documents named in this package are awaiting annotation. DOC-0158 and DOC-0165 already
have frozen P2B authority and must not be annotated again. The reserve cohort is sealed.

For every heading that the complete-document review identifies, add one row in document order. Decide
semantic membership from the original document. Bold text, large text, standalone blocks, numbering,
and table placement may be evidence but are neither automatic acceptance nor automatic rejection.

## Row fields

`documentTitleText` is the heading text as it appears in the original document. Set
`semanticMembership` to `HEADING`, select the most appropriate `semanticFamily`, and use
`optionalRole` only when independently clear. `sourceOccurrenceHint` may identify a packet occurrence
but is not required. Keep uncertainty explicit with `confidence=CERTAIN` or `REVIEW_REQUIRED`.

Do not manually count UTF-16 offsets. Later deterministic tooling will preserve the separate
documentTitleText and source-representation binding text. If the source representation differs from
the visual/original title, record the distinction in reviewNote and leave occurrence resolution for
the human occurrence-review step.

## Independence and adjudication

Human A and Human B must work independently and must not inspect the other pass, B0/A99 artifacts,
known residual strings, R2-B output, or reserve documents. After both passes are frozen, tooling may
compare exact text and occurrence hints, but it may not choose a pass, normalize disagreements, or
silently union rows. Every semantic disagreement requires human adjudication.

These files are offline research Gold preparation only and are prohibited from runtime use.
