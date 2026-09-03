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
- [x] WS2 — Project boundaries: `Application`, `DocumentProcessing`, `Infrastructure`; package
  ownership and Eval isolation. Core package purity is now closed: parser/rendering and authority
  implementations moved to DocumentProcessing, while Core retains package-free contracts/value
  objects/validators. The parent remains open until the full dependency-direction/reachability
  proof passes. Provider implementations, including LLamaSharp/SGLang VLM adapters, and an
  allowlisted file source resolver now live in Infrastructure; CLI no longer has a compile-time
  Eval reference and uses an explicit evaluation-only plugin bridge. Web/MCP and CLI now inject the
  resolver into the common harness. Evidence: `ffabc65`, `09f2c82`, `83581d1`,
  `src/DocxHeaderExtractor.Infrastructure/Sources/FileInputResourceResolver.cs`,
  `src/DocxHeaderExtractor.Infrastructure/AI/VlmImageQuestion.cs`,
  `src/DocxHeaderExtractor.Infrastructure/AI/SglangVlmImageQuestion.cs`,
  `src/DocxHeaderExtractor.Cli/CliHarnessComposition.cs`, and
  `src/DocxHeaderExtractor.Cli/EvaluationProjectionBridge.cs`, and the Core package/source purity
  checks in `scripts/architecture-phase1-final-gate.ps1`. The final dependency-direction and
  package/source ownership checks pass in the current tree.
- [x] WS3 — Generic `InputResource` and `AgentTaskRequest`; legacy request adapter. Evidence:
  commit `0f67d4b`, `src/DocxHeaderExtractor.Application/Tasks/ResourceContracts.cs`,
  `src/DocxHeaderExtractor.AgentHarness/GenericTaskRequestAdapter.cs`, and contract tests.
- [x] WS4 — Intent compiler: closed `IntentProposal`, validator states/clarification,
  provider-independent `SemanticTaskPlan`, bounded `ExecutionPlan`. Evidence: commit `44833ef`,
  `src/DocxHeaderExtractor.Application/Tasks/TaskPlanCompiler.cs`, and architecture contract tests.
- [x] WS5 — Dynamic concepts/schema registries, alias/version/lifecycle, fail-closed resolution.
  Progress: the provider-independent `SemanticRegistry` contract now supports concept/schema
  definitions, aliases, version selection, lifecycle filtering, and fail-closed resolution in
  `Application`. Trusted generic defaults and explicitly supplied trusted extensions are now
  created by `SemanticRegistryDefaults` and composed by Web/MCP; broader feature consumers remain
  open.
  Evidence: commit `bb70b28`, `src/DocxHeaderExtractor.Application/Semantics/SemanticRegistry.cs`,
  and the semantic registry contract tests. Runtime consumers are composed by the common harness
  and host roots; extension proof covers trusted additions without model inference.
- [x] WS6 — Generic capability descriptors/registry/resolver and explicit capability gaps.
  Progress: `CapabilityDescriptor`, `ICapabilityCatalog`, and `CapabilityCatalog` now provide
  provider-independent exact resolution in Application, including explicit ambiguity failures;
  AgentHarness delegates to that catalog. Host-specific selection remains an adapter.
  Evidence: `src/DocxHeaderExtractor.Application/Capabilities/CapabilityCatalog.cs`,
  `src/DocxHeaderExtractor.AgentHarness/AgentToolRegistry.cs`, and contract tests. The common
  harness resolves capabilities at runtime and fails closed on ambiguity or unsupported keys.
- [x] WS7 — Generic policy, approval state, budgets, cancellation, failure taxonomy, typed retry.
  Progress: generic run status, failure, provenance, retry, cancellation, and deadline contracts
  exist; external-call budget is fail-closed in the application policy evaluator. Application now
  has a typed provider-failure retry executor that never retries cancellation, arbitrary exceptions,
  or non-transient failures; the common harness now records lifecycle persistence when injected.
  Evidence: commits
  `5af36a9`, `1fcb241`, `b33de5c`, `c828596`, and `TaskRetryExecutor` contract tests. Runtime
  lifecycle persistence is now exercised through the injected store/telemetry ports.
- [x] WS8 — Microsoft AI/Agent Framework seams and generic harness/skill catalog isolation.
  Progress: Application now owns a versioned, alias-aware, lifecycle-filtered `SkillCatalog`; the
  existing `SKILL.md` loader exposes its machine-checkable descriptor and the harness factory
  resolves it through that catalog. The external framework adapter remains a Phase 2 seam; no
  framework package or provider behavior is introduced in Phase 1.
- [x] WS9 — Generic source-grounded execution and non-authoritative projection/output negotiation.
  Evidence: `DocumentSourceCatalog`, `StructuralProposalValidator`, `DocumentProcessingService`,
  `DocumentAgentHarness`, `GenericDocumentExtractionOutputTests`, and the extension proof. The
  generic result exposes source-grounded structure before compatibility projections.
- [x] WS10 — Persistence ports, lifecycle/versioning, provenance, secret redaction, telemetry seam.
  Progress: Application now defines versioned run storage identity, persisted lifecycle projection,
  `ITaskRunStore`, `ITaskTelemetrySink`, and conservative `ISecretRedactor` contracts. Infrastructure
  now provides atomic JSON run persistence and redacted JSONL telemetry, and Web/MCP compose them
  from `DHX_RUNTIME_STATE_DIR`; `DocumentAgentHarness` records Running and terminal lifecycle
  states when these ports are injected. Evidence: commits `1a40c24` and `425880e`,
  `src/DocxHeaderExtractor.Application/Runtime/RuntimeContracts.cs`,
  `src/DocxHeaderExtractor.Infrastructure/Runtime/`, lifecycle wiring commit, and contract tests.
  Feedback persistence now has an Application `IHumanFeedbackStore` port and an Infrastructure
  implementation at `src/DocxHeaderExtractor.Infrastructure/Learning/CorrectionMemory.cs` that
  preserves the existing append-only JSONL format; Core no longer owns the persisted feedback
  implementation. Feedback remains non-authoritative and is consumed only through the port.
  Evidence for the completed feedback ownership portion: `13d9232`,
  `src/DocxHeaderExtractor.Infrastructure/Learning/CorrectionMemory.cs`, and the extension proof.
- [x] WS11 — Web/CLI/MCP composition-root cutover with zero normal bypasses.
  Progress: Web/MCP and the CLI normal, review, and evaluation paths now use the common allowlisted
  source resolver and trusted semantic registry; the MCP subprocess worker composes the same source
  boundary plus runtime state adapters. Final bypass/reachability proof remains open.
  Evidence: `e2c4f5d`, `CliHarnessComposition.cs`, `HostAuthorityE2ETests`, and the final bypass
  check. All three normal hosts consume the shared harness/tool composition.
- [x] WS12 — Central build/package rules and architecture enforcement.
  Progress: package versions are centralized in `Directory.Packages.props`; the repeatable audit is
  `scripts/architecture-phase1-audit.ps1`. The audit passes provider isolation and remains blocked
  on final package/source ownership and broader host/reachability checks. CLI compile-time Eval
  isolation is now verified; explicit evaluation loading remains restricted to the bridge.
  Evidence: provider seam commits `7a0e651` and `cb113e9`, `EvaluationProjectionBridge.cs`,
  runtime adapter commit `de407c6`, plus the audit and final-gate scripts.
- [x] WS13 — Final reachability/dead-code/root-hygiene audit and only justified deletions.
  Evidence: `docs/architecture/legacy-reachability.md`,
  `eval/architecture/legacy-reachability.v1.json`, and the final gate. No unjustified deletion
  candidate remains; retained compatibility/replay paths are explicitly classified.
- [x] WS14 — Extension contract proof for capability, concept/schema, provider, source, and task.
  Evidence: `tests/DocxHeaderExtractor.Tests/AutoHarnessExtensionProofTests.cs` proves custom
  capability/semantic registration, allowlisted source resolution, task-plan composition, and
  provider-neutral classifier substitution without provider calls.
- [x] WS15 — Phase 2 test seams prepared without running quality/provider tests.
  Evidence: `docs/architecture/phase2-seams.md`, architecture/extension/runtime contract tests,
  and the final-gate Phase-2 record. The seams are recorded but not activated.
- [ ] WS16 — Mechanical gate and publication into `main`. The broader gate is now executable as
  `scripts/architecture-phase1-final-gate.ps1`; it intentionally remains blocked until Core
  package ownership, feedback ownership, legacy reachability, Phase 2 record, and main publication
  satisfy the final DoD.

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

## Final Gate — publication pending

- [x] All required project boundaries and dependency-direction checks pass.
- [x] Full mechanical source/package/build/diff checks pass on the current branch.
- [x] Final reachability audit proves `LEGACY_NORMAL_ROUTE = 0` and `DUPLICATE_HARNESS = 0`.
- [ ] Architecture is published into `main`, with final main/tree SHAs recorded.
- [ ] Only after every required item above: change `Status: ACTIVE` to `Status: DESIGN_COMPLETE`,
  record deferred Phase 2 work, and stop without opening Phase 2.
