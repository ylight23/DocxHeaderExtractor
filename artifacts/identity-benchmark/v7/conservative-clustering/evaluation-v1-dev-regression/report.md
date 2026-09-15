# A99 V7D — conservative clustering dev regression

The cluster-to-pair projection was frozen before opening Gold: same cluster means `SAME_SEMANTIC_REPEAT`; different clusters mean `DISTINCT_SEMANTIC_NODE`. No continuation is inferred from text, role, or raw V7A proposals, so continuation prediction count is zero.

This is a DOC-0252/DOC-0133/DOC-0123 development-exposed regression only; it makes no generalization claim.

```json
{
  "schemaVersion": "a99-v7d-offline-dev-regression-summary-v1",
  "lane": "DEV_EXPOSED_V7D_REGRESSION",
  "gold": {
    "count": 128,
    "artifact": "artifacts/identity-benchmark/v4/semantic-adjudication/results/human-adjudication.frozen.v1.json",
    "sha256": "1b4c10fd20e0840a272611065bfcff524335542ea5d5b71cfe16bae692eaae16",
    "authority": "USER_FINAL_SOURCE_BACKED_ADJUDICATION",
    "independentHumanGold": false
  },
  "projection": {
    "sameCluster": "SAME_SEMANTIC_REPEAT",
    "differentCluster": "DISTINCT",
    "continuationPredictionCount": 0,
    "continuationInference": "FORBIDDEN"
  },
  "correct": 87,
  "total": 128,
  "effectiveCorrectness": 0.6796875,
  "nodeConstraintCorrect": 87,
  "nodeConstraintAccuracy": 0.6796875,
  "falseMerge": 23,
  "falseSplit": 18,
  "wrongRelationWithinCluster": 0,
  "continuationPredictions": 0,
  "perRelation": [
    {
      "relation": "SAME_SEMANTIC_REPEAT",
      "tp": 13,
      "fp": 23,
      "fn": 17,
      "precision": 0.3611111111111111,
      "recall": 0.43333333333333335,
      "f1": 0.39393939393939387
    },
    {
      "relation": "CONTINUATION_OF",
      "tp": 0,
      "fp": 0,
      "fn": 1,
      "precision": 0,
      "recall": 0,
      "f1": 0
    },
    {
      "relation": "DISTINCT",
      "tp": 74,
      "fp": 18,
      "fn": 23,
      "precision": 0.8043478260869565,
      "recall": 0.7628865979381443,
      "f1": 0.7830687830687831
    }
  ],
  "v7cArtifact": "artifacts/identity-benchmark/v7/conservative-clustering/preflight-v1-clique-equivalence",
  "providerCalls": 0,
  "modelCalls": 0
}
```
