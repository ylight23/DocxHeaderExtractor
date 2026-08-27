# Consolidation Source Inventory

Inventory date: 2026-08-27
Canonical repository: `C:\DocxHeaderExtractor`
Canonical HEAD: `b7854b5e1a9832180d0729f65af7b76cf1b5b830`
Remote: `https://github.com/ylight23/DocxHeaderExtractor.git`

All listed directories are linked git worktrees of the canonical repository. No independent clone was found.

| Source | Branch | HEAD | Merge-base with canonical | Working tree | Commits/files not in canonical HEAD |
|---|---|---|---|---|---|
| canonical | `m7-m8-audit-provenance` | `b7854b5` | self | untracked temporary DOCX only | none |
| accuracy-r1 | `accuracy/round2-ranking` | `81e677f` | `b7854b5` | modified `src/DocxHeaderExtractor.Web/wwwroot/index.html` | Round 1/2/3/4/5/6 diagnostics and UI commits; see ledger |
| benchmark-guards | `infra/benchmark-run-guards` | `030a24f` | `030a24f` | clean | already reachable from canonical history |
| canonical-manifest-hash | `infra/canonical-benchmark-manifest-hash` | `cd273a91` | `f068194` | untracked `lane-i-full.err`, `lane-i-full.out` | `cd273a91` |
| eval-integration | `integration/eval-infra-rehearsal` | `9b1508b` | `e06b4c4` | clean | `3f880d1`, `9b1508b` |
| n0-binding-audit | `diagnostics/n0-manifest-binding-drift` | `6b56b93` | self | clean | none |
| n3-7b-diagnosis | `diagnostics/n3-7b-marker-family` | `a172af3` | `b7854b5` | clean | `a172af3` |
| n3-audit | `n3-audit-bootstrap` | `21d8683` | `a04bf0f` | clean | `21d8683` |
| r3-a | `remediation/r3-a-marker-only-span-guard` | `40e2238` | `b7854b5` | clean | R3-A experiment and N3.7 diagnostic commits |
| rank-029-042 | `diagnostics/rank-029-042` | `7bc4317` | `a04bf0f` | clean | `7bc4317` |
| rank-score-029-042 | `diagnostics/rank-score-owner-029-042` | `2ab05db` | `a04bf0f` | clean | `7bc4317`, `2ab05db` |
| silver-audit | `infra/silver-human-audit-evaluator` | `31a8c2d` | self | clean | none |

## Preservation notes

- Do not delete sibling worktrees.
- Do not stage or remove the canonical untracked temporary DOCX.
- Do not stage `accuracy-r1`'s modified `index.html` or canonical-manifest-hash log files without explicit ownership.
- Large diagnostic artifacts are retained as evidence, not automatically promoted to production.
- The canonical branch currently does not contain the later accuracy-r1 diagnostic commits; import decisions are recorded in the per-source ledgers.
