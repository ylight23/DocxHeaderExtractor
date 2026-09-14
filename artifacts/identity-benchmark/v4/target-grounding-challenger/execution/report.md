# A99 V4G-B — frozen target-grounding challenger preflight

Status: **AWAITING_V4G_PROVIDER_EXECUTION_APPROVAL**

This phase intentionally stopped before transport. The V4G-B task requires a new explicit operator approval; earlier V4F-E approval is not reused.

## Frozen execution

- Expected HEAD: `6edb12d07f3d5f468705afca32862440c7fd4077`.
- Target-grounded V2 sample: **128/128**.
- Scheduled provider calls: **128**.
- Actual model/provider calls: **0/0**.
- Retries: **0**.
- Gold reads: **0**.
- Historical comparison: **true**; `temporalProviderDriftControlled=false`.
- The historical OLD and PROJECTED_V1 denominators below were reconstructed from frozen V4F-E attempts, parsed results, and persisted raw bodies before any V2 transport.

## Historical baseline

```json
{
  "schemaVersion": "a99-v4g-b-historical-baseline-v1",
  "source": "FROZEN_V4F_E_EXECUTION_ARTIFACTS",
  "executionManifestSha256": "d4d8e93e74b06d2f74d89a8ce8c94e8d713b4f61ec381904207612fa583e71e0",
  "parsedResultsSha256": "a3c1123c6aec43d49083c59ba10b663f5c95a29c144f2c3082086156c92c17f0",
  "temporalProviderDriftControlled": false,
  "goldReadCount": 0,
  "v4ebEvaluationReadCount": 0,
  "arms": [
    {
      "arm": "OLD",
      "scheduled": 128,
      "transportSuccess": 112,
      "rawAvailable": 111,
      "validatorEvaluable": 111,
      "targetMismatch": 2,
      "valid": 109,
      "otherInvalid": 0,
      "providerError": 16,
      "rawPersistenceFailure": 1
    },
    {
      "arm": "PROJECTED_V1",
      "scheduled": 128,
      "transportSuccess": 109,
      "rawAvailable": 108,
      "validatorEvaluable": 108,
      "targetMismatch": 45,
      "valid": 63,
      "otherInvalid": 0,
      "providerError": 19,
      "rawPersistenceFailure": 1
    }
  ]
}
```

## Integrity and firewall

The V4G-A sample IDs and request hashes were checked without persisting request bodies. Future transport must reconstruct each V4G request, verify candidate ID, byte length, SHA256, model, provider, and configuration before the single attempt. A mismatch must fail closed.

No Gold, IR-018..022, V4E-B labels, OLD outputs, or PROJECTED_V1 outputs were used to select the sample. V4F-E artifacts were read-only inputs and V4G-A artifacts were not modified.

This is not a semantic validation result. It is an execution-gated target-grounding experiment.
