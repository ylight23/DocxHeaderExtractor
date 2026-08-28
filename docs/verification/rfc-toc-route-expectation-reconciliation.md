# RFC-4B RFC TOC Stale Route Expectation Reconciliation

## Joined Identities

The three route assertions belong to the exact RFC test identities already
present in C1 under `AUTHORITY_ROUTE_CUTOVER_EXPECTATION` and
`STALE_TEST_EXPECTATION`. `ALREADY_PRESENT_IN_C1 = true` for all three; no
aggregate count was changed.

Each test directly invokes `HeaderExtractionPipeline.RunAsync`. With the
current default PDF-first lifecycle, execution enters
`PdfFirstValidatedFallback`, then `RunPdfFirstAuthorityPipelineAsync`, whose
deterministic route is `pdf-first-authority-v1`. The old route was produced by
the declared-outline branch, eventually selecting
`auto:rfc-toc-dictionary`; it is only conditionally reachable when the
PDF-first fallback is disabled.

## Minimal Change

Only these three stale assertions were removed:

```text
Assert.Equal("auto:rfc-toc-dictionary", outline.DeterministicRoute)
```

No production code, RFC analyzer logic, candidate behavior, dictionary logic,
or provider path was changed.

## Gate Result

The route assertions are reconciled, but the full
`RfcTocDictionaryOutlineTests` group is not yet passing. The preserved semantic
assertions expose two heading-count failures and one heading-content failure
under the current PDF-first authority output. RFC-4B stops at those new
assertions and does not rewrite semantic expectations without a separate audit.

`PROVIDER_CALLS = 0`, `PRODUCTION_CODE_CHANGED = false`, and C1 remains
`35 / 18 stale / 0 real production / 15 legacy-only / 2 diagnostic`.
