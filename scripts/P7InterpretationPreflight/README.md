# P7-B: parser evidence plus model interpretation

This is a qualification-only, provider-free preflight. It does not implement a
production authority, authorize calls, adjudicate Gold, or restart Web. Treatment
B is a **composite treatment**, not a causal test of references alone. Retrieval
treatment C is not implemented.

## Ownership and separation

`PdfSourceEvidenceStore` owns immutable raw parser/source fields with availability,
basis and content hashes. It contains no `SAME_VISUAL_ROW`, table/heading labels,
or continuation predicates. The default physical inventory D1 is unchanged.

Each source entry preserves alias, source-ID hash, ordinal and exact full-occurrence
UTF-16 span. Fields are canonical text, page, native parser line bounds, raw line
typography summaries, and source reading-order ordinal. Glyph records are explicitly
`NOT_AVAILABLE / GLYPH_RECORDS_NOT_PROJECTED`; they are not synthesized from text or
boxes. Rich typography may be null while basic parser summaries remain observed.
Parser-derived geometry/font summaries are observations, not semantic ground truth
or independent visual-rendering verification.

`PdfInterpretationProtocol` composes three **separate** requests:

| Stage | Decision responsibility | Read-only evidence scope |
| --- | --- | --- |
| F1 | Function membership, every owned occurrence | Owned occurrences; original context/correspondences remain read-only |
| G2A | Anchor existence on frozen F1 positives | Issued primaries and immediate previous/next owned neighbors |
| H2-C | Exact boundary for one frozen HAS anchor | The same ordered tail as the control |

No downstream treatment output is allowed to change this isolated diagnostic's
next-stage cohort: G2A uses **frozen control F1**, and H2-C uses **frozen control
G2A**. This is not a new end-to-end treatment run. Independent references may use
visible neighbors, but context cannot become a new selectable subject.

## Versioned response contract

The active version is `P7_B_INTERPRETATION_RECORD_V2`. The initial provider-free
V1 freeze is retained, **superseded and not executable authority**: V2 clarifies
that exact copied text field values are permitted inside factual assertions,
resolving V1's blanket prohibition on source-text output. No provider calls occurred
under either version; the earlier artifact is not rewritten.

Required root fields:

```text
protocolVersion
stage
sourceSha256
evidenceStoreSha256
stageDecision: original stage's { decisions: [...] } schema
analysis: exactly one record per issued decision subject
  subject
  references: [{ occurrence, sourceAlias, fields }]
  assertions: [{ occurrence, sourceAlias, field, value }]  // may be empty
  interpretation: short decision explanation, at most 1024 characters
```

No relationship enum, ranking, confidence, or internal chain-of-thought transcript
is required. `interpretation` is model-authored and **not certified by the verifier**.
Assertions copy exact raw field values only. Free-text relational or semantic claims
remain independent review material, not new physical facts. Exact text assertions
can disclose source text; responses/captures therefore require private retention.

Validation rebuilds the store from trusted parser inputs, checks source/store identity,
exact occurrence-to-alias mapping, visibility, known observed fields, uniqueness,
cardinality, byte limits, and assertion values. Duplicate JSON keys/extra properties,
invented/unavailable fields, false assertions, and misbound/out-of-scope references
fail closed. It then delegates `stageDecision` to the existing strict F1/G2A/H2-C
parser. A valid result explicitly says:

```text
EvidenceStatus = VERIFIED_FACTS
InterpretationStatus = UNVERIFIABLE_ASSERTION
SemanticStatus = NOT_VERIFIED_REQUIRES_INDEPENDENT_REVIEW
```

Valid evidence cannot certify a false explanation, remove overlapping headings, or
force H2-C to stop at an F1 OTHER occurrence. This helper creates no runtime authority.

`InterpretationEvidenceAssessor` reports physical and interpretation status separately:
`VERIFIED_FACTS`, `UNVERIFIABLE_ASSERTION` (known missing measurements), or
`INVALID_REFERENCE` (unknown/misbound/falsified source facts). A malformed response
is a separate contract failure. This diagnostic hardening does not change frozen
V2 request bytes or make free-form interpretations verifiable.

The separate A/B comparison contract and source-pool freeze checklist are documented
in `scripts/P7CohortInventory/README.md`. Treatment A has the same raw evidence and
common stage rules, but no analysis/reference requirement. The source pool is not
yet a reviewed qualification cohort.

## Request delta and experimental limits

Control prompt/user/provider-body identities are checked against all eight existing
frozen reference captures. Treatment preserves the control stage input, source
occurrences, text, order, horizon and stage decision rules. It adds raw evidence,
reference/assertion/interpretation requirements and a versioned wrapper. Output
instructions are adapted explicitly; this is **not** an unchanged-prompt experiment.
Completion ceiling stays 32768; extended diagnostic response cap is explicitly
262144 bytes rather than silently changing the production cap.

Information and input volume change together. Historical control comparisons are
diagnostic, not fresh paired causal qualification. Isolating reference requirements
would need a separate B0/B1 experiment with identical evidence. No geometry-specific
or reference-specific improvement can be inferred from this preflight.

The single reference PDF is an **engineering fixture**, not the qualification corpus
and not a basis for choosing a winning treatment. Multi-document cohort and scoring
authority must be frozen before generalization qualification. Include administrative
front matter, multicolumn/table layouts, real headings in tables, and wrapped headings.

Score separately after capture freeze:

- Semantic membership per stage; false anchors remain a separate diagnostic lane.
- Exact boundary, under/overextent and signed/absolute boundary distance. Resolve
  title/subtitle source-review authority before scoring ambiguous titles.
- Cross-anchor overlap as observation only; no automatic pruning.
- Invalid/unknown/misbound/unavailable references and copied-assertion mismatches.
- Unsupported interpretation claims by independent review, not the deterministic verifier.
- Input/output/reasoning tokens, bytes, latency and provider calls.

Provider execution, P7-D3 and P7-E remain **LOCKED**. Retry/repair/fallback are zero.

## Reproduction

```powershell
dotnet build scripts/P7InterpretationPreflight -c Release --artifacts-path .verify-build/p7-b
dotnet .verify-build/p7-b/bin/P7InterpretationPreflight/release/dhx-v5-qualify.dll <repo> <reference.pdf> <private-control-root> <new-private-request-directory> <new-sanitized-manifest.json>
```

The private control root contains `20261008-f1-01`, `20261008-g2a-01`, and
`20261008-h2c-01`; receipts, bodies, raw responses, SSE and freeze markers are
hash-verified before preparation. The script has no provider transport, credential
lookup, execution flag or authorization path. Outputs must be new paths.

Full requests and the source store include source text and stay **private**. Only
the sanitized manifest with hashes, handles, counts and gates is repo-facing.
Regression coverage is `P7InterpretationProtocolTests` plus the prior D1/production
protocol/grounding/Web focused suite. Existing production requests and Gold remain
unchanged. Full suite and CI must be reported separately from focused tests.
