# DOC-0116 TRUE_HEADING Official Scoring

Status: `OFFICIAL_SCORING_COMPLETE`

The frozen TRUE_HEADING projection was scored offline against the promoted,
user-approved DOC-0116 Gold authority. Prediction and Gold were not mutated.

## Authority gates

- Prediction SHA unchanged: PASS
- TRUE_HEADING projection SHA unchanged: PASS
- Gold status: `USER_REVIEWED_CANONICAL_GOLD_AUTHORITY`
- Gold validation: PASS
- Gold source-faithful bindings: 120/120
- Gold source SHA equals prediction source SHA: PASS
- Representation bridge: `BRIDGE_PASS`
- Semantic target: `ALL TRUE HEADING OCCURRENCES`
- Independent `headingDecision`: PASS
- Gold-independent projection: PASS

## Score

| Measure | Value |
|---|---:|
| Predicted TRUE_HEADING | 239 |
| Gold TRUE_HEADING | 120 |
| TP | 85 |
| FP | 154 |
| FN | 35 |
| Precision | 35.56% |
| Recall | 70.83% |
| F1 | 47.35% |

Matching used exact physical occurrence identity:
`sourceId + source span start + source span end`.

All 85 physical matches also matched Gold exact text; there were no duplicate
prediction keys, duplicate Gold keys, or span/text mismatches among matches.

The 11 `NON_VERBATIM_TEXT` provider items remain binding-forensic observations;
they were not reintroduced into the prediction or scoring.

## Execution firewall

- Provider calls during scoring: 0
- Gold reads: 1
- Prediction rebuilt or mutated: false
- Gold mutated: false

The historical `91/198/29, F1 44.50%` result remains
`HISTORICAL_PROVISIONAL_ONLY` and is not directly compared because it used a
different semantic contract and prediction authority.
