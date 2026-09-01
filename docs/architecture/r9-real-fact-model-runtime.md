# Round 9: Real Fact Model Runtime

Status: `MERGE_READY`

Round 9 adds provider adapters behind the Round 8 `IFactProposalModel` boundary. Providers return
raw JSON only. The R8 strict parser creates untrusted `FactProposal` coordinates, and the R7
authority remains the only component that materializes source values and produces `ValidatedFact`.

## Revision authority

- Base revision: `5e42ff8c643b0a0f8ca1d38ddcd49e2e205581c9`
- Integrated execution revision: `69d0a3333c7deb6a47d468d293cc278351ce13e3`
- Publication revision: the commit containing this closure artifact

## R9A-I results

- UTF-16 offset map: PASS for ASCII, Vietnamese text and surrogate pairs
- Offset slices round-trip to original source text: PASS
- Source text normalization/truncation before offsets: `0`
- Oversized model source request: rejected instead of truncated
- Provider-neutral coordinate-only prompt: PASS
- OpenRouter adapter: PASS, temperature `0`, JSON object response, ZDR and data-collection deny
- SGLang/vLLM adapter: PASS, non-streaming, thinking disabled and strict closed JSON Schema
- Transport and malformed-content failures: audited as `FactProducerFailure`
- Invalid coordinates: preserved as proposals and rejected by R7 authority
- Provider adapters never construct `ValidatedFact` directly
- No heading runtime, retrieval/index runtime or authority behavior changes

The adapters reuse `OpenRouterOptions` and `SglangOptions`; they do not modify the frozen heading
clients. No live provider request was made. All adapter tests use a fake `HttpMessageHandler` with
provider-compatible wire responses.

## Regression evidence

The focused R7-R9 regression superset passed `45/45`. Host E2E remained unchanged. The unfiltered
full suite was executed at the exact execution revision:

`900 total / 898 passed / 2 failed / 0 skipped`

The only failures are frozen `C1` and `N15`, with no new failures, changed fingerprints or unjoined
failures. No expected failure was rebased.

## Authority flow

```text
FactExtractionContext + FactSchemaDefinition
  -> offset-safe FactProposalModelRequest
  -> OpenRouterFactProposalModel or SglangFactProposalModel
  -> raw JSON content
  -> R8 strict response parser
  -> untrusted FactProposal
  -> R7 FactAuthorityRuntime
  -> FactProposalValidator + semantic authority
  -> ValidatedFact
```

Model value, confidence, transport success, provider identity and producer agreement are not fact
authority. Round 9 canonical tests have `0` external provider calls. A live provider smoke, if run
later, remains supplemental evidence and is not a merge gate.

`ROUND9 = MERGE_READY`
