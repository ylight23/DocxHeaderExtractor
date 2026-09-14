# A99 V4G-B — target-grounding challenger

Status: **RESPONSE_FREEZE_COMPLETE**

Classification: **TARGET_GROUNDING_REPAIRED_ON_DEV_SAMPLE**

Calls: **128/128**. Gold reads: **0**. Semantic accuracy: **not measured**.

Target mismatch: **0/128 scheduled**, **0/95 evaluable**. Valid: **95/128 scheduled**, **95/95 evaluable**. Provider errors: **33**. Other invalid: **0**.

## Historical paired diagnostic

This is a post-freeze, Gold-free comparison against frozen V4F-E PROJECTED_V1 artifacts. It was not used for sampling, request construction, or classification.

```json
{
  "schemaVersion": "a99-v4g-b-historical-paired-analysis-v1",
  "status": "COMPLETE_AFTER_RESPONSE_FREEZE",
  "comparisonArm": "PROJECTED_V1",
  "source": "FROZEN_V4F_E_EXECUTION_ARTIFACTS",
  "goldReadCount": 0,
  "historicalOutputsUsedForSelection": false,
  "semanticAccuracyMeasured": false,
  "scheduledPairs": 128,
  "v1Evaluable": 108,
  "v2Evaluable": 95,
  "bothEvaluable": 77,
  "v1TargetMismatch": 45,
  "v2TargetMismatch": 0,
  "repairedTargetGrounding": 28,
  "regressedTargetGrounding": 0,
  "unchangedTargetMismatch": 0,
  "unavailable": 51,
  "pairs": [
    {
      "candidateId": "DOC-0123:P01-42",
      "historicalStatus": "INVALID_SCHEMA",
      "historicalRawPersisted": true,
      "historicalRejectionReason": "TARGET_PAIR_MISMATCH",
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "REPAIRED_TARGET_GROUNDING"
    },
    {
      "candidateId": "DOC-0123:P05-06",
      "historicalStatus": "INVALID_SCHEMA",
      "historicalRawPersisted": true,
      "historicalRejectionReason": "TARGET_PAIR_MISMATCH",
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "REPAIRED_TARGET_GROUNDING"
    },
    {
      "candidateId": "DOC-0123:P07-08",
      "historicalStatus": "INVALID_SCHEMA",
      "historicalRawPersisted": true,
      "historicalRejectionReason": "TARGET_PAIR_MISMATCH",
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "REPAIRED_TARGET_GROUNDING"
    },
    {
      "candidateId": "DOC-0123:P1006-1007",
      "historicalStatus": "INVALID_SCHEMA",
      "historicalRawPersisted": true,
      "historicalRejectionReason": "TARGET_PAIR_MISMATCH",
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "REPAIRED_TARGET_GROUNDING"
    },
    {
      "candidateId": "DOC-0123:P1035-1036",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "GROUNDED_BOTH"
    },
    {
      "candidateId": "DOC-0123:P1036-1037",
      "historicalStatus": "RAW_PERSISTENCE_FAILURE",
      "historicalRawPersisted": false,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "UNAVAILABLE"
    },
    {
      "candidateId": "DOC-0123:P1056-1057",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "GROUNDED_BOTH"
    },
    {
      "candidateId": "DOC-0123:P1057-1058",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "GROUNDED_BOTH"
    },
    {
      "candidateId": "DOC-0123:P1087-1088",
      "historicalStatus": "INVALID_SCHEMA",
      "historicalRawPersisted": true,
      "historicalRejectionReason": "TARGET_PAIR_MISMATCH",
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "REPAIRED_TARGET_GROUNDING"
    },
    {
      "candidateId": "DOC-0123:P1091-1092",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "GROUNDED_BOTH"
    },
    {
      "candidateId": "DOC-0123:P1093-1094",
      "historicalStatus": "INVALID_SCHEMA",
      "historicalRawPersisted": true,
      "historicalRejectionReason": "TARGET_PAIR_MISMATCH",
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "REPAIRED_TARGET_GROUNDING"
    },
    {
      "candidateId": "DOC-0123:P1094-1095",
      "historicalStatus": "RAW_PERSISTENCE_FAILURE",
      "historicalRawPersisted": false,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "UNAVAILABLE"
    },
    {
      "candidateId": "DOC-0123:P1160-1161",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "GROUNDED_BOTH"
    },
    {
      "candidateId": "DOC-0123:P1161-1162",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "GROUNDED_BOTH"
    },
    {
      "candidateId": "DOC-0123:P1177-1178",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "GROUNDED_BOTH"
    },
    {
      "candidateId": "DOC-0123:P1178-1179",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "GROUNDED_BOTH"
    },
    {
      "candidateId": "DOC-0123:P1228-1229",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "PROVIDER_ERROR",
      "v2RejectionReason": null,
      "outcome": "UNAVAILABLE"
    },
    {
      "candidateId": "DOC-0123:P132-249",
      "historicalStatus": "INVALID_SCHEMA",
      "historicalRawPersisted": true,
      "historicalRejectionReason": "TARGET_PAIR_MISMATCH",
      "v2Status": "PROVIDER_ERROR",
      "v2RejectionReason": null,
      "outcome": "UNAVAILABLE"
    },
    {
      "candidateId": "DOC-0123:P1334-1338",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "PROVIDER_ERROR",
      "v2RejectionReason": null,
      "outcome": "UNAVAILABLE"
    },
    {
      "candidateId": "DOC-0123:P1425-1973",
      "historicalStatus": "INVALID_SCHEMA",
      "historicalRawPersisted": true,
      "historicalRejectionReason": "TARGET_PAIR_MISMATCH",
      "v2Status": "PROVIDER_ERROR",
      "v2RejectionReason": null,
      "outcome": "UNAVAILABLE"
    },
    {
      "candidateId": "DOC-0123:P1482-1485",
      "historicalStatus": "INVALID_SCHEMA",
      "historicalRawPersisted": true,
      "historicalRejectionReason": "TARGET_PAIR_MISMATCH",
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "REPAIRED_TARGET_GROUNDING"
    },
    {
      "candidateId": "DOC-0123:P1533-1534",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "GROUNDED_BOTH"
    },
    {
      "candidateId": "DOC-0123:P1540-1591",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "GROUNDED_BOTH"
    },
    {
      "candidateId": "DOC-0123:P1565-1566",
      "historicalStatus": "RAW_PERSISTENCE_FAILURE",
      "historicalRawPersisted": false,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "UNAVAILABLE"
    },
    {
      "candidateId": "DOC-0123:P1592-1593",
      "historicalStatus": "INVALID_SCHEMA",
      "historicalRawPersisted": true,
      "historicalRejectionReason": "TARGET_PAIR_MISMATCH",
      "v2Status": "PROVIDER_ERROR",
      "v2RejectionReason": null,
      "outcome": "UNAVAILABLE"
    },
    {
      "candidateId": "DOC-0123:P1603-1610",
      "historicalStatus": "RAW_PERSISTENCE_FAILURE",
      "historicalRawPersisted": false,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "UNAVAILABLE"
    },
    {
      "candidateId": "DOC-0123:P1664-1665",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "GROUNDED_BOTH"
    },
    {
      "candidateId": "DOC-0123:P1665-1666",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "GROUNDED_BOTH"
    },
    {
      "candidateId": "DOC-0123:P1761-1762",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "GROUNDED_BOTH"
    },
    {
      "candidateId": "DOC-0123:P1818-2556",
      "historicalStatus": "INVALID_SCHEMA",
      "historicalRawPersisted": true,
      "historicalRejectionReason": "TARGET_PAIR_MISMATCH",
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "REPAIRED_TARGET_GROUNDING"
    },
    {
      "candidateId": "DOC-0123:P2008-2009",
      "historicalStatus": "RAW_PERSISTENCE_FAILURE",
      "historicalRawPersisted": false,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "UNAVAILABLE"
    },
    {
      "candidateId": "DOC-0123:P2048-2050",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "GROUNDED_BOTH"
    },
    {
      "candidateId": "DOC-0123:P2101-2103",
      "historicalStatus": "RAW_PERSISTENCE_FAILURE",
      "historicalRawPersisted": false,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "UNAVAILABLE"
    },
    {
      "candidateId": "DOC-0123:P250-541",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "GROUNDED_BOTH"
    },
    {
      "candidateId": "DOC-0123:P254-1719",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "GROUNDED_BOTH"
    },
    {
      "candidateId": "DOC-0123:P2742-2754",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "GROUNDED_BOTH"
    },
    {
      "candidateId": "DOC-0123:P2843-2844",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "GROUNDED_BOTH"
    },
    {
      "candidateId": "DOC-0123:P3005-3006",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "GROUNDED_BOTH"
    },
    {
      "candidateId": "DOC-0123:P3010-3015",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "GROUNDED_BOTH"
    },
    {
      "candidateId": "DOC-0123:P3013-3018",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "PROVIDER_ERROR",
      "v2RejectionReason": null,
      "outcome": "UNAVAILABLE"
    },
    {
      "candidateId": "DOC-0123:P3086-3088",
      "historicalStatus": "RAW_PERSISTENCE_FAILURE",
      "historicalRawPersisted": false,
      "historicalRejectionReason": null,
      "v2Status": "PROVIDER_ERROR",
      "v2RejectionReason": null,
      "outcome": "UNAVAILABLE"
    },
    {
      "candidateId": "DOC-0123:P3092-3097",
      "historicalStatus": "INVALID_SCHEMA",
      "historicalRawPersisted": true,
      "historicalRejectionReason": "TARGET_PAIR_MISMATCH",
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "REPAIRED_TARGET_GROUNDING"
    },
    {
      "candidateId": "DOC-0123:P3151-3182",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "GROUNDED_BOTH"
    },
    {
      "candidateId": "DOC-0123:P348-354",
      "historicalStatus": "INVALID_SCHEMA",
      "historicalRawPersisted": true,
      "historicalRejectionReason": "TARGET_PAIR_MISMATCH",
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "REPAIRED_TARGET_GROUNDING"
    },
    {
      "candidateId": "DOC-0123:P49-132",
      "historicalStatus": "RAW_PERSISTENCE_FAILURE",
      "historicalRawPersisted": false,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "UNAVAILABLE"
    },
    {
      "candidateId": "DOC-0123:P51-250",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "GROUNDED_BOTH"
    },
    {
      "candidateId": "DOC-0123:P53-251",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "GROUNDED_BOTH"
    },
    {
      "candidateId": "DOC-0123:P55-252",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "GROUNDED_BOTH"
    },
    {
      "candidateId": "DOC-0123:P552-554",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "GROUNDED_BOTH"
    },
    {
      "candidateId": "DOC-0123:P57-253",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "GROUNDED_BOTH"
    },
    {
      "candidateId": "DOC-0123:P59-254",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "GROUNDED_BOTH"
    },
    {
      "candidateId": "DOC-0123:P602-1271",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "GROUNDED_BOTH"
    },
    {
      "candidateId": "DOC-0123:P66-1995",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "GROUNDED_BOTH"
    },
    {
      "candidateId": "DOC-0123:P68-2007",
      "historicalStatus": "INVALID_SCHEMA",
      "historicalRawPersisted": true,
      "historicalRejectionReason": "TARGET_PAIR_MISMATCH",
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "REPAIRED_TARGET_GROUNDING"
    },
    {
      "candidateId": "DOC-0123:P70-260",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "GROUNDED_BOTH"
    },
    {
      "candidateId": "DOC-0123:P783-784",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "GROUNDED_BOTH"
    },
    {
      "candidateId": "DOC-0123:P962-1000",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "GROUNDED_BOTH"
    },
    {
      "candidateId": "DOC-0123:V4P0049-0119",
      "historicalStatus": "INVALID_SCHEMA",
      "historicalRawPersisted": true,
      "historicalRejectionReason": "TARGET_PAIR_MISMATCH",
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "REPAIRED_TARGET_GROUNDING"
    },
    {
      "candidateId": "DOC-0123:V4P0057-0123",
      "historicalStatus": "INVALID_SCHEMA",
      "historicalRawPersisted": true,
      "historicalRejectionReason": "TARGET_PAIR_MISMATCH",
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "REPAIRED_TARGET_GROUNDING"
    },
    {
      "candidateId": "DOC-0123:V4P0062-0126",
      "historicalStatus": "RAW_PERSISTENCE_FAILURE",
      "historicalRawPersisted": false,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "UNAVAILABLE"
    },
    {
      "candidateId": "DOC-0123:V4P0066-0258",
      "historicalStatus": "INVALID_SCHEMA",
      "historicalRawPersisted": true,
      "historicalRejectionReason": "TARGET_PAIR_MISMATCH",
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "REPAIRED_TARGET_GROUNDING"
    },
    {
      "candidateId": "DOC-0123:V4P0068-0259",
      "historicalStatus": "INVALID_SCHEMA",
      "historicalRawPersisted": true,
      "historicalRejectionReason": "TARGET_PAIR_MISMATCH",
      "v2Status": "PROVIDER_ERROR",
      "v2RejectionReason": null,
      "outcome": "UNAVAILABLE"
    },
    {
      "candidateId": "DOC-0123:V4P0070-0130",
      "historicalStatus": "INVALID_SCHEMA",
      "historicalRawPersisted": true,
      "historicalRejectionReason": "TARGET_PAIR_MISMATCH",
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "REPAIRED_TARGET_GROUNDING"
    },
    {
      "candidateId": "DOC-0123:V4P0120-0250",
      "historicalStatus": "INVALID_SCHEMA",
      "historicalRawPersisted": true,
      "historicalRejectionReason": "TARGET_PAIR_MISMATCH",
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "REPAIRED_TARGET_GROUNDING"
    },
    {
      "candidateId": "DOC-0123:V4P0128-0258",
      "historicalStatus": "INVALID_SCHEMA",
      "historicalRawPersisted": true,
      "historicalRejectionReason": "TARGET_PAIR_MISMATCH",
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "REPAIRED_TARGET_GROUNDING"
    },
    {
      "candidateId": "DOC-0123:V4P0132-0200",
      "historicalStatus": "INVALID_SCHEMA",
      "historicalRawPersisted": true,
      "historicalRejectionReason": "TARGET_PAIR_MISMATCH",
      "v2Status": "PROVIDER_ERROR",
      "v2RejectionReason": null,
      "outcome": "UNAVAILABLE"
    },
    {
      "candidateId": "DOC-0123:V4P0200-0249",
      "historicalStatus": "INVALID_SCHEMA",
      "historicalRawPersisted": true,
      "historicalRejectionReason": "TARGET_PAIR_MISMATCH",
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "REPAIRED_TARGET_GROUNDING"
    },
    {
      "candidateId": "DOC-0123:V4P0258-1995",
      "historicalStatus": "INVALID_SCHEMA",
      "historicalRawPersisted": true,
      "historicalRejectionReason": "TARGET_PAIR_MISMATCH",
      "v2Status": "PROVIDER_ERROR",
      "v2RejectionReason": null,
      "outcome": "UNAVAILABLE"
    },
    {
      "candidateId": "DOC-0123:V4P0259-2007",
      "historicalStatus": "INVALID_SCHEMA",
      "historicalRawPersisted": true,
      "historicalRejectionReason": "TARGET_PAIR_MISMATCH",
      "v2Status": "PROVIDER_ERROR",
      "v2RejectionReason": null,
      "outcome": "UNAVAILABLE"
    },
    {
      "candidateId": "DOC-0123:V4P1271-1341",
      "historicalStatus": "INVALID_SCHEMA",
      "historicalRawPersisted": true,
      "historicalRejectionReason": "TARGET_PAIR_MISMATCH",
      "v2Status": "PROVIDER_ERROR",
      "v2RejectionReason": null,
      "outcome": "UNAVAILABLE"
    },
    {
      "candidateId": "DOC-0133:P1069-1070",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "PROVIDER_ERROR",
      "v2RejectionReason": null,
      "outcome": "UNAVAILABLE"
    },
    {
      "candidateId": "DOC-0133:P118-136",
      "historicalStatus": "INVALID_SCHEMA",
      "historicalRawPersisted": true,
      "historicalRejectionReason": "TARGET_PAIR_MISMATCH",
      "v2Status": "PROVIDER_ERROR",
      "v2RejectionReason": null,
      "outcome": "UNAVAILABLE"
    },
    {
      "candidateId": "DOC-0133:P140-157",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "GROUNDED_BOTH"
    },
    {
      "candidateId": "DOC-0133:P1467-1468",
      "historicalStatus": "INVALID_SCHEMA",
      "historicalRawPersisted": true,
      "historicalRejectionReason": "TARGET_PAIR_MISMATCH",
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "REPAIRED_TARGET_GROUNDING"
    },
    {
      "candidateId": "DOC-0133:P1611-1618",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "GROUNDED_BOTH"
    },
    {
      "candidateId": "DOC-0133:P1736-1753",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "GROUNDED_BOTH"
    },
    {
      "candidateId": "DOC-0133:P1803-1805",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "GROUNDED_BOTH"
    },
    {
      "candidateId": "DOC-0133:P1892-1908",
      "historicalStatus": "RAW_PERSISTENCE_FAILURE",
      "historicalRawPersisted": false,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "UNAVAILABLE"
    },
    {
      "candidateId": "DOC-0133:P1982-2046",
      "historicalStatus": "RAW_PERSISTENCE_FAILURE",
      "historicalRawPersisted": false,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "UNAVAILABLE"
    },
    {
      "candidateId": "DOC-0133:P2227-2229",
      "historicalStatus": "RAW_PERSISTENCE_FAILURE",
      "historicalRawPersisted": false,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "UNAVAILABLE"
    },
    {
      "candidateId": "DOC-0133:P2278-2294",
      "historicalStatus": "RAW_PERSISTENCE_FAILURE",
      "historicalRawPersisted": false,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "UNAVAILABLE"
    },
    {
      "candidateId": "DOC-0133:P337-338",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "PROVIDER_ERROR",
      "v2RejectionReason": null,
      "outcome": "UNAVAILABLE"
    },
    {
      "candidateId": "DOC-0133:P339-340",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "GROUNDED_BOTH"
    },
    {
      "candidateId": "DOC-0133:P359-360",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "GROUNDED_BOTH"
    },
    {
      "candidateId": "DOC-0133:P36-41",
      "historicalStatus": "INVALID_SCHEMA",
      "historicalRawPersisted": true,
      "historicalRejectionReason": "TARGET_PAIR_MISMATCH",
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "REPAIRED_TARGET_GROUNDING"
    },
    {
      "candidateId": "DOC-0133:P561-562",
      "historicalStatus": "INVALID_SCHEMA",
      "historicalRawPersisted": true,
      "historicalRejectionReason": "TARGET_PAIR_MISMATCH",
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "REPAIRED_TARGET_GROUNDING"
    },
    {
      "candidateId": "DOC-0133:P827-841",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "PROVIDER_ERROR",
      "v2RejectionReason": null,
      "outcome": "UNAVAILABLE"
    },
    {
      "candidateId": "DOC-0133:P855-856",
      "historicalStatus": "INVALID_SCHEMA",
      "historicalRawPersisted": true,
      "historicalRejectionReason": "TARGET_PAIR_MISMATCH",
      "v2Status": "PROVIDER_ERROR",
      "v2RejectionReason": null,
      "outcome": "UNAVAILABLE"
    },
    {
      "candidateId": "DOC-0133:P927-930",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "PROVIDER_ERROR",
      "v2RejectionReason": null,
      "outcome": "UNAVAILABLE"
    },
    {
      "candidateId": "DOC-0133:P933-934",
      "historicalStatus": "INVALID_SCHEMA",
      "historicalRawPersisted": true,
      "historicalRejectionReason": "TARGET_PAIR_MISMATCH",
      "v2Status": "PROVIDER_ERROR",
      "v2RejectionReason": null,
      "outcome": "UNAVAILABLE"
    },
    {
      "candidateId": "DOC-0133:P937-938",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "PROVIDER_ERROR",
      "v2RejectionReason": null,
      "outcome": "UNAVAILABLE"
    },
    {
      "candidateId": "DOC-0133:P960-962",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "PROVIDER_ERROR",
      "v2RejectionReason": null,
      "outcome": "UNAVAILABLE"
    },
    {
      "candidateId": "DOC-0133:P966-968",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "PROVIDER_ERROR",
      "v2RejectionReason": null,
      "outcome": "UNAVAILABLE"
    },
    {
      "candidateId": "DOC-0133:P99-316",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "PROVIDER_ERROR",
      "v2RejectionReason": null,
      "outcome": "UNAVAILABLE"
    },
    {
      "candidateId": "DOC-0133:V4P0005-0935",
      "historicalStatus": "INVALID_SCHEMA",
      "historicalRawPersisted": true,
      "historicalRejectionReason": "TARGET_PAIR_MISMATCH",
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "REPAIRED_TARGET_GROUNDING"
    },
    {
      "candidateId": "DOC-0133:V4P0011-0013",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "PROVIDER_ERROR",
      "v2RejectionReason": null,
      "outcome": "UNAVAILABLE"
    },
    {
      "candidateId": "DOC-0133:V4P0013-0034",
      "historicalStatus": "INVALID_SCHEMA",
      "historicalRawPersisted": true,
      "historicalRejectionReason": "TARGET_PAIR_MISMATCH",
      "v2Status": "PROVIDER_ERROR",
      "v2RejectionReason": null,
      "outcome": "UNAVAILABLE"
    },
    {
      "candidateId": "DOC-0133:V4P0015-0616",
      "historicalStatus": "INVALID_SCHEMA",
      "historicalRawPersisted": true,
      "historicalRejectionReason": "TARGET_PAIR_MISMATCH",
      "v2Status": "PROVIDER_ERROR",
      "v2RejectionReason": null,
      "outcome": "UNAVAILABLE"
    },
    {
      "candidateId": "DOC-0133:V4P0017-0921",
      "historicalStatus": "INVALID_SCHEMA",
      "historicalRawPersisted": true,
      "historicalRejectionReason": "TARGET_PAIR_MISMATCH",
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "REPAIRED_TARGET_GROUNDING"
    },
    {
      "candidateId": "DOC-0252:P107-235",
      "historicalStatus": "RAW_PERSISTENCE_FAILURE",
      "historicalRawPersisted": false,
      "historicalRejectionReason": null,
      "v2Status": "PROVIDER_ERROR",
      "v2RejectionReason": null,
      "outcome": "UNAVAILABLE"
    },
    {
      "candidateId": "DOC-0252:P16-31",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "GROUNDED_BOTH"
    },
    {
      "candidateId": "DOC-0252:P162-168",
      "historicalStatus": "RAW_PERSISTENCE_FAILURE",
      "historicalRawPersisted": false,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "UNAVAILABLE"
    },
    {
      "candidateId": "DOC-0252:P188-189",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "GROUNDED_BOTH"
    },
    {
      "candidateId": "DOC-0252:P194-731",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "PROVIDER_ERROR",
      "v2RejectionReason": null,
      "outcome": "UNAVAILABLE"
    },
    {
      "candidateId": "DOC-0252:P199-325",
      "historicalStatus": "RAW_PERSISTENCE_FAILURE",
      "historicalRawPersisted": false,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "UNAVAILABLE"
    },
    {
      "candidateId": "DOC-0252:P22-26",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "GROUNDED_BOTH"
    },
    {
      "candidateId": "DOC-0252:P224-226",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "GROUNDED_BOTH"
    },
    {
      "candidateId": "DOC-0252:P311-312",
      "historicalStatus": "RAW_PERSISTENCE_FAILURE",
      "historicalRawPersisted": false,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "UNAVAILABLE"
    },
    {
      "candidateId": "DOC-0252:P393-394",
      "historicalStatus": "INVALID_SCHEMA",
      "historicalRawPersisted": true,
      "historicalRejectionReason": "TARGET_PAIR_MISMATCH",
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "REPAIRED_TARGET_GROUNDING"
    },
    {
      "candidateId": "DOC-0252:P396-402",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "GROUNDED_BOTH"
    },
    {
      "candidateId": "DOC-0252:P418-426",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "GROUNDED_BOTH"
    },
    {
      "candidateId": "DOC-0252:P434-439",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "GROUNDED_BOTH"
    },
    {
      "candidateId": "DOC-0252:P449-453",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "GROUNDED_BOTH"
    },
    {
      "candidateId": "DOC-0252:P508-512",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "GROUNDED_BOTH"
    },
    {
      "candidateId": "DOC-0252:P539-595",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "GROUNDED_BOTH"
    },
    {
      "candidateId": "DOC-0252:P63-83",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "PROVIDER_ERROR",
      "v2RejectionReason": null,
      "outcome": "UNAVAILABLE"
    },
    {
      "candidateId": "DOC-0252:P750-751",
      "historicalStatus": "VALID",
      "historicalRawPersisted": true,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "GROUNDED_BOTH"
    },
    {
      "candidateId": "DOC-0252:P898-899",
      "historicalStatus": "RAW_PERSISTENCE_FAILURE",
      "historicalRawPersisted": false,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "UNAVAILABLE"
    },
    {
      "candidateId": "DOC-0252:P955-957",
      "historicalStatus": "RAW_PERSISTENCE_FAILURE",
      "historicalRawPersisted": false,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "UNAVAILABLE"
    },
    {
      "candidateId": "DOC-0252:V4P0006-0042",
      "historicalStatus": "INVALID_SCHEMA",
      "historicalRawPersisted": true,
      "historicalRejectionReason": "TARGET_PAIR_MISMATCH",
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "REPAIRED_TARGET_GROUNDING"
    },
    {
      "candidateId": "DOC-0252:V4P0042-0253",
      "historicalStatus": "RAW_PERSISTENCE_FAILURE",
      "historicalRawPersisted": false,
      "historicalRejectionReason": null,
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "UNAVAILABLE"
    },
    {
      "candidateId": "DOC-0252:V4P0435-0883",
      "historicalStatus": "INVALID_SCHEMA",
      "historicalRawPersisted": true,
      "historicalRejectionReason": "TARGET_PAIR_MISMATCH",
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "REPAIRED_TARGET_GROUNDING"
    },
    {
      "candidateId": "DOC-0252:V4P0544-0888",
      "historicalStatus": "INVALID_SCHEMA",
      "historicalRawPersisted": true,
      "historicalRejectionReason": "TARGET_PAIR_MISMATCH",
      "v2Status": "PROVIDER_ERROR",
      "v2RejectionReason": null,
      "outcome": "UNAVAILABLE"
    },
    {
      "candidateId": "DOC-0252:V4P0544-0893",
      "historicalStatus": "INVALID_SCHEMA",
      "historicalRawPersisted": true,
      "historicalRejectionReason": "TARGET_PAIR_MISMATCH",
      "v2Status": "PROVIDER_ERROR",
      "v2RejectionReason": null,
      "outcome": "UNAVAILABLE"
    },
    {
      "candidateId": "DOC-0252:V4P0851-0904",
      "historicalStatus": "INVALID_SCHEMA",
      "historicalRawPersisted": true,
      "historicalRejectionReason": "TARGET_PAIR_MISMATCH",
      "v2Status": "PROVIDER_ERROR",
      "v2RejectionReason": null,
      "outcome": "UNAVAILABLE"
    },
    {
      "candidateId": "DOC-0252:V4P0872-0875",
      "historicalStatus": "INVALID_SCHEMA",
      "historicalRawPersisted": true,
      "historicalRejectionReason": "TARGET_PAIR_MISMATCH",
      "v2Status": "PROVIDER_ERROR",
      "v2RejectionReason": null,
      "outcome": "UNAVAILABLE"
    },
    {
      "candidateId": "DOC-0252:V4P0875-0878",
      "historicalStatus": "INVALID_SCHEMA",
      "historicalRawPersisted": true,
      "historicalRejectionReason": "TARGET_PAIR_MISMATCH",
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "REPAIRED_TARGET_GROUNDING"
    },
    {
      "candidateId": "DOC-0252:V4P0888-0893",
      "historicalStatus": "INVALID_SCHEMA",
      "historicalRawPersisted": true,
      "historicalRejectionReason": "TARGET_PAIR_MISMATCH",
      "v2Status": "VALID",
      "v2RejectionReason": null,
      "outcome": "REPAIRED_TARGET_GROUNDING"
    }
  ]
}
```
