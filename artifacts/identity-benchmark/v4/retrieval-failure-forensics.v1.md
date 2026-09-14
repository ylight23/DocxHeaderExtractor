# A99 Identity Retrieval v4A — Broad-retrieval failure forensics

Status: `READY_FOR_V4_RETRIEVAL_CHALLENGER_DESIGN`

This is a DEV-exposed, offline forensic artifact. It does not implement a retrieval rule, create a v4 shortlist, build requests, call a provider, or modify V1/V2/V3 artifacts.

## Frozen current behavior

- Generator: `HdsaDeterministicIdentityCandidateGenerator v1`.
- Active rules: sorted-array adjacency OR exact normalized-text equality.
- Broad candidates: `95,999`.
- `NORMALIZED_TEXT_AFFINITY` incidences: `89,476`.
- `MODEL_CALLS=0`; `PROVIDER_CALLS=0`.
- Gold/model output was not an input to candidate generation; target endpoints are DEV-exposed forensic references.

## Target traces

| Case | Relation | Adjacent rule | Text affinity | Broad candidate | Outcome |
|---|---|---|---|---|---|
| IR-019 | CONTINUATION_OF | DISTANCE_FAILED | TEXT_SIGNAL_FAILED | no | FILTERED_NO_ACTIVE_RULE |
| IR-020 | SAME_SEMANTIC_REPEAT | DISTANCE_FAILED | TEXT_SIGNAL_FAILED | no | FILTERED_NO_ACTIVE_RULE |
| IR-021 | SAME_SEMANTIC_REPEAT | DISTANCE_FAILED | ELIGIBLE | yes | EMITTED |

## Diagnosis

- IR-019 is a broad miss because its endpoints are non-adjacent and parser-preserved text is not exactly equal after the current normalization. The frozen source contains a terminal continuation marker and a shared `SESSION V` key, but the current generator has no such rule.
- IR-020 is a broad miss because its endpoints are non-adjacent and `(ITP)` makes exact normalized equality fail. The current generator has no bounded terminal-acronym or structural-key rule.
- IR-021 is retrieved because its endpoints are non-adjacent but their normalized texts are exactly equal, so `NORMALIZED_TEXT_AFFINITY` emits the pair.
- Narrowest architectural classification: `CURRENT_BROAD_RETRIEVAL_MULTI_FACTOR_GAP`.

## Current rule coverage

- {
  "rule": "ADJACENT_ORDER",
  "candidateCount": 6535,
  "documentsAffected": 3,
  "perDocumentCandidateCount": {
    "DOC-0123": 3213,
    "DOC-0133": 2353,
    "DOC-0252": 969
  },
  "maximumEndpointFanOut": 2,
  "canTheoreticallyRetrieveIr019": false,
  "canTheoreticallyRetrieveIr020": false,
  "canTheoreticallyRetrieveIr021": false,
  "interpretation": "Generator emits only adjacent sorted source-array indices; it is not a broad distance window."
}
- {
  "rule": "NORMALIZED_TEXT_AFFINITY",
  "candidateCount": 89476,
  "documentsAffected": 3,
  "perDocumentCandidateCount": {
    "DOC-0123": 700,
    "DOC-0133": 69764,
    "DOC-0252": 19012
  },
  "maximumEndpointFanOut": 273,
  "canTheoreticallyRetrieveIr019": false,
  "canTheoreticallyRetrieveIr020": false,
  "canTheoreticallyRetrieveIr021": true,
  "interpretation": "Generator emits only exact equality after Unicode normalization, whitespace collapse, and uppercasing."
}

## Counterfactual signals

The following generic source-only transforms were declared before corpus-wide measurement. Their counts are impact estimates only; no signal was selected and no production behavior changed.
- `FINAL_ACRONYM_PARENTHETICAL_EQUIVALENCE`: +10 deduplicated pairs; max added endpoint fan-out `3`.
- `SHARED_STRUCTURAL_SESSION_SECTION_KEY`: +67 deduplicated pairs; max added endpoint fan-out `5`.
- `CONTINUATION_MARKER_WITH_SHARED_STRUCTURAL_KEY`: +2 deduplicated pairs; max added endpoint fan-out `2`.

## Gate

`READY_FOR_V4_RETRIEVAL_CHALLENGER_DESIGN`

Next single recommendation: design one Gold-independent deterministic high-recall retrieval challenger using a bounded structural/lexical signal set, then freeze its broad candidate set before any verifier/provider call. Do not tune thresholds against these five DEV-exposed cases.
