# LEGACY-3 — Eval/replay authority cutover

Status: PASS (scoped cutover; Round-3 full-suite gate remains pending)

Base: `f4e92c12cd09a88059ba80c4d794243c71268340`  
Cutover: `2846f66da06fe1c31279a51bdd4d14350d71bc09`

LEGACY-3 moved executable evaluation and replay callers away from Slim-shaped APIs. Source-only evaluation now uses:

```text
IEvaluationSourceReader
    -> AuthorityEvaluationSourceReader
    -> SourceDocument
```

Evaluation that consumes authority output continues to use the authority pipeline output (`DocumentOutline`) explicitly. The Slim compatibility projection is consumed only inside the source reader adapter; it is not exposed to evaluator callers.

Migrated surfaces include `EvalRunner`, `BenchDocumentFactory`, `EvaluationAnchorResolver`, `ReviewBundle`, `TocAnswerKeyGenerator`, the CLI review/TOC/anchor paths, the Web review bundle path, and their focused fixtures. Historical JSON, manifests, replay evidence, and architecture records were preserved and not rewritten.

The behavior-neutral gates are green:

```ini
EXECUTABLE_LEGACY_EVAL_CALLERS_BEFORE = 9
EXECUTABLE_LEGACY_EVAL_CALLERS_AFTER = 0
EVAL_BEHAVIOR_UNEXPECTED_DELTA = 0
REPLAY_HISTORICAL_ARTIFACTS_REWRITTEN = 0
BENCHMARK_EXPECTED_CHANGED = false
HISTORICAL_EVIDENCE_CHANGED = false
LEGACY_DELETED = false
DOCX_SLIM_REMOVED = false
PROVIDER_CALLS = 0
FULL_SUITE_EXECUTED = false
```

Validation: Release build passed. Focused evaluation, replay, review, source-facts, and repair-boundary tests passed `19/19`. The raw TRX remains local and is not part of the published artifact; its SHA256 is recorded in the JSON summary.

This task does not authorize removal of `DocxSlimExtractor`, `SlimDocument`, or `SlimParagraph`. Next task: `LEGACY-4`.
