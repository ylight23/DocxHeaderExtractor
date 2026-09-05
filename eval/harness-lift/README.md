# HARNESS-LIFT

This directory contains evaluation-only reconciliation artifacts. Source facts and references are joined by exact identity; silver, heuristic, and aggregate-only evidence cannot enter official denominators.

The deterministic pre-model pass runs with provider calls disabled. Current model measurements, when explicitly enabled, are limited to the frozen official-reference subset and record unavailable occurrence joins as NOT_MEASURED.

The current measurement used 95 exact corpus matches and a frozen provider subset of three official-reference documents across distinct families. The provider run made 132 calls over three repeats; occurrence-level model proposal joins remain NOT_MEASURED, so no accuracy or harness-lift claim is made.

The terminal full-suite result was 1012 total, 1011 passed, 1 failed, and 0 skipped. The only failure is the pre-existing frozen N15 ranking-diagnosis artifact hash drift; it is recorded in `full-suite-reconciliation.v1.json` and was not repaired or rebased.

Final decision snapshot:

```json
{
  "artifactKind": "harness_lift_final_decision",
  "schemaVersion": "1.0",
  "status": "MEASURED_WITH_LIMITATIONS",
  "codeSha": "2c99c35a5f9c8c06d1e854263b5200f868c4105d",
  "corpusFiles": 95,
  "matchedA99Files": 95,
  "matchedA99Groups": 95,
  "referenceCounts": {
    "ModelAssistedSilver": 9,
    "HumanKey": 25,
    "SourceStructuralReference": 7,
    "HeuristicReference": 9
  },
  "historicalEvidence": {
    "provenOccurrence": 2166,
    "provenDocument": 739,
    "partial": 151,
    "aggregateOnly": 176,
    "unknown": 0
  },
  "preModel": {
    "sourceRecall": "NOT_MEASURED",
    "candidateRecall": "NOT_MEASURED",
    "modelExposureRecall": "NOT_MEASURED"
  },
  "modelConditional": {
    "roleP": "NOT_MEASURED",
    "roleR": "NOT_MEASURED",
    "roleF1": "NOT_MEASURED",
    "levelAccuracy": "NOT_MEASURED",
    "parentAccuracy": "NOT_MEASURED",
    "spanAccuracy": "NOT_MEASURED"
  },
  "harnessRecovery": {
    "modelErrorsTotal": "NOT_MEASURED",
    "correctedByMarker": "NOT_MEASURED",
    "correctedByStructural": "NOT_MEASURED",
    "rejectedByValidator": "NOT_MEASURED",
    "introducedByDeterministicStages": "NOT_MEASURED",
    "modelErrorsSurvivedFinal": "NOT_MEASURED"
  },
  "observedPostModelHarnessLift": "NOT_MEASURED",
  "finalSystem": {
    "precision": "NOT_MEASURED",
    "recall": "NOT_MEASURED",
    "f1": "NOT_MEASURED",
    "levelAccuracy": "NOT_MEASURED",
    "parentAccuracy": "NOT_MEASURED",
    "hierarchyAccuracy": "NOT_MEASURED"
  },
  "attributionCoverage": "NOT_MEASURED_WITHOUT_OCCURRENCE_JOIN",
  "trustedMeasurementDocuments": 3,
  "trustedMeasurementGroups": "GROUP_LEVEL_REFERENCE_JOIN_PENDING",
  "humanReviewRequired": true,
  "trueBlindAvailable": false,
  "correctionMemoryRuntime": "NOT_OBSERVABLE",
  "correctionMemoryActiveRecords": "NOT_OBSERVABLE",
  "provider": "OpenRouter",
  "model": "qwen/qwen3.5-9b",
  "providerCalls": 132,
  "repeats": 3,
  "focusedHarnessLift": "PASS: evaluator contracts",
  "accuracy99Claim": "NOT_MEASURED",
  "harnessLiftStatus": "MEASURED_ON_TRUSTED_SUBSET_WITH_LIMITATIONS",
  "primaryBottleneck": "REFERENCE_EXPANSION",
  "nextRecommendedDirection": "SOURCE_FIRST_REFERENCE_EXPANSION_AND_BLIND_ADJUDICATION",
  "unknownGapCount": 124
}
```
