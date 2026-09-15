# A99 V6A — V5 failure diagnosis

This is an offline forensic artifact over the frozen V5C v2 false-merge set. It does not mutate V5B/V5C predictions, infer structural-owner Gold, or install a production rule.

```json
{
  "schemaVersion": "a99-v6a-v5-failure-diagnosis-v1",
  "status": "V6A_COMPLETE",
  "source": "FROZEN_V5C_V2_FALSE_MERGES",
  "modelCalls": 0,
  "providerCalls": 0,
  "predictionsMutated": false,
  "noProductionRuleInferred": true,
  "totalFalseMerges": 55,
  "byMergeMode": [
    {
      "mergeMode": "MERGED_VIA_EXPLICIT_CONTINUATION_EDGE",
      "count": 11
    },
    {
      "mergeMode": "MERGED_WITHOUT_EXPLICIT_CONTINUATION_EDGE",
      "count": 44
    }
  ],
  "byDocument": [
    {
      "documentId": "DOC-0123",
      "count": 29
    },
    {
      "documentId": "DOC-0133",
      "count": 17
    },
    {
      "documentId": "DOC-0252",
      "count": 9
    }
  ],
  "byEvidence": [
    {
      "reason": "NORMALIZED_TEXT_AFFINITY",
      "count": 41
    },
    {
      "reason": "ADJACENT_ORDER",
      "count": 23
    },
    {
      "reason": "TERMINAL_ACRONYM_VARIANT",
      "count": 2
    },
    {
      "reason": "SHARED_STRUCTURAL_HEADING_KEY",
      "count": 1
    }
  ],
  "interpretation": "All cases are observed model grouping overreach against frozen V4H relation authority. This artifact does not infer structural owners or install a veto rule; owner induction remains a separate V6 experiment."
}
```

The cases retain cluster evidence, source occurrence text/order, and the model grouping mode. `sourceOwnerEvidenceAvailable=false` is intentional: V5B did not freeze an owner annotation, so V6A does not retrofit one from Gold.
