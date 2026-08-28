# Candidate Selection First-Loss Diagnosis

Diagnostic-only offline replay over frozen 004 source identities. No provider call, no production change.

- `TOTAL_SELECTION_LOSS = 28`
- `BY_FIRST_REJECTING_OPERATION = { global_budget: 28 }`
- `CUTOFF_NEIGHBORHOOD_COMPLETE = false`
- `CROSS_DOCUMENT_RECURRENCE = PROVEN`
- `REMEDIATION_JUSTIFIED = NO`
- `PROVIDER_CALLS = 0`
- `PRODUCTION_CODE_CHANGED = false`

`CUTOFF_NEIGHBORHOOD_COMPLETE=false` because one frozen loss is rank `2653` in a `2653`-candidate pool, so ranks `r+1..r+3` do not exist to serialize.

## Finding

All 28 frozen 004 selection losses are reviewed heading occurrences whose covering candidate exists in the generated pool, but falls below the global top-160 cutoff. The selector applies no page budget, dominance, duplicate/canonical collision, diversity constraint, or hard exclusion before the cutoff.

The nearest lost rank is 822, so `budget +1`, `+5`, and `+10` recover zero headings while exposing additional candidates. Removing the global budget recovers all 28 but selects the full 2,653-candidate pool, which is diagnostic evidence against treating budget increase as a justified fix. Forcing all lost occurrences into the fixed budget recovers them only by displacing 28 baseline candidates.

## Counterfactuals

| counterfactual | recoveredHeading | displacedHeading | nonHeadingExposure | netReviewedGain | additionalCandidatesSelected | pageCoverageChange |
|---|---:|---:|---:|---:|---:|---:|
| `budget_plus_1` | 0 | 0 | 1 | 0 | 1 | 0 |
| `budget_plus_5` | 0 | 0 | 5 | 0 | 5 | 0 |
| `budget_plus_10` | 0 | 0 | 10 | 0 | 10 | 0 |
| `remove_global_budget_predicate` | 28 | 0 | 2376 | 28 | 2493 | 10 |
| `force_lost_occurrences_keep_budget` | 28 | 0 | 0 | 28 | 28 | -3 |

## Cross-Document

| class | document | reviewedProof | minRank | maxRank |
|---|---|---:|---:|---:|
| `CLASS_1` | `004` | 28 | 822 | 2653 |
| `CLASS_1` | `030` | 200 | 177 | 3457 |
| `CLASS_1` | `043` | 39 | 208 | 1922 |
| `CLASS_1` | `058` | 28 | 165 | 1737 |
