# A99 Identity Promotion Benchmark v1

Status: `BLOCKED_ON_IDENTITY_GOLD_BINDING`

This is a prepared benchmark lane, not an executed accuracy result.

## Authority and firewall

The semantic decisions are recorded in `artifacts/identity-gold/semantic-identity-gold.user-reviewed.v2.json` with authority `USER_REVIEWED_IDENTITY_GOLD_FROZEN`. They are not independent A/B Gold. All five listed decisions have semantic confidence `HIGH`, but exact binding is intentionally separate and currently incomplete.

No model output, candidate output, DOC-0205 forensic label, or historical hierarchy output contributed to the Gold. No Gold was read before a prediction freeze because no prediction campaign was started.

## Why execution is blocked

IR-018 through IR-022 do not currently have stable source occurrence IDs and validated source-backed bindings in the repository. The benchmark therefore has:

```text
semantic Gold items       5
machine-evaluable items   0
binding-incomplete items  5
fabricated IDs            0
fuzzy joins               0
```

The benchmark must not invent aliases/spans, use fuzzy matching, or treat semantic `HIGH` as an exact-coordinate freeze. It remains blocked until each item is materialized against a matching source hash and an exact/visual binder identity.

## Intended execution order after binding freeze

```text
source-backed occurrences
  -> deterministic candidate generation
  -> candidate freeze/hash
  -> pair-verifier request freeze/hash
  -> raw response freeze
  -> parsed prediction freeze
  -> current IdentityPromotionGate
  -> promotion freeze
  -> only then Gold join and scoring
```

The current model-only positive policy remains unchanged: `MODEL_PROPOSED` does not auto-merge. Candidate recall, verifier relation metrics, promotion precision/recall, false merges/splits, component explosion, continuation conflicts, and cycles will be measured only after the binding gate passes.

Provider/model calls for this prepared lane: `0/0`. No new call is authorized by this artifact.

GlobalDecoder, Stage B, and level logic were not touched. Level remains derived from validated tree depth.
