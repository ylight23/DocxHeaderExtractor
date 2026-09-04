# R18.0 Decision Ownership Audit

R18.0 is an evaluation-only audit of the current authority route. It does not change candidate
selection, model prompts, validation, hierarchy resolution, output, or writeback.

Run the deterministic audit with:

```text
dhx r18 ownership <file.docx> --out decision-ownership-report.json
```

The report keeps document and source identities, observed route traces, final decisions, optional
reference authority, ownership classes, disagreement metrics, and first-loss attribution together.
Every stage that the current route does not expose is explicitly marked `NOT_OBSERVABLE`; the audit
does not infer model ownership from a missing field or from a final output alone.

R18.0 deterministic runs force `providerCalls = 0` and are not an Accuracy-99 claim. Existing
human keys or source structural references may be attached by a later evaluation profile, with
their authority class preserved in the report.
