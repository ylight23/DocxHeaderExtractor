# Pairwise grouping revision 2: composite titles versus structural levels

The first pairwise revision is frozen and rejected: independent PDF076 repeats both split a true
two-line title, and PDF072 splits its appendix title once. These failures remain under the original
source identity. None is replaced with a favorable repetition or counted as a successful full run.

Revision 2 keeps the pairwise protocol, exact-source parser and measured facts unchanged. It clarifies
that document type/program and organization/committee/meeting names can jointly complete one local
document/event title. Complete noun phrases and different fonts alone do not establish independent
content units. Program/organization containment is not a section/subsection relationship. Separately
labelled structural units still retain boundaries. No document name, alias or Gold label appears in
runtime text. Custom instructions retain precedence. Selector identity includes the pairwise prompt
hash, as the request and cache keys already do.

The predeclared plan repeats the same controlled fixtures twice each on PDF072 and PDF076. In addition
to the preceding numeric gates, every expected Gold group spanning multiple aliases must remain
intact on both documents, in controlled and full runs. This strengthens the title protection after
an observed regression; it does not waive a failure. Failed gates prevent advancement or promotion.
No semantic verifier or C# semantic splitter is introduced. Experimental flags remain false by default.

If controlled gates pass, two full PDF072 samples and an independent full PDF076 must meet their
recorded membership, exact-group, coverage, placement-consistency and under-300-second gates before
general promotion. Gold remains unchanged and is never sent to the provider. It does not support
hierarchy scoring; consistent parents are not independently verified parent correctness. Controlled
grouping time is not end-to-end extraction latency, and a few samples do not establish a general SLA.

Artifacts and frozen plan: `artifacts/qualification/pdf072-pairwise-grouping-v2-20261024/`.
Source identity records 394 C# and project files across src/tools/tests.

The final revision passes 17 focused tests and 260 broader regression tests with no failures or
skips (`pdf-pairwise-v2-focused.trx`, `pdf-pairwise-v2-regression.trx`). The broader run occurs without
a concurrent build. It covers the source-bound pair parser/fold/repair, selected spans, selection
request invariance, default/custom behavior, grouping, retry budgets, parent repair, PDF/DOCX/scan
and architecture. Unit results alone do not establish model boundary accuracy.

## Controlled results

All four predeclared controlled repetitions pass the strengthened gate. Every expected multiline
Gold group remains intact, every selected alias appears once, and there are no protocol failures
or fallback groups. Gold is checked by the scorer after inference, never in the provider payload.

| Frozen selection | Repetition | Gold aliases retained | Exact Gold groups | Grouping-only seconds |
|---|---:|---:|---:|---:|
| PDF072 full | 1 | 44/44, one existing extra | 41/42 | 45.052 |
| PDF072 full | 2 | 44/44, one existing extra | 41/42 | 29.945 |
| PDF076 first scope | 1 | 5/5, no extras | 4/4 | 20.855 |
| PDF076 first scope | 2 | 5/5, no extras | 4/4 | 21.081 |

PDF072's sole remaining group mismatch is L0000:S0 + L0001:S0 rather than L0000:S0 alone, because
L0001:S0 is already selected in the frozen input. Grouping has no authority to remove it. The
appendix title, genuine wrapped title and independent day/session/section/subsection units are
preserved correctly in both repetitions. PDF076's genuine two-line title is joined in both.
These tests open the full-extraction gate; they do not establish initial-selection or parent accuracy.

## Full results and independent blocker

Both predeclared full PDF072 samples pass the recorded gates, including intact Gold multiline titles,
complete 650-alias/15-scope coverage, no grouping fallback or residual parent conflicts, and time <300 s.

| Sample | Seconds | Gold aliases | Extra aliases | Exact Gold groups | Output headings | Unresolved parents |
|---|---:|---:|---:|---:|---:|---:|
| full-01 | 241.105 | 44/44 | 2 | 41/42 | 43 | 0 |
| full-02 | 253.476 | 44/44 | 3 | 41/42 | 44 | 0 |

The main title includes the selected extra L0001:S0; L0550:S0 is another selection extra in both,
and L0554:S0 is extra in full-02. These are not silently removed by grouping. All genuine independent
day/session/section/subsection and multiline-title boundaries are preserved. Both grouping batches
in full-02 complete in one post with zero grouping repairs; the additional model callbacks belong
to selection repair. Full-01's whole-alias extent audit finds no partial Gold-alias extents. The
per-heading output and every membership change are exported under each full sample's `heading-review.md`.

The independent full PDF076 completes in 174.436 seconds but fails selection: 27/35 Gold aliases
are selected, zero extras, yielding 26/34 exact groups and zero incorrect emitted groups. Eight
institutional participant-subgroup labels (L0427, L0430, L0433, L0436, L0439, L0443, L0446, L0448,
all :S0) are already raw NOT_SELECT in complete SEG-012, and match accepted decisions. The same unit
selects the first institutional subgroup label but describes the remaining targets as participant
entries. Its public JSON explanation is fallible model commentary; the raw decisions and source
text establish the omission. `independent-full/omission-audit.json` records raw-to-accepted agreement.
Scope/source coverage and grouping/placement consistency checks pass; Gold cannot validate hierarchy.

General promotion is withheld. The grouping change has positive measured evidence, but the combined
pipeline still misses genuine headings on the independent document. All 394 snapshotted source files
match before a separate membership revision. `qualification-result.json` retains every failed gate.
The next membership revision addresses institutional labels introducing participant subgroups while
keeping individual entries and affiliations within an entry as body, without changing grouping or Gold.
