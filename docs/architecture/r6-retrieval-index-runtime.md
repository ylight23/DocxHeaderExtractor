# Round 6 Retrieval and Index Runtime

Round 6 adds deterministic retrieval/index runtime boundaries on top of the Round 5
`DocumentExtractionResult` projections. The core contracts do not reference an Elasticsearch,
OpenSearch, vector, embedding, LLM, `HeadingRecord`, or Slim type.

## Runtime path

`DocumentExtractionResult` is projected to `SearchIndexDocument` records and sent through
`ISearchIndexSink`. `SearchIndexRuntime.ReplaceAsync` replaces all chunks for one `DocumentId`,
so rerunning a document cannot leave stale chunks or append duplicates. `DeleteAsync` removes the
document's complete indexed lifecycle.

`ISearchIndexRetriever` accepts a deterministic `RetrievalQuery` with query text, top-k, document,
section, and structural-type filters. `InMemorySearchIndex` is the contract adapter for local and
test execution. Its text-match score is ranking metadata only and is never structural authority.

Retrieval hits preserve chunk text, source IDs, section path, structural context, and validated
relations (`ParentChild`, `CaptionOf`, and `Labels`).

## Verification

- Base revision: `8838fd0f4b844822c820dc0755d28be51513b137`
- Execution revision: `2093890217087d5ef67a0ac1259fe5775c5785fe`
- Publication revision: containing closure commit
- Index contract records: non-empty; retrieval hits: non-empty
- Replace lifecycle: idempotent, no duplicate chunks, no stale chunks
- Delete lifecycle: all chunks for a document removed
- Document, section, and structural-type filters: PASS
- Source/chunk/section/structural joins: `0` unjoined
- Index/retrieval text invention: `0`
- Search score used as authority: `0`
- Elasticsearch/OpenSearch/embedding/LLM provider calls: `0`
- R5 replay `028/056/091`: unchanged; extraction/product/heading deltas `0`
- Host fingerprint unchanged:
  `16284414abee710236b27fe92f710b95efb32928e169ba0e1ede2e63891b8429`
- Focused integrated suite: `80/80` passed
- Release build: PASS; `git diff --check`: PASS
- Full suite at the exact execution revision: `863 total / 861 passed / 2 failed / 0 skipped`
- Frozen failures: `C1`, `N15`; new failures `0`, changed fingerprints `0`, unjoined `0`

## Result

`R6A-C = PASS`.

The stable path is now:

`DOCX/PDF -> DocumentExtractionResult -> SearchIndexProjection -> SearchIndexDocument -> ISearchIndexSink`

and:

`RetrievalQuery -> ISearchIndexRetriever -> RetrievalHit[]`.

Elasticsearch/OpenSearch integration and the separate `FactProposal -> FactValidator` authority
pipeline remain outside this round.
