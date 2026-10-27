# Membership V6 with pairwise grouping

> On 2026-10-25 a general V7 prompt candidate was tested. Its PDF072 controlled gate
> failed, so the previously qualified V6 selection and grouping defaults remain active.
> See [V7 prompt and trial results](pdf-general-structural-prompt-20261025.md).

> Gold correction, 2026-10-25: the user explicitly approved the first title as
> `MINUTES OF THE INTERNATIONAL COMPARISON PROGRAM TECHNICAL ADVISORY GROUP`,
> comprising `L0000:S0 + L0001:S0`. Both saved full PDF072 runs now score 45/45 aliases
> and 42/42 exact alias groups, with no extras or omissions. Their original elapsed times
> remain 236.614 and 248.588 seconds. The earlier “metadata extra” diagnosis for L0001
> is superseded. Measurements below retain their original Gold vintage and are historical;
> no inference was rerun and no prior score file was overwritten. See the
> [correction and route prompt audit](pdf072-gold-correction-and-route-prompt-audit-20261025.md).

## Independent omission audit

Pairwise grouping revision 2 passes four controlled trials and both complete PDF072 gates, under
300 seconds without missed Gold aliases. Independent PDF076 preserves every emitted group correctly
but fails overall: eight institutional participant-subgroup labels are raw NOT_SELECT in SEG-012.
The accepted decisions match those raw responses. The same unit selects the first institutional
subgroup label, then treats subsequent organizational labels as ordinary participant-list content.
This is a membership failure before grouping; no collector loss or grouping omission is observed.

The previous pipeline is not promoted. All results, including this independent failure, remain
under `pdf072-pairwise-grouping-v2-20261024/` and its frozen 394-file identity.

## Candidate and controlled gate

V6 appends a generic membership rule: an institutional/category label introducing its following
participant subgroup is a structural subheading; every peer label gets the same local-function
test. Individual entries, list markers and affiliations inside one person's entry remain body.
Style/block repetition supplies evidence and never an automatic C# membership label. The grouping
contract, exact-source parser, context facts and title-composition guidance remain unchanged.
Custom instruction precedence is retained. No document name, alias or Gold label is in runtime text.

The controlled INITIAL-selection trials run the actual AgentUnit/AgentModel, with complete original
PDF072 scopes 0/7/12/13/14 and PDF076 scope 12 twice each. Frozen decisions define the baseline and
evaluation universe but never enter the initial request. Preflight validates exact target-set identity;
it also compares V5/V6 first request bodies and permits only instruction/instructionSha256 to differ.
Every complete scope must return every original target once, miss no Gold alias, and increase no
extras over the frozen cohort (PDF072 three, PDF076 zero). These are initial membership tests, not
the older semantic-review experiment. Provider/schema failures count. No unchanged failed trial is
selectively repeated. Source binding is unchanged and no semantic C# veto is added.

If that gate passes, two full PDF072 samples and an independent full PDF076 use V6 membership,
source binding, pairwise grouping revision 2 and parent-chain repair, with optional membership review
and inline grouping disabled. The predeclared plan retains the under-300-second, membership,
exact-group, intact multiline-title, coverage and parent-consistency gates. General promotion is
withheld unless all required gates pass. Gold remains registry-verified and is used only after
inference. It has no hierarchy-scoring capability; a few samples do not establish a universal SLA.

Plan, isolated builds, requests, raw replies, outcomes and scores:
`artifacts/qualification/pdf072-membership-v6-pairwise-20261024/`.
The final pre-provider identity freezes 395 C# and project files across src/tools/tests. An unused
earlier identity is retained before adding the harness-only instruction-ablation preflight; no
provider call used that earlier identity. The focused run passes 17 tests without failures or skips.

The wider no-build regression passes 260/260 tests without failures or skips in 1 minute 23 seconds
(`membership-v6-pairwise-regression.trx`). The isolated controlled/full harness builds succeed with
seven existing analyzer warnings. All 395 frozen code/project hashes match before provider calls.

## Controlled initial-selection results

All four predeclared repetitions pass. Request-ablation preflight succeeds for every tested scope;
wire bodies contain neither selectionReview nor previous decisions. Each valid initial outcome
exactly covers its original targets. The scorer's reused allReviewsApplied field denotes complete
application of the INITIAL trial outcome here, not an extra model-review stage.

| Cohort | Repetition | Gold aliases before → after | Extras before → after | Seconds |
|---|---:|---:|---:|---:|
| PDF076 participant scope | 1 | 2/10 → 10/10 | 0 → 0 | 59.926 |
| PDF076 participant scope | 2 | 2/10 → 10/10 | 0 → 0 | 57.506 |
| PDF072 five scopes | 1 | 24/24 → 24/24 | 3 → 2 | 72.652 |
| PDF072 five scopes | 2 | 24/24 → 24/24 | 3 → 3 | 84.001 |

Both PDF076 initial responses recover all eight previously omitted institutional subgroup labels.
PDF072 retains every expected alias in the tested title/agenda/participant cases, including the
earlier L0315/L0335 source-binding cases, with no increase in extras. These selection-only results
open the full gate; they do not claim full-document grouping/parent accuracy or end-to-end latency.

## Full qualification

All three predeclared complete runs pass their gates. Each owns its source targets exactly once,
retains the expected multiline Gold groups, has no grouping fallback/unknown membership or placement
failure/residual chain conflict, and reports zero unresolved parents. Gold remains unchanged.

| Run | Seconds | Gold aliases | Extra aliases | Exact Gold groups | Output headings |
|---|---:|---:|---:|---:|---:|
| PDF072 full-01 | 236.614 | 44/44 | 1 | 41/42 | 42 |
| PDF072 full-02 | 248.588 | 44/44 | 1 | 41/42 | 42 |
| PDF076 independent-full | 191.239 | 35/35 | 0 | 34/34 | 34 |

PDF072's single remaining mismatch is the selected L0001:S0 metadata line in the main title group
with L0000:S0. The appendix title/Agenda and independent day/session/section/subsection boundaries
are preserved, and no genuine Gold alias is missing. PDF076's eight formerly missing institutional
subgroup labels are restored; every expected heading group matches. The complete per-heading tables,
source aliases and parent proposals are under each run's `heading-review.md` / `headings.json`.
Parent consistency is structural evidence only, because these Gold authorities do not score hierarchy.

## Application-default promotion and verification

After the full gates pass, PipelineOptions enables source binding, membership V6, separate boundary
instruction and pairwise grouping by default for native PDF. DocumentRunner uses a PDF-specific
agent set with bounded parent-chain repair; generic RepairParentChains stays false, preserving the
shared DOCX/scan placement policy. Provider-supplied selectors and placers retain precedence and are
requested once. Qualification modes explicitly disable the new PDF-only repair switch and keep
their original explicit generic switch, preserving the exact profile already measured above.

ProductionPdfProfileTests compares default PDF selector identity and placement contract against the
explicit qualified profile, verifies PDF-only repair scope, custom-agent precedence and legacy opt-out.
The initial test build needed the App assembly's normal friend-test declaration; after that, 19 focused
tests pass. Expanded regression runs 300 cases and exposes an existing in-flight cancellation failure:
HttpClient's linked cancellation token escapes through Parallel.For as AggregateException. It also
fails alone. AgentModel now normalizes an actually cancelled caller token before rethrowing; the
strengthened scan test checks the exact token, no second send and no cache write. All 19 targeted
retry/cancellation/default-profile tests then pass. Healthy-request wire bodies and inference rules
are unchanged by this cancellation-only correction. The failed TRX records are retained.

The final expanded regression passes **300/300**, zero failures/skips, in 1 minute 30 seconds
(`pdf-production-promotion-final.trx`). It includes native PDF parity/grouping, default-route wiring,
host overrides, retry/cancellation, DOCX, scan production, model-provider/problem routing, grouping
reuse and wire goldens. All three whole-alias extent audits pass and raw/accepted decisions match.
Both registered Gold hashes are unchanged. Final `git diff --check` and Python scorer syntax checks
are clean apart from existing line-ending notices.

**Promoted to the native-PDF application default in code on 2026-10-25 (Asia/Saigon)** after all
required gates and the final regression pass. This is a local code/default promotion, not an external
server deployment. The qualified 395-file source identity remains frozen; the 396-file promotion
identity records only the default/route wiring, friend-test visibility, profile tests, explicit
qualification repair override and cancellation-token correction. No healthy inference rule or wire
format changes between the measured profile and its default wiring. ProductionPdfProfileTests
checks the actual constructed selector identity and placement contract against the qualified profile.

Known residual: PDF072 still selects L0001:S0 as part of its main title, so its exact-group score is
41/42 despite complete 44/44 alias recall. This is retained, within the predeclared gate, and is not
described as 100% heading-boundary accuracy. No parent accuracy claim is made beyond graph consistency.
The three full runs are measured observations, not a universal five-minute latency guarantee.
`qualification-result.json` records the promotion, all gates, failures and remaining limitation.
