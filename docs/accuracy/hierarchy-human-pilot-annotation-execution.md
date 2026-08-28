# G1A Human Hierarchy Pilot Annotation

Status: `READY_FOR_BLIND_HUMAN_ANNOTATION`.

This execution packet contains 24 occurrence-level rows across documents 004, 030, 043, and 058. Each row includes source context and neighboring lines, but no prediction, confidence, validator, or output fields.

A human annotator must fill level, parent, scope, and typePath independently with `OBSERVABLE`, `NOT_OBSERVABLE`, or `AMBIGUOUS`, then add annotatorId, annotatedAt, and annotationVersion. Until those fields are persisted, `PILOT_REVIEWED=0` and hierarchy authority is not created.

`PROVIDER_CALLS=0` and `PRODUCTION_CODE_CHANGED=false`.

Output artifact: `eval/accuracy/hierarchy-human-pilot-annotation.v1.json`.
