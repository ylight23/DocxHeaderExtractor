# Span production canary

The single controlled Phase B run used OpenRouter `qwen/qwen3.5-9b` on
document 004 with the frozen 160-candidate profile. No VLM call, retry, or
production-code change was made.

## Result

- Semantic lane: `complete`; 160 scheduled, 81 completed, 79 timed out.
- Span lane: `complete`; 21 checkpoint rows, 81 resolved blocks, 3 null blocks.
- Selected cohort: 160 candidates and 181 source-line IDs.
- Selection match: `false`. The raw source-line hashes differ because the
  canary and frozen artifacts contain different Unicode spellings in source
  identities. They must not be compared as one experiment.
- Product counters: 79 validated structures, 78 grounded, 69 product headings.

The checkpoint contains 43 rows: 1 selection, 20 semantic, 21 span, and 1
downstream. It does not contain `startedAt`, `completedAt` pairs, request
latencies, or a timing decorator trace. Therefore call-started/cancelled/
faulted counts and latency statistics are recorded as `NOT_OBSERVABLE`; no
HTTP timeout claim is inferred from the semantic counters.

## Preservation and hypotheses

The span lane completed, so no after-timeout preservation cohort was observed:
preserved spans after timeout, validator accepts from preserved spans, and
product output from preserved spans are all zero. H1 architecture gap remains
proven by Phase A, while operational impact is not applicable to this run.
H2 is refuted for this canary; H3 and H4 remain unresolved without timing
evidence. Partial-timeout recurrence is proven and recurrence of the durable
R1 preservation mechanism remains partial.

Provider runs: `1`. VLM calls: `0`. Production behavior changed: `false`.
See `eval/accuracy/span-production-canary.v1.json` for hashes and the complete
machine-readable accounting.
