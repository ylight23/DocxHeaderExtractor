# AUTO-HARNESS PHASE 1 — Architecture Cutover Plan

Status: `ACTIVE`

This file is the execution contract for Phase 1. It must remain in the repository. A checkbox is
changed only when its evidence exists in the same tree; a partial skeleton does not complete a
workstream. Phase 1 must not perform human adjudication, Accuracy-99 tuning, provider-quality
tuning, or benchmark tuning.

## Invariants

- MODEL PROPOSES; CODE VALIDATES; SOURCE MATERIALIZES; POLICY AUTHORIZES.
- `ValidatedStructure` and `ValidatedFact` remain the only structural/fact authorities.
- No second authority pipeline, no long-lived Harness v2, no model permission bypass, no corpus-
  specific rule in generic Core, and no AI auto-promotion of canonical concepts/schemas.

## Workstream checklist

- [x] WS1 — Baseline, reachability, and canonical ADR. Evidence: `docs/architecture/current.md`,
  `docs/architecture/legacy-reachability.md`, `docs/architecture/clean-architecture-review.md`.
- [ ] WS2 — Project boundaries: `Application`, `DocumentProcessing`, `Infrastructure`; package
  ownership and Eval isolation. Boundary project shells now exist, but the parent is not complete
  until provider/source package ownership, dependency direction, and Eval isolation pass. Provider
  implementations and an allowlisted file source resolver now live in Infrastructure; CLI no longer
  has a compile-time Eval reference and uses an explicit evaluation-only plugin bridge. Web/MCP now
  inject the resolver into the common harness; standalone CLI wiring and Core package purity remain
  open. Evidence: `ffabc65`, `09f2c82`, `83581d1`,
  `src/DocxHeaderExtractor.Infrastructure/Sources/FileInputResourceResolver.cs`, and
  `src/DocxHeaderExtractor.Cli/EvaluationProjectionBridge.cs`.
- [x] WS3 — Generic `InputResource` and `AgentTaskRequest`; legacy request adapter. Evidence:
  commit `0f67d4b`, `src/DocxHeaderExtractor.Application/Tasks/ResourceContracts.cs`,
  `src/DocxHeaderExtractor.AgentHarness/GenericTaskRequestAdapter.cs`, and contract tests.
- [x] WS4 — Intent compiler: closed `IntentProposal`, validator states/clarification,
  provider-independent `SemanticTaskPlan`, bounded `ExecutionPlan`. Evidence: commit `44833ef`,
  `src/DocxHeaderExtractor.Application/Tasks/TaskPlanCompiler.cs`, and architecture contract tests.
- [ ] WS5 — Dynamic concepts/schema registries, alias/version/lifecycle, fail-closed resolution.
  Progress: the provider-independent `SemanticRegistry` contract now supports concept/schema
  definitions, aliases, version selection, lifecycle filtering, and fail-closed resolution in
  `Application`. Trusted generic defaults are now created by `SemanticRegistryDefaults` and
  composed by Web/MCP; external configuration registration and feature consumers remain open.
  Evidence: commit `bb70b28`, `src/DocxHeaderExtractor.Application/Semantics/SemanticRegistry.cs`,
  and the semantic registry contract tests.
- [ ] WS6 — Generic capability descriptors/registry/resolver and explicit capability gaps.
  Progress: `CapabilityDescriptor`, `ICapabilityCatalog`, and `CapabilityCatalog` now provide
  provider-independent exact resolution in Application, including explicit ambiguity failures;
  AgentHarness delegates to that catalog. Host-specific selection remains an adapter.
  Evidence: `src/DocxHeaderExtractor.Application/Capabilities/CapabilityCatalog.cs`,
  `src/DocxHeaderExtractor.AgentHarness/AgentToolRegistry.cs`, and contract tests.
- [ ] WS7 — Generic policy, approval state, budgets, cancellation, failure taxonomy, typed retry.
  Progress: generic run status, failure, provenance, retry, cancellation, and deadline contracts
  exist; external-call budget is fail-closed in the application policy evaluator. Application now
  has a typed provider-failure retry executor that never retries cancellation, arbitrary exceptions,
  or non-transient failures; lifecycle persistence integration remains open. Evidence: commits
  `5af36a9`, `1fcb241`, `b33de5c`, `c828596`, and `TaskRetryExecutor` contract tests.
- [ ] WS8 — Microsoft AI/Agent Framework seams and generic harness/skill catalog isolation.
- [ ] WS9 — Generic source-grounded execution and non-authoritative projection/output negotiation.
- [ ] WS10 — Persistence ports, lifecycle/versioning, provenance, secret redaction, telemetry seam.
  Progress: Application now defines versioned run storage identity, persisted lifecycle projection,
  `ITaskRunStore`, `ITaskTelemetrySink`, and conservative `ISecretRedactor` contracts. Infrastructure
  now provides atomic JSON run persistence and redacted JSONL telemetry, and Web/MCP compose them
  from `DHX_RUNTIME_STATE_DIR`; end-to-end lifecycle callers remain open. Evidence: commit
  `1a40c24`, `src/DocxHeaderExtractor.Application/Runtime/RuntimeContracts.cs`,
  `src/DocxHeaderExtractor.Infrastructure/Runtime/`, commit `de407c6`, and contract tests.
- [ ] WS11 — Web/CLI/MCP composition-root cutover with zero normal bypasses.
- [ ] WS12 — Central build/package rules and architecture enforcement.
  Progress: package versions are centralized in `Directory.Packages.props`; the repeatable audit is
  `scripts/architecture-phase1-audit.ps1`. The audit passes provider isolation and remains blocked
  on final package/source ownership and broader host/reachability checks. CLI compile-time Eval
  isolation is now verified; explicit evaluation loading remains restricted to the bridge.
  Evidence: provider seam commits `7a0e651` and `cb113e9`, `EvaluationProjectionBridge.cs`,
  runtime adapter commit `de407c6`, plus the audit script.
- [ ] WS13 — Final reachability/dead-code/root-hygiene audit and only justified deletions.
- [ ] WS14 — Extension contract proof for capability, concept/schema, provider, source, and task.
- [ ] WS15 — Phase 2 test seams prepared without running quality/provider tests.
- [ ] WS16 — Mechanical gate and publication into `main`.

## Completed implementation slice

- [x] Generic task envelope introduced in `DocxHeaderExtractor.Application` and consumed by
  `DocxHeaderExtractor.AgentHarness`:
  `IntentProposal`, `ValidatedIntent`, `SemanticTaskPlan`, `ExecutionPlan`, `PolicyDecision`,
  `PromptDrivenProjection<T>`, and `GenericTaskResult<T>`.
- [x] Existing `DocumentAgentHarness` emits the explicit intent/plan/capability/policy/
  validation/projection stages without changing authority behavior or step-budget semantics.
- [x] Web, CLI, and MCP consume the common `TaskResult.Value` surface; legacy `Outline` remains a
  compatibility projection only.
- [x] Contract tests cover unsupported intent, policy denial/defer, and result projection identity.

Evidence for this slice:

```text
Commits: 38c65f3, ffabc65, 09f2c82, 0f67d4b, 83581d1, bb70b28
Files: src/DocxHeaderExtractor.Application/Tasks/TaskContracts.cs,
  src/DocxHeaderExtractor.Application/Tasks/ResourceContracts.cs,
  src/DocxHeaderExtractor.DocumentProcessing/ProcessingContracts.cs,
  src/DocxHeaderExtractor.DocumentProcessing/DocumentProcessingService.cs,
  src/DocxHeaderExtractor.Infrastructure/AI/ProviderContracts.cs,
  src/DocxHeaderExtractor.Infrastructure/DocxHeaderExtractor.Infrastructure.csproj,
  src/DocxHeaderExtractor.AgentHarness/DocumentTaskAdapters.cs,
       src/DocxHeaderExtractor.AgentHarness/GenericTaskRequestAdapter.cs,
       src/DocxHeaderExtractor.AgentHarness/DocumentAgentHarness.cs,
       src/DocxHeaderExtractor.Web/Program.cs,
       src/DocxHeaderExtractor.Cli/Program.cs,
       src/DocxHeaderExtractor.Mcp/McpExtractionService.cs,
  tests/DocxHeaderExtractor.Tests/AutoHarnessArchitectureContractTests.cs
```

## Phase controls

- [x] HUMAN_ADJUDICATION = NOT_STARTED_IN_PHASE1
- [x] ACCURACY_TUNING = NOT_STARTED_IN_PHASE1
- [x] PROVIDER_QUALITY_TUNING = NOT_STARTED_IN_PHASE1
- [x] MULTI_PROMPT_FUNCTIONAL_VERIFICATION = NOT_STARTED_IN_PHASE1

## Final Gate — not reached

- [ ] All required project boundaries and dependency-direction checks pass.
- [ ] Full mechanical source/package/build/diff checks pass.
- [ ] Final reachability audit proves `LEGACY_NORMAL_ROUTE = 0` and `DUPLICATE_HARNESS = 0`.
- [ ] Architecture is published into `main`, with final main/tree SHAs recorded.
- [ ] Only after every required item above: change `Status: ACTIVE` to `Status: DESIGN_COMPLETE`,
  record deferred Phase 2 work, and stop without opening Phase 2.
