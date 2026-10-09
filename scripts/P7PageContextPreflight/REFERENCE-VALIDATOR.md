# P7-D2.2 Reference and Assertion Validator

Qualification-only engineering seam. No provider requests are composed here, and the frozen B
protocol, production transport, source universe, Gold, aliases and spans remain unchanged.

`PdfPageContextEvidenceValidator` rebuilds the raw store from the trusted source snapshot/parser
details and rebuilds context using independently supplied subjects and policy. Context canonical
bytes must match, including `selectable=false`, source identity, measurements and scope. It checks
the original issuance before validating `stageDecision` through the original stage parser.
Expanded context is never used to derive H2-C's selectable tail, endpoint or successor universe.

The bounded engineering-only envelope contains `sourceSha256`, `evidenceStoreSha256`,
`contextSha256`, `stageDecision`, and `analysis`. This is a validator test contract, **not** an
authorized/frozen B-Spatial provider schema. Each analysis record has an issued subject, source
references, optional exact-copy assertions and a short free interpretation. References identify
an alias plus exact source-ID hash, page and whole-occurrence span. Raw field assertions must
match the parser-owned JSON, including bbox. Duplicate properties, extras, duplicate references,
duplicate assertions, invalid cardinalities, excessive nesting and byte limits fail closed.

Three fact statuses remain distinct:

- `FACT_VERIFIED`: reference and exact observed field/copy checked against this snapshot.
- `FACT_CONTRADICTED`: forged/stale identity, source/page/span mismatch, out-of-context source,
  unknown field or copied raw measurement disagreement.
- `FACT_NOT_VERIFIABLE`: a known parser field has no available observation (e.g. missing bbox,
  glyphs not projected). It is never coerced to `false` or verified.

Any non-verified fact prevents acceptance. Protocol errors throw separately; they are not
silently reported as semantic falsehood. Interpretation always remains `UNVERIFIABLE_ASSERTION`,
even when all physical facts verify. A verified geometry reference does not prove a semantic
claim about headings, tables or continuation. No fixed semantic relation predicates, F1 veto,
overlap pruning, new source selection, tie-break or semantic authority is introduced.

This engineering gate does not qualify semantic correctness, generalization, provider token
budget or production promotion. Those gates remain separate and closed until independently
frozen and authorized.
