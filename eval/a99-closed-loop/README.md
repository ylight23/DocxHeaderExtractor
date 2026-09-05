# A99 Autonomous Closed Loop

This directory records the first A99 checkpoint from `origin/main` at `ee6d683...`.

Phase A is complete: the route boundary records explicit source representations, request
membership, and one occurrence trace per parser-owned source unit. The deterministic trusted
population contains 3 documents and 1,168 source occurrences. Route and final-lineage coverage
are both 100%; provider calls are 0 and compatibility output delta is 0.

This is not an accuracy claim. Exhaustive independently reviewed HUMAN_GOLD, negative
opportunities, and a frozen independent holdout are not imported. The correct status is
`HUMAN_REFERENCE_REQUIRED`; no production accuracy intervention is authorized yet.

The N15 full-suite artifact-hash drift remains frozen and was not rebased. The exact execution
run at `1eb0338` measured 1,080 tests: 1,079 passed, 1 frozen N15 failure, and 0 skipped.
