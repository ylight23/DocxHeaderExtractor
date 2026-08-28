# Human Hierarchy Annotation Guideline

## Status

G1 freezes a blind protocol for 422 occurrence seeds. The pilot contains 24 rows across four documents.
No labels are fabricated by the harness. The generated authority artifact remains `PENDING_HUMAN_ANNOTATION` until a human annotator persists labels and provenance.

## Blindness

Annotators may see only the document, source occurrence, and neighboring source lines needed to understand structure. Do not expose predicted level, parent, scope, type/path, confidence, validator output, or emitted output. Set `PREDICTION_VISIBLE_DURING_ANNOTATION=false`.

## Identity

Join only by `documentSha256 + sourceLineIds + occurrenceId`. Never join by text, title, array position, candidate id, or rank. Duplicate text remains separate occurrences.

## Dimensions

Annotate each dimension independently with `OBSERVABLE`, `NOT_OBSERVABLE`, or `AMBIGUOUS`.

- `level`: integer semantic hierarchy level when supported by document structure; do not use font size as authority.
- `parentOccurrenceId`: exact occurrence id, or null only when the occurrence is demonstrably a root. Do not use parent text or array index.
- `scope`: deterministic `scopeStartOccurrenceId` and `scopeEndOccurrenceId`; use `NOT_OBSERVABLE` when boundaries are unclear.
- `typePath`: annotate only against an already frozen ontology. If no frozen ontology applies, use `NOT_OBSERVABLE`.

Document title, running headers, TOC entries, appendices, and table-only text must be considered as context. G1 does not change heading labels.

## Provenance

Every completed row requires `annotatorId`, `annotatedAt`, `annotationVersion`, and optional evidence source line ids. Disagreement is recorded per dimension and adjudicated before full annotation.

## Closure gate

Only after human labels exist may the artifact change to `HUMAN_AUTHORITY_CREATED=true`. Then report dimension counts independently; denominators are the counts with that dimension status `OBSERVABLE`.

`PROVIDER_CALLS=0` and `PRODUCTION_CODE_CHANGED=false`.
