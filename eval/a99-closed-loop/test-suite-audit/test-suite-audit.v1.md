# A99 deterministic test-suite runtime audit

## Scope and authority

This audit is offline. It reads only the TRX emitted by the full deterministic
test run and the filtered-suite result. It does not call a model/provider, read
Gold to make a test-tier decision, or change N15 artifacts.

Observed build/test input:

- Test project: `tests/DocxHeaderExtractor.Tests/DocxHeaderExtractor.Tests.csproj`
- Full TRX: `tests/DocxHeaderExtractor.Tests/TestResults/a99-full-suite-tier-audit.trx`
- Full run: `1607 PASS / 1 known N15 failure / 1608`, runner duration `7m44s`
- Core tier: `1598 PASS / 1598`, runner duration `6m26s`, wrapper wall `389.42s`
- Known failure: `PdfN15RankingLossDiagnosisProbe.CommittedDiagnosisReproducesByteForByte`
  (historical hash mismatch; intentionally not rebaselined)

The TRX contains per-test duration. Its duration sum is test CPU time, not wall
clock time, because the runner may execute tests concurrently.

## Implemented tier boundary

Only the following unambiguous historical artifact reproductions are currently
trait-marked. They remain available on explicit historical runs and are excluded
from the default core filter:

| Classes | Traits | Count |
| --- | --- | ---: |
| `PdfN13SilverCandidateCensusProbe` | `HistoricalForensic`, `LongRunning` | 5 |
| `PdfN14SilverAuditAndN2PreparationProbe` | `HistoricalForensic`, `LongRunning` | 2 |
| `PdfN15RankingLossDiagnosisProbe` | `HistoricalForensic`, `LongRunning` | 3 |

The default wrapper command is:

```powershell
pwsh -File scripts/Invoke-A99TestTier.ps1 -Tier CoreDeterministic -NoBuild
```

Its effective filter is:

```text
SuiteTier!=HistoricalForensic&SuiteTier!=LongRunning&SuiteTier!=ProviderBenchmark
```

The wrapper fails closed if a matching testhost/dotnet test process or the same
tier mutex is active. A UI timeout must attach/wait rather than start another
run.

## Category accounting

Classification below is based on the explicit traits implemented in this phase;
the 10 N13–N15 tests intentionally carry both historical and long-running
labels.

| Category | Tests | Test-duration sum | Decision |
| --- | ---: | ---: | --- |
| `HistoricalForensic` | 10 | 1056.68s | `MOVE_HISTORICAL_FORENSIC` (implemented) |
| `LongRunning` | 10 | 1056.68s | `MOVE_LONG_RUNNING` (implemented) |
| `ProviderBenchmark` | 0 | 0s | no provider tests were reclassified |
| Unclassified/core candidate pool | 1598 | 2898.90s | retain pending explicit overlap review |

The category sums are per-test duration sums and therefore overlap where a test
has two traits. No test was deleted. No additional test was moved merely because
it was slow; that would require an unambiguous coverage/ownership decision.

## Top 30 by per-test duration

These are the slowest observed test cases in the full TRX. Recommendations are
review decisions, not silent tier changes. The N13–N15 entries are already
quarantined; the other entries remain in the core candidate pool until their
coverage overlap is proven.

| # | Test | Seconds | Outcome | Purpose / overlap assessment | Recommendation |
| ---: | --- | ---: | --- | --- | --- |
| 1 | `PdfC1CrossDocumentRegressionInventoryProbe.IndependentPartialTimeoutEvidenceNowExistsAndWasConsumedByN35` | 425.47 | PASS | cross-document historical regression inventory; artifact/forensic overlap candidate | `MOVE_HISTORICAL_FORENSIC` (review) |
| 2 | `PdfProcurementRecurrenceAuthorityProbe.WriteProcurementRecurrenceReport` | 316.67 | PASS | procurement recurrence authority artifact; specialized forensic coverage | `MOVE_HISTORICAL_FORENSIC` (review) |
| 3 | `PdfN14SilverAuditAndN2PreparationProbe.CommittedArtifactsBindHistoricalExecutionInputsAndCurrentByteAuthority` | 312.80 | PASS | N14 historical audit/artifact reproduction | `MOVE_HISTORICAL_FORENSIC` |
| 4 | `PdfN15RankingLossDiagnosisProbe.CommittedDiagnosisReproducesByteForByte` | 256.10 | FAIL (known) | N15 frozen diagnosis reproduction | `MOVE_HISTORICAL_FORENSIC` |
| 5 | `PdfD2RankingInversionDiagnosisProbe.DiagnosisIsOfflineAndPreservesFrozenBoundary` | 200.55 | PASS | offline ranking diagnosis; historical forensic | `MOVE_HISTORICAL_FORENSIC` (review) |
| 6 | `PdfRound6dSelectedCohortNegativeAuthorityProbe.WriteSelectedCohortSourceFirstPacket` | 185.97 | PASS | source-first cohort/negative authority artifact | `MOVE_HISTORICAL_FORENSIC` (review) |
| 7 | `PdfHAuthorityPipelineCostModelProbe.CostModelIsMeasuredWithoutProvider` | 170.80 | PASS | authority cost model; deterministic measurement | `MOVE_LONG_RUNNING` (review) |
| 8 | `PdfN2S057MarkerAwareGroundingCounterfactualProbe.CommittedCounterfactualReproduces` | 164.59 | PASS | historical S057 counterfactual forensic | `MOVE_HISTORICAL_FORENSIC` (review) |
| 9 | `PdfN3FreshHoldoutBootstrapV2Probe.CommittedV2BootstrapAndReplacementPacketsReproduce` | 156.00 | PASS | frozen holdout bootstrap/replacement packets | `MOVE_HISTORICAL_FORENSIC` (review) |
| 10 | `PdfN13SilverCandidateCensusProbe.CommittedCensusReproducesFromTheCurrentBuild(stem: "029")` | 141.72 | PASS | N13 historical census reproduction | `MOVE_HISTORICAL_FORENSIC` |
| 11 | `PdfN3SilverCandidateCensusProbe.CommittedCensusReproducesFromFrozenPopulationAndSilver(stem: "030")` | 139.75 | PASS | frozen silver census reproduction | `MOVE_HISTORICAL_FORENSIC` (review) |
| 12 | `PdfN13SilverCandidateCensusProbe.CommittedCensusReproducesFromTheCurrentBuild(stem: "057")` | 120.09 | PASS | N13 historical census reproduction | `MOVE_HISTORICAL_FORENSIC` |
| 13 | `PdfN15RankingLossDiagnosisProbe.DiagnosisKeepsN13DenominatorsAndSourceAuthority` | 119.86 | PASS | N15 denominator/source-authority guard | `MOVE_HISTORICAL_FORENSIC` |
| 14 | `PdfN37ADegenerateSpanInvariantProbe.CommittedInvestigationReproduces` | 119.12 | PASS | historical span investigation | `MOVE_HISTORICAL_FORENSIC` (review) |
| 15 | `PdfN16RankScoreOwnerDiagnosisProbe.CommittedDiagnosisReproducesByteForByte` | 114.60 | PASS | historical rank-score ownership diagnosis | `MOVE_HISTORICAL_FORENSIC` (review) |
| 16 | `PdfN16RankScoreOwnerDiagnosisProbe.ExactReplayUsesSourceAuthorityAndReconcilesScoreComponents` | 101.89 | PASS | historical score reconciliation replay | `MOVE_HISTORICAL_FORENSIC` (review) |
| 17 | `PdfOccurrenceRankTests.TheSnapshotReportsExactlyTheRankingTheAuditReports` | 94.69 | PASS | ranking audit consistency; possible overlap with N16/D2 | `KEEP_CORE` pending review |
| 18 | `PdfN3FreshHoldoutBootstrapProbe.CommittedBootstrapAndPacketsReproduce` | 80.37 | PASS | frozen holdout bootstrap | `MOVE_HISTORICAL_FORENSIC` (review) |
| 19 | `PdfN3SilverCandidateCensusProbe.CommittedCensusReproducesFromFrozenPopulationAndSilver(stem: "058")` | 75.84 | PASS | frozen silver census reproduction | `MOVE_HISTORICAL_FORENSIC` (review) |
| 20 | `PdfN3SilverCandidateCensusProbe.CommittedCensusReproducesFromFrozenPopulationAndSilver(stem: "004")` | 68.25 | PASS | frozen silver census reproduction | `MOVE_HISTORICAL_FORENSIC` (review) |
| 21 | `PdfN13SilverCandidateCensusProbe.CommittedCensusReproducesFromTheCurrentBuild(stem: "042")` | 62.49 | PASS | N13 historical census reproduction | `MOVE_HISTORICAL_FORENSIC` |
| 22 | `PdfN36TwelveOutputCausalDiagnosisProbe.CommittedDiagnosisReproduces` | 58.82 | PASS | bounded causal diagnosis | `MOVE_HISTORICAL_FORENSIC` (review) |
| 23 | `PdfD1CandidateSelectionFirstLossProbe.CandidateSelectionDiagnosisReplaysOffline` | 55.85 | PASS | offline first-loss diagnosis | `MOVE_HISTORICAL_FORENSIC` (review) |
| 24 | `PdfN3SilverCandidateCensusProbe.CommittedCensusReproducesFromFrozenPopulationAndSilver(stem: "043")` | 55.32 | PASS | frozen silver census reproduction | `MOVE_HISTORICAL_FORENSIC` (review) |
| 25 | `PdfDocument056GoldBuilderProbe.FrozenBridgeIsOccurrenceSafeAndSourceGrounded` | 54.08 | PASS | source-grounded historical bridge | `MOVE_HISTORICAL_FORENSIC` (review) |
| 26 | `PdfDocument041GoldBuilderProbe.FrozenBridgeIsOccurrenceSafeAndSourceGrounded` | 44.33 | PASS | source-grounded historical bridge | `MOVE_HISTORICAL_FORENSIC` (review) |
| 27 | `PdfN13SilverCandidateCensusProbe.CommittedCensusReproducesFromTheCurrentBuild(stem: "003")` | 43.61 | PASS | N13 historical census reproduction | `MOVE_HISTORICAL_FORENSIC` |
| 28 | `RfcTocResidualSemanticDiagnosisTests.Capture_rfc5_residuals_without_changing_production_or_expectations` | 27.63 | PASS | residual forensic diagnosis | `MOVE_HISTORICAL_FORENSIC` (review) |
| 29 | `StructuralAuthorityMaterializerTests.Doc0252_catalog_builder_includes_supplemental_parser_candidate` | 13.72 | PASS | current structural authority regression | `KEEP_CORE` |
| 30 | `PdfTocDictionaryOutlineTests.DirectPdfTocProducerPreservesIbrdInformationStatement054` | 13.49 | PASS | representative PDF outline regression | `KEEP_CORE` |

## Decisions and follow-up

- `SAFE_TO_DELETE`: none. The audit found no duplicate that is safe to delete
  without a dedicated coverage map.
- The only implemented quarantine is N13/N14/N15. Their artifacts and the N15
  known failure remain unchanged and can be run explicitly with the historical
  filter.
- The other historical/diagnostic recommendations above are intentionally
  review-only. Moving them requires a separate explicit patch and focused
  verification; this audit does not silently remove them from CoreDeterministic.
- No provider/model calls were made.
- Source, Gold, benchmark baselines, and N15 denominators were not changed.
