# Strict heading canonical hierarchy review v2

Status: **READY_FOR_HUMAN_CANONICAL_HIERARCHY_REVIEW**

The lane contains 9 documents and 520 source-backed heading occurrences.
Every packet has deterministic opaque H references, source text, source IDs,
source spans where available, reference provenance, and deterministic nearby
heading context. Every adjudication array is empty.

Historical level is hidden and was not read for packet construction. No
semantic node, parent edge, ROOT edge, or level was assigned.

The future review authority is split into occurrenceAssignments,
semanticNodes, and parentEdges. ROOT is a synthetic document-local node. After
review, validation will require exactly one node membership per occurrence and
exactly one parent or ROOT per semantic node; level will then be derived from
tree depth.

Construction-only context QA covers DOC-0001, DOC-0205, and DOC-0258. It checks
field/coverage availability only and makes no semantic decision.
