# PDF source preflight and bounded name review — 2026-10-25

The source-tool acquisition code is implemented and verified. The candidate heading classifier remains unqualified. All live experiments below failed the conjunctive accuracy and repeatability gate, so no full extraction or production promotion followed. The native PDF default remains membership V6. Gold, DOCX prompts, and scan prompts were not changed.

## Why V8 did not fix selection

V8 required successful page-layout results before accepting a final answer. It did not guarantee that the first classification waited for complete pagination: PDF076 first answered after 40 of 63 lines, received a repair, read the remainder, then retained its earlier interpretation. V8 also retained V7's broad structural predicate. Identical source observations still produced different classifications of names, attributes, list labels, and agenda entries. More observations alone did not settle those semantic choices.

## Implemented tool acquisition

The opt-in V9 source format executes the existing read-only `get_page_layout` implementation before sending the first model request. The program computes target windows from source positions, includes six neighboring lines on each side on the same page, and continues the tool's pagination until every target alias has actually been returned. Zero-progress, failed reads, and missing target coverage abort acquisition.

The exact tool results, arguments, provenance, covered aliases, and window bounds are placed in `sourceEvidence`. These are explicitly marked program source reads. No assistant tool call or tool-history message is fabricated. Model-initiated tool counters therefore remain separate from program source-read counts. The existing PDF coverage gate independently checks the successful tool ledger before accepting classification.

`PrepareFirstRequest` and the actual selection task share the same acquisition function. Actual evidence bytes participate in request planning, admission, and SHA-256 identities. The model can request additional evidence for a specific unresolved relationship, but it no longer needs paid model turns just to request the next page slice.

The V9 instruction defines heading function through the actual content scope named by an occurrence, including the document as a whole. It contains no source aliases, document names, Gold answers, or document-specific examples.

## Optional bounded review

An explicitly enabled `PdfBoundaryReview` with V9 runs a fresh name-contribution review on at most two centered opening clusters, routed from the first complete scope and the scope with the largest measured font. Centering is measured against the page's content extent with an eight-point tolerance. Geometry only routes review; it assigns no heading label.

The reviewer receives only the contiguous candidate aliases as targets, their source context, and preloaded tool results. It receives no earlier model explanation or decisions. A complete validated response may only remove a candidate the reviewer marks `NOT_SELECT`; it cannot add headings or alter membership outside the candidate run. Failed or incomplete review leaves the initial selection intact and records failure. Left-aligned adjacent parent/child headings do not enter this centered-cluster review.

V9 preflight and bounded review have separate opt-ins. Review is not automatically activated by choosing membership V9. Their selector identities include the preflight policy and, when enabled, the review instruction hash. Both remain experimental after these failed trials.

## Live qualification

Each experiment used the same five PDF072 scopes (212 target aliases, 25 Gold-positive aliases) and the same PDF076 scope (63 targets, 10 Gold-positive aliases), twice each. Gold registry hashes and frozen target universes were checked. Passing required complete scopes, no missed Gold aliases, no more extras than frozen V6, and identical selected sets across repetitions.

| Experiment | PDF072 repeat 1 TP/FP/FN | PDF072 repeat 2 | PDF076 repeat 1 | PDF076 repeat 2 | Gate |
|---|---:|---:|---:|---:|---|
| Source preflight + general scope rule | 25/4/0 | 25/3/0 | 10/0/0 | 10/0/0 | Fail |
| Stronger name/property rule across every target | 24/27/1 | 25/0/0 | 10/0/0 | 1/0/9 | Fail |
| Source preflight + bounded name review | 25/2/0 | 25/3/0 | 1/0/9 | 1/0/9 | Fail |

The broad name/property rewrite was not retained in the main selection instruction. Its exact tested prompt is preserved in its own artifact root. The main V9 selection instruction retains the first general scope rule; the stronger name/property check is available only in the explicitly enabled bounded reviewer.

In the final bounded trial, PDF072 used seven program source reads plus two review source reads per repetition, and seven model replies including review. It took 97.937 and 88.269 seconds, with approximately eight seconds of source preparation and 2.147/0.784 seconds of request-budget preparation recorded separately. PDF076 used two program reads, no routed name review, and one model reply per repetition; selection took 31.128 and 33.006 seconds. These are partial-scope timings, not end-to-end extraction latency claims.

Raw review responses still called the date an “essential component of the event title” and kept date/location aliases despite the name-versus-property instruction. PDF076 still classified the institution group labels as list entries, leaving nine Gold headings unselected. The preflight and bounded mutation checks all passed; those checks do not establish semantic correctness.

## Verification and artifacts

Meaningful tests cover pagination before the first classification, exact parity between measured and sent first requests, real tool-result provenance and target coverage, scope ownership, removal-only bounded review, exclusion of left-aligned parent/child groups from title review, and preservation of the V6 default. The final focused run passed eight tests; the explicit opt-in test is recorded separately in `pdf-v9-explicit-opt-in.trx`. Earlier overlapping focused suites are retained alongside them. Existing analyzer warnings remain.

Artifacts under `artifacts/qualification/`:

- `pdf-source-preflight-v9-20261025/`: first preflight experiment.
- `pdf-source-preflight-v9-name-only-20261025/`: full-scope name/property rewrite experiment.
- `pdf-source-preflight-v9-bounded-review-20261025/`: final bounded-review experiment, `experiment-comparison.json`, request/reply traces, source identity, exact resolved prompt exports, and `live/initial/score.json`.

All three artifact roots preserve the actual prompt strings from their captured requests and a prompt hash manifest. No failed run was overwritten. The final result is a verified improvement to when and how source evidence is acquired; it is not a verified fix for selection accuracy. Production promotion remains withheld.
