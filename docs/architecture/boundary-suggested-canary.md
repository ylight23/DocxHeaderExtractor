# Qualification C: structural suggestion from a frozen REGROUP complaint

`boundary-suggested-c <ab-manifest.json> <seed.json> <frozen.json> <new-output-directory> --live`

This command consumes the private seed and frozen snapshot from `boundary-static-ab`. It validates the source document, alias universe, evidence-store hashes, packet static evidence, rejected groups and complete original verdict. Transcription must already be cached; a cache miss cannot make a preflight HTTP request. There is no new selection, initial grouping, or cycle-zero verifier call.

One fresh proposal conversation receives the exact frozen verifier packet plus its full runtime REGROUP complaint. This is explicitly a new proposer, not a claimed continuation of the historical verifier conversation. The qualification-only prompt extends the verdict with `suggestedGroups`; it can also recant to ACCEPT or report membership dispute/unresolved. No tools are exposed. One proposal POST is allowed, with no format repair or semantic retry. The shared experiment budget caps all proposal and independent verifier calls at 12 POST admissions and USD 1.

The existing production verdict validator checks ordered per-alias membership and evidence references after the qualification field is removed. The existing strict grouping parser checks the suggested partition's exact coverage, order, contiguity and group size. A malformed response, incomplete completion or tool call cannot qualify a partition. A non-REGROUP proposer decision never authorizes the original partition.

A structurally identical suggestion stops as `SAME_REJECTED_PARTITION`; there is no random re-verification of the rejected proposal. A valid changed suggestion enters a fresh production verifier conversation with source evidence and selected reading/extent maps. It receives neither the frozen complaint nor proposal reason. Only its ACCEPT yields `QualifiedAccept=true`; other decisions remain unresolved. No semantic regroup recursion is invoked, and even qualified acceptance emits no production headings, binding, hierarchy or writeback.

Safe `report.json` contains aliases, partitions, hashes, status and costs. Original packets, reasons, model replies and independent verifier evidence remain in the separate local `private` directory. Production's prompt/schema, two-cycle limit, no-progress guard and resolver are unchanged. The only shared-code adjustment is internal visibility of the existing strict verdict parser for reuse by this qualification harness.

This tests whether an explicit structural suggestion can escape the frozen disagreement on this seed. Acceptance by a fresh verifier is qualification evidence, not Gold correctness. A new proposer can itself disagree with the historical seed; record that outcome rather than sampling until a desired result appears.
