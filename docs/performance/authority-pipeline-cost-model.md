# Authority Pipeline Cost Model

Measured counts come from frozen run/checkpoint artifacts. Population sensitivity is an offline estimate of work units, not measured provider latency.

- `K=160`, role batch `8`, span batch `4`, concurrency `2`
- role timeout `90s`, batch timeout `120s`, lane deadline `300s`
- `ACTUAL_PROVIDER_LATENCY=NOT_OBSERVABLE`, `PROVIDER_CALLS=0`, `PRODUCTION_CODE_CHANGED=False`

| document | sourceLines | generatedCandidates | selectedCandidates | roleInputs | roleBatches | spanInputs | spanBatches | checkpointWrites | checkpointBytes | validatedHeadings | emittedHeadings |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `004` | 2688 | 2653 | 160 | 160 | 20 | 112 | 28 | 49 | 146403 | 96 | 85 |
| `030` | 6273 | 3457 | 160 | 160 | 20 | 59 | 15 | 36 | 123616 | 49 | 49 |
| `043` | 7047 | 2038 | 160 | 160 | 20 | 18 | 5 | 26 | 106372 | 17 | 11 |
| `058` | 5327 | 1884 | 160 | 160 | 20 | 66 | 17 | 38 | 128872 | 31 | 12 |

## Counterfactual estimates

For each candidate population increase, role/span input deltas are scaled work estimates; checkpoint write delta counts additional role and span batches only.

### 004

| population | role input delta | role batch delta | span input delta | span batch delta | checkpoint write delta |
|---|---:|---:|---:|---:|---:|
| `population_plus_10%` | 16 | 2 | 12 | 3 | 5 |
| `population_plus_25%` | 40 | 5 | 28 | 7 | 12 |
| `population_plus_50%` | 80 | 10 | 56 | 14 | 24 |

### 030

| population | role input delta | role batch delta | span input delta | span batch delta | checkpoint write delta |
|---|---:|---:|---:|---:|---:|
| `population_plus_10%` | 16 | 2 | 6 | 2 | 4 |
| `population_plus_25%` | 40 | 5 | 15 | 4 | 9 |
| `population_plus_50%` | 80 | 10 | 30 | 8 | 18 |

### 043

| population | role input delta | role batch delta | span input delta | span batch delta | checkpoint write delta |
|---|---:|---:|---:|---:|---:|
| `population_plus_10%` | 16 | 2 | 2 | 0 | 2 |
| `population_plus_25%` | 40 | 5 | 5 | 1 | 6 |
| `population_plus_50%` | 80 | 10 | 9 | 2 | 12 |

### 058

| population | role input delta | role batch delta | span input delta | span batch delta | checkpoint write delta |
|---|---:|---:|---:|---:|---:|
| `population_plus_10%` | 16 | 2 | 7 | 2 | 4 |
| `population_plus_25%` | 40 | 5 | 17 | 4 | 9 |
| `population_plus_50%` | 80 | 10 | 33 | 8 | 18 |

No timing field in the retained artifacts identifies actual provider latency; it remains `NOT_OBSERVABLE`.
