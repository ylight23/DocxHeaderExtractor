# DOC-0258 — canonical parent hierarchy v1

Status: **READY_FOR_USER_APPROVAL_CANONICAL_HIERARCHY**

This lane uses the 44 corroborated semantic nodes from the identity proposal. It assigns source-backed parent relations only; no historical parent or level authority is read.

## Frozen hierarchy result

- Semantic nodes: 44
- Parent edges: 44
- ROOT children: 3
- Max depth: 3
- Ambiguities: 0
- Validator: READY_FOR_USER_APPROVAL_CANONICAL_HIERARCHY
- Provider/model calls: 0

The tree uses synthetic ROOT with direct branches for the document title/narrative branch, Annex 1 agenda, and Annex 2 participants. Agenda day headings parent their session entries; regional narrative headings parent their regional subsections.

Levels are stored only in derived-levels.json and are computed as validated tree depth. No level was entered during adjudication.
