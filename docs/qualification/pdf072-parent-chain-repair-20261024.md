# PDF072 focused parent-chain repair — 2026-10-24

## Decision

**Do not promote the combined candidate.** The controlled parent-repair trial passes, but the full
extraction fails its predeclared membership and boundary gates. Both `RepairParentChains` and
`PdfSourceBoundSelection` remain false by default. Production keeps the previously qualified retry
behavior. This is a failed full qualification result, not an invitation to rerun the same candidate
until a favorable sample appears.

## Change

`RepairParentChains` is an opt-in qualification candidate. Initial placement requests, response
protocol, model and provider route remain unchanged. After initial placement, a structural check finds
claimed parent chains that cannot reach ROOT because they end in NONE, UNRESOLVED, an unavailable
proposal or an invalid edge. A terminal NONE or UNRESOLVED with no claimed descendants is valid and
does not trigger a repair.

The model reconsiders only the affected descendants and their model-owned blocking ancestors, with
their source context and the complete heading catalog. Upstream parent relations and unrelated
relations stay fixed. C# never chooses a replacement relation, changes membership/text/grouping, or
silently turns NONE into ROOT. The model may return explicit UNRESOLVED.

There are at most two passes, each bounded by the existing three protocol rounds, provider retry,
request-byte/context/output limits and extraction attempt/cost budgets. Identical refused responses
and unchanged proposals stop early. Failed repair leaves the original proposals available to the
existing fail-closed hierarchy resolver. Each pass rechecks the whole graph to detect newly blocked
descendants. Oversized repair evidence is refused rather than truncated.

Audit exports initial and final conflicts, attempted scopes, changed aliases, repair calls and residual
conflicts separately. Historical `GraphConflicts` rows carry before/after checkpoint labels; their
total count must not be interpreted as the number of residual conflicts. A structurally consistent
proposal is still not proof of semantically correct ancestry.

## Controlled live trial

The source-bound run from the preceding experiment is frozen: source run ID
`7b456281-c7d5-4e87-a5d6-11dfdc8f2612`, run artifact SHA-256
`5a59a58a4a21f84d321f99d74907a00332f82479f9ae18a20fa5df126d46e037`.
Selection, grouping, extents and all three initial placement request/reply pairs are held fixed.
The harness checks reconstructed placement packets for complete JSON equality before returning each
replayed response. A reproduces the original 13 unresolved parent decisions; B receives those same
responses and sends only repair requests live. No Gold is sent to the model.

| Measurement | Frozen A | Same initial proposals plus repair B |
|---|---:|---:|
| Frozen headings | 42 | 42 |
| Unresolved parents | 13 | 0 |
| Residual inconsistent chains | 13 | 0 |
| Initial placement responses replayed | 3 | 3 |
| Live provider calls | 0 | 1 |

One live call asks for the 15 aliases in the two affected branches. The model changes exactly two
relations: L0519:S0 (Agenda), NONE → ROOT, and L0556:S0 (Annex 2), NONE → ROOT. It retains every
descendant relation and every unrelated relation. No protocol failures occur. Confirmed live spend
is USD 0.000444562. The entire controlled harness takes 8.063 seconds; this is not an end-to-end
extraction time and must not be compared with the 228.179-second preceding full run.

The controlled gate passes. This establishes removal of the inspected inconsistency on frozen input,
not parent accuracy. Registered DOC-0252 Gold supports alias membership and partitions, not hierarchy.

## Full extraction

| Measurement | Previous source-bound run | Source-bound plus chain-repair option |
|---|---:|---:|
| End-to-end seconds | 228.179 | 230.220 |
| Materialized headings | 42 | 40 |
| Selected-alias TP / FP / FN | 44 / 3 / 0 | 41 / 3 / 3 |
| Selected-alias precision | 0.936170 | 0.931818 |
| Selected-alias recall | 1.000000 | 0.931818 |
| Exact Gold groups | 41 / 42 | 39 / 42 |
| Boundary precision | 0.976190 | 0.975000 |
| Boundary recall | 0.976190 | 0.928571 |
| Unresolved parents | 13 | 0 |
| Selection / grouping / placement generations | 17 / 2 / 3 | 16 / 2 / 3 |
| Live chain-repair generations | 0 | 0 |
| Total provider attempts | 22 | 21 |
| Confirmed USD | 0.021168580 | 0.022093534 |

The full run has no inconsistent parent chains after its initial placement, so no chain-repair call
is sent. Its zero unresolved parents cannot be attributed to the repair feature. The controlled
trial above is the direct evidence that the repair addresses the previous 13 blocked decisions.

All 15 actual first selection request hashes and complete scope plans are identical to the preceding
source-bound run. The only selection decision changes are in SEG-012: L0513:S0 and L0514:S0 (the
two-line ICP title at the start of the agenda annex), and L0519:S0 (Agenda), change from SELECT to
NOT_SELECT. The registered Gold includes these aliases. Thus source quotation alone does not make
heading membership stable: a correctly bound source occurrence can still be rejected semantically.
Grouping has no fallback or unresolved aliases; placement has no protocol failures. It is the
selection-stage decisions that omit the two Gold headings, not grouping or parent repair.

The previously known extra aliases L0001:S0 through L0003:S0 remain merged with L0000:S0. Gold is
checked through the current registry and unchanged. The reference has no hierarchy capability;
neither zero unresolved decisions nor zero chain conflicts constitutes a parent-accuracy score.

The development gate fails on all four membership/boundary precision/recall comparisons against the
previous source-bound run, while time, parent consistency and regression checks pass. The next
selection experiment must address the distinction between a document title, a structural title
inside an annex and title-adjacent metadata, with independent cases and bounded review. Do not
restore these aliases by hardcoded PDF072 names or by copying Gold labels into runtime decisions.

## Verification and artifacts

35 focused tests and 239 broader regression tests pass with no failures or skips. Cases cover blocked
descendant closure, reconsidering the blocker, unaffected/upstream relations, valid uncertainty,
unchanged or invalid repair responses, legacy placement behavior, hierarchy derivation, source-bound
selection, grouping, retry/budgets, PDF/DOCX/scan and architecture.

Regression TRX: `tests/DocxHeaderExtractor.Tests/TestResults/parent-chain-regression.trx`.
Artifacts: `artifacts/qualification/pdf072-parent-chain-repair-20261024/`, including the predeclared
experiment plan, 308-file source identity, isolated builds, controlled replay proof, live repair
requests/replies and controlled score. The full extraction is recorded separately under `full/`.
`full/selection-comparison.json` records matching actual request hashes and all changed decisions.
`qualification-result.json` records each gate, including failures. All 308 snapshotted source files
still match after both experiments. One sample below 300 seconds does not establish a five-minute
p95 or a guaranteed deadline.
