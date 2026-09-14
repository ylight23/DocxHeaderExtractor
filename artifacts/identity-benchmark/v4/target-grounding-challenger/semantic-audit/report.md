# A99 V4G-C — offline semantic behavior audit

Status: **COMPLETE**

Provider calls: **0**. Model calls: **0**. V4G-B execution artifacts were read-only.

## Conclusions

- Target grounding: **REPAIRED_ON_DEV_SAMPLE** (V4G-B, 0/95 target mismatch).
- Behavioral stability: **HIGH**.
- Semantic correctness: **INSUFFICIENT_LABELS**.

## Behavioral layers

```json
{
  "schemaVersion": "a99-v4g-c-population-denominators-v1",
  "scheduledV2": 128,
  "transportOrEvaluatorV2": 95,
  "v1V2JointlyEvaluable": 77,
  "v1TransportOrEvaluator": 108,
  "v2TransportOrEvaluator": 95,
  "jointlyValidRelationOutputs": 49,
  "v1TargetMismatch": 45,
  "unavailableOrNotJointlyValid": 79,
  "existingGoldLabeledCandidates": "computed after behavioral and strata artifacts freeze"
}
```

## Clean V1/V2 relation comparison

```json
{
  "schemaVersion": "a99-v4g-c-clean-cohort-analysis-v1",
  "definition": "V1 valid and target-grounded, V2 valid and target-grounded; V1/V2 outputs only, not Gold",
  "jointlyValid": 49,
  "relationAgreement": 47,
  "relationChanges": 2,
  "relationAgreementRate": 0.9591836734693877,
  "relationChangeRate": 0.04081632653061224,
  "rows": [
    {
      "candidateId": "DOC-0123:P1533-1534",
      "v1Relation": "DISTINCT",
      "v2Relation": "DISTINCT",
      "changed": false
    },
    {
      "candidateId": "DOC-0123:P1761-1762",
      "v1Relation": "DISTINCT",
      "v2Relation": "DISTINCT",
      "changed": false
    },
    {
      "candidateId": "DOC-0252:P750-751",
      "v1Relation": "CONTINUATION_OF",
      "v2Relation": "DISTINCT",
      "changed": true
    },
    {
      "candidateId": "DOC-0123:P962-1000",
      "v1Relation": "SAME_SEMANTIC_REPEAT",
      "v2Relation": "SAME_SEMANTIC_REPEAT",
      "changed": false
    },
    {
      "candidateId": "DOC-0123:P3005-3006",
      "v1Relation": "DISTINCT",
      "v2Relation": "DISTINCT",
      "changed": false
    },
    {
      "candidateId": "DOC-0252:P508-512",
      "v1Relation": "DISTINCT",
      "v2Relation": "DISTINCT",
      "changed": false
    },
    {
      "candidateId": "DOC-0123:P783-784",
      "v1Relation": "DISTINCT",
      "v2Relation": "DISTINCT",
      "changed": false
    },
    {
      "candidateId": "DOC-0252:P188-189",
      "v1Relation": "DISTINCT",
      "v2Relation": "DISTINCT",
      "changed": false
    },
    {
      "candidateId": "DOC-0252:P418-426",
      "v1Relation": "DISTINCT",
      "v2Relation": "DISTINCT",
      "changed": false
    },
    {
      "candidateId": "DOC-0123:P1540-1591",
      "v1Relation": "SAME_SEMANTIC_REPEAT",
      "v2Relation": "SAME_SEMANTIC_REPEAT",
      "changed": false
    },
    {
      "candidateId": "DOC-0123:P1161-1162",
      "v1Relation": "DISTINCT",
      "v2Relation": "DISTINCT",
      "changed": false
    },
    {
      "candidateId": "DOC-0133:P140-157",
      "v1Relation": "DISTINCT",
      "v2Relation": "DISTINCT",
      "changed": false
    },
    {
      "candidateId": "DOC-0133:P1611-1618",
      "v1Relation": "DISTINCT",
      "v2Relation": "DISTINCT",
      "changed": false
    },
    {
      "candidateId": "DOC-0123:P2843-2844",
      "v1Relation": "DISTINCT",
      "v2Relation": "DISTINCT",
      "changed": false
    },
    {
      "candidateId": "DOC-0133:P1803-1805",
      "v1Relation": "DISTINCT",
      "v2Relation": "DISTINCT",
      "changed": false
    },
    {
      "candidateId": "DOC-0252:P449-453",
      "v1Relation": "DISTINCT",
      "v2Relation": "DISTINCT",
      "changed": false
    },
    {
      "candidateId": "DOC-0123:P1160-1161",
      "v1Relation": "DISTINCT",
      "v2Relation": "DISTINCT",
      "changed": false
    },
    {
      "candidateId": "DOC-0252:P434-439",
      "v1Relation": "DISTINCT",
      "v2Relation": "DISTINCT",
      "changed": false
    },
    {
      "candidateId": "DOC-0123:P3151-3182",
      "v1Relation": "SAME_SEMANTIC_REPEAT",
      "v2Relation": "SAME_SEMANTIC_REPEAT",
      "changed": false
    },
    {
      "candidateId": "DOC-0252:P16-31",
      "v1Relation": "DISTINCT",
      "v2Relation": "DISTINCT",
      "changed": false
    },
    {
      "candidateId": "DOC-0133:P1736-1753",
      "v1Relation": "SAME_SEMANTIC_REPEAT",
      "v2Relation": "DISTINCT",
      "changed": true
    },
    {
      "candidateId": "DOC-0123:P2048-2050",
      "v1Relation": "SAME_SEMANTIC_REPEAT",
      "v2Relation": "SAME_SEMANTIC_REPEAT",
      "changed": false
    },
    {
      "candidateId": "DOC-0252:P224-226",
      "v1Relation": "DISTINCT",
      "v2Relation": "DISTINCT",
      "changed": false
    },
    {
      "candidateId": "DOC-0123:P66-1995",
      "v1Relation": "SAME_SEMANTIC_REPEAT",
      "v2Relation": "SAME_SEMANTIC_REPEAT",
      "changed": false
    },
    {
      "candidateId": "DOC-0252:P396-402",
      "v1Relation": "DISTINCT",
      "v2Relation": "DISTINCT",
      "changed": false
    },
    {
      "candidateId": "DOC-0252:P22-26",
      "v1Relation": "DISTINCT",
      "v2Relation": "DISTINCT",
      "changed": false
    },
    {
      "candidateId": "DOC-0123:P2742-2754",
      "v1Relation": "DISTINCT",
      "v2Relation": "DISTINCT",
      "changed": false
    },
    {
      "candidateId": "DOC-0123:P3010-3015",
      "v1Relation": "SAME_SEMANTIC_REPEAT",
      "v2Relation": "SAME_SEMANTIC_REPEAT",
      "changed": false
    },
    {
      "candidateId": "DOC-0252:P539-595",
      "v1Relation": "DISTINCT",
      "v2Relation": "DISTINCT",
      "changed": false
    },
    {
      "candidateId": "DOC-0123:P1178-1179",
      "v1Relation": "DISTINCT",
      "v2Relation": "DISTINCT",
      "changed": false
    },
    {
      "candidateId": "DOC-0123:P59-254",
      "v1Relation": "SAME_SEMANTIC_REPEAT",
      "v2Relation": "SAME_SEMANTIC_REPEAT",
      "changed": false
    },
    {
      "candidateId": "DOC-0123:P53-251",
      "v1Relation": "SAME_SEMANTIC_REPEAT",
      "v2Relation": "SAME_SEMANTIC_REPEAT",
      "changed": false
    },
    {
      "candidateId": "DOC-0123:P552-554",
      "v1Relation": "SAME_SEMANTIC_REPEAT",
      "v2Relation": "SAME_SEMANTIC_REPEAT",
      "changed": false
    },
    {
      "candidateId": "DOC-0123:P602-1271",
      "v1Relation": "SAME_SEMANTIC_REPEAT",
      "v2Relation": "SAME_SEMANTIC_REPEAT",
      "changed": false
    },
    {
      "candidateId": "DOC-0123:P1035-1036",
      "v1Relation": "DISTINCT",
      "v2Relation": "DISTINCT",
      "changed": false
    },
    {
      "candidateId": "DOC-0123:P57-253",
      "v1Relation": "SAME_SEMANTIC_REPEAT",
      "v2Relation": "SAME_SEMANTIC_REPEAT",
      "changed": false
    },
    {
      "candidateId": "DOC-0123:P1056-1057",
      "v1Relation": "DISTINCT",
      "v2Relation": "DISTINCT",
      "changed": false
    },
    {
      "candidateId": "DOC-0123:P1665-1666",
      "v1Relation": "CONTINUATION_OF",
      "v2Relation": "CONTINUATION_OF",
      "changed": false
    },
    {
      "candidateId": "DOC-0123:P1664-1665",
      "v1Relation": "CONTINUATION_OF",
      "v2Relation": "CONTINUATION_OF",
      "changed": false
    },
    {
      "candidateId": "DOC-0123:P55-252",
      "v1Relation": "SAME_SEMANTIC_REPEAT",
      "v2Relation": "SAME_SEMANTIC_REPEAT",
      "changed": false
    },
    {
      "candidateId": "DOC-0123:P250-541",
      "v1Relation": "SAME_SEMANTIC_REPEAT",
      "v2Relation": "SAME_SEMANTIC_REPEAT",
      "changed": false
    },
    {
      "candidateId": "DOC-0123:P51-250",
      "v1Relation": "SAME_SEMANTIC_REPEAT",
      "v2Relation": "SAME_SEMANTIC_REPEAT",
      "changed": false
    },
    {
      "candidateId": "DOC-0123:P70-260",
      "v1Relation": "SAME_SEMANTIC_REPEAT",
      "v2Relation": "SAME_SEMANTIC_REPEAT",
      "changed": false
    },
    {
      "candidateId": "DOC-0123:P254-1719",
      "v1Relation": "SAME_SEMANTIC_REPEAT",
      "v2Relation": "SAME_SEMANTIC_REPEAT",
      "changed": false
    },
    {
      "candidateId": "DOC-0133:P359-360",
      "v1Relation": "SAME_SEMANTIC_REPEAT",
      "v2Relation": "SAME_SEMANTIC_REPEAT",
      "changed": false
    },
    {
      "candidateId": "DOC-0123:P1177-1178",
      "v1Relation": "DISTINCT",
      "v2Relation": "DISTINCT",
      "changed": false
    },
    {
      "candidateId": "DOC-0123:P1057-1058",
      "v1Relation": "DISTINCT",
      "v2Relation": "DISTINCT",
      "changed": false
    },
    {
      "candidateId": "DOC-0133:P339-340",
      "v1Relation": "SAME_SEMANTIC_REPEAT",
      "v2Relation": "SAME_SEMANTIC_REPEAT",
      "changed": false
    },
    {
      "candidateId": "DOC-0123:P1091-1092",
      "v1Relation": "SAME_SEMANTIC_REPEAT",
      "v2Relation": "SAME_SEMANTIC_REPEAT",
      "changed": false
    }
  ]
}
```

## Existing DEV Gold diagnostic

Gold was opened only after behavioral and source-strata artifacts were frozen. It is not an independent holdout and OLD/V1 outputs were not treated as Gold.

```json
{
  "schemaVersion": "a99-v4g-c-dev-gold-diagnostic-v1",
  "goldArtifact": "USER_REVIEWED_IDENTITY_GOLD_FROZEN",
  "latestGoldBindingsSha256": "09184c106d4c271e71dc4dea72a43eb215290d86ed83e03927d20ae6287fb7d4",
  "bindingGoldSha256": "122bf50fee967a2ef9a33aee3bc146d7df842d9e469126744fc0f68a017059b8",
  "goldReadAfterBehaviorFreeze": true,
  "independentGeneralizationClaim": false,
  "evaluable": 0,
  "correct": 0,
  "incorrect": 0,
  "accuracy": null,
  "rows": [],
  "note": "No exact frozen Gold endpoint pair occurs naturally in the 128-candidate sample; semantic correctness is unavailable."
}
```

No production rollout or prompt/model change was made.
