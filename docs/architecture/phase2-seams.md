# AUTO-HARNESS PHASE 2 — Prepared Seams

Status: `VERIFIED`

This matrix records the extension points prepared during Phase 1. It is a boundary
record only: Phase 1 does not activate an external agent framework, provider-quality
run, Accuracy-99 adjudication, or benchmark tuning.

| Seam | Phase-1 contract | Current adapter/proof | Phase-2 work |
| --- | --- | --- | --- |
| Intent production | `IntentProposal` → `IntentValidator` → `ValidatedIntent` | `DocumentIntentProposalProducer`, intent-state tests | Verified; framework producer remains injectable |
| Semantic registry | Versioned, aliased, lifecycle-filtered `SemanticRegistry` | Explicit-kind/alias/fail-closed tests | Verified; external discovery remains fail-closed |
| Capability resolution | `ICapabilityCatalog` with exact/ambiguous/unsupported outcomes | Exact/ambiguous/missing matrix | Verified |
| Policy and approval | `PolicyDecision`, budget, cancellation, retry contracts | Denial/defer and typed retry tests | Verified |
| Task execution | `ExecutionPlan`, `GenericTaskResult<T>`, source-grounded projection | `DocumentAgentHarness`, generic output and host E2E tests | Verified; framework adapter is outer-only |
| Persistence/lifecycle | `ITaskRunStore`, telemetry, provenance, redaction | Infrastructure JSON/JSONL round-trip tests | Verified |
| Skill catalog | Alias/version/lifecycle-filtered `SkillCatalog` | Active-version resolution and loader tests | Verified |
| Provider boundary | Provider-neutral Application contracts | Provider factory/interchangeability tests | Verified; no provider-quality run in Phase 2 |

Phase-2 verification is complete after the Phase-1 final mechanical gate, source-tree hygiene
gate, full regression, Release build, and Phase-2 final gate all pass. External framework/provider
calls remain optional integration work and are not silently substituted by a local test double.
