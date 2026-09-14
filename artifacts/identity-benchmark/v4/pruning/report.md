# A99 Identity Retrieval V4D-A — V3 pruning compatibility

Status: **`BLOCKED_ON_V3_PRUNER_CONTRACT_INCOMPATIBILITY`**

This phase is source-only and deliberately does not open Gold, V4C evaluation, requests, predictions, or provider execution.

## Freeze integrity

- V4B integrity: `PASS`.
- V4 broad: `96,069`; SHA256 `a9aad1afabb0645112f6aefae743d91b8dfb6f8681231881fcf791538053c9a0`.
- V3 pruning ranking config SHA256: `e5df9b2d1868d98957825984c80b039956eb53ca21db63fca8d9704c74e30e06`.
- V3 feature contract SHA256: `3479723189addb2469e69acb05e25e318c9ca85df0ade2a8f18ac33601a5092d`.

## Compatibility

The frozen V3 ranker explicitly maps candidate reason labels to evidence tiers. The V4-only candidates carry new reason labels without a frozen V3 interpretation.
- Frozen V3-supported labels: `ADJACENT_ORDER, NORMALIZED_TEXT_AFFINITY`.
- Unsupported V4 labels: `EXPLICIT_CONTINUATION_VARIANT, SHARED_STRUCTURAL_HEADING_KEY, TERMINAL_ACRONYM_VARIANT`.
- No new tier, weight, feature, or bonus was invented.

## V4D-A result

Pruning, dominance, ranking, budgets, shortlist, and request freeze were not applied because the frozen V3 contract cannot rank the new V4 representation without changed semantics.

## Firewall

- GoldReadCount: `0`.
- ProviderCalls: `0`; ModelCalls: `0`.
- V4C evaluation was not read; V4B/V3 artifacts were not modified.

## Gate

`BLOCKED_ON_V3_PRUNER_CONTRACT_INCOMPATIBILITY`
