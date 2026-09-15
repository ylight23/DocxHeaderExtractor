# Strict heading parent review contract audit

Authoritative preflight: e26f28e
Provider/model calls: 0
Historical level read: false
Human adjudication started: false

## Status

**BLOCKED_ON_REVIEW_CONTRACT_ABSTRACTION**

## Finding

The v1 packets contain headingOccurrenceId, source/provenance fields,
semanticNodeId, parentSemanticNodeId, isRoot, and an
occurrenceRelation field. However, those node/parent facts are embedded in
each occurrence annotation. The packet has no canonical semanticNodes[]
universe and no node-level parentEdges[]/nodeRelations[] authority.

Therefore it is not safe to begin the 520-occurrence review as canonical
semantic-node hierarchy Gold. It is a review packet with node fields, but its
contract is not yet a node-level hierarchy authority.

The revised contract in review-contract-v2.json separates:

1. occurrence -> semanticNodeId and occurrenceRole;
2. semanticNodeId -> parentSemanticNodeId or ROOT;
3. deterministic validation and level derivation.

No parent has been assigned by this audit.
