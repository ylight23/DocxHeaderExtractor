# DOC-0252 — canonical exhaustive heading occurrence authority

Status: **CANONICAL_EXHAUSTIVE_OCCURRENCE_READY**

## Current source authority

- Source: todo10_8/generated-docx/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.docx
- Format: DOCX
- Representation: HYBRID
- Source SHA-256: 4dda3c8ec8cd74e3a61503db0f8e9f168270d39036e3825441ab6167f9e16a77
- Raw source paragraphs: 244
- Non-empty source containers reviewed: 210
- Canonical exact heading occurrences: **40**

The current generated DOCX is the canonical source for this lane. The prior Key/PDF lineage is not used to decide occurrence truth.

## Source-only review

The accepted unit is **source container + exact heading span**. The review preserves repeated physical displays as separate occurrences and does not assign semantic identity, parentage, or level.

Excluded source material includes document metadata, embedded agenda metadata, agenda schedule item rows, page-number fragments, and prose containers that do not independently introduce a heading.

## Historical bridge — diagnostic only

- Historical rows read after current bindings were materialized: 27
- Exact cross-source bridges: 0
- Text-only diagnostic matches: 11
- Historical-only rows: 14
- Canonical-only occurrences: 25
- Ambiguous bridges: 2
- Prior semantic total observed post-freeze: 41

The historical occurrence rows and prior semantic total are non-binding diagnostics. They did not determine the accepted current-source spans. Historical level and parent fields were not read.

## Firewall

- historicalLevelRead: false
- historicalParentRead: false
- historicalHierarchyUsedForDecision: false
- historicalOccurrenceRowsUsedForDecision: false
- oldSemanticTotalUsedForDecision: false
- providerCalls: 0
- modelCalls: 0
- identityReviewed: false
- parentReviewed: false
- levelReviewed: false
- GoldMutationOutsideNewLane: false

## Validation

- Source-container coverage: PASS
- Exact span binding: PASS
- Duplicate occurrence refs: PASS
- Duplicate/overlapping source spans: PASS
- Deterministic rebuild hash: 1a55eaff45ec22dac6a5d2e46a111d49b9e7eec06aa100f972dc05d3a5502c1d
- Validator: **PASS**

No identity, hierarchy, or historical compatibility continuation was performed beyond the post-freeze bridge diagnostic. The next authorized phase is identity only after explicit review of this occurrence authority.
