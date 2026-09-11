# A99 Terminal Research Closure

Status: `A99_DEV_TARGET_NOT_REACHED`

This document is the current-status closure for the A99 closed-loop research generation. It
does not change the frozen L2 predictions, Strict Gold, runtime defaults, or historical reports.
The closure was produced offline at terminal checkpoint
`213475a17cb498df19114df3f6ebdfe55e706d3`.

## 1. Terminal decision

The optimization loop is closed. The A99 DEV target was not reached, so there is no release
candidate:

```text
terminalStatus       = A99_DEV_TARGET_NOT_REACHED
optimizationLoop     = CLOSED
causalSpaceStatus    = NO_EVIDENCE_BACKED_INTERVENTION_REMAINS
releaseCandidate     = HOLD
modelCalls           = 0
providerCalls        = 0
```

The next phase is research generation, not another prompt/model/runtime optimization loop:
`A99-R2_INDEPENDENT_RESIDUAL_GENERALIZATION_STUDY`.

## 2. Canonical authority

The accepted behavioral parent is B0 at `76c4e01`, using `qwen/qwen3.7-flash` with the
semantic-text exact-binding contract. The canonical 5-document, 15-cell, 3-repeat aggregate is:

| Gold | TP | FP | FN | Precision | Recall | F1 |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 459 | 428 | 32 | 31 | 0.9304347826 | 0.9324618736 | 0.9314472252 |

The accepted architecture is:

```text
source semantic text
  -> model semantic proposal: sourceAlias + verbatimText + role
  -> deterministic exact UTF-16 binder
  -> existing validator/projection
```

`SYSTEM_LOSS=0` and `BIND_FAILURE=0` in the canonical B0 authority. The authoritative machine
record is [canonical-authority.v1.json](../../eval/a99-closed-loop/closure/c1/canonical-authority.v1.json).

The earlier `425/22/34` lineage is retained as historical evidence only; it is not the current
aggregate authority.

## 3. Residuals

There are seven persistent target omissions across the frozen B0 repeats. The residual owner is
not promoted to a runtime defect. Role and hierarchy quality remain explicitly unmeasured:

```text
persistent target omissions = 7
MODEL_ROLE_ERROR            = NOT_MEASURED
HIERARCHY_ERROR             = NOT_MEASURED
```

Per-document and per-repeat frozen measurements are in the canonical authority artifact. Gold is
used for offline evaluation only and is not available to runtime transformation logic.

## 4. Causal ledger

The audited intervention families do not provide evidence for another generic optimization within
the current generation. I1 generic omission review, I2 duplicate identity, I3 lossless source
boundaries, I5 semantic contrast wording, and I6 discovery/role decomposition were rejected or
reverted. I4 challenger-model work, E3 occurrence-context authorization, structural enrichment,
VLM, candidate-first, and numeric-offset contract lines are closed under their recorded gates.

I6 is recorded at both required layers:

| Layer | TP | FP | FN | F1 | Result |
| --- | ---: | ---: | ---: | ---: | --- |
| Pass A discovery | 423 | 105 | 36 | 0.857142857 | No target recovery; 7/7 persistent misses |
| Final after role pass | 423 | 16 | 36 | 0.942093541 | Role pass did not change heading identity |

The lower Pass-A score is the relevant decomposition test because Pass B had no authority to add,
remove, or alter a discovered heading. The full ledger is in
[causal-ledger.v1.json](../../eval/a99-closed-loop/closure/c1/causal-ledger.v1.json).

## 5. System versus evaluation-only observations

The following are evaluation observations, not runtime knowledge and not legal/document-specific
rules:

- persistent omissions and false positives;
- exact Gold occurrence comparisons;
- residual-family labels;
- I6 pass-layer comparisons;
- any target strings used to describe the frozen benchmark.

No Gold-derived condition was added to source presentation, binding, validation, projection, or
default application behavior.

## 6. New-evidence admission gate

A future experiment may start only if it targets an observed residual, identifies one causal owner,
is distinct from rejected families, changes exactly one generic causal variable, requires no
Gold-derived runtime rule, and has a pre-registered metric and KEEP/REVERT gate. The gate is
frozen in [new-evidence-gate.v1.json](../../eval/a99-closed-loop/closure/c1/new-evidence-gate.v1.json).

The following are insufficient evidence by themselves: another wording variant, another prompt
example, a larger context window, another boundary syntax, another generic second pass, another
random model, higher F1 on unrelated cases, or a manual impression that output “looks better”.

## 7. A99-R2 preparation

R2 is prepared but not executed. It is an independent residual generalization study, not an
intervention. Documents must be selected by fixed document-level metadata/domain/length/structure
strata before heading labels are inspected, exclude the current five-document cohort, and must not
be selected by searching for the known target strings. Human annotators remain blind to model
outputs and the current seven cases.

The Gold annotation protocol requires source-backed exact UTF-16 spans, semantic-family labels,
independent double review, and adjudication. Labels are created for offline measurement after the
document sample is frozen; they are unavailable to runtime. The prepared protocol is
[research-r2/manifest.v1.json](../../eval/a99-closed-loop/research-r2/manifest.v1.json).

R2 must not change the model, prompt, runtime, tuning, or current cohort authority. No selected
documents are recorded yet because the study has not been admitted or executed.

## 8. Reproduction and verification

The closure artifacts can be regenerated without inference:

```powershell
pwsh -File tools/a99-c1-closure-offline.ps1
```

The command must report `C1_MODEL_CALLS=0` and `C1_PROVIDER_CALLS=0`. Inspect the frozen B0
authority and repeat artifacts under `eval/a99-closed-loop/semantic-text-generalization/` for
the source measurements. The known N15 artifact-hash failure remains unrelated, frozen, and must
not be rebaselined as part of C1.

## 9. Historical immutability

Historical experiment reports and frozen predictions remain byte-for-byte evidence of their own
campaigns. This document and the C1 artifacts supersede stale roadmap/status summaries where they
conflict, without rewriting those historical records.

## 10. Stop condition

Stop after C1 closure and R2 preparation. Do not open I7, another wording variant, another random
model, or another generic representation variant without new evidence satisfying the admission
gate.
