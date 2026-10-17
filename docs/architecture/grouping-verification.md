# Independent heading-group verification

2026-10-17. Production selector IDs now end in `verified-groups-v1` for scan, text PDF,
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
3. `ACCEPT` admits the proposed groups. `MEMBERSHIP_DISPUTE` requests the existing bounded
   selection review. `REGROUP` requests a targeted division with membership unchanged and the
   verifier's feedback. `UNRESOLVED` withholds the affected aliases and records partial work.
4. A targeted regroup is verified once more; it cannot request another boundary repair. Groups
   produced after selection review are also verified, with no further selection-review cycle.
   Unresolved aliases never become single-line fallback headings.

Verification uses `HEADING_GROUP_VERIFICATION_V1`. Every candidate must receive exactly one
verdict, a nonempty reason (up to 1000 characters), and 1–8 evidence references to shown or
successfully returned source aliases. C# validates shape, scope, grounding and budgets;
semantic membership and boundaries remain model decisions. No phrase, regex or pixel-distance
rule assigns roles to country headers, mottos, form titles, clauses or headings.

The verifier may request existing read-only source tools (context, source spans, facts, etc.)
through an explicit `requests` reply, up to four requests per round. Scan also supplies
`get_scan_crop(aliases)`: 1–24 aliases on one page. The program checks the stored page SHA-256,
crops the union of the boxes with 32-pixel padding and supplies a black/white PNG at original
scale. Unavailable/mismatched images are reported as unavailable evidence, not inferred content.
Crop metadata and hashes are retained; the base64 image is not copied into output audit.
There are at most three POST attempts per verification conversation, including evidence
rounds and protocol repairs. Every request passes the shared byte/cost/call budget. There are
no unbounded semantic retries. Source context tools can provide context beyond the initial
eight lines; the verifier may return unresolved when it cannot obtain adequate evidence.

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

Regression: the stored 023 page-32 image was inspected before writing a synthetic protocol
fixture. The fixture reproduces separate source functions for a section label, text inside a
cover form, and the next section label. Scripted-model tests establish that a protocol-valid
merge can trigger targeted regroup or membership review without an initial grouping dispute,
while valid multi-line chapter/article headings remain intact. They also cover unavailable
evidence, unknown evidence references, missing verdicts, repeated boundary disputes, image
crop delivery and Web counters. No Gold was edited; this is not a real-provider accuracy score.
