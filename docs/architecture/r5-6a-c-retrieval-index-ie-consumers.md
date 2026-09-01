# R5-6A-C Retrieval, Index, and IE Consumers

R5-6A-C adds three deterministic consumers over `DocumentExtractionResult`:

- `RetrievalProjection` produces retrieval records whose text is exactly
  `DocumentChunk.Text` and carries section, source, structural type, and relation metadata.
- `SearchIndexProjection` produces a stable index DTO without coupling callers to
  `ValidatedStructure` or any search/vector SDK.
- `IEContextProjection` produces source-backed input context only. It does not create facts or
  treat model output as validated authority.

A shared projection guard verifies that every chunk text is the newline concatenation of its
`DocumentSourceCatalog` units, every section and structural ID joins, and every structural source
reference resolves. Invalid or invented downstream text is rejected explicitly.

The consumer layer does not reference `HeadingRecord`, Slim types, an embedding provider, or an
LLM. Figure/table/list metadata and validated `ParentChild`, `CaptionOf`, and `Labels` relations
remain available to downstream consumers while the existing heading/product output is unchanged.

## Verification

- Base revision: `c056d7a72e667b33556315ce0139de1a41522309`
- Execution revision: `4a7ccee0882cde8ffd811e9da323c38cca246e50`
- Publication revision: `containing-closure-commit`
- Retrieval/index/IE records: non-empty (`1/1/1` in deterministic contract coverage)
- Retrieval/index/IE text invention: `0/0/0`
- Source, section, structural, and relation unjoined references: `0`
- Figure/table context: PASS
- List-item context: PASS
- Section path preservation: PASS
- Consumer dependency on `HeadingRecord`/Slim: `0/0`
- Embedding provider calls: `0`; LLM provider calls: `0`
- R5 replay `028/056/091`: joined; structure/decision/product/final-heading deltas `0`
- Host E2E: PASS; fingerprint unchanged:
  `16284414abee710236b27fe92f710b95efb32928e169ba0e1ede2e63891b8429`
- Release build: PASS; `git diff --check`: PASS
- Focused integrated suite: `55/55` passed, including consumer contract tests `7/7`
- Full suite at the exact execution revision: `859 total / 857 passed / 2 failed / 0 skipped`
- Frozen failures: `C1`, `N15`; new failures `0`, changed fingerprints `0`, unjoined failures `0`

The full-suite count is measured from the current tree and includes the two new consumer tests;
it is not an assumed historical inventory.

## Result

`R5-6A-C = PASS`. The downstream path is now:

`DocumentExtractionResult -> source-backed sections/chunks -> retrieval, search-index, and IE context projections`.

The next layer can add embeddings or a separate `FactProposal -> FactValidator` pipeline without
making those systems understand extraction internals.
