# LEGACY-2 — Behavior-Neutral Repair Workflow Cutover

Status: `PASS` on the isolated Round-3 worktree.

## Scope

The repair workflow no longer constructs `HeaderExtractionPipeline`. A narrow `IRepairOutlineRunner` boundary now delegates to `AuthorityRepairOutlineRunner`, which owns an `AuthorityExtractionPipeline`.

Changed repair callers:

- `AutoRepairWorkflow`
- `RepairGateCalibration`
- `RepairCorpusAudit`
- CLI repair-key-package review-rate collection

The historical `HeaderExtractionPipeline` definition remains. `DocxSlimExtractor` compatibility/review/eval callers, Slim models, deprecated APIs, eval/replay paths, and historical tests/evidence were intentionally left untouched.

## Parity

The focused repair fixture `AutoRepairWorkflowTests.RepairWorkflowWritesEvidencePromptAndRuntimePolicy` passed before cutover at `24a1745` (`1/1`) and after cutover (`1/1`). The same observable evidence contract remains present: failure case, probe, candidate, validation, analysis plan, prompt, runtime policy, and learning log artifacts. No expected value was changed.

This is focused parity evidence, not a claim that all repair corpus semantics are globally frozen. A broader Round-3 regression gate remains required before merging a wider repair cutover.

## Verification

```ini
repairLegacyPipelineCallersBefore = 4
repairLegacyPipelineCallersAfter = 0
HEADER_EXTRACTION_PIPELINE_REPAIR_REACHABILITY = 0
REPAIR_BEHAVIOR_UNEXPECTED_DELTA = 0
productionNormalRouteChanged = false
benchmarkExpectedChanged = false
legacyDeleted = false
providerCalls = 0
fullSuiteExecuted = false
```

Release restore/build passed with `0` errors and `175` existing warnings. The focused repair test passed `1/1`. Static source inspection confirms the four repair constructions now use `AuthorityRepairOutlineRunner`; remaining `HeaderExtractionPipeline` references are outside the repair caller set.

`LEGACY_REMOVAL_AUTHORIZED = false` and `DOCX_SLIM_REMOVAL_AUTHORIZED = false`.

Next task: `LEGACY-3` — eval/replay cutover.
