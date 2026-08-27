# Span production canary - offline evidence closure V2

This is an offline audit of the one retained Phase B canary. It makes no
provider call and preserves V1 unchanged.

## Checkpoint coherence

The checkpoint has 43 rows: 1 selection, 20 semantic, 21 span, and 1
downstream. Semantic candidate IDs are 160 total and 160 unique, with no
duplicate semantic identities and no selected IDs missing. Span block IDs are
84 total and 84 unique, with no duplicates. The single-run coherence result is
`PASS`.

## Recovered role cohort

Reading the semantic checkpoint payloads directly gives:

| role | unique IDs |
|---|---:|
| `HeadingTopic` | 144 |
| `BodySentence` | 16 |
| `TableOrChartLabel` | 0 |
| `DecorativeNoise` | 0 |
| `Uncertain` | 0 |
| **total** | **160** |

This supersedes the V1 approximation based on route validation counters for
offline role accounting. The route counters `completed=81` and `timedOut=79`
are execution counters, not role counts.

## Span reconciliation

The checkpoint contains 84 unique span block IDs, of which 81 resolved and 3
are null. This is internally consistent (`81 + 3 = 84`) but does not equal the
144 recovered `HeadingTopic` IDs. Therefore:

`SPAN_ROLE_COHORT_CONSISTENCY = FAIL`

The evidence does not support treating the 84 span blocks as the complete
heading role cohort. No repair or re-interpretation is applied in this audit.

## Budget and hypotheses

The direct span budget upper bound is 90 seconds because production passes
`RemainingOr(RequestTimeout)` into the span lane. The five-minute lane deadline
is shared semantic budget and must not be used as the direct span budget.
The upper-bound mean budget for the 21 observed span batches is
`4285.714285714286 ms`.

For the canary, H1 architecture gap is proven, operational impact is not
applicable, H2 is refuted, H3 is not applicable because the span lane had no
failure, and H4 is refuted for this completed sequential run. These statements
do not generalize to the frozen 004 run: frozen H2/H3/H4 remain unresolved.

Exact selection match remains `false`; frozen and canary raw source-line hashes
are different and no identity normalization was used. The canary demonstrates
that the unchanged span contract can complete this observed workload, but it
does not demonstrate R1 material recovery after a timeout.

Provider calls in this task: `0`. Retained provider runs: `1`. Production code
changed: `false`. Remediation: `false`.
