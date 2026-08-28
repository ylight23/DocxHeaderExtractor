# C1 Full-Suite Failure Root-Cause Triage

## Authority And Scope

This triage is a source join over the exact 35 failed-test rows in the C0
baseline/current TRX packets. The baseline is
`3b4e358c2696190e2aafd5a609587ad335cb1eea`; the current triage revision is
`a0a3638178e0b6092880abbce933a8954fa1780f`. No production code, test, or
expected value was changed, and no provider was called.

The ledger contains exactly 35 unique fully qualified test identities and
retains the C0 failure fingerprint, assertion, expected/actual text, source
file, and assertion line.

## Classification Summary

| Classification | Count |
| --- | ---: |
| `STALE_TEST_EXPECTATION` | 17 |
| `REAL_PRODUCTION_FAILURE` | 12 |
| `DIAGNOSTIC_CONTRACT_MISMATCH` | 2 |
| `LEGACY_ONLY_TEST` | 4 |
| `ENVIRONMENT_DEPENDENT` | 0 |
| `UNKNOWN` | 0 |

## Root-Cause Groups

| Group | Count | Classification | Finding |
| --- | ---: | --- | --- |
| `AUTHORITY_ROUTE_CUTOVER_EXPECTATION` | 17 | `STALE_TEST_EXPECTATION` | Tests assert superseded `auto:*`/null route contracts; current authority route is `pdf-first-authority-v1`. |
| `LEGACY_PDF_ROUTE_PROBE` | 4 | `LEGACY_ONLY_TEST` | Historical tagged-route coverage probes are not current production-authority contracts. |
| `CRITIC_REJECTION_PRESERVATION` | 4 | `REAL_PRODUCTION_FAILURE` | Current output violates the invariant that critic/document-title rejection preserves the disputed structural item. |
| `MERGED_PARAGRAPH_SPLIT_CONTRACT` | 2 | `REAL_PRODUCTION_FAILURE` | Explicit `splitMergedParagraphs` behavior does not produce the expected slices/headings. |
| `ROLLING_OUTLINE_INPUT_CONTRACT` | 4 | `REAL_PRODUCTION_FAILURE` | Rolling outline fixtures receive an empty result instead of the required skeleton/anchors. |
| `SLIM_EXTRACTION_REVIEWED_CANDIDATE_CONTRACT` | 1 | `REAL_PRODUCTION_FAILURE` | Heuristic-only projection reports zero candidates instead of the expected six. |
| `RFC_TOC_DICTIONARY_ANALYSIS` | 1 | `REAL_PRODUCTION_FAILURE` | Direct `RfcTocDictionaryOutline.Analyze` contract fails on the RFC fixture. |
| `C1_HISTORICAL_INVENTORY_ARTIFACT` | 1 | `DIAGNOSTIC_CONTRACT_MISMATCH` | Historical 001 evidence is unavailable to the inventory in this checkout. |
| `N15_RANKING_DIAGNOSIS_ARTIFACT_HASH` | 1 | `DIAGNOSTIC_CONTRACT_MISMATCH` | Replay output disagrees with the committed diagnosis artifact hash. |

The route group is supported by `HeaderExtractionPipeline`'s current
`RunPdfFirstAuthorityPipelineAsync`/`pdf-first-authority-v1` path. The RFC
analyzer row is kept separate because it calls the analyzer directly and is
not explained by route selection. The C1 and N15 rows remain diagnostic
failures; they are not evidence of a product extraction failure.

## Contract Boundary

`STALE_TEST_EXPECTATION` means the test still asserts the superseded route
contract while the current authority contract is explicitly present in the
implementation. `LEGACY_ONLY_TEST` is reserved for historical/evaluation
probes that intentionally exercise a route no longer authoritative in the
production pipeline. The 12 real failures retain their current invariants
and require production investigation; this task deliberately does not fix
them.

Full per-test evidence is in
`eval/verification/full-suite-failure-ledger.v1.json`. No expected values were
updated and no failure was suppressed.

`PROVIDER_CALLS = 0`

`PRODUCTION_CODE_CHANGED = false`

`TEST_EXPECTATIONS_CHANGED = false`
