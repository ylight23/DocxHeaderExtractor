# Final Consolidation Report

Date: 2026-08-27
Integration branch: `integration/consolidate-source-20260827`
Canonical pre-merge SHA: `b7854b5e1a9832180d0729f65af7b76cf1b5b830`
Canonical post-consolidation SHA: `4dd9a0c979dcf302466d53846eefc8467846b822`
Backup tag: `backup/pre-consolidation-20260827`

## Imported or retained

- Consolidation inventory and per-source ledgers.
- Shared `BenchmarkManifestHash` Core implementation and tests; duplicate test-only helper removed.
- Accuracy Round 1-6 diagnostic probes and frozen reports from `accuracy/round2-ranking`.
- N3.7-B marker-family diagnosis, N3 source-first holdout already present in the newer accuracy artifacts, and R3-A offline qualification report.
- 029/042 ranking diagnostics and score-owner reports.
- Human-audit/evaluation infrastructure was already reachable from canonical history.

## Explicitly not promoted

- R3-A marker-only span guard production behavior.
- Ranker tuning, candidate-generation remediation, and provider/model changes.
- Dirty `accuracy-r1` Web `index.html` change and untracked sibling logs.
- Temporary/untracked corpus DOCX in the canonical worktree.

## Source heads

| Source | HEAD |
|---|---|
| accuracy-r1 | `81e677f6884288c75bb4f08d5e9aac383874029a` |
| benchmark-guards | `030a24f98a34c45a8bf96a5203bdf4730f802283` |
| canonical-manifest-hash | `cd273a91a3e8c039285c93aa33c8e5e9e7f1da69` |
| eval-integration | `9b1508b646b4693f7773b61f4c60a1ae38ac8579` |
| n0-binding-audit | `6b56b9307aee79cd4378e407609715b2903ca54a` |
| n3-7b-diagnosis | `a172af35acf794da071a835c4560627e3ec7a7a4` |
| n3-audit | `21d868310dbba458941874c0ca46766f40c98bfb` |
| r3-a | `40e22381169e80476fc69ca051c868f5d8b740e0` |
| rank-029-042 | `7bc43176c2a253ee355db6692bc68029688e3846` |
| rank-score-029-042 | `2ab05db8e9d6680b1723080f3a0e7f0da4bfbc08` |
| silver-audit | `31a8c2d024cb23a47265f38e49dfe15926c18a7a` |

## Verification

- `dotnet restore`: passed.
- `dotnet clean`: passed; generated outputs only.
- Full solution build: blocked by environment disk exhaustion while copying multi-platform native assets. Core and AgentHarness compiled before the copy failure. No source compile failure was observed in the reported output.
- Provider calls: `0`.
- Full test suite: not run to completion because the required build could not complete.
- Focused post-consolidation suite: not claimed; requires disk space and a fresh build.

## Known unresolved accuracy status

- Document 004 source reference census remains LAW 1, Chapter 7, Section 8, Article 77, total 93; observed extraction remains 78.
- The committed occurrence-level audit records 15 non-Article structural misses; no remediation was added.
- R3-A remains experimental/blocked for production promotion.
- Ranking owner and semantic recovery production promotion remain unresolved/blocked by their frozen evidence.

## Consolidation status

`MECHANICAL_CONFLICT_RESOLVED` for the performed source integrations. Final release/main promotion is not asserted until the full verification gate completes in an environment with sufficient disk space.
