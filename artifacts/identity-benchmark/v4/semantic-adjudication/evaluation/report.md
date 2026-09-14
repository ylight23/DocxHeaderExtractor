# A99 V4H-C — frozen semantic evaluation

Status: **COMPLETE**

This is an offline, evaluation-only join of the immutable V4H-B user-final source-backed adjudication and the frozen V4G-B predictions. The V4H-B authority remains `USER_FINAL_SOURCE_BACKED_ADJUDICATION`; it is not relabeled as independent human Gold. The original model-assisted source SHA remains in V4H-B provenance.

## Firewall

- New model calls: **0**.
- New provider calls: **0**.
- The V4G-B source execution contains **128 historical provider calls** and was already response-frozen with `goldReadCount=0`.
- Gold was opened only for this post-freeze evaluation join.
- Frozen adjudication and V4G prediction artifacts were not mutated.
- Exact frozen candidate join: **128/128**.
- Returned prediction occurrence endpoints matched exactly: **95/95**. The 33 provider-error cells returned no endpoints and therefore remain `INVALID_PREDICTION`.

## Summary

```json
{
  "schemaVersion": "a99-v4h-c-semantic-evaluation-summary-v1",
  "gold": {
    "itemCount": 128,
    "authority": "USER_FINAL_SOURCE_BACKED_ADJUDICATION",
    "independentHumanGold": false,
    "artifact": "artifacts/identity-benchmark/v4/semantic-adjudication/results/human-adjudication.frozen.v1.json"
  },
  "prediction": {
    "artifact": "artifacts/identity-benchmark/v4/target-grounding-challenger/execution/parsed-results.json",
    "validCount": 95,
    "invalidCount": 33,
    "coverage": 0.7421875,
    "source": "FROZEN_V4G_B_PREDICTIONS"
  },
  "join": {
    "exactCandidateIdJoin": 128,
    "exactPredictionEndpointJoin": 95,
    "providerErrorEndpointUnavailable": 33,
    "policy": "Provider-error cells retain frozen candidate join but are INVALID_PREDICTION; no semantic label is inferred."
  },
  "overall": {
    "correct": 71,
    "total": 128,
    "effectiveCorrectness": 0.5546875,
    "validPredictionAccuracy": 0.7473684210526316
  },
  "falseMerge": 23,
  "falseSplit": 1,
  "perRelation": [
    {
      "relation": "SAME_SEMANTIC_REPEAT",
      "tp": 22,
      "fp": 18,
      "fn": 8,
      "precision": 0.55,
      "recall": 0.7333333333333333,
      "f1": 0.6285714285714286
    },
    {
      "relation": "CONTINUATION_OF",
      "tp": 1,
      "fp": 5,
      "fn": 0,
      "precision": 0.16666666666666666,
      "recall": 1,
      "f1": 0.2857142857142857
    },
    {
      "relation": "DISTINCT",
      "tp": 48,
      "fp": 1,
      "fn": 49,
      "precision": 0.9795918367346939,
      "recall": 0.4948453608247423,
      "f1": 0.6575342465753425
    }
  ],
  "executionManifest": "artifacts/identity-benchmark/v4/target-grounding-challenger/execution/execution-manifest.json",
  "modelCalls": 0,
  "providerCalls": 0
}
```

`effectiveCorrectness` is correct / all 128. `validPredictionAccuracy` excludes provider-error cells and is reported separately; it is not the primary all-cell result.

## Confusion matrix

```json
{
  "schemaVersion": "a99-v4h-confusion-matrix-v1",
  "rows": {
    "SAME_SEMANTIC_REPEAT": {
      "SAME_SEMANTIC_REPEAT": 22,
      "CONTINUATION_OF": 0,
      "DISTINCT": 1,
      "INVALID_PREDICTION": 7
    },
    "CONTINUATION_OF": {
      "SAME_SEMANTIC_REPEAT": 0,
      "CONTINUATION_OF": 1,
      "DISTINCT": 0,
      "INVALID_PREDICTION": 0
    },
    "DISTINCT": {
      "SAME_SEMANTIC_REPEAT": 18,
      "CONTINUATION_OF": 5,
      "DISTINCT": 48,
      "INVALID_PREDICTION": 26
    }
  },
  "invalidPredictionCount": 33
}
```

## Per document

```json
[
  {
    "documentId": "DOC-0123",
    "total": 70,
    "validPredictions": 58,
    "invalidPredictions": 12,
    "predictionCoverage": 0.8285714285714286,
    "correct": 41,
    "effectiveCorrectness": 0.5857142857142857,
    "validPredictionAccuracy": 0.7068965517241379,
    "falseMerge": 16,
    "falseSplit": 1
  },
  {
    "documentId": "DOC-0133",
    "total": 29,
    "validPredictions": 15,
    "invalidPredictions": 14,
    "predictionCoverage": 0.5172413793103449,
    "correct": 9,
    "effectiveCorrectness": 0.3103448275862069,
    "validPredictionAccuracy": 0.6,
    "falseMerge": 6,
    "falseSplit": 0
  },
  {
    "documentId": "DOC-0252",
    "total": 29,
    "validPredictions": 22,
    "invalidPredictions": 7,
    "predictionCoverage": 0.7586206896551724,
    "correct": 21,
    "effectiveCorrectness": 0.7241379310344828,
    "validPredictionAccuracy": 0.9545454545454546,
    "falseMerge": 1,
    "falseSplit": 0
  }
]
```

## Frozen sanity cases

```json
[
  {
    "reviewId": "SA-0067",
    "candidateId": "DOC-0252:V4P0544-0888",
    "goldRelation": "DISTINCT",
    "predictedRelation": "INVALID_PREDICTION",
    "predictionStatus": "PROVIDER_ERROR",
    "correct": false
  },
  {
    "reviewId": "SA-0108",
    "candidateId": "DOC-0252:V4P0544-0893",
    "goldRelation": "DISTINCT",
    "predictedRelation": "INVALID_PREDICTION",
    "predictionStatus": "PROVIDER_ERROR",
    "correct": false
  },
  {
    "reviewId": "SA-0040",
    "candidateId": "DOC-0252:V4P0888-0893",
    "goldRelation": "CONTINUATION_OF",
    "predictedRelation": "CONTINUATION_OF",
    "predictionStatus": "VALID",
    "correct": true
  }
]
```

The V4H-B pairwise lane is closed after this evaluation. The next architecture is `GLOBAL_CLUSTER_SEMANTIC_NODE_INDUCTION`; no V4I pairwise tuning is authorized from this 128-case development-exposed lane.
