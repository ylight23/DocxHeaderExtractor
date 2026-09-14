# A99 V4F-B — bounded local identity evidence projection

Status: **READY_FOR_V4F_PROJECTED_REQUEST_FREEZE**

This is a source-only counterfactual projection. It preserves all **7,702** frozen candidate pairs and does not call a model/provider or read Gold/V4E-B evaluation.

## Contract

- Pair core: source occurrence IDs, raw/comparison surfaces, source kind, document order and parser-owned source container.
- Local context: previous/next bounds `2/2` and at most `8` intervening occurrences.
- Structural context: bounded same-container peers (`4`); no fabricated ancestor, numbering, scope, layout or visual facts.
- Semantic conclusions are not projected. The projection configuration hash is `4c75f16682e143b0cdec21359917a51cdcd7fce8fbd6727e9cab22213cded631`.

## Size

- Old frozen request context: **7,070,469,069 bytes** / **1,767,619,122 estimated tokens**.
- New projected packets: **53,572,661 bytes** / **13,395,917 estimated tokens**.
- Reduction: **7,016,896,408 bytes (99.2423%)**; compression ratio **131.98x**.

The complete distributions and per-document values are in `packet-size-distribution.json`; packet bodies are not persisted.

## Coverage

`evidence-availability.json` reports pair core, bounded local/structural evidence, literal branch/continuation marker facts, and unavailable fields. Packet classes are descriptive only: `STRUCTURALLY_PARTIAL`, `LOCAL_ONLY`, `PAIR_ONLY`. No semantic sufficiency claim is made.

## Firewall

`GoldReadCount=0`; `V4EBEvaluationReadCount=0`; `ProviderCalls=0`; `ModelCalls=0`; candidate set changed=false; candidate order does not affect canonical packet set; no relation conclusions; no request prompt change.

## Gate

**READY_FOR_V4F_PROJECTED_REQUEST_FREEZE**. The next authorized step is to freeze canonical projected verifier requests for the same 7,702 candidates, still with zero provider calls.
