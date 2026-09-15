# Hierarchy proposal occurrence-universe authority audit

Status: **PROPOSALS_NOT_CANONICAL_EXHAUSTIVE**

This audit does not change parent edges, tree structure, proposal packets, or
Gold. It does not read historical level and made zero provider/model calls.

The proposal inputs are the 9 strict-gold-v4 heading-row packets. Those rows
are source-backed and materialized exactly once, but the strict heading row set
is not proven to be the canonical all-true-heading occurrence universe.
Canonical vNext semantic totals are not occurrence lists; where present,
exactOccurrenceFreeze is false. Missing occurrences were not inferred.

## Per-document result

- DOC-0001: proposal=7, strictHistoricalTotal=7, canonicalVNextSemanticTotal=7, classification=UNKNOWN, lineageMatch=True, canonicalExactFreeze=False, explicitUserApproval=True, recommendedLabel=HISTORICAL_STRICT_HIERARCHY_PROPOSAL
- DOC-0116: proposal=22, strictHistoricalTotal=22, canonicalVNextSemanticTotal=, classification=KNOWN_POSITIVE_SUBSET, lineageMatch=, canonicalExactFreeze=, explicitUserApproval=False, recommendedLabel=HISTORICAL_STRICT_HIERARCHY_PROPOSAL
- DOC-0122: proposal=126, strictHistoricalTotal=126, canonicalVNextSemanticTotal=, classification=KNOWN_POSITIVE_SUBSET, lineageMatch=, canonicalExactFreeze=, explicitUserApproval=False, recommendedLabel=HISTORICAL_STRICT_HIERARCHY_PROPOSAL
- DOC-0205: proposal=71, strictHistoricalTotal=71, canonicalVNextSemanticTotal=72, classification=KNOWN_POSITIVE_SUBSET, lineageMatch=True, canonicalExactFreeze=False, explicitUserApproval=False, recommendedLabel=HISTORICAL_STRICT_HIERARCHY_PROPOSAL
- DOC-0216: proposal=117, strictHistoricalTotal=117, canonicalVNextSemanticTotal=, classification=KNOWN_POSITIVE_SUBSET, lineageMatch=, canonicalExactFreeze=, explicitUserApproval=False, recommendedLabel=HISTORICAL_STRICT_HIERARCHY_PROPOSAL
- DOC-0243: proposal=102, strictHistoricalTotal=102, canonicalVNextSemanticTotal=, classification=KNOWN_POSITIVE_SUBSET, lineageMatch=, canonicalExactFreeze=, explicitUserApproval=False, recommendedLabel=HISTORICAL_STRICT_HIERARCHY_PROPOSAL
- DOC-0252: proposal=27, strictHistoricalTotal=27, canonicalVNextSemanticTotal=41, classification=SOURCE_LINEAGE_MISMATCH, lineageMatch=False, canonicalExactFreeze=False, explicitUserApproval=False, recommendedLabel=HISTORICAL_STRICT_HIERARCHY_PROPOSAL
- DOC-0256: proposal=24, strictHistoricalTotal=24, canonicalVNextSemanticTotal=34, classification=SOURCE_LINEAGE_MISMATCH, lineageMatch=False, canonicalExactFreeze=False, explicitUserApproval=False, recommendedLabel=HISTORICAL_STRICT_HIERARCHY_PROPOSAL
- DOC-0258: proposal=24, strictHistoricalTotal=24, canonicalVNextSemanticTotal=37, classification=UNKNOWN, lineageMatch=True, canonicalExactFreeze=False, explicitUserApproval=False, recommendedLabel=HISTORICAL_STRICT_HIERARCHY_PROPOSAL

## Authority conclusion

No document passes the canonical-exhaustive occurrence gate in this audit.
The five previously valid trees remain source-backed proposals only. Their
safe authority label is HISTORICAL_STRICT_HIERARCHY_PROPOSAL. DOC-0116,
DOC-0122, and DOC-0216 remain REVIEW_REQUIRED_CANONICAL_HIERARCHY for their
existing parent-context ambiguity. DOC-0001 has explicit user approval in the
conversation provenance, but its canonical occurrence exhaustiveness is still
unproven; the existing approved artifact is not mutated here.

Historical-level comparison is blocked until a canonical-exhaustive,
source-compatible occurrence universe is independently established.
