# Document 004 first-loss trace V4

V4 reconstructs pre-span role evidence from the frozen run and never uses final `blockDecisions`.

Source present: 93/93. Candidate generation loss: 10. Candidate selection loss: 28. Selected: 55.

Role heading proposal: 6; non-heading: 0; uncertain: 0; evidence unrecoverable: 0. Span execution timeout is counted only after a proven heading-like role: 6. Post-selection trace unresolved: 0.

Semantic lane: `complete` (scheduled 160, completed 0, timed-out counter 160); span lane: `partial_timeout` (scheduled 160, completed 0, timed-out counter 160). HTTP timeout/request counts are not observable from these counters.

Unique role contracts: 20; unique role responses: 20. V2 exact: 55; reproduced: 55; mismatch: 0.

Carrier binding is not unique; ambiguous occurrences: 62. Provider calls: 0. Production changed: false. Remediation: false.
