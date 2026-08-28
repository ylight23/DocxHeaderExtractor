# C1 Full-Suite Failure Root-Cause Triage

## Authority And Scope

This triage is a source join over the exact 35 failed-test rows in the C0
baseline/current TRX packets. The baseline is
`3b4e358c2696190e2aafd5a609587ad335cb1eea`; the current triage revision is
`a0a3638178e0b6092880abbce933a8954fa1780f`. No production code, test, or
expected value was changed, and no provider was called.

The 11 route-diversion rows were reclassified after the ARCH-2 authority-route
reachability audit. The source for that reclassification is
`ARCH-2_AUTHORITY_ROUTE_REACHABILITY`; the original C0 identity, assertion,
expected/actual values, and root-cause evidence remain unchanged.

The ledger contains exactly 35 unique fully qualified test identities and
retains the C0 failure fingerprint, assertion, expected/actual text, source
file, and assertion line.

## Classification Summary

| Classification | Count |
| --- | ---: |
| `STALE_TEST_EXPECTATION` | 17 |
| `REAL_PRODUCTION_FAILURE` | 1 |
| `DIAGNOSTIC_CONTRACT_MISMATCH` | 2 |
| `LEGACY_ONLY_TEST` | 15 |
| `ENVIRONMENT_DEPENDENT` | 0 |
| `UNKNOWN` | 0 |

## Root-Cause Groups

| Group | Count | Classification | Finding |
| --- | ---: | --- | --- |
| `AUTHORITY_ROUTE_CUTOVER_EXPECTATION` | 17 | `STALE_TEST_EXPECTATION` | Tests assert superseded `auto:*`/null route contracts; current authority route is `pdf-first-authority-v1`. |
| `LEGACY_PDF_ROUTE_PROBE` | 4 | `LEGACY_ONLY_TEST` | Historical tagged-route coverage probes are not current production-authority contracts. |
| `CRITIC_REJECTION_PRESERVATION` | 4 | `LEGACY_ONLY_TEST` | ARCH-2 shows these fixtures directly use the legacy HeaderExtractionPipeline and are diverted before critic processing. |
| `MERGED_PARAGRAPH_SPLIT_CONTRACT` | 2 | `LEGACY_ONLY_TEST` | ARCH-2 shows route diversion before merged-paragraph splitter integration; the splitter contract is not exercised. |
| `ROLLING_OUTLINE_INPUT_CONTRACT` | 4 | `LEGACY_ONLY_TEST` | ARCH-2 shows route diversion before BuildRollingOutline; the rolling contract is not exercised. |
| `SLIM_EXTRACTION_REVIEWED_CANDIDATE_CONTRACT` | 1 | `LEGACY_ONLY_TEST` | ARCH-2 shows route diversion before heuristic-only reviewed-candidate projection. |
| `RFC_TOC_DICTIONARY_ANALYSIS` | 1 | `REAL_PRODUCTION_FAILURE` | Direct `RfcTocDictionaryOutline.Analyze` contract fails on the RFC fixture. |
| `C1_HISTORICAL_INVENTORY_ARTIFACT` | 1 | `DIAGNOSTIC_CONTRACT_MISMATCH` | Historical 001 evidence is unavailable to the inventory in this checkout. |
| `N15_RANKING_DIAGNOSIS_ARTIFACT_HASH` | 1 | `DIAGNOSTIC_CONTRACT_MISMATCH` | Replay output disagrees with the committed diagnosis artifact hash. |

The four route groups are retained as root-cause groups, but their
classification is now `LEGACY_ONLY_TEST`: all 11 exact rows use
`HeaderExtractionPipeline` directly and do not enter the normal
`PipelineDocumentExtractionTool -> AuthorityExtractionPipeline` route. The
RFC analyzer row remains the sole `REAL_PRODUCTION_FAILURE`; it calls the
analyzer directly and is not explained by route selection. The C1 and N15 rows
remain diagnostic failures.

## Contract Boundary

`STALE_TEST_EXPECTATION` means the test still asserts the superseded route
contract while the current authority contract is explicitly present in the
implementation. `LEGACY_ONLY_TEST` is reserved for historical/evaluation
probes that intentionally exercise a route no longer authoritative in the
production pipeline. ARCH-2 establishes that the 11 reclassified rows are
such direct legacy/evaluation route fixtures. Only the RFC TOC analyzer row
remains classified as a real production failure; this task deliberately does
not fix it.

## Reclassification contract

```text
TOTAL = 35
REAL_PRODUCTION_FAILURE = 1
LEGACY_ONLY_TEST = 15
RECLASSIFICATION_SOURCE = ARCH-2_AUTHORITY_ROUTE_REACHABILITY
```

Full per-test evidence is in
`eval/verification/full-suite-failure-ledger.v1.json`. No expected values were
updated and no failure was suppressed.

`PROVIDER_CALLS = 0`

`PRODUCTION_CODE_CHANGED = false`

`TEST_EXPECTATIONS_CHANGED = false`
