# R18.1 Document-Mode Evidence Experiment

R18.1 tested one opt-in prompt change: expose the already measured `DocumentModeReport` in the
document view sent to the model. It did not expose gold labels, expected answers, final decisions,
verified spans, or writeback data, and it did not change authority or validation.

The experiment used the same ten-document deterministic benchmark, model, runtime profile,
temperature, and seed for three baseline runs and three mode-evidence runs. The model was
`qwen/qwen3.5-9b` through OpenRouter. The raw run logs are kept outside the repository; the
reproducible aggregate is recorded in `eval/r18/mode-evidence-experiment.v1.json`.

The baseline mean overall F1 was 91.93%; mode evidence was 91.87%. Recall fell from 91.03% to
90.40%, while the mode arm had wider run-to-run variance. The keys do not contain explicit role
labels, so role precision/recall is not measured.

Decision: `NO_EVIDENCE`. The opt-in production path was reverted, while this evaluation record is
retained. The default prompt and authority route remain unchanged. This experiment does not support
an Accuracy-99 claim.
