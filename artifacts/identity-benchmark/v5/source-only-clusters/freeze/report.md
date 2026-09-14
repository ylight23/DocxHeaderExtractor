# A99 V5A — source-only semantic ambiguity cluster freeze

Status: **FROZEN_SOURCE_ONLY_AMBIGUITY_CLUSTERS**

V5A constructs high-recall ambiguity components from the frozen V4F-D source-only candidate sample. It does not assign semantic nodes, classify relations, merge occurrences, or read V4H Gold.

## Firewall

- Model calls: **0**.
- Provider calls: **0**.
- Gold reads: **0**.
- Known pair labels read/used: **false/false**.
- Semantic merge: **false**.
- Pair-label derivation: **false**.

## Freeze manifest

```json
{
  "schemaVersion": "a99-v5a-source-only-cluster-freeze-v1",
  "phase": "V5A_SOURCE_ONLY_SEMANTIC_CLUSTER_FREEZE",
  "status": "FROZEN_SOURCE_ONLY_AMBIGUITY_CLUSTERS",
  "input": "artifacts/identity-benchmark/v4/projected-verifier-experiment/sample.json",
  "inputSha256": "6982fbe2e897dc6ceff1101d68feb7c7de166d71e2d68bb9b2a41dbf3a7668c9",
  "inputArtifactKind": "a99_identity_benchmark_v4f_d_sample",
  "selectionSeed": "a99-v4f-d-source-only-stratified-sha256-v1",
  "clusterSeed": "a99-v5a-source-only-ambiguity-components-v1",
  "candidateCount": 128,
  "occurrenceCount": 226,
  "clusterCount": 102,
  "nonSingletonClusterCount": 102,
  "edgeCount": 128,
  "highRecallDiscovery": true,
  "modelCalls": 0,
  "providerCalls": 0,
  "goldReadCount": 0,
  "knownPairLabelsRead": false,
  "knownPairLabelsUsed": false,
  "semanticMergePerformed": false,
  "semanticNodeIdsAssigned": false,
  "pairLabelsDerived": false,
  "note": "Clusters are source-only ambiguity components. They authorize joint inspection only; they are not semantic identity groups or merge decisions."
}
```

## Cluster policy

Each connected component is an inspection cluster only. Edge reasons explain why source-owned evidence caused two occurrences to be co-inspected; they are not merge confidence and are not semantic labels. V5B may jointly reason over a cluster and document context, but must assign occurrence roles and semanticNodeId independently.

## Determinism

Clusters: **102**; source-only edges: **128**. Cluster IDs are SHA-256-derived from the sorted occurrence IDs and the fixed V5A seed.
