# Production request architecture V1 (frozen 2026-09-28)

Status: `FROZEN` · request optimization stops here until production telemetry says otherwise.

## Configuration

| concern | V1 setting | where |
|---|---|---|
| streaming | ON | OpenRouter streaming transport (T2 live qualification) |
| reasoning default | `none` | `RemoteInferenceOptions.OpenRouterReasoningEffort` |
| transient retry | bounded (`TransientRequestRetries`, 0..4) | `RemoteInferenceOptions` |
| boundary-whitespace matching | tolerant but exact: a quote whose only difference from its atom is edge whitespace is the whole atom; anything else is refused as before | `SemanticSourcePartCanonicalizer` CASE B2 |
| semantic recovery | existing deterministic path | unchanged |
| selective medium escalation | OFF / not implemented | experimental V2 (P7) |
| packing | **OPEN — see correction below.** Code default is still `FIXED_OWNED_COUNT_120` | `SemanticEvidencePackingPolicies.Default` |

## Correction (2026-09-28): packing of the evidence

Every run scored below used **P05 `RESOURCE_BOUNDED_SOURCE_PACKING_V1`** (96 owned / 8 halo /
28k estimated-token ceiling, 31 calls), not the production default `FIXED_OWNED_COUNT_120`.
The only fixed120 run on this cohort (V4R3) is `INCOMPLETE_EXPERIMENT_ONLY` (48 calls, 20/25 parent
coverage, never scored). So these numbers support reasoning=none + streaming + CASE B2 **with P05
packing**; they say nothing about fixed120. The P05 policy code is not yet committed. The packing
decision for production is therefore open, not "unchanged".

## Evidence (provider calls during scoring: 0)

Same accepted responses, SRC-089 + SRC-095, exact-bind scorer:

| arm | TP | FP | FN | F1 | extra completion tokens |
|---|---:|---:|---:|---:|---:|
| reasoning=none, byte-exact canonicalizer (T4 as first scored) | 110 | 51 | 29 | 0.7333 | — |
| **reasoning=none, V1 canonicalizer** | **114** | **53** | **25** | **0.7451** | 0 |
| reasoning=medium, whole cohort (P05) | 120 | 61 | 19 | 0.7500 | +175k (222k vs 47k) |

- The SRC-089 none-vs-medium gap was mostly harness refusal, not missing reasoning: under
  `none` the model quoted multipart continuation lines with a leading join space, which the
  byte-exact canonicalizer refused (`eval/a99-closed-loop/request-architecture-v2/openrouter-streaming-t5b-escalation-signal.v1.md`).
- The rule binds 2 genuine FPs on SRC-095 that strict refusal had hidden; this is accepted as
  the harness now reporting what the model actually claimed.

## Verification

Targeted gate, per the narrow-boundary rule:

- canonicalizer unit tests incl. edge-whitespace regressions and refused near-misses
  (inner whitespace, partial atom, case, whitespace-only), and halo/`OutOfOwnedSegment` negative case
- binder / locator / selection-mode / membership / source-parts tests: 88/88
- T4 / T5A / T5B freezes: rescore moved exactly as the T5B `P2_NONE_BOUNDARY_WS_REPAIR` replay predicted
- replay + integration smoke (lane, MCP, product replay determinism, release smoke, SRC-089/095 blind): 121/121
- `git diff --check` clean

**Full suite: deferred to CI / post-promotion confidence run.** Not run for this checkpoint.

## Deferred: experimental V2

`P7_WS_REPAIR_THEN_ESCALATE_ON_RESIDUAL_OWNED_REFUSAL` (replay): F1 0.7672, 5/31 leaves escalated,
+35.5k completion tokens. Not implemented: it needs a per-call reasoning override across the
classifier interface and in-loop refusal counting, and it misses the SRC-089-closeness gate
(ordinal 5 has no pre-Gold signal). Revisit only if production telemetry shows quality/recall as
a significant bottleneck.
