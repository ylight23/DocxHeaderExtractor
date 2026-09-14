# A99 V4F-C — projected identity verifier request freeze

Status: **READY_FOR_PROJECTED_VERIFIER_EXPERIMENT_DESIGN**

This offline freeze consumes V4F-B packet identities as frozen hash artifacts and reconstructs them deterministically from the same source catalog, candidate IDs and projection config. No provider/model call, Gold read, or V4E-B evaluation read occurred.

## Checkpoint

- Parent: `V4F-B@6133127`; candidates: **7,702 → 7,702**.
- Old context: **7,070,469,069 bytes** / **1,767,619,122 estimated tokens**.
- Projected requests: **64,990,323 bytes** / **16,250,318 estimated tokens**.
- Reduction: **7,005,478,746 bytes (99.0808%)**; compression **108.79x**.
- Projection config SHA: `4c75f16682e143b0cdec21359917a51cdcd7fce8fbd6727e9cab22213cded631`; packet bodies and request bodies persisted: `false`.

## Contract

The decision contract remains `hdsa-global-identity-retrieve-verify-v1` with the unchanged four relation options and verification schema. The only semantic input change is replacing full-document context with the frozen bounded packet. No new evidence was added. Full-document context in the projected request is **0 bytes**.

## Payload and context

See `payload-decomposition.json` for exact category sums and `context-limit-analysis.json` for configured-limit classification. The latter is not provider-verified.

## Firewall

`GoldReadCount=0`; `V4EBEvaluationReadCount=0`; `ProviderCalls=0`; `ModelCalls=0`; candidate universe unchanged; ranking unchanged; dry-run transport calls `0`; packet/request reconstruction `PASS`.

## Gate

**READY_FOR_PROJECTED_VERIFIER_EXPERIMENT_DESIGN**. Next step is to design a small stratified old-vs-projected verifier experiment; do not execute provider calls from this checkpoint.
