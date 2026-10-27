# PDF072 source-bound selection qualification — 2026-10-24

## Decision

**Keep the candidate opt-in; do not promote.** The source-binding audit and regression tests pass,
and the full run fixes the inspected membership errors. However, unresolved parents increase from
5 to 13, failing the predeclared full-pipeline gate. `PdfSourceBoundSelection` remains false by
default. The existing retry fix remains production behavior.

## Scope and evidence

Investigate the four selected-alias membership errors in the retry-fixed PDF072 baseline before
changing selection, grouping or placement policy. Gold is the registered, user-approved DOC-0252
reference: 42 heading groups containing 44 source aliases. Its capabilities allow structured alias
membership and partition scoring, but do not allow hierarchy or character-span accuracy scoring.
Gold is not changed or supplied to the model.

Source SHA-256: `a005f25e3bb9754cd6c8c7000682d00eb68238fb8937d3475fe807ffbbd94b61`.
Gold SHA-256: `a26b83e14005a9a3e4957cd7a027155e97a534a73fd737551726d18a1c695654`.

The reconstructed first request for SEG-007 matches the frozen baseline's recorded request hash,
`b93fb3584464d465ef0bacf1b4cb327a0bd8f55f380fa28bd5be29efa9931096`.
All 43 target alias/text/style bindings agree with the canonical source facts. The observed model
response attributes neighboring heading text and style to preceding body aliases:

| Alias | Actual source | Baseline | Gold |
|---|---|---|---|
| L0314:S0 | Body continuation ending in “NSOs.”, 11pt regular | SELECT | NOT_SELECT |
| L0315:S0 | “3. Terms of Reference for a Task Force on ICP Classification Update”, 11pt bold | NOT_SELECT | SELECT |
| L0333:S0 | “beneficial.”, 11pt regular | SELECT | NOT_SELECT |
| L0335:S0 | “1. The Treatment of Import and Export Prices in International Comparisons”, 11pt bold | NOT_SELECT | SELECT |

This supports a response binding failure for the inspected cases. Model reasoning is diagnostic
evidence, not an independent source of truth.

## Candidate and controls

The opt-in `PdfSourceBoundSelection` protocol requires every SELECT to include `sourceText`, copied
verbatim from the entire text of that alias, including when a heading extent covers only part of it.
The parser compares the two with ordinal equality. A mismatch refuses the response and invokes the
existing bounded model repair loop. C# never moves the decision to a different alias, infers a heading
from typography, or vetoes a semantically wrong selection whose source binding is valid.

Selection instructions, target facts, model, provider route, reasoning settings, output cap, transport
retry, grouping and placement remain the same. The selection system prompt and protocol schema change.
Inline grouping and whole-document placement remain disabled. Candidate code is frozen under the
artifact directory's `source-identity.json` and built into isolated directories.

The predeclared canary runs the same 43 owned targets in A,B,B,A order. Both B calls must complete
with exact Gold alias membership and complete target coverage before a full-document candidate run.

| Canary call | Seconds | Exact selected aliases | Repairs |
|---|---:|---|---:|
| 1 — A | 43.739 | Yes | 0 |
| 2 — B | 46.439 | Yes | 0 |
| 3 — B | 44.394 | Yes | 0 |
| 4 — A | 43.172 | No: reproduced all four original membership errors | 0 |

The first A/B user messages and non-message wire settings are identical. Both B calls select
L0315:S0, L0334:S0 and L0335:S0. Two repetitions are exploratory evidence; they do not establish
a general accuracy improvement. Exact source binding also cannot distinguish identical source texts
and cannot guarantee that a selected source occurrence is semantically a heading.

## Full-document result

| Measurement | Retry-fixed baseline | Source-bound candidate |
|---|---:|---:|
| End-to-end seconds | 255.519 | 228.179 |
| Materialized headings | 42 | 42 |
| Selected-alias TP / FP / FN | 42 / 10 / 2 | 44 / 3 / 0 |
| Selected-alias precision | 0.807692 | 0.936170 |
| Selected-alias recall | 0.954545 | 1.000000 |
| Exact Gold alias groups | 37 / 42 | 41 / 42 |
| Boundary precision / recall / F1 | 0.880952 | 0.976190 |
| Selection generations | 15 | 17 |
| Grouping generations | 2 | 2 |
| Placement generations | 4 | 3 |
| Total provider attempts / generations | 21 | 22 |
| HTTP 429 observations | 0 | 0 |
| Unresolved parents | 5 | 13 |
| Grouping fallback runs | 0 | 0 |
| Confirmed USD | 0.021409339 | 0.021168580 |

All 15 selection scopes complete, covering all 650 target aliases with no unresolved membership.
The candidate adds the two missing Gold aliases L0315:S0 and L0335:S0 and removes seven false
positives: L0314:S0, L0333:S0, L0515:S0 through L0518:S0, and L0550:S0. No previously correct
selected alias is lost. The remaining false positives L0001:S0 through L0003:S0 are merged with
the document title L0000:S0; this is the single incorrect alias group against Gold.

Two selection repairs in SEG-003 correct optional headingExtent offsets. All live candidate SELECT
quotes bind to their own source alias; none exercises a sourceText-mismatch repair. The live
membership improvement is therefore evidence for the candidate protocol/prompt as a whole, not
proof that the runtime mismatch check caused it. The callback file contains 19 selection/grouping
replies combined; the three placement generations are recorded separately in the run audit.

The 13 unresolved parent decisions are in the agenda and participant annexes. The proposal marks
L0519:S0 (Agenda) and L0556:S0 (Annex 2) as NONE, yet assigns later headings directly or indirectly
under them. The resolver cannot derive an in-tree depth from those chains. Eight shared aliases
become newly unresolved: L0536:S0, L0539:S0, L0540:S0, L0553:S0, L0557:S0, L0566:S0, L0576:S0
and L0587:S0. This is observable inconsistency, not a Gold-scored claim of parent accuracy.
Placement has no protocol failures, but the reported zero graph conflicts does not establish a
usable hierarchy: unresolved descendants still exist.

One full candidate run below 300 seconds does not establish p95, a guaranteed five-minute return,
or a causal speedup versus the earlier frozen baseline. Broader qualification is not attempted after
the no-regression gate fails. Further work should address parent proposals that reference NONE or
unresolved ancestors, retain source grounding, and qualify the residual title/metadata boundary.

## Verification and artifacts

230 relevant regression tests passed with no failures or skips. They cover source binding, legacy
protocol compatibility, retry and budgets, selection, grouping, placement, PDF, DOCX, scan and
architecture. New negative cases reject neighboring text, missing/null text, extra whitespace, and
sourceText on a non-SELECT decision. A source-bound but semantically wrong selection remains accepted.
TRX: `tests/DocxHeaderExtractor.Tests/TestResults/source-bound-regression.trx`.
All 305 files in the experiment source identity still match their snapshot hashes after the run.
The registered Gold hash is unchanged. Detailed score and change sets are in `qualification-result.json`.

Artifacts: `artifacts/qualification/pdf072-membership-audit-20261024/` contains `audit.json`, canonical
source facts, reconstructed requests, the predeclared experiment plan, source identity, isolated
binaries, all canary outcomes and traces, Gold-checked canary scores, and the full-document run.
