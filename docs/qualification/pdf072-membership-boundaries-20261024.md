# PDF072: separate membership and grouping instructions

## Evidence and candidate

The preceding V5 candidate is rejected. Its two full samples select the same complete 44 Gold aliases
plus one extra, but return 42 versus 35 headings. First grouping requests have identical SHA-256s.
The second response merges section/subsection and agenda/day/session boundaries. No selected alias
is dropped by C#; the model supplies valid but semantically wrong partitions. Historical omissions
of title/Agenda aliases were separately raw NOT_SELECT decisions, including under serial scheduling.

The new opt-in PdfSeparateGroupingInstruction keeps V5 selection/source-bound requests unchanged.
Grouping instead receives a boundary-only instruction: adjacency proposes no heading; genuine title
continuation is distinct from a containing section immediately followed by an independently named
subsection or agenda/day/session unit. Every selected alias remains required exactly once. The model
still chooses boundaries; no C# text rule or Gold-based split is added. Group request hashes and cache
keys incorporate the actual grouping instruction. An explicit custom instruction continues to govern
both stages. Default PDF, DOCX and scan paths retain their existing instruction behavior.

The possible influence of the shared membership wording is an experimental hypothesis, not a proven
cause of V5's variation. Controlled grouping trials fix membership and extents and change the grouping
instruction only. Two PDF072 repetitions must recover at least 41/42 exact groups without losing any
Gold alias. Two PDF076 first-scope repetitions must retain all four exact groups, including its real
two-line title. All selected aliases must be covered once, with no fallback or failures.

If the controlled gate passes, two full PDF072 samples use the V5 membership instruction, source binding,
separate grouping instruction and chain repair. Optional membership review is disabled in this combined
candidate: it changed no membership in the preceding full samples but added latency and a 429 failure.
Therefore the full runs are not a single-factor causal experiment. Both must meet the predeclared
membership, boundary, coverage, placement and under-300-second gates; no unchanged failed provider
trial is repeated until favorable. General promotion requires further independent full-document evidence.

## Verification record

Eight focused tests pass, including byte-identical selection requests between the two grouping settings,
complete output coverage and preservation of custom instructions. The first broad run during an isolated
build has 246 pass / 1 failure: the existing shared-gate cancellation test exceeds its five-second handler
barrier. The failure is preserved in `membership-boundaries-regression.trx`. That same test passes
alone after the build, in 267 ms (`membership-boundaries-gate-isolated.trx`). A full no-build rerun checks
whether the failure repeats without build contention; no runtime retry change is inferred from it.
The complete no-build rerun passes all 247 tests without failures or skips in 1 minute 8 seconds
(`membership-boundaries-regression-no-build.trx`). No test or runtime source changed between the
failed and passing broad runs. The timing failure remains recorded; this does not prove its cause.

Artifacts and plan: `artifacts/qualification/pdf072-membership-boundaries-20261024/`. The source identity
snapshots 392 C# and project files across src/tools/tests, including the test code. Provider trials use
no Gold input; scorers verify the registry Gold hashes. Gold has no hierarchy accuracy capability.

## Controlled result and decision

Both predeclared PDF072 repetitions run against the same frozen 45 selected aliases (44 correct,
one extra) and exact extents. Membership remains unchanged and every alias is covered exactly once.
There are no protocol failures, fallback groups, retries, or source changes during these trials.

| Repetition | Seconds, grouping only | Output headings | Exact Gold groups | Gate |
|---|---:|---:|---:|---|
| 1 | 23.449 | 41 | 39/42 | Fail |
| 2 | 23.159 | 42 | 41/42 | Pass |

The first repetition keeps DAY 2 (L0539:S0) and SESSION V (L0540:S0) in one group, as well as the
known title/extra-line group L0000:S0 + L0001:S0. The second separates DAY 2 and SESSION V but retains
the title extra. Both complete raw partitions and scores are retained. The explicit boundary wording
does not eliminate model variation; one successful repetition is not a replacement for the failed one.

The conjunctive controlled gate fails. PDF076 and the two new full extractions are **not run**, because
they cannot make this failed gate pass. Their absence is not presented as success or independent
validation. No candidate is promoted, and PdfSeparateGroupingInstruction remains false by default.
The new code is a tested opt-in candidate, not a completed production accuracy fix. The selection
omission repair has positive evidence (four controlled membership trials and complete initial selection
in two full runs), while stable boundary accuracy and the remaining title extra are still unresolved.

The evidence now distinguishes two failure mechanisms: raw NOT_SELECT for missing title/Agenda aliases,
and valid but incorrect grouping that merges already selected headings. Neither inspected mechanism
is a lost parallel collector result. A next boundary intervention needs model review/verification with
focused repair and independent context checks, not unconditional C# splitting or a larger batch. It
must pass the same gates before promotion. Source/Gold verification and the rejection are saved in
`qualification-result.json`; the original IDE result files are preserved.
