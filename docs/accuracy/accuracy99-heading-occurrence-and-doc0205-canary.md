# Accuracy99 heading occurrence identity

Gold v2 identifies a logical heading with `HeadingOccurrenceId`, derived from the parser-owned
`sourceId` and the exact `headingSpan`:

```text
<sourceId>@<start>:<end>
```

`sourceId` remains the physical source-occurrence identity. It is not sufficient as the positive
heading identity because one source occurrence can contain more than one reviewed heading span.
Parent references, prediction matching, TP/FP/FN, hierarchy scoring, and autonomy coverage use
the logical heading occurrence identity.

The reviewer stores multiple heading rows per source occurrence and recomputes the logical ID when
the reviewed span changes. The v2 validator rejects a duplicate source-plus-span pair while
accepting distinct spans in the same source occurrence.

## DOC-0205 reconciliation canary

The evaluation-only command is:

```text
dhx accuracy99 doc-0205-canary --root <repo>
```

It joins the 71 retained DOC-0205 references to the existing model traces only where the exact
reference span is retained. It never infers a heading span from the full source paragraph and it
never changes production behavior. The current bridge has a physical source owner for all 71
references, but no exact reference span, so the current result is:

```text
BLOCKED_ON_REFERENCE_SPAN_EVIDENCE
UNRESOLVED = 71
PROVIDER_CALLS = 0
```

The report intentionally distinguishes representation, candidate construction, selection, model
exposure, and final lineage loss. A missing exact span remains `UNRESOLVED`; it is never emitted as
the old generic `candidateLost` label.
