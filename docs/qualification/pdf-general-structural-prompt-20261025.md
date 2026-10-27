# General structural PDF instruction — 2026-10-25

The user requested returning to general structural criteria rather than accumulating
rules for the document types encountered during qualification. The resulting V7
selection and general boundary prompts are retained as an opt-in candidate. V7 did not
pass the declared PDF072 controlled-selection gate, so the native-PDF default remains
the previously qualified V6 profile, including its paired grouping prompts. The
V4–V6 membership versions remain available for comparison; old inference artifacts
remain unchanged.

## Selection

`PdfMembershipInstructionV7` selects every occurrence naming, introducing or dividing
a content unit at its location, at every level. It evaluates each occurrence consistently,
includes every contributing part of a multiline heading and keeps only its exact text.
Descriptive content qualifies only if it independently performs the heading function there.
Measurements inform the decision; they never supply an automatic label. Insufficient
evidence calls for more source evidence or an unresolved outcome rather than a guess.

The V7 candidate membership instruction contains no document-type examples, organization names,
participant-list categories, date/venue lists, agenda/day/session rules, document IDs,
source aliases or Gold answers. It does not inherit the older shared instruction's
Chapter I / GENERAL PROVISIONS examples. DOCX and scan instructions are unchanged.

## Grouping

`PdfGeneralBoundaryInstruction` uses the same general definition and boundary-only rules;
it no longer inherits a selection instruction with examples. `PdfBoundaryDecisions.GeneralPrompt`
removes program/committee/meeting and day/session guidance. JOIN requires source context
showing that selected parts jointly form one heading. SPLIT identifies a separately
introduced content unit. Containment, adjacency, a complete phrase or matching/different
formatting alone does not settle a boundary. Explicit user instructions retain precedence.

The request/response schema, PDF facts/context adapters, selection sourceText checks,
UTF-16 extent checks, complete ordered boundary coverage, maximum group size, parser,
retry behavior and parent repair are unchanged. Instruction and grouping prompt hashes
already participate in selector/request identities, so this revision changes those identities.
Explicitly selecting membership version V7 opts into both general prompts and updates the
selector and request hashes. The application default is still version V6.

Resolved strings exported from the compiled runtime constants are saved under
`artifacts/qualification/pdf-general-structural-v7-20261025/`:

- `v6-default-selection.txt`, `v6-default-grouping-instruction.txt` and `v6-default-grouping-system.txt` show the active default.
- `selection-instruction.txt`, `grouping-instruction.txt` and `grouping-system.txt` show the tested V7 candidate.
- `resolved-profile-prompts.json` records the runtime prompt hashes.
- `qualification-source-identity.json` freezes the source used for live V7 trials.

## Validation scope

Offline verification exercises the V6 default and V7 opt-in route, custom-instruction
precedence, source binding, grouping coverage and repair, parent consistency, scan/DOCX
regression and cancellation/retry behavior. A wire-comparison test confirms that replacing
V6 membership with V7 changes only the instruction and its hash, preserving evidence,
system prompt, tools and the rest of the selection request on the same source.

The final sequential regression passes **301/301**, with zero failures or skips
(`pdf-general-structural-v7-serial-20261025.trx`, 2 minutes 14 seconds). The initial
parallel run passed 300 and failed one existing concurrency/timer test,
`Shared_gate_limits_inflight_requests_across_model_instances`: it received the fake
handler's 402 response instead of the expected cancellation. That test passes alone
(1/1) without code changes and in the final sequential suite. All three TRX results
are retained under `tests/DocxHeaderExtractor.Tests/TestResults/`; no retry/runtime
logic was changed to accommodate the test. The parallel scheduling sensitivity remains
an observed limitation of that test. The build reports existing test analyzer warnings.

All 397 source/project hashes match the saved identity. The compiled prompt exports
and their hashes validate; the active registry and user-approved Gold correction also
validate. Summary evidence is in `validation.json` beside the exported prompts.

The live V7 controlled initial-selection trial ran the five predeclared PDF072 scopes
twice and the PDF076 scope twice. V7 missed no Gold aliases in these targets. PDF072
had 2 false positives in the first repetition, equal to the corrected V6 baseline, but
28 in the second; PDF076 selected all 10 scoped Gold aliases with no extras in both
repetitions. The conjunctive gate therefore **failed**. No full V7 extraction ran.
PDF072 scope selection took 67.139 and 80.194 seconds; PDF076 took 45.397 and 62.178
seconds. These partial-scope times are not full-document latency measurements.

The V7 result is retained at `artifacts/qualification/pdf-general-structural-v7-20261025/live/initial/`.
The run-level selection prompt behaved inconsistently on PDF072; this trial does not
establish that the general prompt reliably handles varied documents. No new LLM qualification
is claimed beyond these controlled outcomes. The two stored V6 PDF072
outputs still score 45/45 aliases and 42/42 groups against the user-corrected Gold;
those are historical outputs, not V7 output. V6 timings cannot be attributed to V7.
The original V6 default remains active. DOCX/scan prompts and defaults were not changed.

The follow-up V8 candidate makes `get_page_layout` mandatory for every target and adds a runtime gate that checks the returned aliases. Its controlled trial had complete tool-evidence coverage but unstable membership (PDF072: 23/16/2 then 25/3/0; PDF076: 1/0/9 then 10/0/0), so it also failed qualification. Details and traces are recorded in `pdf-tool-evidence-v8-20261025.md`; V6 remains active.
