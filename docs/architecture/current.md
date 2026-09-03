# Current architecture contract

Status: `ACTIVE — PHASE 2 SOURCE-TREE HYGIENE`

Baseline: `main@5678b454dc28c8bab811c5ce35a789d540fa82be`

The Phase-2 source-tree hygiene work is being developed on
`verification/auto-harness-phase2` in `C:\DocxHeaderExtractor-auto-harness-phase2`. The
Accuracy-99 branch is not a source of architecture changes and is not modified by this
workstream.

## Current host routes

```text
Web / CLI / MCP
  -> DocumentAgentHarness
  -> PipelineDocumentExtractionTool
  -> DocumentProcessingService
  -> AuthorityExtractionPipeline
  -> ValidatedStructure
  -> PromptDrivenProjection
  -> GenericTaskResult
```

All three hosts currently use the same `DocumentAgentHarness`; Web, CLI, and MCP consume the
validated `TaskResult.Value` projection. The compatibility `DocumentAgentRunResult.Outline` is
retained for existing library/test callers and is not a second authority route.

## Trust and authority boundaries

- Model output is a proposal, never authority.
- Input documents and tool output are untrusted until deterministic validation.
- Parser-owned source coordinates are the only materialization source.
- `ValidatedStructure` is structural authority.
- `ValidatedFact` is fact authority.
- Application plan compilation creates stable `PlanId` values from task/resource identity and
  capability metadata; explicit idempotency keys override the resource identity when supplied.
- Capability metadata is registered and resolved by the provider-independent Application catalog;
  host tool selection cannot silently overwrite duplicate capability names.
- Policy/guardrails authorize transfer and mutation; a model cannot grant permission. A remote
  capability with an exhausted provider-call budget is denied before execution.
- Retry is typed and policy-driven: only explicitly transient `ProviderCallException` failures may
  be retried, and cancellation/untyped failures remain fail-closed.
- Projection and formatting cannot create authority.
- Accuracy-99 gold, human adjudication, and provider-quality tuning are outside Phase 1.

## Project dependency baseline

| Project | Current role | Current references |
|---|---|---|
| `Core` | pure source/structure/fact contracts and authority value objects/validators | no project or parser/render/provider package references |
| `Application` | provider-independent intent, plan compiler, policy, projection, task/resource, capability, semantic-registry and runtime contracts | `Core` |
| `DocumentProcessing` | DOCX/PDF source adapters, authority pipeline implementations, processing service, bounded review/repair compatibility | `Application`, `Core`; owns OpenXML/PdfPig/PDFtoImage |
| `AgentHarness` | host-neutral orchestration, registry, guardrails, validators, task envelope | `Application`, `DocumentProcessing`, `Core` |
| `Web` | HTTP host and UI composition root | `AgentHarness`, `Core`, `DocumentProcessing`, `Infrastructure` |
| `Cli` | command host and explicit evaluation/repair commands | `AgentHarness`, `Core`, `DocumentProcessing`, `Infrastructure`, explicit `Eval` plugin bridge |
| `Mcp` | MCP host and async job adapter | `AgentHarness`, `Core`, `DocumentProcessing`, `Infrastructure` |
| `Eval` | evaluation/replay-only adapters | `Core`, `DocumentProcessing` |
| `Infrastructure` | provider implementations, prompt/cache adapters, source infrastructure ports and fact-provider adapters | `Application`, `Core`, `DocumentProcessing` |

`Application`, `DocumentProcessing`, and `Infrastructure` project boundaries now exist. Package
versions are centrally declared in `Directory.Packages.props` without changing the pinned versions.
DocumentProcessing now owns source/parser/rendering and authority pipeline implementations; Core
contains only package-free contracts/value objects/validators. Infrastructure now contains provider
contracts, heading-provider implementations, prompt/cache
adapters, fact-provider adapters, LLamaSharp/SGLang VLM adapters, and an allowlisted file resource
resolver. Core exposes only package-free contracts. Web/MCP wire normal and review paths; CLI evaluation commands use an explicit Eval project boundary and the CLI normal path does not activate Eval. The hosts wire the resolver
and trusted semantic registry into the common harness; the MCP subprocess worker composes the same
source boundary plus runtime state adapters. Evaluation commands use `EvaluationProjectionBridge`
and the normal extraction route never activates the Eval project.

## Persisted artifacts and ownership

- correction memory: Web writes through Application `IHumanFeedbackStore` and the Infrastructure
  `CorrectionMemoryFeedbackStore`; the append-only implementation is owned by Infrastructure at
  `src/DocxHeaderExtractor.Infrastructure/Learning/CorrectionMemory.cs` and remains compatible
  with existing JSONL files
- skill policy: versioned `skills/heading-extraction/SKILL.md`, parsed into the provider-independent
  Application `SkillCatalog` before harness creation; framework-specific adapter remains deferred
  without adding a runtime dependency in Phase 1
- semantic definitions: provider-independent concept/schema registry in Application; trusted
  generic defaults are composed by Web/MCP, while external configuration registration and feature
  consumers remain open
- run lifecycle: versioned persistence/telemetry ports and secret redaction contract in Application;
  Web/MCP compose `JsonFileTaskRunStore` and `JsonLinesTaskTelemetrySink` from the configurable
  `DHX_RUNTIME_STATE_DIR` boundary, and the common harness records Running and terminal states;
  persistence failures remain non-authoritative and provider payloads are not part of these artifacts
- MCP job state: temporary `McpJobStore` snapshots owned by the MCP host
- generated/writeback files: request-owned temp directories and explicit writeback adapters
- Accuracy-99 review/gold: evaluation-owned and excluded from this cutover

The extension seam is executable-tested in
`tests/DocxHeaderExtractor.Tests/AutoHarnessExtensionProofTests.cs`: a custom capability, semantic
definition, allowlisted source, compiled task plan, and provider-neutral classifier can compose
without adding a second authority route or making a provider call.

## Open architecture findings

The current reachability audit retains `HeaderExtractionPipeline` only for repair/evaluation
compatibility, keeps `DocxSlimExtractor` behind source preparation, and retains `LegacyDocConverter`
only as an explicit input compatibility adapter before the canonical authority pipeline. The normal
authority pipeline now receives normalized OOXML and has no converter call.

The repeatable mechanical audits are `scripts/architecture-phase1-audit.ps1` and
`scripts/source-tree-hygiene-gate.ps1`. The Phase-1 mechanical audit passes project presence,
central package versions, the Core project-reference boundary, heading-provider isolation, the
explicit CLI Eval bridge, and host source/semantic composition checks. The source-tree gate adds
ownership, namespace, folder, Eval isolation, legacy-route, and duplicate-harness checks. The
complete Phase-1 publication gate remains intentionally separate: this Phase-2 branch is not
merged to `main`, so that gate must not be presented as publication evidence here.

## Phase control

`HUMAN_ADJUDICATION = NOT_STARTED_IN_PHASE1`
`ACCURACY_TUNING = NOT_STARTED_IN_PHASE1`
`PROVIDER_QUALITY_TUNING = NOT_STARTED_IN_PHASE1`
`MULTI_PROMPT_FUNCTIONAL_VERIFICATION = NOT_STARTED_IN_PHASE1`
