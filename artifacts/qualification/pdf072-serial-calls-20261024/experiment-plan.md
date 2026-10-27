# Serial-call qualification plan

Baseline is a frozen production build after the retry fix. Candidate modes independently enable PDF
inline grouping and measured whole-document placement; combined mode is examined only after the
individual modes have been measured. Model, route, reasoning, source facts, parser and Gold remain fixed.
Runs are sequential, not concurrent, to avoid manufacturing provider contention. No source writeback.

Canonical Gold is resolved through gold-current/registry.v1.json, hash-checked, and source-hash-checked.
DOC-0252 declares 42 headings, whole-alias structured parts and no hierarchy/character-span capability.
Score alias membership and exact alias partitions separately. Do not manufacture hierarchy Gold or
count valid graph relations as semantic parent accuracy.

Promotion requires: passing regressions/architecture/parity tests; unchanged source-binding guarantees;
no lower Gold membership precision/recall or exact-boundary precision/recall than the new baseline;
no new unresolved boundary or parent failures; no graph conflicts; lower serial calls and elapsed time.
Reject a faster run that drops coverage or headings. Inspect every membership/boundary change against
Gold, and every changed parent against source context. A single passing document is local evidence;
it is insufficient to establish broad production quality or p95. Repeat a passing candidate before
promotion. A candidate that fails a quality gate stays opt-in with defaults disabled.

For hierarchy, absence of a Gold hierarchy capability must be reported explicitly. Parent consistency
against a baseline is a diagnostic, not an accuracy score; unresolved parent chains must not be hidden.
No automatic promotion based only on output count, JSON validity, or the target 300 seconds.
