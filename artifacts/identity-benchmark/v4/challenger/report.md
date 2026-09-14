# A99 Identity Retrieval V4B — DEV-exposed structural/lexical challenger

Status: `READY_FOR_V4_RETRIEVAL_EVALUATION`

This artifact freezes a source-only broad-retrieval challenger. It is explicitly a `DEV_EXPOSED_CHALLENGER`, informed by `V3B_RETRIEVAL_FAILURE` and `V4A_RETRIEVAL_FORENSICS`; it makes no independent generalization claim.

## Contract

- `V4 broad = V3 broad UNION source-only structural/lexical candidates`.
- Structural key grammar: leading `SESSION`/`SECTION` plus Roman or decimal identifier; same key is retrieval evidence only.
- Acronym grammar: one terminal bounded uppercase parenthetical; arbitrary parentheticals are never stripped.
- Continuation grammar: terminal `Cont’d`, `Contd`, or `Continued`; base comparison or compatible structural key is retrieval evidence only.
- No weights, V3 pruning, request manifest, relation label, merge, Gold, or provider call.

## Scale

- V3 broad candidates: `95,999`.
- V4-only additions: `70`.
- V4 broad candidates: `96,069`.
- Added candidates by reason: `{"SHARED_STRUCTURAL_HEADING_KEY":67,"TERMINAL_ACRONYM_VARIANT":10,"EXPLICIT_CONTINUATION_VARIANT":2}`.
- Maximum added endpoint fan-out: `5`.

## Firewall

- `developmentStatus=DEV_EXPOSED_CHALLENGER`.
- `independentGeneralizationClaim=false`.
- `GoldReadCount=0`; `MODEL_CALLS=0`; `PROVIDER_CALLS=0`.
- Source order is canonicalized before generation; metamorphic self-tests pass.
- V1/V2/V3 artifacts are inputs only and remain unchanged.

## Gate

`READY_FOR_V4_RETRIEVAL_EVALUATION`

Next phase: evaluate this frozen V4 broad set against the already user-reviewed DEV Gold in a separate V4C task. Do not modify this freeze during evaluation.
