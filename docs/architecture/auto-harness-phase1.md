# Auto-harness Phase 1 architecture cutover

Status: `DESIGN_COMPLETE`

Baseline: `main@732c3505afc5dd312423ed0fa58056192fb39608`

This phase is developed in the isolated branch `architecture/auto-harness-phase1` so the
Accuracy-99 branch and its human-adjudication state are not changed during the cutover.

## Canonical execution flow

```text
host prompt/request
  -> IntentProposal
  -> IntentValidator
  -> ValidatedIntent
  -> SemanticTaskPlan
  -> ExecutionPlan
  -> capability resolver (AgentToolRegistry)
  -> policy/approval (PolicyEvaluator + existing guardrails)
  -> capability execution (IDocumentExtractionTool)
  -> source/authority validation (IDocumentAgentValidator)
  -> PromptDrivenProjection< DocumentOutline >
  -> GenericTaskResult< DocumentOutline >
```

`DocumentAgentHarness` is the single application orchestration surface. Web, CLI, and MCP all
resolve their result through `TaskResult.Value`; the older `DocumentAgentRunResult.Outline` remains
only as a compatibility projection while downstream callers migrate.

## Ownership and boundaries

| Concern | Owner | Boundary rule |
|---|---|---|
| Intent and task planning | `DocxHeaderExtractor.AgentHarness` | Host input is validated before capability execution |
| Capability selection | `AgentToolRegistry` | Selection is code-owned, never model-owned |
| Consent and mutation policy | `PolicyEvaluator` + existing guardrails | No implicit external transfer or writeback |
| Extraction authority | `AuthorityExtractionPipeline` in `DocumentProcessing` | Existing `ValidatedStructure` authority is preserved |
| Validation | `OutlineGroundingValidator`, `RunProvenanceValidator` | Fail closed on ungrounded output/provenance |
| Projection | `PromptDrivenProjection<T>` | Projection cannot create authority |
| Evaluation/legacy | `DocxHeaderExtractor.Eval`, repair compatibility paths | Not part of normal authority routing |

## Legacy audit decision

No deletion is justified in this slice. The current audit shows `HeaderExtractionPipeline` remains
reachable from repair/evaluation callers, `DocxSlimExtractor` remains an implementation dependency
of source preparation, and `LegacyDocConverter` is used only by explicit input compatibility
adapters before the canonical pipeline. These are migration candidates, not dead code. Removal
will require a separate reachability proof and regression gate.

## Verification checkpoint

- isolated checkout: clean before implementation
- baseline `dotnet build DocxHeaderExtractor.sln -c Release`: PASS, 0 errors
- focused harness/host tests after cutover: PASS, 32/32
- Release build after cutover: PASS, 0 errors
- `git diff --check`: PASS
- provider calls: 0
- Accuracy-99 gold/adjudication files: unchanged
