# RFC-5 RFC TOC Residual Semantic Failure Diagnosis

## Reproduction

After RFC-4B, the exact RFC test group has three latent failures:

- two `Assert.True` heading-count failures (`expected >= 67`, `expected >= 97`);
- one `Assert.Contains` semantic-heading failure, with an empty actual collection.

Their fingerprints differ from the C1 fingerprints because C1 stopped at the
earlier route assertion. They are not new production regressions.

## Common First Loss

All three tests directly call `HeaderExtractionPipeline.RunAsync` with
`DisableLlm = true`. The current path is:

```text
RunAsync
-> PdfFirstValidatedFallback
-> RunPdfFirstAuthorityPipelineAsync
-> pdf-first-authority-v1
-> no RFC TOC heading result
```

The old expectations belong to the declared-outline path that could select
`auto:rfc-toc-dictionary`. The current PDF-first authority path does not invoke
that RFC route. A passing sibling exists: the direct 092
`RfcTocDictionaryOutline.Analyze` contract still produces `67` dictionary
entries and `67` body anchors with ratio `1.0`.

## Occurrence And Semantic Evidence

The expected heading sets are not committed gold occurrence sets. Two tests
specify only lower-bound counts, and the third specifies named headings. The
current actual pipeline result contains zero headings, so no expected heading
can be authoritatively joined to an actual occurrence. Unmatched entries are
therefore `NOT_OBSERVABLE`, not asserted false positives or missing true
headings.

The semantic failure is similarly a stale semantic contract: it expects RFC
TOC headings from a route that is no longer the current authority. Its first
divergence is route selection, before RFC dictionary generation.

## Classification

| Failure type | Count | Classification |
| --- | ---: | --- |
| Heading count | 2 | `STALE_HEADING_COUNT_EXPECTATION` |
| Semantic heading | 1 | `STALE_SEMANTIC_EXPECTATION` |

The primary classifications differ, but the operational first loss is proven
common: `AUTHORITY_ROUTE_SELECTION`. Production remediation is not justified
for any row because no authoritative expected occurrence diverges from a
known RFC production result. Test-expectation review is justified for all 3.

RFC-5 leaves C1 unchanged at `35 / 18 / 0 / 15 / 2`, changes no production
code or expectations, and calls no provider. A follow-up RFC-6 should decide
whether these three tests are route-contract tests or direct RFC semantic
tests before changing their remaining assertions.
