# DOC-0258 — span-aware canonical occurrence review

Status: **CANONICAL_EXHAUSTIVE_OCCURRENCE_READY**

The prior whole-container review at 82641d4 is preserved and superseded for DOC-0258 granularity only. The corrected unit is source container + exact UTF-16 heading span.

## Source-only freeze

- Source containers reviewed: 146
- Accepted heading spans: 44
- Unresolved containers: 0
- Run boundaries preserved: TRUE
- Exact UTF-16 offsets: TRUE
- Provider/model calls: 0
- Historical level read: FALSE

The source-only decisions were materialized before reading historical exact bindings or semantic total. Those authorities were used only for post-freeze diagnostics.

## Post-freeze diagnostics

- Historical exact positives: 24
- Preserved exactly: 24
- Inside new true-heading containers: 24
- Inside new no-heading containers: 0
- Known-positive conflicts: 0
- Source-only span count: 44
- Existing semantic total: 37

No hierarchy, identity, parent, or level authority was created. DOC-0258 must not reopen hierarchy automatically from this lane.
