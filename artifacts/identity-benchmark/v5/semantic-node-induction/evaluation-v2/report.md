# A99 V5C — graph to pair evaluation

V5C is an offline evaluation of the immutable V5B primary prediction freeze. Pair labels are derived deterministically from node assignments and exact directional continuation edges; occurrenceRole alone is never used. Gold was opened only after prediction-freeze.json was written.

## Summary

```json
{
  "schemaVersion": "a99-v5c-semantic-node-evaluation-summary-v2",
  "phase": "V5C_GRAPH_TO_PAIR_EVALUATION_V2",
  "gold": {
    "itemCount": 128,
    "authority": "USER_FINAL_SOURCE_BACKED_ADJUDICATION",
    "independentHumanGold": false
  },
  "prediction": {
    "validCount": 103,
    "invalidCount": 25,
    "coverage": 0.8046875,
    "source": "FROZEN_V5B_CLUSTER_INDUCTION"
  },
  "overall": {
    "correct": 46,
    "total": 128,
    "effectiveCorrectness": 0.359375,
    "validPredictionAccuracy": 0.44660194174757284
  },
  "falseMerge": 55,
  "falseSplit": 1,
  "continuationOnlyErrors": 1,
  "nodeConstraint": {
    "correct": 47,
    "validPairs": 103,
    "accuracy": 0.4563106796116505
  },
  "derivationPolicy": "INVALID cluster first; DISTINCT for different nodes; CONTINUATION_OF for an exact edge in either direction; otherwise SAME_SEMANTIC_REPEAT. occurrenceRole alone is never used.",
  "perRelation": [
    {
      "relation": "SAME_SEMANTIC_REPEAT",
      "tp": 18,
      "fp": 44,
      "fn": 12,
      "precision": 0.2903225806451613,
      "recall": 0.6,
      "f1": 0.3913043478260869
    },
    {
      "relation": "CONTINUATION_OF",
      "tp": 0,
      "fp": 12,
      "fn": 1,
      "precision": 0,
      "recall": 0,
      "f1": 0
    },
    {
      "relation": "DISTINCT",
      "tp": 28,
      "fp": 1,
      "fn": 69,
      "precision": 0.9655172413793104,
      "recall": 0.28865979381443296,
      "f1": 0.4444444444444444
    }
  ],
  "modelCalls": 0,
  "providerCalls": 0,
  "goldReadBeforePredictionFreeze": false
}
```

## Failure ownership

```json
[
  {
    "category": "CORRECT",
    "count": 46,
    "items": [
      "SA-0001",
      "SA-0005",
      "SA-0011",
      "SA-0016",
      "SA-0018",
      "SA-0023",
      "SA-0024",
      "SA-0025",
      "SA-0026",
      "SA-0027",
      "SA-0028",
      "SA-0032",
      "SA-0034",
      "SA-0035",
      "SA-0036",
      "SA-0037",
      "SA-0041",
      "SA-0043",
      "SA-0047",
      "SA-0049",
      "SA-0052",
      "SA-0053",
      "SA-0054",
      "SA-0055",
      "SA-0056",
      "SA-0066",
      "SA-0070",
      "SA-0074",
      "SA-0075",
      "SA-0076",
      "SA-0082",
      "SA-0083",
      "SA-0086",
      "SA-0089",
      "SA-0091",
      "SA-0092",
      "SA-0093",
      "SA-0094",
      "SA-0095",
      "SA-0099",
      "SA-0101",
      "SA-0113",
      "SA-0117",
      "SA-0119",
      "SA-0121",
      "SA-0124"
    ]
  },
  {
    "category": "INVALID_CLUSTER_VALIDATION",
    "count": 19,
    "items": [
      "SA-0008",
      "SA-0009",
      "SA-0017",
      "SA-0048",
      "SA-0059",
      "SA-0060",
      "SA-0064",
      "SA-0069",
      "SA-0072",
      "SA-0078",
      "SA-0080",
      "SA-0087",
      "SA-0090",
      "SA-0096",
      "SA-0100",
      "SA-0103",
      "SA-0104",
      "SA-0110",
      "SA-0118"
    ]
  },
  {
    "category": "INVALID_PROVIDER",
    "count": 6,
    "items": [
      "SA-0012",
      "SA-0038",
      "SA-0040",
      "SA-0067",
      "SA-0098",
      "SA-0108"
    ]
  },
  {
    "category": "WRONG_CONTINUATION_EDGE",
    "count": 1,
    "items": [
      "SA-0107"
    ]
  },
  {
    "category": "WRONG_NODE_MERGE",
    "count": 55,
    "items": [
      "SA-0002",
      "SA-0003",
      "SA-0004",
      "SA-0006",
      "SA-0007",
      "SA-0010",
      "SA-0013",
      "SA-0014",
      "SA-0015",
      "SA-0019",
      "SA-0020",
      "SA-0021",
      "SA-0022",
      "SA-0029",
      "SA-0030",
      "SA-0031",
      "SA-0033",
      "SA-0039",
      "SA-0042",
      "SA-0044",
      "SA-0045",
      "SA-0046",
      "SA-0050",
      "SA-0051",
      "SA-0057",
      "SA-0058",
      "SA-0061",
      "SA-0062",
      "SA-0063",
      "SA-0065",
      "SA-0068",
      "SA-0071",
      "SA-0073",
      "SA-0077",
      "SA-0079",
      "SA-0081",
      "SA-0084",
      "SA-0085",
      "SA-0088",
      "SA-0097",
      "SA-0102",
      "SA-0105",
      "SA-0109",
      "SA-0111",
      "SA-0112",
      "SA-0114",
      "SA-0115",
      "SA-0116",
      "SA-0120",
      "SA-0122",
      "SA-0123",
      "SA-0125",
      "SA-0126",
      "SA-0127",
      "SA-0128"
    ]
  },
  {
    "category": "WRONG_NODE_SPLIT",
    "count": 1,
    "items": [
      "SA-0106"
    ]
  }
]
```

## Confusion matrix

```json
{
  "rows": {
    "SAME_SEMANTIC_REPEAT": {
      "SAME_SEMANTIC_REPEAT": 18,
      "CONTINUATION_OF": 1,
      "DISTINCT": 1,
      "INVALID_PREDICTION": 10
    },
    "CONTINUATION_OF": {
      "SAME_SEMANTIC_REPEAT": 0,
      "CONTINUATION_OF": 0,
      "DISTINCT": 0,
      "INVALID_PREDICTION": 1
    },
    "DISTINCT": {
      "SAME_SEMANTIC_REPEAT": 44,
      "CONTINUATION_OF": 11,
      "DISTINCT": 28,
      "INVALID_PREDICTION": 14
    }
  },
  "invalidPredictionCount": 25
}
```
