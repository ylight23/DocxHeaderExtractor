# Current architecture contract

Status: `ACTIVE — PHASE 1 CUTOVER`

Baseline: `main@732c3505afc5dd312423ed0fa58056192fb39608`

The architecture cutover is being developed on `architecture/auto-harness-phase1` in an isolated
worktree. The Accuracy-99 branch is not a source of architecture changes and is not modified by
this workstream.

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
- Policy/guardrails authorize transfer and mutation; a model cannot grant permission. A remote
  capability with an exhausted provider-call budget is denied before execution.
- Projection and formatting cannot create authority.
- Accuracy-99 gold, human adjudication, and provider-quality tuning are outside Phase 1.

## Project dependency baseline

| Project | Current role | Current references |
|---|---|---|
| `Core` | authority contracts, OpenXML/PDF pipeline, provider-neutral classifier contracts/options | package dependencies include OpenXML, PdfPig, PDFtoImage, LLamaSharp for remaining VLM support |
| `Application` | provider-independent intent, plan compiler, policy, projection, task/resource and capability contracts | `Core` |
| `DocumentProcessing` | application processing service and processing contracts; delegates authority | `Application`, `Core` |
| `AgentHarness` | host-neutral orchestration, registry, guardrails, validators, task envelope | `Application`, `DocumentProcessing`, `Core` |
| `Web` | HTTP host and UI composition root | `AgentHarness`, `Core` |
| `Cli` | command host and evaluation/repair commands | `AgentHarness`, `Core`, `Eval` |
| `Mcp` | MCP host and async job adapter | `AgentHarness`, `Core` |
| `Eval` | evaluation/replay-only adapters | `Core` |
| `Infrastructure` | provider implementations, prompt/cache adapters, source infrastructure ports and fact-provider adapters | `Application`, `Core` |

`Application`, `DocumentProcessing`, and `Infrastructure` project boundaries now exist. Package
versions are centrally declared in `Directory.Packages.props` without changing the pinned versions.
Infrastructure now contains provider contracts, heading-provider implementations, prompt/cache
adapters, and fact-provider adapters. Core exposes only the neutral classifier seam; source-adapter
ownership and Eval isolation remain open Phase 1 work.

## Persisted artifacts and ownership

- correction memory: local JSONL through `CorrectionMemory` (migration to a feedback port is open)
- skill policy: versioned `skills/heading-extraction/SKILL.md` (generic catalog migration is open)
- semantic definitions: provider-independent concept/schema registry in Application; runtime
  registrations and composition-root consumers remain open
- MCP job state: temporary `McpJobStore` snapshots owned by the MCP host
- generated/writeback files: request-owned temp directories and explicit writeback adapters
- Accuracy-99 review/gold: evaluation-owned and excluded from this cutover

## Open architecture findings

The existing reachability audit proves no safe deletion candidate yet: `HeaderExtractionPipeline`
remains reachable from repair/evaluation, `DocxSlimExtractor` remains source-preparation reachable,
and `LegacyDocConverter` remains a normal input compatibility adapter. The correct next action is
boundary extraction and caller migration, followed by a new reachability audit; deletion before
those gates would risk breaking compatibility.

The repeatable mechanical audit is `scripts/architecture-phase1-audit.ps1`. At the current
checkpoint it passes project presence, central package versions, the Core project-reference
boundary, and heading-provider isolation; it remains blocked by the CLI's direct reference to
`DocxHeaderExtractor.Eval`.

## Phase control

`HUMAN_ADJUDICATION = NOT_STARTED_IN_PHASE1`
`ACCURACY_TUNING = NOT_STARTED_IN_PHASE1`
`PROVIDER_QUALITY_TUNING = NOT_STARTED_IN_PHASE1`
`MULTI_PROMPT_FUNCTIONAL_VERIFICATION = NOT_STARTED_IN_PHASE1`
