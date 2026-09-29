# V4R2 P05 offline scoring

Status: COMPLETE_SOURCE_COVERAGE; combined-manifest authority = v2; goldOpenedAfterExecutionFreeze = true; providerCallsDuringScoring = 0.

Execution is separate from semantic quality: 31 primary requests, 27 direct contract-valid, 4 invalid primary responses, 6 recovery calls total, 2 adaptive child calls, 37 provider calls, final parent ownership coverage 31/31.

- SRC-089: Gold=36, accepted model claims=33, TP=30, FP=3, FN=6, precision=0.9091, recall=0.8333, F1=0.8696, partial-only=2, OUT_OF_OWNED_SEGMENT=8. Split-boundary evidence is retained for SRC-095 ordinal 030; child A/B claims were scored independently.
- SRC-095: Gold=103, accepted model claims=148, TP=90, FP=58, FN=13, precision=0.6081, recall=0.8738, F1=0.7171, partial-only=3, OUT_OF_OWNED_SEGMENT=23. Split-boundary evidence is retained for SRC-095 ordinal 030; child A/B claims were scored independently.

Comparison guard: this is an absolute P05-medium result. Any comparison with V4 fixed120 reasoning=none is descriptive/non-causal; no fixed120-medium causal claim or attribution to packing, reasoning, or provider behavior is made.

Gold was opened only after v2 lineage, accepted-response, and ownership-partition checks. Raw primary/recovery artifacts, Gold, combined-run-manifest.v1.json, and production were not modified.
