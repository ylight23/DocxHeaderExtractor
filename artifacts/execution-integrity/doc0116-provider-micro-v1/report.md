# DOC-0116 provider micro-run

Status: DOC0116_REAL_PROVIDER_MICRO_V1_COMPLETE

Primary classification: G. DOCUMENT_BUDGET_EXHAUSTED_BY_MULTIPLE_HEALTHY_CALLS

- Logical calls: 34
- Physical attempts: 34
- Provider calls: 34
- Completed calls before watchdog: 33
- Blocking call start offset: 299332.48 ms
- Blocking call elapsed before kill: 744.264 ms
- Sum completed-call durations: 296786.202 ms
- Median completed-call latency: 5494.08 ms
- P95 completed-call latency: 28025.768 ms
- Document-budget exhaustion evidence: True
- Gold reads: 0
- Non-scorable forensic run: true

The run uses the 300-second process-tree watchdog. Call-scoped transport evidence is in `classification.json`; the complete offline timing reconstruction is in `timing-audit.json`.
