# N1.5 Silver Ranking-Loss Diagnosis

Model-free diagnostic only. Labels are `MODEL_ASSISTED_SILVER`; claims are `SILVER_PROXY_ONLY`.

## 029

- `silverReviewed=160`, `fullCandidate=149`, `selectedAt160=3`
- `candidateConstructionLoss=11`, `rankingLoss=146`
- rank: min `39`, p50 `2189`, p90 `3561`, p95 `3694`, max `4066`
- Recall@160 `2.0%`, @320 `5.4%`, @640 `10.7%`, @all `100.0%`
- diagnostic conclusion: `SCORE_SEPARATION_FAILURE`

## 042

- `silverReviewed=159`, `fullCandidate=148`, `selectedAt160=12`
- `candidateConstructionLoss=11`, `rankingLoss=136`
- rank: min `4`, p50 `800`, p90 `1184`, p95 `1200`, max `1751`
- Recall@160 `8.1%`, @320 `29.7%`, @640 `33.8%`, @all `100.0%`
- diagnostic conclusion: `SCORE_SEPARATION_FAILURE`

