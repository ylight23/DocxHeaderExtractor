# Candidate Generation First-Loss

The diagnosis-only probe audits the ten frozen candidate-construction losses in document `004` using exact source occurrence identity (`documentSha256`, page, source line IDs, and source span). It replays the existing source, grouping, and candidate producer stages without provider calls or production changes.

The current trace identifies `PdfSemanticBlockGrouper.Build` / `LINE_GROUP` as the first rejecting boundary for all ten occurrences. Nine are frozen as `LINE_GROUP_BOUNDARY_SPLIT`; one (`004/section/5`) remains the previously classified `LINE_GROUP_ABSORBED_OR_TRUNCATED` case. At each producer, no single candidate covers every source line in the occurrence, so producer availability is reported as false without attributing the loss to a producer predicate that was not observed.

The constrained boundary simulation already committed in `eval/accuracy-round1/candidate-boundary-counterfactual.v1.json` recovers the nine boundary cases with zero candidate inflation in its measured scope. That simulation is not equivalent to a safe production grouping repair. The absorbed/truncated case has no isolated predicate and no counterfactual recovery claim.

Existing reviewed lineage shows the same grouping-stage mechanism across multiple documents, so producer/stage recurrence is `PROVEN`; a selective, collateral-safe invariant is not proven. Remediation is therefore `NO`. This round makes no provider calls and changes no production code.

Artifact: `eval/accuracy/candidate-generation-first-loss.v1.json`
