# C2-P Production Failure Reproduction

## Scope

C2-P consumes only the 12 `REAL_PRODUCTION_FAILURE` rows from the C1 ledger.
It reproduces the exact test fixtures without changing production code, tests,
expected values, or provider configuration.

## Result

All five groups reproduce deterministically from the committed test fixtures.
However, four groups do not reach the subsystem named by their historical test
name. The current default is `PdfFirstValidatedFallback = true`
(`HeaderExtractionPipeline.cs:185`), and `RunAsync` diverts at line 460.
Temporary DOCX fixtures without native structure therefore take the PDF-first
authority path; the no-sibling-PDF branch returns before the legacy critic,
split, rolling-outline, or heuristic-only projection code runs.

This is the first observable operation for the 11 affected rows and is a route
contract issue, not evidence for patching each downstream subsystem. The C1
classification is retained as input authority; C2-P records the more precise
first-loss diagnosis.

The RFC row is different: it invokes `RfcTocDictionaryOutline.Analyze`
directly. Its `Accepted` assertion fails deterministically, but the existing
failure packet does not retain the analyzer diagnostics needed to distinguish
TOC-cluster rejection, dictionary rejection, or body-anchor-ratio rejection.

## Group Summary

| Group | Count | First observable loss | Shared cause | Remediation |
| --- | ---: | --- | --- | --- |
| `CRITIC_PRESERVATION` | 4 | PDF-first route before critic | `PROVEN` | `NO` |
| `SPLIT_MERGED` | 2 | PDF-first route before splitter | `PROVEN` | `NO` |
| `ROLLING_OUTLINE` | 4 | PDF-first route before rolling callback | `PROVEN` | `NO` |
| `SLIM_EXTRACTION` | 1 | PDF-first route before heuristic projection | `NOT_PROVEN` | `NO` |
| `TOC_DICTIONARY` | 1 | Analyzer acceptance gate | `NOT_PROVEN` | `NO` |

The detailed artifact contains every affected FQN, minimal fixture identity,
expected invariant, actual behavior, negative control, and production entry
point:

`eval/verification/production-failure-reproduction.v1.json`

## Boundary

`deterministicReproduction = true` means the packet failure is reproducible
from the same committed fixture on the baseline/current suite runs. It does
not mean the intended downstream invariant was reached. No production fix is
justified by this pass.

`PROVIDER_CALLS = 0`

`PRODUCTION_CODE_CHANGED = false`

`TEST_EXPECTATIONS_CHANGED = false`
