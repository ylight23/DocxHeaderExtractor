# PDF072 local-title membership and bounded review — 2026-10-24

## Why this revision exists

The scheduler audit and V2 study are recorded in `pdf072-membership-v2-20261024.md`. Local collection
owns all 650 aliases exactly once, preserves every accepted provider decision, and passes forced
completion-order tests at parallelism 1/6/6. In the live comparison, V2 omits the three agenda aliases
even when run serially. Thus parallelism is not necessary for the inspected omission. This does not
rule out hidden provider-load effects on semantic inference.

V2 is rejected. Its wording about organizational labels as descriptive metadata reduced false positives
but also suppressed genuine titles on PDF072 and PDF076. V3 explicitly treats institutional wording
as neither an inclusion nor exclusion rule: judge whether it names the local content unit. It retains
the distinction between a multi-line title and the date/location/format next to it, and separates
membership from parent placement. No document name, source alias or Gold label appears in the rule.

## Code behavior

The opt-in `PdfBoundaryReview` candidate reconsiders at most two complete scopes before grouping:
the first complete scope, and one other with the largest measured font size, ties in source order.
Style routes review; it never selects or rejects a source occurrence. This fixed cap is not exhaustive
coverage of every possible boundary or omission in a document.

The reviewer sees the complete scope's normal text/facts/context, earlier decisions as fallible data,
and a source-ordered catalog of initially selected lines from the document. It uses source-bound V1,
including exact sourceText validation and checked optional extents. No new parser semantics are added.
Only a complete, structurally valid replacement of that owned scope applies. An invalid or incomplete
review keeps the original decisions and records a failure. A valid semantically wrong decision can
still pass; the runtime has no independent semantic oracle.

Each review has at most two conversation posts; transport remains bounded by the shared retry layer,
and all calls use existing request/context/output and run cost/attempt ceilings. The two scopes may
run concurrently. Review inputs are frozen before either result applies, avoiding cross-scope mutation
or completion-order feedback. Decisions outside the planned scopes are untouched. Audit events retain
before/after decisions, whether the review applied, post/repair counts and request identities. Selection
and review wall times are reported separately without counting the review interval twice.

V2 and V3 are explicit instruction versions. Custom user instructions retain precedence. Selector IDs
include the instruction hash and review flag. `PdfClarifyMembership`, `PdfBoundaryReview`, source binding
and focused parent repair remain disabled by default pending qualification. DOCX/scan defaults do not
enter this PDF-only review branch.

## Qualification method

The controlled experiment freezes the previous full PDF072 selection with the three missed agenda
aliases and three cover false positives, and V2's serial PDF076 first-page selection with its two
missing title aliases. Only C's review calls go live. Each fixture has two predeclared repetitions.
Gold is used only by a separate scorer. These controlled times are not end-to-end extraction times.
The full criteria and subsequent two-run extraction gate are frozen in the artifact experiment plan.

Artifacts: `artifacts/qualification/pdf072-membership-v3-review-20261024/`, with a 315-file source
identity, isolated builds, controlled fixtures/outcomes, request/reply traces and scores.

## Result

V3 is rejected and not advanced to full extraction. The four predeclared controlled outcomes are:

| Frozen fixture | Repetition | TP | FP | FN | Review failures |
|---|---:|---:|---:|---:|---:|
| PDF072, initially TP41/FP3/FN3 | 1 | 43 | 1 | 1 | 0 |
| PDF072, same initial decisions | 2 | 41 | 1 | 3 | 0 |
| PDF076 first scope, initially TP3/FP0/FN2 | 1 | 5 | 0 | 0 | 0 |
| PDF076, same initial decisions | 2 | 5 | 0 | 0 | 0 |

All reviews apply with complete coverage; that structural validity does not establish correctness.
PDF072 repetition 1 recovers both title aliases but still rejects L0519:S0. Repetition 2 rejects
all three again. Both retain only one original cover false positive, L0001:S0. PDF076 succeeds twice.
The controlled durations are 41.424/45.656 seconds for PDF072 and 43.694/62.643 for PDF076, with no
live initial selection or grouping. They are not comparable to a full pipeline's latency.

245 regression tests pass, including the new review behavior. All 315 snapshotted source files match
before moving to the next revision. V3's qualification-result.json preserves its failed gate. The next
revision tests a shorter affirmative instruction with the same review mechanism; it does not count a
later favorable sample as a success of V3.
