# Autonomous Accuracy-99 Closed Loop

The A99 branch begins with behavior-neutral observability. The normal extraction output keeps
its compatibility JSON shape while route telemetry is captured in an evaluation-only trace:

`source occurrence -> representation -> route -> candidate -> explicit model request -> proposal -> validation -> marker -> structure -> final element`

Joins are explicit and namespace-safe. Equal-looking IDs and fuzzy text matching are not used;
unknown membership remains unknown. Human gold and holdout labels never enter runtime reasoning.

At the first checkpoint (`1eb0338`), 3 trusted documents produced 1,168 occurrence traces with
100% route observability, 100% explicit model-exposure observability, 100% final-lineage
observability, zero provider calls, and zero compatibility output delta.

The A99 quality metrics remain `NOT_MEASURED` because exhaustive independent HUMAN_GOLD,
negative opportunities, and a frozen blind holdout are absent. The next authorized action is
reference import and validation, not heuristic or model tuning. The known N15 artifact-hash
failure remains frozen.
