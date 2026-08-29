# LEGACY-5 — Ordered demotion compatibility cutover

Status: `PASS` (focused, behavior-neutral cutover; Round-3 cumulative full-suite gate remains pending)

Base: `8258f24b8e52a575e097846e1216cc6a199f72f5`  
Cutover: `42b75d29fecf9b424a511dd479562ae3452671db`

## Phase A — state contract

ARCH-4R established that the three demotion operations depend on ordered mutable state and that `HasBuiltInHeadingStyle` is not a pure source fact. LEGACY-5 introduces `OrderedDemotionState` with an explicit split:

```text
immutable: SourceParagraph, NumberingStyleFeatures, source identity/order
policy state: candidate status, TrustedHeadingStyle, current Role, current Score
ordered context: paragraph sequence, first-prose boundary, current demotion run
```

`TrustedHeadingStyle` is the policy/trust representation of the former `HasBuiltInHeadingStyle` flag. It is not copied into `SourceDocument` and is not replaced by `BuiltInHeadingStyleLevel`.

The contract does not copy an entire `SlimParagraph`. Source facts and numbering/style facts are read-only during demotion; only `Role` and `Score` are mutable. The final policy state is projected to the legacy Slim object only after all three operations complete, for downstream compatibility code.

## Phase B — ordered cutover

All three operations now execute against `OrderedDemotionState`:

```text
Initial HeadingCandidatePolicy
  -> DemoteCoverPageBlock
  -> DemoteInlineEmphasis
  -> DemoteRunsWithoutOwnProse
  -> TocStructuralFeatureDeriver
  -> PostClassificationPolicy
```

The custom-style anchor helper has a corresponding state overload, so the ordered demotion path does not reach back into Slim numbering/style fields. The operation order and mutable Role/Score semantics are preserved.

## Gate result

```ini
DEMOTION_SLIM_EXECUTION_DEPENDENCY_AFTER = 0
COVER_DEMOTION_DELTA = 0
INLINE_EMPHASIS_DEMOTION_DELTA = 0
RUN_WITHOUT_PROSE_DEMOTION_DELTA = 0
CANDIDATE_DELTA = 0
ROLE_DELTA = 0
SCORE_DELTA = 0
LEVEL_DELTA = 0
DEMOTION_ORDER_EQUIVALENT = true
SOURCE_FACT_MUTATION = false
NUMBERING_STYLE_MUTATION = false
EXPECTED_CHANGED = false
LEGACY_DELETED = false
DOCX_SLIM_REMOVED = false
PROVIDER_CALLS = 0
```

Release build passed with `0` errors. The focused boundary/repair/demotion suite passed `20/20`. Full suite was intentionally not run. The raw TRX remains local and is not published; its SHA256 is recorded in the JSON summary.

Next task: `LEGACY-6`. The branch is not yet merge-ready until the planned Round-3 cumulative regression/full-suite gate.
