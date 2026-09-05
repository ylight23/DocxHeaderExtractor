# HARNESS-LIFT measurement

This evaluation records where current extraction decisions are observed and where attribution remains unknown. It does not modify production extraction behavior, prompts, thresholds, candidate policy, resolver authority, validator behavior, or repair behavior.

The corpus is joined to the A99 inventory by exact source SHA. References retain HUMAN_KEY, SOURCE_STRUCTURAL_REFERENCE, MODEL_ASSISTED_SILVER, HEURISTIC_REFERENCE, UNLABELED, and INVALID_REFERENCE distinctions. Human review packets contain parser-owned source facts only.

Current model output is joined after the run. A missing occurrence-level proposal trace is recorded as NOT_MEASURED; no model error or harness lift is inferred from final disagreement alone.

The exact full-suite terminal result for this evaluation tree was 1012 total, 1011 passed, 1 failed, and 0 skipped. The sole failure is the pre-existing frozen N15 ranking-diagnosis artifact hash drift (`8b0811...` computed from the census input versus `034422...` retained in the committed diagnosis). It remains unmodified and is not treated as a new failure; the detailed reconciliation is in `eval/harness-lift/full-suite-reconciliation.v1.json`.

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
