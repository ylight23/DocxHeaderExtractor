# Legacy Caller Ledger

Scope: `feature/authority-pipeline-cutover`, 2026-08-27. The legacy type is retained where the
caller is explicitly repair, evaluation, replay, or diagnostic. The normal extraction surface has
no legacy production caller.

| Caller | Classification | Rationale |
| --- | --- | --- |
| `PipelineDocumentExtractionTool` | NORMAL_PRODUCTION -> Authority | Shared CLI/Web/MCP/AgentHarness extraction adapter. |
| CLI normal `RunExtractAsync` | NORMAL_PRODUCTION -> Authority | Constructs `PipelineDocumentExtractionTool`; canonical ProductOutput writeback. |
| Web upload route | NORMAL_PRODUCTION -> Authority | Constructs the shared extraction tool; canonical ProductOutput writeback. |
| MCP extraction service | NORMAL_PRODUCTION -> Authority | Constructs the shared extraction tool. |
| `HeaderExtractionPipeline` internal implementation | KEEP_EVAL | Historical compatibility/evaluation implementation. |
| CLI diagnostic/report/replay commands | KEEP_DIAGNOSTIC / KEEP_REPLAY | Explicit command-specific legacy probes, not normal extract. |
| `RepairGateCalibration` | KEEP_EVAL | Calibration experiment, intentionally measures legacy behavior. |
| `RepairCorpusAudit` | KEEP_EVAL | Corpus audit path, not user extraction. |
| `AutoRepairWorkflow` | KEEP_REPLAY | Explicit repair workflow and historical replay behavior. |
| `PartialKeyPackage` | NORMAL_PRODUCTION -> Authority | Action-facing package generation now uses AuthorityExtractionPipeline. |

## Gate

- `NORMAL_PRODUCTION` constructions of `HeaderExtractionPipeline`: **0**.
- `UNRESOLVED` callers: **0**.
- Legacy code is not a fallback from Authority failure.
- Remaining legacy constructions are bounded to named evaluation, repair, replay, or diagnostic
  surfaces and are not selected by normal extraction orchestration.
