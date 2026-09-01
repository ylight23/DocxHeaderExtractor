# Round 8: Fact Proposal Producers

Status: `MERGE_READY`

Round 8 adds deterministic producer boundaries without changing fact authority. Rules and models
produce untrusted `FactProposal` values that contain only schema coordinates, source identities and
spans. `FactAuthorityRuntime` remains the only path that materializes source-backed values and
creates `ValidatedFact` records.

## Revision authority

- Base revision: `8f66d3263da72a6134275e18a3957d951bad5f16`
- Integrated execution revision: `532a905b8211db2c4a8b2c0b1e754cc5f1afe7e6`
- Publication revision: the commit containing this closure artifact

## R8A-I gates

- Producer request and provenance contracts: PASS
- Closed model request/response parser: PASS
- Rule and deterministic model producers: PASS
- Composite exact dedupe and distinct source occurrence preservation: PASS
- Production orchestration into Round 7 authority: PASS
- Producer failure isolation and audit: PASS
- Stable request-keyed replay with zero provider calls: PASS
- Model values, unknown properties and malformed JSON rejected: PASS
- Source and span errors reach authority and are rejected: PASS
- Semantic rejection remains authoritative over confidence: PASS
- Provider, embedding and search-index calls: `0`
- Direct producer creation of `ValidatedFact`: `0`

Focused producer/authority/consumer regression: `33/33 PASS`.
Host E2E: `2/2 PASS`; the host fingerprint remained
`16284414abee710236b27fe92f710b95efb32928e169ba0e1ede2e63891b8429`.
Release build: PASS. `git diff --check`: PASS.

## Canonical full suite

The unfiltered suite was executed at the exact execution revision:

`890 total / 888 passed / 2 failed / 0 skipped`

The two failures are the frozen Round 7 failures `C1` and `N15`. Their fingerprints and failure
ownership are unchanged. There are no new failures, changed fingerprints or unjoined failures.

Round 8 does not rebase or remove these known failures. Raw TRX and test-run directories are runtime
outputs and are not part of the publication.

## Authority flow

```text
DocumentExtractionResult
  -> IEContextProjection
  -> FactProposalProductionRequest
  -> rule/model/composite producer
  -> ProducedFactProposal + provenance
  -> FactAuthorityRuntime
  -> FactProposalValidator
  -> IFactSemanticAuthority
  -> ValidatedFact
```

The producer layer has no domain regexes, schema registry authority, model value authority,
confidence authority or voting authority. Round 9 may add a real provider adapter behind the model
interface; it is outside this closure.

`ROUND8 = MERGE_READY`
