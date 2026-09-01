# ACCURACY-99 Phase B gold protocol

This branch establishes parser-owned annotation coordinates and human-review packets only. Production extraction code, thresholds, prompts, and validators are unchanged.

- Base revision: `732c3505afc5dd312423ed0fa58056192fb39608`
- Status: `READY_FOR_HUMAN_ADJUDICATION`
- Blind holdout: `NOT_AVAILABLE`

## Historical accounting

The 222 historical positive labels are accounted for in `gold-rebinding.v1.json`. The earlier 224 first-loss records reconcile as 222 occurrence records plus two explicit missing-input sentinels for datasets 025 and 063.

Rebinding status counts: AMBIGUOUS=24, EXACT_REBOUND=98, REVIEW_REQUIRED=100.

`EXACT_REBOUND` means only that a unique parser source identity and historical text are compatible. It is not `GOLD_READY`: exact heading span, semantic label, level, parent, and exhaustive negative review remain pending.

## Review protocol

Reviewers first see source identity, raw text, exact parser coordinates, and neighboring context. Production predictions remain hidden until initial labels are frozen. Every source occurrence must receive `HEADING`, `NON_HEADING`, `UNCERTAIN`, or `EXCLUDED`; unlabeled is never a negative.

## Dataset status

- `010`: `AVAILABLE`, occurrences=496
- `025`: `SOURCE_MISSING`, occurrences=0
- `051`: `AVAILABLE`, occurrences=1155
- `052`: `AVAILABLE`, occurrences=1139
- `056`: `AVAILABLE`, occurrences=3151
- `063`: `SOURCE_MISSING`, occurrences=0
- `092`: `AVAILABLE`, occurrences=1555

Precision/recall remain unavailable until an exhaustive reviewed development set and a genuinely blind holdout are frozen. No accuracy remediation is authorized by this phase.
