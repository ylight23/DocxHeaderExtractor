# Qualification of fewer serial heading calls — 2026-10-24

## Decision

**Do not promote either candidate.** Production defaults remain `PdfInlineGrouping=false` and
`WholeDocumentPlacement=false`. The previously implemented retry fix remains production behavior.
Both candidates are implemented behind explicit pipeline options for further qualification.

## Method

Source: PDF072, SHA-256 `a005f25e3bb9754cd6c8c7000682d00eb68238fb8937d3475fe807ffbbd94b61`.
Baseline was built into an isolated directory before source edits. Candidate source files were
hashed and compiled into a separate directory. Provider calls were sequential between runs,
using the same Qwen model, Alibaba route, reasoning settings and source evidence. No writeback.

Gold was resolved through `gold-current/registry.v1.json`: DOC-0252, 42 headings, approved by the
user; Gold SHA-256 `a26b83e14005a9a3e4957cd7a027155e97a534a73fd737551726d18a1c695654`.
Source and Gold hashes were checked. Gold supports structured alias parts, not hierarchy or
character spans. Scores below measure alias membership and alias partition boundaries; no
parent accuracy or character-span score is asserted. Gold was not modified.

## Live results

| Measurement | Retry-fixed baseline | Selection with inline grouping |
|---|---:|---:|
| End-to-end seconds | 255.519 | 253.140 |
| Materialized headings | 42 | 40 |
| Exact Gold alias groups | 37 / 42 | 36 / 42 |
| Boundary precision | 0.880952 | 0.900000 |
| Boundary recall | 0.880952 | 0.857143 |
| Boundary F1 | 0.880952 | 0.878049 |
| Selected-alias precision | 0.807692 | 0.893617 |
| Selected-alias recall | 0.954545 | 0.954545 |
| Grouping HTTP generations | 2 | 0 |
| Selection HTTP generations | 15 | 18 |
| Placement generations | 4 | 3 |
| Total provider attempts | 21 | 21 |
| HTTP 429 observations | 0 | 0 |
| Unresolved parents | 5 | 7 |
| Confirmed USD | 0.021409339 | 0.022963487 |

Inline selection completed all 15 units, with zero grouping fallback. It removed five false-positive
selected aliases, but merged the first document-title block with Session I, and Session II with its
first agenda item. Those are structurally legal groups that disagree with Gold. Three extra selection
calls repaired source extents, consuming the two saved grouping calls and the saved placement call.
The lower elapsed time in one sample is not a demonstrated speedup; parent unresolved count and
boundary recall regress, so the candidate fails the predeclared gate.

## Controlled hierarchy trial

The accepted baseline selection, grouping and extents were frozen, without Gold feedback, and fed
back through the normal pipeline. All 42 materialized heading texts and source IDs were identical.
Only placement was called live. This isolates hierarchy from selection variability.

| Placement measurement | Baseline | Whole-document proposal |
|---|---:|---:|
| Generations | 4 | 1 |
| Placement seconds | 13.3 | 10.1 |
| Unresolved parents | 5 | 14 |
| Graph conflicts | 0 | 0 |

The frozen-selection trial's 22.819-second total includes source parsing and assembly, with no live
selection or grouping. It must not be compared against the baseline's end-to-end 255.519 seconds.
Nine parent proposals changed. The unresolved regression rejects promotion even before any semantic
parent judgement; Gold has no hierarchy capability, so no hierarchy-accuracy claim is made.

Combined mode was not called live because the independent candidates failed quality gates. Failed
candidates were not rerun selectively until a favorable sample appeared. The baseline's single run
below 300 seconds does not establish p95 or prove that the retry change caused the time difference
from older runs: neither current baseline nor inline run received HTTP 429.

## Code and verification

- `InlineGrouping` parses a complete local partition of selected targets. Invalid proposals are
  audited and returned to the existing grouping step; membership is not rewritten.
- Only a complete selected run owned by one completed unit can use inline groups. Cross-scope
  runs, unresolved boundaries and runs adjacent to unknown scopes retain the existing grouping call.
- Whole-document placement measures the complete wire body, prompt estimate and enforced output cap,
  including the full heading catalog and source context. Oversized requests split into ordered batches;
  unsplittable evidence is refused. Accepted rows are supplied to focused protocol repair.
- Defaults remain disabled; DOCX and scan production behavior stays on the established route.
- **222 regression tests passed**, including new partition/coverage tests, mixed inline/deferred runs,
  measured placement, focused repair, oversized refusal, provider retry, architecture, legacy request
  parity, PDF, DOCX and scan. Result: `tests/DocxHeaderExtractor.Tests/TestResults/serial-calls-regression.trx`.

Artifacts: `artifacts/qualification/pdf072-serial-calls-20261024/` contains the predeclared plan,
baseline/candidate source identities, isolated binaries, raw local model traces, complete run results,
hash-checked Gold scores and frozen-selection hierarchy comparison. Artifacts contain source text.

Future qualification must resolve boundary overmerging and uncertain parent proposals before wider
canaries and repeat runs. Runtime validators are source/structure guards, not semantic proof.
