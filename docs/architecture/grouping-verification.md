# Independent heading-group verification

Updated 2026-10-19. Production selector IDs now include `verified-groups-v3-grounded-regroup` for scan, text PDF,
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
4. Boundary verification starts at cycle 0. `REGROUP` with every membership still `HEADING`
   may trigger boundary repair 1. A complete valid model division with unchanged ordered
   alias scope and unchanged ordered partition stops immediately with
   `BOUNDARY_REPAIR_NO_PROGRESS`, before another verifier call; affected aliases remain
   unresolved with their `HEADING` membership intact. A changed proposal proceeds to fresh
   verification at cycle 1. The same condition
   may trigger boundary repair 2, followed by final verification at cycle 2. `ACCEPT` admits the
   verified groups; `REGROUP` at cycle 2 withholds the candidate as unresolved. Membership
   disputes and unknown membership retain their existing review/refinement handling; they do
   not qualify for boundary-only repair. `MaxBoundaryRepairCycles = 2` is a semantic limit,
   separate from protocol repair rounds within a grouping or verification request. Groups
   produced after selection review are also verified, with no further selection-review cycle.
   Unresolved aliases never become single-line fallback headings.

In a mixed run, confirmed `HEADING` aliases are proposed separately for fresh verification in
their original source context. Removing contested aliases only cuts candidate pieces at the
resulting gaps; these pieces are not emitted until a model accepts their boundaries. The fresh
pass preserves the incoming boundary cycle and may use only the remaining boundary repairs;
survivor refinement does not reset the cycle counter. There is at most one membership refinement
per verification branch, and only one selection-review cycle. A failed/repeated dispute withholds
the affected aliases without discarding independently accepted neighbours. At a bound where
boundaries remain unverified, membership can still be `HEADING` while binding stays unresolved;
it is never silently rewritten to `NOT_SELECT`.

Verification uses `HEADING_GROUP_VERIFICATION_V3`. Every candidate must receive exactly one
verdict, a nonempty reason (up to 1000 characters), and 1–8 evidence references to shown or
successfully returned source aliases, plus complete per-alias membership with 1–8 evidence
references for each alias. C# validates shape, completeness, scope, consistency, grounding and budgets;
semantic membership and boundaries remain model decisions. No phrase, regex or pixel-distance
rule assigns roles to country headers, mottos, form titles, clauses or headings.

For REGROUP, two additional fields are mandatory: `currentGroups` must exactly echo the input
`proposedGroups`, and `suggestedGroups` must be a different valid ordered partition. Each alias
appears exactly once in source order, with nonempty contiguous groups of at most eight lines.
Missing/invalid groups, mismatched current groups, and identical suggestions are protocol errors.
In particular, equality of suggestion and input raises `BOUNDARY_VERDICT_SELF_CONTRADICTORY`.
The actual assistant response and deterministic consistency feedback remain in the SAME verifier
conversation under the existing three-round bound; no semantic boundary cycle is consumed.
An ACCEPT after that protocol repair is handled as an ordinary verified ACCEPT. Other decisions
may omit the two fields or use null, and may echo the exact currentGroups with a null/omitted
suggestion; membership and evidence requirements are unchanged.

Suggestions have NO repair authority. Valid suggestions are recorded in the share-safe
`grouping.boundaryVerdicts` observations, including unit, attempt, round, candidate, cycle,
alias-only current/suggested partitions, error code and `applied=false`. Invalid arbitrary alias
strings are not retained as partitions; reason/source text is excluded. The existing grouper
repair path still decides its own partition. Its feedback deliberately omits the two new verdict
properties, so this promotion does not silently turn the verifier proposal into repair input.
No-progress detection, the two-cycle semantic cap, survivor refinement and hierarchy fail-closed
are unchanged. Older V2 REGROUP replies are not accepted under the V3 production contract.
Historical canary V2 prompt bytes are frozen separately in qualification for request-hash replay;
the qualification harness's explicit membership-only parser use has no production caller.

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
The document's default reserve includes this bounded allowance. Semantic regroup POSTs have
a separate reserve from initial/review grouping and verification: two initial/review trees,
two boundary cycles, at most `floor(sourceLines / 2)` provider-backed grouping chunks per cycle,
and up to `1 + AgentGrouping.MaxRepairs + AgentUnit.TransportRetries` POSTs per chunk.
Independent candidates do not share grouping requests. Their aliases are disjoint at a given
cycle, including survivor refinement, and each provider-backed run requires at least two lines.
The regroup cost reserve uses the request byte cap. There are
no unbounded semantic retries. Source context tools can provide context beyond the initial
eight lines; the verifier may return unresolved when it cannot obtain adequate evidence.

The no-progress check applies only to a successful model division covering the complete
candidate: no terminal grouping failure, no fallback, no dispute, positive model-divided run
count and `DIVIDED` provenance for every alias. A recovered protocol round may still produce
a valid final division. Terminal protocol failure, `FALLBACK`, `DISPUTED`, singleton runs with
no model division, and alias-scope changes keep their existing verification/review handling;
they do not activate this guard. The check compares the new partition to the proposal just
rejected by `REGROUP`, not to an unrelated earlier branch or any document-specific pattern.
It introduces no acceptance authority and does not promise higher recall.

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
repairs overlap at `jobs=2` without exceeding that cap across one or two boundary cycles; this
is not a live 023 speed measurement. `Second_semantic_boundary_repair_is_bounded_and_never_overrides_changed_membership`
covers `REGROUP → REGROUP → ACCEPT`, `REGROUP → REGROUP → REGROUP`, changed membership,
and protocol repair within a semantic cycle. `Membership_refinement_does_not_reset_the_semantic_boundary_cycle_limit`
locks cycle preservation through survivor verification. The budget regression
`Many_candidates_can_use_both_boundary_cycles_without_borrowing_verifier_reserve` exercises
24 independent candidates, both semantic cycles and both initial/review passes, exhausting
the per-request protocol/transport allowance against a separate GROUP-only admission ledger.

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

Alias provenance records `BoundaryRepairCycle`, grouping status (`DIVIDED`, `DISPUTED`,
`FALLBACK`), verifier decision, evidence references and reason length. `DIVIDED` at successive
cycles does not establish whether a partition changed. Safe `Grouping.Partitions` now records
the actual proposed partition presented to each verifier candidate, including cycle 0, even
when the verifier ultimately refuses it. Each `GroupingPartitionObservation` contains phase,
unit, candidate ID, cycle, ordered candidate aliases, prior rejected `InputGroups` (when passed
from boundary repair), new `OutputGroups`, and nullable `ChangedFromInput`.

`CandidateKey` is SHA-256 of the JSON-encoded ordered candidate aliases. Candidate IDs may
change between cycles and are not trajectory identity. Group and alias order are preserved:
`[[A,B],[C]]` differs from `[[A],[B,C]]`. C# computes `ChangedFromInput` by structural sequence
comparison only when the flattened alias scopes match. It is null at initial verification or
when no comparable prior partition is supplied; survivor refinement keeps its cycle but may
change alias scope/key. These snapshots do not authorize headings; the separate no-progress
guard below can stop an unchanged valid model repair before a new verifier snapshot exists.
The snapshot describes verifier input, not an accepted model partition; fallback proposals
remain subject to verification. A grouping dispute that produces no verifier candidate has
no partition snapshot; its existing grouping/dispute provenance remains available.

`Grouping.BoundaryRepairs` retains a `BoundaryRepairObservation` for every semantic regroup
invocation **before** recursive verification. It records the rejecting verifier's `ParentUnitId`,
candidate ID/key/ordered aliases, repair `Phase` (matching grouping batch phase), from/to cycle,
ordered input/output groups, nullable `Changed`, result (`NO_PROGRESS`, `CHANGED`, `DISPUTED`,
`FAILED`) and a safe failure class. A stopped no-op therefore retains its output partition
without pretending that verifier cycle N+1 ran. Its aliases carry `boundary-repair-result`
provenance with `NO_PROGRESS` and `BOUNDARY_REPAIR_NO_PROGRESS`. Merge logic preserves these
observations across independent parallel repairs and review/survivor branches.

Safe `Grouping.VerifierEvidence` records the **shared conversation scope**: unit, cycle,
ordered alias lists of all candidates, initially visible aliases, final observed aliases,
and requests with round, registered tool name, status, returned aliases and crop-return flag.
Unknown tool names become `UNREGISTERED`, not arbitrary provider strings. Tool results are
shared by the batch and must not be attributed to a specific candidate merely because its
alias appears in the batch. `SuppliedToVerifier` means a subsequent POST containing the tool
history was admitted and attempted; it does not claim successful transport or model attention.
`UsedScanCrop` requires both a successful crop and that subsequent POST. Failed/unavailable
crops retain their status without claiming image use. No tool arguments, source text, reason,
prompt, image bytes or tool-result bodies enter these observations.

The selector retains grouping responses and verification/tool evidence in `ModelAnswers`
(including `groupingEvidence` envelopes), which the authority carries as `RawModelResponses`.
This is retention in the execution object, not a durable raw-response cache. Web `SafeAudit`
omits those raw fields but includes the partition/evidence observations above regardless of
`showRaw`. `/api/extract` emits optional raw reply events only when `showRaw` is enabled.
Older safe-only results, including execution `d8faadf8-4988-4fbf-8fc8-26f75e29e88d` before this
instrumentation, cannot reconstruct omitted partitions or verifier reasons. Missing snapshots
must remain `not-recorded`, with transitions `unknown`, not inferred from repeated verdicts.
OCR/transcription cache entries are not a substitute for grouping responses from that execution.

There is a capability difference to investigate if a recorded trajectory shows unchanged
partitions: verification can request source context/spans/facts and scan crops, while
`AgentGrouping.Group` receives its run packet and `verificationFeedback`, without an evidence
tool conversation. Feedback includes the proposed groups and verdict/reason/evidence references,
but not the verifier's returned tool evidence or crop images. This difference alone does not
prove that a particular live repair lacked evidence: that conclusion needs its recorded tool
history and partitions. Zero tool requests rules out additional-tool/crop asymmetry for that
execution, but not base-context asymmetry: the verifier's initial packet includes target facts,
±8 source lines, styles and selected reading/span, while the scan grouper receives run
alias/page/text/layout and one clipped neighbour on each side. Comparing REGROUP EvidenceRefs
against actual run/before/after source-item aliases can identify a missing source item. Its
alias identifier may already be in feedback; that is not the same as receiving the source
item. An in-scope evidence ref does not establish parity of facts, styles or context breadth,
and unavailable raw reasons prevent ruling out quoted/paraphrased evidence in feedback.
No static-evidence mirroring, suggestedGroups canary, third cycle, hierarchy change or ancestor
recovery is introduced here. Regressions cover unchanged/changed partitions across
two cycles, changing candidate IDs with stable alias keys, scope changes, shared extra context,
successful/unavailable scan crops and unknown tool names. A production scan fixture confirms
partition and verifier-evidence observations survive through `WebPipelineProjection.SafeAudit` without raw capture.
No-progress regressions assert no subsequent verifier call, preserved independent headings,
safe repair-result projection, and exclusion of fallback/disputed/failed/scope-changing repairs.
Two-cycle and concurrency fixtures propose genuinely changed partitions at each repair; they
do not obtain acceptance by repeatedly verifying an unchanged, previously rejected proposal.

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
