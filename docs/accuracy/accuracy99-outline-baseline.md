# ACCURACY-99 outline baseline

This is a measurement-only baseline on the frozen architecture revision. It does not tune production behavior.

- Status: `NOT_YET_MEASURABLE`
- Base and accuracy revision: `732c3505afc5dd312423ed0fa58056192fb39608`
- Branch: `accuracy/outline-99-baseline`
- Blind holdout: `NOT_AVAILABLE`
- Provider calls: `0` (deterministic `DisableLlm=true` run)

## Contract status

The selected reviewed keys provide positive occurrence labels, but do not provide parser-owned exact spans or reviewed exhaustive negatives. Therefore strict precision, exact-span recall, parent accuracy, and hierarchy accuracy are `NOT_MEASURABLE`; unlabeled occurrences are not treated as negatives.

| Dataset | Class | Predicted outline | Gold occurrences | Source-joined | Level compared/correct | Unjoined |
|---|---|---:|---:|---:|---:|---:|
| 010 | HUMAN_GOLD | 0 | 50 | 0 | 0/0 | 50 |
| 025 | HUMAN_GOLD | 0 | 0 | 0 | 0/0 | 0 |
| 051 | HUMAN_GOLD | 0 | 30 | 0 | 0/0 | 30 |
| 052 | HUMAN_GOLD | 0 | 32 | 0 | 0/0 | 32 |
| 056 | HUMAN_GOLD | 0 | 46 | 0 | 0/0 | 46 |
| 063 | HUMAN_GOLD | 0 | 0 | 0 | 0/0 | 0 |
| 092 | HUMAN_GOLD | 0 | 64 | 0 | 0/0 | 64 |

## First-loss ledger

222 labeled occurrences were not joined to the generic outline (2 missing-input ledger entries). The current result envelope cannot attribute a deeper candidate-stage loss, so these remain `UNKNOWN` unless the source itself was absent (`SOURCE_NOT_PARSED`).

## Historical reconciliation

Historical accuracy and architecture artifacts were not overwritten. Pipeline-derived TOC keys, silver packets, and the unfrozen 560-occurrence negative adjudication packet remain inventory/evidence only.

## Next remediation owner

Dataset/adjudication: freeze parser-owned `SourceId + exact span + parent relation` gold occurrences and reviewed exhaustive negatives, then rerun this evaluator before any production tuning.
