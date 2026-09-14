# A99 V5B — semantic node induction preflight

Status: **READY_FOR_PROVIDER_EXECUTION**. This checkpoint prepares requests only; it does not execute a model/provider or open Gold.

## Contract boundary

The model is asked to jointly assign occurrences to cluster-local semantic nodes and occurrence roles. It is not asked for pair labels, hierarchy, levels, promotion decisions, or global node IDs.

## Firewall

- Model/provider calls: **0**.
- Gold reads: **0**.
- Known pair labels included: **false**.
- Hierarchy requested: **false**.
- Prediction freeze: **false**.

## Manifest

```json
{
  "schemaVersion": "a99-v5b-semantic-node-induction-preflight-v1",
  "phase": "V5B_GLOBAL_CLUSTER_SEMANTIC_NODE_INDUCTION",
  "status": "READY_FOR_PROVIDER_EXECUTION",
  "clusterFreeze": "artifacts/identity-benchmark/v5/source-only-clusters/freeze/clusters.json",
  "clusterFreezeSha256": "83d52bf6c661b8c0ff51716f7675b248fd7b2f6041eb10e8f792b7e0215df2ff",
  "clusterManifestSha256": "776cf9b55840b490bdebfddf5ea6ffb854d6b5b236b5fed93c31bea80e8e8361",
  "sourceCatalog": "artifacts/identity-benchmark/v2/source-catalog.json",
  "sourceCatalogSha256": "ccd43fccd9b6948baf252a4e4429ac20934b10a75d78896c45915b98de02f8eb",
  "requestSchemaVersion": "a99-v5b-global-cluster-semantic-node-induction-v1",
  "clusterCount": 102,
  "occurrenceCount": 226,
  "requestCount": 102,
  "localContext": "2_source_occurrences_each_side",
  "modelCalls": 0,
  "providerCalls": 0,
  "goldReadCount": 0,
  "goldDerivedInput": false,
  "knownPairLabelsRead": false,
  "knownPairLabelsIncluded": false,
  "hierarchyRequested": false,
  "rawResponsesPersisted": false,
  "parsedResponsesPersisted": false,
  "predictionsFrozen": false,
  "note": "Offline V5B request preflight only. Requests contain source-owned cluster evidence and context; they contain no pair labels, Gold, hierarchy, or promotion decisions."
}
```

Prepared requests: **102**. Each request hash is indexed in `request-index.json`; raw and parsed response artifacts are intentionally absent until an authorized provider campaign.
