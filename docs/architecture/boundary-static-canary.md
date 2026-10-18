# Paired static-evidence boundary canary

Qualification only; introduced after `e5a619e`. No production flag, Web endpoint, heading
authorization, hierarchy change, ancestor recovery, third boundary cycle or suggestedGroups.

Run with `dotnet run --project src/DocxHeaderExtractor.V6Qualification -c Release -- boundary-static-ab <manifest.json> <new-output-directory> --live`.
`OPENROUTER_API_KEY` must be set. Without the explicit `--live` argument the command returns
usage before reading the manifest or making calls. Output must be a new directory.

Manifest fields are `sourceDocument`, `ocrEvidence`, `evidenceEntry`, `storeRoot`, `pairs`
(1–4, default 2), and `cases` (1–8 `{id, aliases}` scopes). Each scope is 2–24 distinct,
contiguous aliases in source order. Case IDs are distinct ASCII letters/digits/hyphens.

The command validates the document against the OCR source hash and loads the existing scan
snapshot. Transcription preflight uses cached replies with **new preflight HTTP forbidden**;
cache misses abort the experiment. Typography is measured locally. It executes production
selection only for source cores intersecting the requested scopes, retaining the selected
readings and UTF-16 heading extents. This is a fresh qualification seed, not reconstruction of
the unavailable raw C0 verdict from an earlier live extraction. No full extraction, selection
review, binding, placement or writeback runs.

For each scope:

1. If selection did not SELECT every target, report `SELECTION_NOT_ALL_HEADING` and skip.
2. Obtain one valid initial partition using the current grouper, then run the current independent
   verifier at C0. Freeze its full runtime verdict, including reason, membership and evidence refs.
   ACCEPT/unknown/disputed/invalid seeds are reported and never forced or resampled to REGROUP.
   Any seed requesting tools is excluded as `SEED_REQUESTED_DYNAMIC_EVIDENCE` to isolate static evidence.
3. Arm A calls the production grouper with the production `BoundaryFeedback` builder unchanged.
   Arm B uses the same feedback plus `verifierStaticEvidence`: exact `targetFacts`, line source/
   readingViewId/extent/selectedText, context and documentStyles copied from that seed's first
   actual verifier packet. No tool definitions, crops or tools are added to either repair arm.
4. All pairs reuse the same C0 partition, source, instruction and complete feedback. Two pairs
   use A→B then B→A order; four pairs alternate likewise. They are fresh conversations, not
   continuations of one another. The common base-packet SHA (after removing only B's supplement)
   and frozen feedback SHA must match across arms and pairs.
5. Only a complete, valid model division with a changed ordered partition goes to a **fresh**
   invocation of the current verifier. It sees the new proposal and source evidence, without the
   seed verdict/reason or repair reasoning. No semantic retries follow its verdict. Fallback,
   dispute, failure and unchanged outputs are recorded, never converted into acceptance.

Primary observations: `changed` and output groups for each arm/pair; secondary observation:
`independentVerdict`. `A=same/B=changed/ACCEPT` supports static-evidence asymmetry; a changed
proposal still rejected shows a response to evidence without resolution. Both same means this
intervention did not fix no-progress. Any A change demonstrates baseline variance and prevents
attributing a single B change to the supplement. Small paired samples are diagnostic, not an
accuracy/Gold score or proof that a runtime verifier judgment is correct.

`report.json` contains safe aliases, partitions, hashes, eligibility status, call/cost totals and
independent-verifier metadata. `private/` retains source-bearing seed packets, exact full C0
verdicts/reasons, selected reading/extent maps, frozen feedback/static evidence and arm evidence
for local forensic review. These are deliberately separate from the share-safe report. API keys
are never serialized. The shared admission budget is 180 POSTs / USD 10; all protocol repairs
and transport retries retain their current bounded production behavior and consume that budget.

No production selector calls this harness. The only production-code changes are an internal
friend surface for qualification and extraction of the existing feedback object into a shared
builder; serialized production feedback and the verifier protocol remain unchanged. Regressions
lock identical A/B feedback/base packets, alternate reading + partial extent fidelity, AB/BA
order, independent verification only for changed proposals, no raw reason in the safe report,
and exclusion of accepted/tool-informed seeds. Arm C remains outside this experiment.
