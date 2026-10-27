# PDF072 membership repair: V5 local unit versus attributes

## Audit finding

The missed agenda aliases are explicit NOT_SELECT responses, not lost results. All 650 aliases are
owned once in 15 complete scopes, and raw decisions equal collected decisions. Forced completion-order
tests at parallelism 1/6/6 preserve every request identity and final decision. The 16-call live study
also reproduces the three-alias omission under serial scheduling. Parallelism is therefore not
necessary for these omissions; this does not rule out unknown provider-load effects on inference.

## Revision and preserved failures

V2 loses genuine institutional titles. V3's bounded review corrects some titles but remains unstable.
V4 recovers all 44 Gold aliases in both frozen PDF072 repetitions, but has 7/3 false positives against
the initial 3, so it fails. Every result is preserved under its own plan and source identity; no
unchanged revision is repeated selectively until a favorable score appears.

V5 keeps the V4 affirmative local-title and section-label rule and distinguishes a content unit from
attributes of that same unit. An occurrence supplying only when/where/address/meeting-format details
is judged separately from the unit's title. A dated divider can still be a heading when introducing
its own following content. SELECT reasons identify the content unit being named, introduced or divided.
These are semantic instructions to the model, not keyword filters or automatic C# label overrides.

The runtime mechanism remains explicitly opt-in bounded review: at most two complete scopes, chosen
by first scope and strongest measured font size; at most two conversation posts per reviewed scope;
source-bound V1 and extent validation; only a complete valid replacement applies. A failed or incomplete
review preserves the original decisions and records a failure. All review inputs are frozen before
concurrent calls. Facts route review but never decide membership. Grouping follows the final membership
and parent placement remains separate. No document name, alias or Gold label appears in V5's prompt.

## Qualification

The predeclared controlled trials freeze the previous failed PDF072 selection and the failed PDF076
first-page selection, with two repetitions each. Initial selection/grouping/placement are not live.
Both repetitions must recover all Gold aliases in their original complete target universe, avoid
additional false positives and strictly reduce them where initially present, with valid complete
reviews. A conjunctive failed gate stops progression. The subsequent full gate requires two samples,
no missed aliases, at most three extras, at least 41/42 exact Gold groups, no fallback/placement failures
or residual chain conflicts, at most five unresolved parents, and each full sample under 300 seconds.

Source identity: 317 snapshotted code files. Artifacts, isolated builds, raw local traces and scores:
`artifacts/qualification/pdf072-membership-v5-review-20261024/`. Gold is used by scorers only, is checked
against the registry, and is unchanged. Gold has no hierarchy accuracy capability.

### Controlled results

All four predeclared frozen-input repetitions pass; the full-extraction gate is therefore opened.
These elapsed times measure review only, not document extraction.

| Document / repetition | Gold aliases before → after | Extras before → after | Misses before → after | Review seconds |
|---|---:|---:|---:|---:|
| PDF072 / 1 | 41 → 44 | 3 → 1 | 3 → 0 | 52.001 |
| PDF072 / 2 | 41 → 44 | 3 → 1 | 3 → 0 | 56.929 |
| PDF076 first scope / 1 | 3 → 5 | 0 → 0 | 2 → 0 | 45.830 |
| PDF076 first scope / 2 | 3 → 5 | 0 → 0 | 2 → 0 | 52.584 |

In both PDF072 repetitions, the only changed decisions are L0002:S0 and L0003:S0 SELECT → NOT_SELECT,
and L0513:S0, L0514:S0, L0519:S0 NOT_SELECT → SELECT. L0001:S0 remains an extra. All 650 owned targets
remain covered. PDF076's 33 owned first-scope targets remain covered; the rest of that document is
outside this controlled fixture. All reviews apply completely without failures. The score and raw
decisions are retained under `controlled/`; no result from another revision substitutes for a V5 run.

All 245 broader regression tests pass without skips. The isolated full harness builds without errors
(seven existing analyzer warnings). Before full extraction, all 317 snapshotted source hashes and
both registry Gold hashes match. Experimental flags remain false by default pending full results.

### First full extraction

The first predeclared full sample completes in 297.184 seconds, selecting all 44 Gold aliases and
one extra (L0001:S0), with 42 output headings and 41/42 exact Gold groups. The remaining boundary
error is the first group: L0000:S0 + L0001:S0 instead of L0000:S0 alone. All 650 aliases are covered
exactly once by 15 complete scopes. No grouping fallback or unresolved membership remains.

The sample nevertheless fails qualification: REVIEW-SEG-000 returns
`CALL:TRANSPORT:RATE_LIMITED_NO_RETRY` (HTTP 429), so its original complete selection is preserved.
The attempt audit shows four HTTP 429 sends for that logical review (retry indices 0–3); the retry
owner exhausts its configured limit. The failure token is not evidence that no retry occurred.
REVIEW-SEG-012 completes. Initial selection already has the final 44 correct / 1 extra membership;
the failed review must not be counted as a successful repair. The semantic-lane summary says 15/15
initial scopes complete, but its failureClass explicitly records the failed review.

Parent-chain repair makes one call and changes three blocking ancestors (L0513:S0, L0539:S0,
L0556:S0); the 13 initial conflicts fall to zero final conflicts and zero unresolved parents.
GraphConflicts records the **before-chain-repair** history; it does not denote remaining conflicts.
The full scorer checks GraphUnresolvedAliases and ChainRepair.FinalConflicts for the residual gate
and retains the historical conflicts in the score. This is a structural check, not Gold hierarchy
accuracy. There are no placement failures. Total: 27 provider attempts, USD 0.024995306.

The two-sample gate remains failed even if the second sample succeeds: every predeclared sample must
pass. Neither a successful initial selection nor a run just below five minutes excuses a failed
review. No production promotion is made on the first sample's evidence.

### Second full extraction and rejection

The second sample completes in 274.505 seconds. Initial and final membership both contain all
44 Gold aliases and one extra; both reviews complete without changing membership. However, grouping
returns only 35 headings and 28/42 exact Gold groups. Seven extra groups replace fourteen expected
groups: the title plus L0001:S0, session/subsection pairs L0025/26, L0165/66, L0271/72, L0334/35,
the Agenda/Day/Session triple L0519/20/21, and the L0539/40 pair. Grouping's first-request hashes are
identical across the two full samples. The model emitted these merges in valid responses; the
collector preserves every selected alias, and schema/coverage validation cannot detect a semantic
false merge. This is a boundary failure, not new missing SELECT decisions. Both full samples' complete
per-heading tables, alias changes and parent outputs are exported to `full-*/heading-review.md` and
`headings.json`. All 317 source hashes still match after both samples.

V5 is **rejected for production**. Four controlled membership-repair trials pass, but full-01 fails
review completion and full-02 fails boundary accuracy. Both elapsed times are under 300 seconds;
that does not override the quality/completeness gates. Flags remain false by default and all failures
are retained in `qualification-result.json`. The next candidate must address grouping separately:
selection's positive title criteria and SELECT-reason instructions are currently also sent as the
grouping instruction. Their causal role in the variable merges has not yet been established.
