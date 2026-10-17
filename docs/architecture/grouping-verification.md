# Independent heading-group verification

2026-10-17. Production selector IDs now end in `verified-groups-v2-per-alias` for scan, text PDF,
and DOCX. The heading instruction and OCR/transcription caches are unchanged. The additional
model conversation checks every proposed heading, including singleton and fallback groups.
Protocol-valid grouping alone no longer admits a heading to binding.

The verifier explicitly requests a **JSON object** in its system message. Alibaba rejects
`response_format=json_object` with HTTP 400 when no message contains the word `json`.
This provider prerequisite is tested through the actual request builder and transport with
fake HTTP validation (automatic fake verification disabled). A minimal live synthetic probe
reproduced HTTP 400 without the word and HTTP 200 after adding it; see
[probe report](../../eval/scan-text/reports/verifier-json-mode-smoke.v1.json). This probe does
not evaluate document accuracy. The transport exposes the allowlisted diagnostic
`CLIENT_ERROR:HTTP_400:JSON_MODE_REQUIRES_JSON_MESSAGE` without copying raw provider bodies.

Flow:

1. Selection decides membership; grouping proposes contiguous partitions or reports dispute.
2. A fresh verifier conversation sees the proposed partition, selected reading/span, full core
   facts, eight nearby source lines each side, measured layout and document styles. It does not
   receive the proposing model's reasoning as authority. Adjacent proposed headings are checked
   together so false merges and false splits can both be challenged.
3. Every verdict supplies membership (`HEADING`, `NOT_HEADING`, `UNRESOLVED`) for every alias
   exactly once in source order, with source evidence references per alias. `ACCEPT` and
   `REGROUP` require all memberships to be `HEADING`. `MEMBERSHIP_DISPUTE` requires at least
   one non-`HEADING` alias; only those aliases go to selection review. `REGROUP` requests a
   targeted boundary-only division. The program never reads keywords in `reason` to change a
   verdict. Inconsistent structured claims are sent back as protocol errors.
4. A targeted regroup is verified once more; it cannot request another boundary repair. Groups
   produced after selection review are also verified, with no further selection-review cycle.
   Unresolved aliases never become single-line fallback headings.

In a mixed run, confirmed `HEADING` aliases are proposed separately for fresh verification in
their original source context. Removing contested aliases only cuts candidate pieces at the
resulting gaps; these pieces are not emitted until a model accepts their boundaries. The fresh
pass can request its own bounded boundary repair. There is at most one membership refinement
per verification branch, and only one selection-review cycle. A failed/repeated dispute withholds
the affected aliases without discarding independently accepted neighbours. At a bound where
boundaries remain unverified, membership can still be `HEADING` while binding stays unresolved;
it is never silently rewritten to `NOT_SELECT`.

Verification uses `HEADING_GROUP_VERIFICATION_V2`. Every candidate must receive exactly one
verdict, a nonempty reason (up to 1000 characters), and 1–8 evidence references to shown or
successfully returned source aliases, plus complete per-alias membership with 1–8 evidence
references for each alias. C# validates shape, completeness, scope, consistency, grounding and budgets;
semantic membership and boundaries remain model decisions. No phrase, regex or pixel-distance
rule assigns roles to country headers, mottos, form titles, clauses or headings.

The verifier may request existing read-only source tools (context, source spans, facts, etc.)
through an explicit `requests` reply, up to four requests per round. Scan also supplies
`get_scan_crop(aliases)`: 1–24 aliases on one page. The program checks the stored page SHA-256,
crops the union of the boxes with 32-pixel padding and supplies a black/white PNG at original
scale. Unavailable/mismatched images are reported as unavailable evidence, not inferred content.
Crop metadata and hashes are retained; the base64 image is not copied into output audit.
There are at most three model-response rounds per verification conversation, including evidence
rounds and protocol repairs. Up to three transport retries are shared across that conversation
(at most six POSTs total). The retry allowlist is the agent's existing transient stream/network,
rate-limit and server-error list; client/payment/audit/endpoint failures do not qualify. A transport
retry sends identical prepared bytes without advancing the response round, checks cancellation
during the delay, and passes the shared byte/cost/call budget again. Both successful and failed
attempts carry request hashes; retry decisions and exhausted/budget outcomes are recorded.
The document's default reserve includes this bounded allowance. There are
no unbounded semantic retries. Source context tools can provide context beyond the initial
eight lines; the verifier may return unresolved when it cannot obtain adequate evidence.

Within a valid response envelope, schema, complete ordered membership and source evidence are
checked independently for each candidate. A duplicate run invalidates that candidate rather
than choosing either copy. Valid verdicts are frozen in source order; later malformed replies,
transport failures or budget exhaustion cannot discard or overwrite them. Repair packets contain
only pending candidates and retain original source context. Missing/invalid candidates remain
unresolved; a transport failure never supplies a semantic `NOT_HEADING` judgement. Global guards
(JSON envelope/version, finish reason, tool-call mode and byte cap) still fail closed for the whole
new response. The audit records specific envelope/candidate error codes, accepted IDs and pending
IDs for each response round, alongside the original visible response. Recovered protocol/transport
errors remain in evidence without making a fully recovered batch partial.

Offline regressions inject one bad verdict among eight candidates, failed repairs, stale verdict
overwrites, exhausted transport retries, budget refusal and cancellation. An HTTP/SSE fixture
exercises a real stream-error path followed by success. These are synthetic fault replays, not a
replay of the user's 023 execution: its raw verifier responses have not been supplied or found in
the local evidence cache. No claim is made that these changes correct model hierarchy judgements.

Boundary-repair candidates execute concurrently up to `jobs`. Within each worker the dependent
regroup → reverify sequence remains serial and each nested operation uses one worker, so
parallelism cannot multiply across nested loops. Results/evidence are combined in candidate
order and final groups remain in source order. The scheduler changes no prompt, membership
policy, verification coverage or reasoning setting. A barrier-based regression proves two
repairs overlap at `jobs=2` without exceeding that cap; this is not a live 023 speed measurement.

The selector records wall-clock `selection`, `grouping`, `verification` and `review` seconds.
Verification includes boundary repairs; review includes its regroup and re-verification.
Scan merges these into `StageSeconds` alongside OCR/preflight and total `conversation`.
`conversation` includes those four heading phases, so do not add them to it as independent
durations. Web safe audit carries `HeadingStageSeconds` and the execution list shows a
`heading-phase-timing` row. Timings are measured observations, not a predicted speedup.

`selection.Grouping` and runtime `routeAudit.Grouping` carry actual cumulative observations:
model-divided runs, fallback runs, explicit disputes, verification checks and acceptances,
membership/boundary disputes, final unresolved aliases, and ignored review changes. Web safe
audit exposes these counters. Web no longer guesses fallback execution from `GROUP-` occurring
in a failure string. Scan fallback is one heading per line; text PDF/DOCX use their declared
flavor rules. A fallback proposal still needs semantic verification before emission.

Selection review continues to apply only disputed aliases. Its evidence now explicitly lists
`ignoredDecisionChanges` and the `outside-disputed-alias-scope; not applied` basis. Review,
verification, regroup and failed visible replies remain in `ModelAnswers` / raw model audit.
The Web safe audit still withholds raw model responses. Counts describe execution, not measured
accuracy; an accepted verifier judgement can still be wrong.

`HeadingSelection.AliasStates`, runtime `routeAudit.HeadingAliasStates`, and the
`pre-binding-alias-state` audit entry record initial selection, final selection, latest model
verification membership and binding eligibility per alias. `UNRESOLVED` is distinct from
`NOT_SELECT`; `READY_FOR_BINDING` does not claim a placement, level or validated parent.

Appendix regression uses a synthetic alias distribution based on the inspected stored 023
page-23 image and the user's expected outline. It checks precise single-alias review, survivor
verification after an initial dispute or a dispute discovered after regroup, failed review,
reselected body text and unknown membership. The production-pipeline test supplies scripted
model parent relations for the seven expected H1/H2/H3 entries and two outside-tree entries.
Those levels are derived from the scripted relations; they are not hardcoded in production,
not Gold edits, and not evidence that a live placement model has returned them.

Regression: the stored 023 page-32 image was inspected before writing a synthetic protocol
fixture. The fixture reproduces separate source functions for a section label, text inside a
cover form, and the next section label. Scripted-model tests establish that a protocol-valid
merge can trigger targeted regroup or membership review without an initial grouping dispute,
while valid multi-line chapter/article headings remain intact. They also cover unavailable
evidence, unknown evidence references, missing verdicts, repeated boundary disputes, image
crop delivery and Web counters. No Gold was edited; this is not a real-provider accuracy score.
