# AUTO-HARNESS PHASE 2 — Prepared Seams

Status: `READY_NOT_STARTED`

This matrix records the extension points prepared during Phase 1. It is a boundary
record only: Phase 1 does not activate an external agent framework, provider-quality
run, Accuracy-99 adjudication, or benchmark tuning.

| Seam | Phase-1 contract | Current adapter/proof | Phase-2 work |
| --- | --- | --- | --- |
| Intent production | `IntentProposal` → `IntentValidator` → `ValidatedIntent` | Application task contracts and architecture tests | Connect a conversational intent producer |
| Semantic registry | Versioned, aliased, lifecycle-filtered `SemanticRegistry` | Trusted defaults plus extension proof | External concept/schema discovery, still fail-closed |
| Capability resolution | `ICapabilityCatalog` with exact/ambiguous/unsupported outcomes | `AgentToolRegistry` and extension proof | Framework capability metadata adapter |
| Policy and approval | `PolicyDecision`, budget, cancellation, retry contracts | Application policy/retry tests | Interactive approval UI and host policy provider |
| Task execution | `ExecutionPlan`, `GenericTaskResult<T>`, source-grounded projection | `DocumentAgentHarness` and generic output tests | Framework execution adapter |
| Persistence/lifecycle | `ITaskRunStore`, telemetry, provenance, redaction | Infrastructure JSON/JSONL adapters and lifecycle tests | Durable store/telemetry backend |
| Skill catalog | Alias/version/lifecycle-filtered `SkillCatalog` | `SKILL.md` loader and harness factory | External skill marketplace or framework registration |
| Provider boundary | Provider-neutral Application contracts | Infrastructure/provider adapters | Provider selection and quality experiments |

Phase-2 entry conditions are: Phase 1 final mechanical gate passes, the architecture
is published to `main`, and the status is changed to `DESIGN_COMPLETE`. No Phase-2
work is implied by this document.
