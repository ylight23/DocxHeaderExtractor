# Source-tree hygiene plan

Status: SOURCE_TREE_HYGIENE_COMPLETE

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
- [x] WS-H16 — Add and pass `source-tree-hygiene-gate.ps1`.
- [x] WS-H17 — Restore, Release build, relevant tests, Phase-1 gates, and diff check pass.
- [x] WS-H18 — Commit and push Phase-2 cleanup; do not merge `main`.

## Evidence log

| Checkpoint | Commit | Evidence |
|---|---|---|
| Inventory | `6ef3759` | Complete production `.cs` ledger generated from `src/` excluding build output; `tests/`, `scripts/`, and `docs/` inventory coverage is recorded in the ledger. |
| Semantic normalization | `6ef3759` | `DocumentProcessing/Application` and `/Eval` absent; namespaces match projects; provider adapters are outside DocumentProcessing; production pipeline has no legacy converter route; Eval-only diagnostics/replay/calibration implementations are under `DocxHeaderExtractor.Eval`. |
| Naming normalization | `6ef3759` | `HeadingClassificationProposal`/`HeadingProposalJson` make untrusted classifier output explicit; analyzer/projection/inference/review folders and target names are recorded in the ledger. |
| Tests/build | `927ee92` + working-tree verification | Release build PASS; focused affected tests PASS `51/51` after provider-boundary refactor; full suite PASS `930/930`, exit `0`, with crash diagnostics enabled. |
| Provider ownership | working tree | Provider/runtime options and selection moved to `Infrastructure/AI`; `PipelineOptions` is provider-neutral; semantic scan finds no provider configuration or vendor endpoints in `DocumentProcessing`. |
| Pipeline re-audit | working tree | Strategy, runtime, and projection helpers moved under bounded `Pipeline/Strategies`, `Pipeline/Runtime`, and `Pipeline/Projections`; namespaces and public contracts remain unchanged. |
| Eval classification | working tree | `9989cd1` is classified as verification maintenance: four stale test source/assembly paths after relocation, two retained silver artifact hash corrections, and the C1 inventory expectation update to existing retained evidence. Gold labels, predictions, thresholds, production rules, prompts, and accuracy logic are unchanged; accuracy tuning is `0`. |
| Gate split | working tree | Default hygiene gate is a pre-merge candidate gate; `-RequirePublication` invokes the post-merge Phase-1 publication gate. Phase-1 final-gate semantics are unchanged. |
| Final candidate gate | working tree | Source hygiene candidate gate PASS; Phase-1 architecture audit PASS; focused tests PASS `75/75`; Release build PASS `0 errors`; full suite PASS `930/930`, exit `0`; `git diff --check` PASS; provider calls `0`. |
| Publication | `6ef3759` | Branch pushed to `origin/verification/auto-harness-phase2`; no merge to `main`. |

## Boundaries

- `LEGACY_NORMAL_ROUTE = 0` and `DUPLICATE_HARNESS = 0` remain required.
- `Accuracy-99` and Human Gold files are not edited.
- Existing compatibility behavior is preserved until caller evidence proves replacement complete.
- Phase 1 remains frozen; this worktree is not merged to `main` by this task.

## Current gate evidence

- `scripts/architecture-phase1-audit.ps1`: PASS.
- Source ownership, namespace, folder, Eval isolation, route, and duplicate-harness checks:
  PASS.
- `scripts/architecture-phase1-final-gate.ps1`: intentionally deferred on this unmerged Phase-2
  candidate; use `source-tree-hygiene-gate.ps1 -RequirePublication` only after publication.
- Provider calls: 0. Accuracy-99 and Human Gold: untouched.
- Current source-tree status: candidate semantic gate PASS; full suite PASS; H16/H17 complete.
  The post-merge publication gate remains intentionally separate and is not run on this unmerged
  branch. No main merge is authorized by this checkpoint.

## Final status contract

```text
SOURCE_TREE_HYGIENE = PASS
FILE_NAMING = PASS
FOLDER_NAMING = PASS
NAMESPACE_NORMALIZATION = PASS
EVAL_ISOLATION = PASS
PROVIDER_OWNERSHIP = PASS
GENERIC_VS_CAPABILITY_NAMING = PASS
COMPATIBILITY_NAMING = PASS
DEAD_CODE_AUDIT = PASS
FULL_SUITE = PASS
```
