# PDF072: explicit source-bound boundary decisions

## Change and evidence

The prior whole-group contract admits semantically wrong merges as long as aliases cover each run
in order. The earlier grouping requests contain selected text, extents and compact box/font data,
but omit the document style table and the measured block/style facts available to selection. The
instruction-only candidate still varies (39/42 versus 41/42 exact groups on fixed membership).
The removed semantic verifier remains qualification-only; it is not reintroduced as authority.

PdfPairwiseGrouping is a new opt-in candidate. Each adjacent pair in a selected run has one explicit
JOIN or SPLIT verdict. JOIN continues the same title; SPLIT begins an independently named heading.
The model echoes the exact left/right aliases and selectedText, with a short boundary justification.
C# validates pair ownership, order, coverage, exact selected spans, JSON shape and allowed decisions,
then folds the model decisions into groups. Existing group coverage/contiguity/maximum-size checks
still apply. It never changes JOIN to SPLIT based on text, a keyword or Gold.

Each line also receives its existing measured layout/block/style facts; the request includes the
document style table and at most three source context lines before/after each run. Wider context is
marked context-only, without falsely labeling a selected line from another run NOT_SELECT. Facts
are measurements, not semantic boundary labels. No review stage or additional provider call is added.

Malformed or missing pair decisions receive bounded protocol repair. Exhaustion retains fallback
provenance and the selector withholds those aliases as unresolved, as before. No partial response
causes an inferred boundary. Request/cache hashes and selector identity distinguish the new contract.
Selection bytes, source extents and membership are preserved. PDF inline grouping is incompatible
with this separate stage; DOCX, scan and default PDF behavior remain unchanged.

## Qualification plan

The plan is frozen under `artifacts/qualification/pdf072-pairwise-grouping-20261024/experiment-plan.md`.
Two controlled repetitions each use the failed V5 full PDF072 selection and the completed V5 PDF076
first-scope selection, keeping membership/extents fixed. PDF072 must reach >=41/42 exact groups with
all 44 Gold aliases; PDF076 must retain all four exact groups, including its genuine two-line title.
Every selected alias must appear once, without fallback or failures. Failed conjunctive gates stop
progression; unchanged failed trials are not repeated until favorable.

If controlled trials pass, two full PDF072 samples must meet the predefined membership, boundary,
coverage, parent consistency and <300-second gates. An independent full PDF076 must also have exact
registered membership and groups before general promotion. Optional membership review and inline
grouping are disabled in this combined candidate. Gold is used only by scorers and its hashes are
verified against the registry. It has no hierarchy-evaluation capability, so parent consistency is
not claimed as hierarchy accuracy. No universal five-minute SLA follows from these samples.

## Verification

The initial focused run passes 17 tests without failures or skips. Tests cover model-decided JOIN/SPLIT
folding, exact selected extents, invalid alias/text/coverage/decision/reason/duplicate-run responses,
oversized joins, bounded repair/fallback, cache identity, selection-byte invariance and source-fact
immutability. A wider run checks the final source after the wider-context membership-label correction.
The source identity freezes 394 C# and project files across src/tools/tests before provider trials.

The final broader run passes 260/260 tests without failures or skips (`pdf-pairwise-regression.trx`).
Both isolated harnesses build without errors; seven existing analyzer warnings remain.

## Controlled results: rejected

PDF072 repetitions score 41/42 and 42/42 exact Gold groups, preserving all 44 Gold aliases and the
one frozen membership extra. The first splits the two-line appendix title; the second preserves
all expected groups and emits the existing extra as its own heading. Previously merged independent
day/session and section/subsection units stay separate. Times are grouping-only: 37.515 / 31.352 s.

PDF076 fails both repetitions: all five selected aliases remain, but its true two-line title is split,
so only 3/4 expected groups match (20.165 / 18.750 s). All four responses complete with no protocol
failures/fallback. Source hashes still match before the next revision. The independent gate exposes
a false-split regression; it prevents promotion despite the improved PDF072 score. No full extraction
or unchanged favorable retry is run. `qualification-result.json` preserves the rejection.

The next revision retains the contract/parser/facts and distinguishes composite program/group/event
titles from actual labelled section/subsection levels. It must preserve real wrapped/composite titles
on both sources while keeping independent headings separate. No document alias or Gold label is added
to runtime instructions; the new prompt receives a distinct identity and its own frozen experiment.
