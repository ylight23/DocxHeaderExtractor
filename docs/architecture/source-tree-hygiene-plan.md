# Source-tree hygiene plan

Status: ACTIVE

Baseline: `main@5678b454dc28c8bab811c5ce35a789d540fa82be`
Worktree: `C:\DocxHeaderExtractor-auto-harness-phase2`
Branch: `verification/auto-harness-phase2`

This plan is the first Phase-2 workstream. It normalizes semantic ownership and names without changing extraction behavior, authority, Accuracy-99, Human Gold, provider prompts, or provider execution.

## Checklist

- [x] WS-H1 — Full production inventory recorded in `source-tree-hygiene.md`.
- [x] WS-H2 — Remove `DocumentProcessing/Application` false layer.
- [x] WS-H3 — Move evaluation-only implementation out of `DocumentProcessing/Eval`.
- [x] WS-H4 — Normalize namespaces to project ownership.
- [x] WS-H5 — Replace generic production folders with bounded semantic folders.
- [x] WS-H6 — Isolate provider-specific configuration and adapters under Infrastructure/AI.
- [x] WS-H7 — Normalize untrusted model-output terminology to proposal/candidate semantics.
- [x] WS-H8 — Separate generic harness names from heading capability names.
- [x] WS-H9 — Make retained compatibility paths explicit.
- [x] WS-H10 — Audit `DocumentProcessing/Pipeline` and relocate non-orchestration artifacts.
- [x] WS-H11 — Verify file-to-primary-type consistency.
- [x] WS-H12 — Apply the project naming vocabulary consistently.
- [x] WS-H13 — Record product-name scope without renaming the product.
- [x] WS-H14 — Delete only proven dead files, aliases, registrations, and folders.
- [x] WS-H15 — Update current architecture comments/docs.
- [ ] WS-H16 — Add and pass `source-tree-hygiene-gate.ps1`.
- [ ] WS-H17 — Restore, Release build, relevant tests, Phase-1 gates, and diff check pass.
- [ ] WS-H18 — Commit and push Phase-2 cleanup; do not merge `main`.

## Evidence log

| Checkpoint | Commit | Evidence |
|---|---|---|
| Inventory | working tree | Complete production `.cs` ledger generated from `src/` excluding build output. |
| Semantic normalization | working tree | `DocumentProcessing/Application` and `/Eval` absent; namespaces match projects; provider adapters are outside DocumentProcessing; production pipeline has no legacy converter route; Eval-only diagnostics/replay/calibration implementations are under `DocxHeaderExtractor.Eval`. |
| Naming normalization | working tree | `HeadingClassificationProposal`/`HeadingProposalJson` make untrusted classifier output explicit; analyzer/projection/inference/review folders and target names are recorded in the ledger. |
| Tests/build | working tree | Release restore/build PASS; focused affected tests PASS; full-suite follow-up is recorded below. |

## Boundaries

- `LEGACY_NORMAL_ROUTE = 0` and `DUPLICATE_HARNESS = 0` remain required.
- `Accuracy-99` and Human Gold files are not edited.
- Existing compatibility behavior is preserved until caller evidence proves replacement complete.
- Phase 1 remains frozen; this worktree is not merged to `main` by this task.

## Current gate evidence

- `scripts/architecture-phase1-audit.ps1`: PASS.
- Source ownership, namespace, folder, Eval isolation, route, and duplicate-harness checks:
  PASS.
- `scripts/architecture-phase1-final-gate.ps1`: BLOCKED only because this Phase-2 branch is not
  contained in `origin/main`; publication is intentionally deferred and must not be represented
  as complete here.
- Provider calls: 0. Accuracy-99 and Human Gold: untouched.
