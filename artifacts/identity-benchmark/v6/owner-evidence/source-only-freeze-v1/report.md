# A99 V6B — source-only structural owner evidence freeze

This phase freezes parser-owned container and context evidence only. It does not assign semantic structural owners, does not read Gold, and does not call a model/provider. `sourceContainerIdentity` is an evidence key, not an autonomous-document-unit decision.

{
  "packetCount": 226,
  "scopeGroupCount": 22,
  "documentCounts": [
    {
      "documentId": "DOC-0123",
      "occurrences": 116
    },
    {
      "documentId": "DOC-0133",
      "occurrences": 57
    },
    {
      "documentId": "DOC-0252",
      "occurrences": 53
    }
  ],
  "sourceUnitKinds": [
    {
      "kind": "DOCUMENT_CONTAINER",
      "occurrences": 55
    },
    {
      "kind": "TABLE_CONTAINER",
      "occurrences": 61
    },
    {
      "kind": "TEXT_STREAM",
      "occurrences": 110
    }
  ]
}
