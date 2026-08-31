# R4-12 Cumulative Focused Regression

Status: PASS

R4-12 reruns the cumulative native, authority, PDF, retirement, and host protections after
R4-10 physical Slim removal. The gate uses deterministic execution only: no LLM, VLM, provider, or
network call.

## Revision Authority

- `baseRevision`: `838622b4a37bd48162b164c3876e51b167e52fa9`
- `executionRevision`: `ce890674d70a8203c76f179bb44d82d753c1bc82`
- `publicationRevision`: the commit containing this closure pair
- `fullSuite`: `NOT_RUN`

The execution revision only removes three test literals that made the static legacy census match
the host gate's own assertions. No production source changed and no expected behavior was rebased.

## Regression Results

The focused union was measured from its TRX result, not from previous milestone counts:

- `focusedTests`: `109 total, 109 passed, 0 failed, 0 skipped`
- Release solution build: `PASS`
- Diagnostic corpus: `3 joined, delta 0`
- PDF corpus: `3 joined, delta 0`
- `providerCalls`: `0`
- `unmeasured`: `0`
- `expectedChanged`: `false`

Diagnostic comparison used `3b350543d3f6b88d074553915169d84587fecf00` as baseline. PDF comparison
used `a920b2adfc0a7e6caa8eea3c2d93fb63067b530c` as baseline. The same three corpus files were
hash-validated for both comparisons.

## Host Oracle

The R4-11 deterministic host fingerprint remains unchanged:

`16284414abee710236b27fe92f710b95efb32928e169ba0e1ede2e63891b8429`

Canonical tool, CLI, Web, MCP, and AgentHarness all joined this fingerprint. `UNJOINED_HOST_RESULTS`
is `0`, and the normal-host direct-bypass and fallback gates remain `0`.

## Retirement Gates

The static census over `src`, `tests`, and `tools` found zero references for the retired authority
and Slim symbols, including `HeaderExtractionPipeline`, `DocxSlimExtractor`, `SlimDocument`,
`SlimParagraph`, `SlimCompatibilityBoundary`, and `ForLegacyCompatibility`. `UNKNOWN_LEGACY_REFS`
is `0`.

One old PDF route-contract test remains outside the R4-12 union because it asserts the pre-R4-10
route `auto:pdf-toc-dictionary`; it reproduces as a pre-existing stale contract and was not changed.
It is not a new regression in `ce890674`.

R4-12 is closed and R4-13 is authorized. The canonical 1338-test suite remains the next gate.
