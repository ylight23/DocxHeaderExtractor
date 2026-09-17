# DOC-0116 canonical prediction scoring

This is offline scoring of frozen provider output. No provider call or prediction rebuild was used as an authority.

- Prediction occurrences: **289**
- Canonical Gold occurrences: **120**
- TP / FP / FN: **91 / 198 / 29**
- Precision / Recall / F1: **0.3149 / 0.7583 / 0.445**

The 5 `NonVerbatimText` observations remain diagnostic attribution only; they are not returned to the prediction set.

Gold freeze: `C:\DocxHeaderExtractor-a99-closed-loop\artifacts\level-accuracy\canonical-vnext-correctness-doc0116-live-execution-v2-1\gold-scoring-v1\gold-freeze.v1.json`
The historical `strict-gold-v4` DOC-0116 artifact was not used because it is partial/non-exhaustive.