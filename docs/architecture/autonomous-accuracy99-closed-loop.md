# Autonomous Accuracy-99 Closed Loop

The A99 branch begins with behavior-neutral observability. The normal extraction output keeps
its compatibility JSON shape while route telemetry is captured in an evaluation-only trace:

`source occurrence -> representation -> route -> candidate -> explicit model request -> proposal -> validation -> marker -> structure -> final element`

Joins are explicit and namespace-safe. Equal-looking IDs and fuzzy text matching are not used;
unknown membership remains unknown. Human gold and holdout labels never enter runtime reasoning.

## Historical snapshot

At the first checkpoint (`1eb0338`), 3 trusted documents produced 1,168 occurrence traces with
100% route observability, 100% explicit model-exposure observability, 100% final-lineage
observability, zero provider calls, and zero compatibility output delta. That snapshot and the
historical reports remain immutable.

## Current authoritative status

The current A99 authority is B0 at `76c4e01`, with `qwen/qwen3.7-flash` semantic-text proposals
and a deterministic exact UTF-16 binder. Across the canonical 5-document, 15-cell, 3-repeat
Strict-Gold cohort it measures `TP=428 FP=32 FN=31 F1=.9314472252`, with
`SYSTEM_LOSS=0` and `BIND_FAILURE=0`. The current status is
`A99_DEV_TARGET_NOT_REACHED`; the optimization loop is closed and the release candidate is held.

Seven target omissions remain persistent, while role and hierarchy errors are not measured. The
audited causal families do not currently justify another generic intervention. C1 closure and
the prepared independent R2 residual-generalization protocol are recorded in
[`docs/accuracy/a99-terminal-research-closure.md`](../accuracy/a99-terminal-research-closure.md)
and `eval/a99-closed-loop/closure/c1/`. No Gold-derived rule enters runtime behavior, and the
known N15 artifact-hash failure remains unrelated and frozen.
