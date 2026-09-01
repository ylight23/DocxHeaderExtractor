# Round 10: Fact Schema Packs and Product Extraction API

Status: `MERGE_READY`

Round 10 turns the existing R7-R9 fact authority and R8-R9 proposal producers into an application
API. It does not change structural authority, fact validation rules, or provider protocols.

## Runtime shape

```text
DocumentExtractionResult
  -> DocumentFactExtractionRequest
  -> schema pack registry
  -> one IE context projection
  -> one producer pass per selected schema pack
  -> schema-routed semantic authority
  -> FactProposalValidator
  -> ValidatedFact only in the public result
```

`SchemaRoutedFactSemanticAuthority` selects policy from the proposal schema key. A missing pack is a
hard `fact-schema-pack-missing` rejection path; there is no default semantic authority. The runtime
reuses one parsed `DocumentExtractionResult` and one projected context set for all selected packs.

The audit retains produced proposals, rejections, and producer failures separately. Consumers use
`DocumentFactExtractionResult.Facts`, whose element type is `ValidatedFact`; untrusted proposals are
never exposed as facts.

## Revision authority

- Base revision: `dec5495745591a119f76f35e4087bade115ce092`
- Integrated execution revision: `63825c65c175f02bd5cf21779b7b2151000123f0`
- Publication revision: the commit containing this closure artifact

## Gates

- Schema packs: `3` (`test.amount`, `test.entity`, `test.period`)
- Cross-schema semantic authority: `0`
- Unknown-schema producer calls: `0`
- Product facts: `3`, validated-only: `100%`
- Shared multi-schema extraction/context projection: `1`
- Source-backed fact fields: `100%`; unjoined sources: `0`
- Structural, heading compatibility, search-index, and retrieval deltas: `0`
- Provider calls: `0`
- Host fingerprint changed: `false`
- Release build: `PASS`
- Diff check: `PASS`

The exact execution full suite measured `907 total / 905 passed / 2 failed / 0 skipped`. The only
failures are frozen `C1` and `N15`; new failures, changed fingerprints, and unjoined failures are
all `0`. The full suite was run once at the integrated execution revision.

`R10A-I = PASS`  
`ROUND10 = MERGE_READY`
