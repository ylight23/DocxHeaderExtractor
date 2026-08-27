# Final Consolidation Report

Date: 2026-08-27
Integration branch: `integration/consolidate-source-20260827`
Canonical pre-merge SHA: `b7854b5e1a9832180d0729f65af7b76cf1b5b830`
Canonical post-consolidation SHA: `b133ed9f2fd72f68a0af0b1da42eabfb7a3e2adc`
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
- Latest `accuracy-r1` Web `index.html` was already identical to the selectively retained UI in integration; no sibling logs were imported.
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
- Runtime-specific builds (`win-x64`) for CLI, MCP, Web, and Tests: passed. The solution-level multi-RID build remains blocked by native-asset/disk behavior on this machine.
- Provider calls: `0`.
- Focused deterministic/replay/provenance suite: `27 passed / 0 failed`.
- Web UI syntax/contract suite: `2 passed / 0 failed`.
- Web/API smoke: `/` and `/api/defaults` both returned HTTP 200.
- Full test suite: `1217 passed / 21 failed / 1238 total`; remaining failures are historical route/tagged-fixture/hash/RID/file-lock expectations and are not claimed resolved here.

## Known unresolved accuracy status

- Document 004 source reference census remains LAW 1, Chapter 7, Section 8, Article 77, total 93; observed extraction remains 78.
- The committed occurrence-level audit records 15 non-Article structural misses; no remediation was added.
- R3-A remains experimental/blocked for production promotion.
- Ranking owner and semantic recovery production promotion remain unresolved/blocked by their frozen evidence.

## Consolidation status

`MECHANICAL_CONFLICT_RESOLVED` for the performed source integrations. Final release/main promotion is not asserted until the full verification gate completes in an environment with sufficient disk space.

## Final Failure-Set Delta

The final same-machine `win-x64` comparison was completed without changing production code or expected values:

- Base `b7854b5`: `1179 passed / 17 failed / 1196 total`.
- Integration `b2f814c`: `1217 passed / 21 failed / 1238 total`.
- Exact identity delta: 17 pre-existing failures, 4 integration-only test identities, 0 fixed identities.
- Three integration-only corpus probes still fail when run individually because corpus `004` is externally locked; under the frozen protocol they remain `UNRESOLVED`, not `ENVIRONMENT_FILE_LOCK`.
- `PdfN15RankingLossDiagnosisProbe` remains an artifact SHA drift and was not “fixed” by changing the expected hash.

Therefore the full-suite delta gate is `BLOCKED`; the Draft PR remains open and `main` was not merged. See [full-suite-failure-delta.v1.json](../../eval/consolidation/full-suite-failure-delta.v1.json) and [consolidation-verification.v2.json](../../eval/consolidation/consolidation-verification.v2.json).

## Fresh Checkout Closeout

Fresh checkout `4da3fc24b3db44a732f496884dde73733fd1adc9` verified the remaining diagnostic failures without changing production code, expected values, or frozen artifacts:

- Three corpus probes pass sequentially after the external `004` file lock disappeared: `ENVIRONMENT_FILE_LOCK_RESOLVED`.
- N15 passes with raw LF SHA `245c4c7b9b57d0e5331c740ab0f9df7baf0e2d7fca2408e4000226f9546ef1f9`; the prior CRLF `709999...` was stale worktree state: `STALE_WORKTREE_EOL_RESOLVED`.
- Focused deterministic/replay/provenance: `27/27`; Web UI: `2/2`; `/` and `/api/defaults`: HTTP 200; provider calls: `0`.

The baseline suite still has 17 pre-existing failures. The consolidation delta has `0` unresolved failures and `0` new production regression failures; this is not claimed as `FULL_SUITE_PASS`. See [full-suite-failure-delta.v2.json](../../eval/consolidation/full-suite-failure-delta.v2.json) and [consolidation-verification.v3.json](../../eval/consolidation/consolidation-verification.v3.json).
