# A99 — I2 Duplicate-Text Exact Identity Recovery

Status: completed and rejected by paired DEV evaluation.

## Scope

- Parent: B0 frozen semantic-text contract.
- Rejected ancestor: I1.
- One generic duplicate-identity intervention.
- Gold was used only after each prediction was frozen.
- No document-specific rule, Gold/runtime leakage, projection change, or I3 follow-up.

The B0 audit at the actual parent commit contained 6 ambiguous duplicate-text
losses, not 16. The count 16 belongs to the rejected I1 residual report and is
not a valid B0 parent target.

## Audit

| Category | Count |
|---|---:|
| Cross-occurrence duplicate cases | 0 |
| Intra-occurrence duplicate cases | 6 |
| Binder-lost identity | 0 |
| Model-missing identity | 6 |

Because the frozen B0 model outputs did not contain sufficient identity context,
I2 required fresh inference. It made 20 provider calls in total; the offline
rebuild made no provider calls.

## Paired DEV results

| Repeat | B0 | I2 |
|---|---|---|
| R1 | TP 143 / FP 7 / FN 10 / F1 .943894 | TP 144 / FP 9 / FN 9 / F1 .941176 |
| R2 | TP 142 / FP 8 / FN 11 / F1 .937294 | TP 142 / FP 8 / FN 11 / F1 .937294 |
| R3 | TP 143 / FP 17 / FN 10 / F1 .913738 | TP 144 / FP 9 / FN 9 / F1 .941176 |
| Micro | TP 425 / FP 22 / FN 34 / F1 .938190 | TP 430 / FP 26 / FN 29 / F1 .939891 |

## Decision

```text
ambiguous duplicate text: 6 -> 14
resolvedDuplicates: 0
incorrectlyResolvedDuplicateCount: 0
systemLoss: 0
classification: A99_DUPLICATE_IDENTITY_REGRESSION
keepOrRevert: REVERT_INTERVENTION
```

The intervention is rejected because it resolved no duplicate identities,
increased ambiguity, and regressed the R1 paired precision/F1 gate. B0 remains
the parent. No I3 was started.

## Verification

- Focused tests: 52/52 passed.
- Release build: passed.
- Full deterministic suite: 1412 passed, 1 known unrelated N15 diagnosis
  failure; no N15 changes were made.
- `git diff --check`: passed.

Primary machine-readable result:
`eval/a99-closed-loop/semantic-text-residual-loop/i2-duplicate-identity/summary.v1.json`
