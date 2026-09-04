# R18 Final Decision

R18.0 decision ownership telemetry is implemented without changing production authority. The
deterministic audit remains provider-free and records `NOT_OBSERVABLE` whenever a stage cannot expose
ownership.

R18.1 document-mode prompt evidence was measured three times per arm on the same ten-document
corpus. Its mean F1 was neutral within run variance, recall was lower, and role metrics were not
available from the current keys. Decision: `NO_EVIDENCE`; the production experiment was reverted.

R18.2 deterministic diagnostics are telemetry-only and tri-state. The current corpus has no
reference-backed errors, so diagnostic precision and recall are not measurable. Targeted repair is
`NOT_JUSTIFIED`; no R18.3 repair path is promoted.

The A99 infrastructure merge remains separate from R18, and `ACCURACY_99` remains
`NOT_MEASURED`. Frozen N15 was not changed.
