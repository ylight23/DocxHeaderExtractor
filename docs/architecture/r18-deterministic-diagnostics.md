# R18.2 Deterministic Diagnostics

R18.2 adds evaluation-only diagnostics to the R18 decision-ownership report. Diagnostics read
parser-owned marker facts, hierarchy facts, resolved structural elements, and the existing optional
reference observations. They never reject a candidate, alter a level or parent, trigger repair, or
call a provider.

The initial checks are tri-state:

- `MARKER_SEQUENCE` checks marker depth, numeric path/components, and resolved-level coherence.
- `HIERARCHY_CONSTRAINT` checks resolved level bounds, parent presence, source order, and parent
  level ordering.
- `SIBLING_CONSISTENCY` stays `NOT_APPLICABLE` when the current route does not expose an explicit
  sibling relation; absence is not treated as a pass.

When a legitimate reference is attached, each diagnostic reports alerts, true error alerts, false
alerts, precision, recall, and conditional final-error rates. Without a comparable reference, those
quality fields remain null and the report says `NOT_MEASURED_WITHOUT_REFERENCE`.

The deterministic `mau.docx` audit produced 57 diagnostic observations, four applicable marker
checks, zero alerts, and zero provider calls. These results are telemetry only. Because no
reference-backed errors were present, R18.2 does not justify targeted repair; R18.3 is therefore
`NOT_JUSTIFIED`.
