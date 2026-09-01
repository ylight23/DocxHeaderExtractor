# Round 7 Generic Fact Authority

Round 7 adds a generic fact authority boundary without adding a domain ontology or an AI fact
provider. `FactProposal` contains only schema, context, field names, source IDs, and exact spans;
it deliberately has no model-supplied value field.

## Authority path

`DocumentExtractionResult` is projected to source-backed `FactExtractionContext` records carrying
document, chunk, section, structural, and parser-owned source-unit identity. A proposal is checked
against the canonical extraction result by `FactProposalValidator`:

`context -> schema -> field shape -> source membership -> exact span -> materialized value -> semantic authority`

Values are sliced from `DocumentSourceCatalog`; proposal values cannot become fact values. Source
IDs must belong to the proposal's context chunk, and structural context IDs must resolve. A missing
semantic policy rejects the proposal rather than default-accepting it.

`IFactSchemaRegistry` keeps schema knowledge outside generic core. `IFactSemanticAuthority` is the
only semantic acceptance boundary. `FactAuthorityRuntime` returns both `ValidatedFact` records and
`RejectedFactProposal` audit entries, so rejected proposals are never silently discarded.

Validated fact identity is a deterministic SHA-256 derived from document ID, schema key, and sorted
field source coordinates. It is independent from proposal and source identity.

## Verification

- Base revision: `b747c92d50c0597c268790f553d4e5ddf9d1a414`
- Execution revision: `c4360da15a668ec58888e202d177781d08760710` (exact full-suite revision)
- Publication revision: containing closure commit
- Fact matrix: `13/13` passed
- Integrated focused suite: `93/93` passed
- Validated facts: non-empty; expected rejections: non-empty
- Schema/domain hardcoding in generic fact authority: `0`
- Direct proposal authority, model value authority, and confidence authority: `0`
- Source/context/span/cross-context/semantic-policy bypasses: `0`
- Fact values from source slices: `100%`
- IE context source-backed: `100%`
- Provider calls: `0`
- Elasticsearch, embedding, vector, and LLM dependencies: `0`
- Round 5 replay `028/056/091`: unchanged; extraction/search/retrieval deltas `0`
- Host fingerprint unchanged:
  `16284414abee710236b27fe92f710b95efb32928e169ba0e1ede2e63891b8429`
- Release build: PASS; `git diff --check`: PASS

## Final full suite

The unfiltered suite ran at the exact execution revision:

- `876 total / 874 passed / 2 failed / 0 skipped`
- Frozen failures: `C1`, `N15`
- New failures: `0`
- Changed failure fingerprints: `0`
- Unjoined failures: `0`

The frozen failures were not rebased or treated as resolved.

## Result

`R7A` through `R7F`: PASS

`ROUND7 = MERGE_READY`

Round 8 may add rule or model producers that submit untrusted `FactProposal` records. Retrieval,
Elasticsearch, embeddings, and vector integration remain outside this round.
