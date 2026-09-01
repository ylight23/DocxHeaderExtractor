# R5-5A-C Generic Document Extraction Output

R5-5A-C adds the generic document-output layer beneath the existing heading
compatibility API. `AuthorityExtractionPipeline.RunDocumentAsync` now exposes a
`DocumentExtractionResult` containing:

- a validated source catalog;
- the canonical `ValidatedStructure` graph;
- sections projected from validated outline elements and `ParentChild` relations;
- deterministic, source-backed chunks for downstream retrieval and extraction.

`RunAsync` remains the compatibility entry point. It executes the same generic
core and projects the result to `DocumentOutline`; no heading output is rebuilt
into structural authority. Generic output contracts contain no `HeadingRecord`
and the new chunk projection does not use `SlimXmlChunker` or any model call.

Source catalog units are parser/source-owned text with unique identity and
validated spans. Canonical source-document units are retained for grounded
document occurrences; structural parser facts that are not present in that
catalog are added as separately identified `parser-fact` units. This preserves
PDF structural evidence without treating a structural label as the whole
document body. Chunk text is only newline concatenation of catalog unit text,
and chunk structural references are joined back to the validated graph.

Sections derive parentage and paths only from validated `ParentChild` relations.
They do not infer hierarchy from text similarity. Non-heading elements and
relations remain available in the generic structure and chunk metadata while
`HeadingOutlineProjection` continues to emit only `Title`, `Subtitle`, and
`Heading` compatibility records.

## Verification

- Base revision: `a96a251f70770831111f918a3d296274ab836659`
- Execution revision: `90b949acc59a8af1ff3a6a49c14280e6590a161f`
- Publication revision: `containing-closure-commit`
- Focused integrated suite: `96/96` passed
- Generic output contract tests: `3/3` passed
- Release build: `PASS` (`0` errors; existing repository warnings remain)
- `git diff --check`: `PASS`
- Deterministic replay `028/056/091`: joined `3/3`, all structure/decision/product/final-heading deltas `0`
- Host E2E: `2/2` passed
- Host fingerprint unchanged: `16284414abee710236b27fe92f710b95efb32928e169ba0e1ede2e63891b8429`
- Provider calls: `0`

The unfiltered full suite at the exact execution revision measured `855 total,
853 passed, 2 failed, 0 skipped`. The two failures are the frozen C1 and N15
diagnostic probes. Regression reconciliation is `NEW_FAILURES=0`,
`CHANGED_FINGERPRINTS=0`, and `UNJOINED_FAILURES=0`; no known failure or expected
output was rebased.

## Closure

`GENERIC_EXTRACTION_RESULT = PASS`. The generic result preserves validated
heading, list, figure, table, title, and caption elements in the contract test
fixture; sections and chunks are non-empty; figure/table relations survive in
the graph; and the legacy heading projection remains parity-compatible.

The current host/API surface remains unchanged. The next layer can consume
`DocumentExtractionResult` for section-aware chunking, RAG, IE, indexing, and
report generation without making `DocumentOutline` the internal authority.
