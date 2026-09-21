# gold-current — the canonical Gold authority root

This is the **only** active Gold root. Every evaluator, scorer and preflight resolves Gold through
`registry.v1.json`; nothing scans folders, and there is no fallback to an older root.

```
registry.v1.json                  the only discovery authority
documents/<AUTHORITY-ID>.gold.v1.json   one file per authority, semantic + occurrence together
```

## Why one root

There used to be four active ones — `strict-gold-v4`, the semantic vNext freezes, the occurrence
artifacts, and the DOC-0252 review pack — and which one a piece of code happened to open decided
what it believed the truth was. Two tasks could disagree about how many headings a document has and
both be reading a real file:

| Authority | Canonical | An older artifact says |
| --- | --- | --- |
| DOC-0205 | 72 | 71 |
| DOC-0256 | 34 | 24 |
| DOC-0258 | 37 | 24 |
| DOC-0264 | 159 | 158 |
| DOC-0252 | 41 | 27 |

Picking a root was picking an answer.

## Semantic truth is primary

The latest user-approved semantic truth sets `semanticHeadingTotal`. An older occurrence artifact
covering fewer headings never reduces it. Where such an artifact exists but does not describe the
current authority, it is recorded in `provenance` as `NON_CANONICAL_PROVENANCE_ONLY` and the source
is simply not occurrence-evaluable yet — a smaller loss than a quietly wrong total.

Occurrence evidence is imported only when it provably describes the same source bytes and the same
heading count. Nothing is invented to fill a gap.

## One root, not one coordinate system

A registered authority declares what may be scored against it:

| Capability | Meaning |
| --- | --- |
| `semanticEvaluable` | the heading total is authoritative |
| `occurrenceEvaluable` | headings are bound to identified source occurrences |
| `characterSpanEvaluable` | those bindings carry exact UTF-16 spans |
| `visualBindingEvaluable` | headings are bound to visual regions |

A scanned source can carry semantic truth with no bindings at all. `DOC-0001` binds with UTF-16
spans; `DOC-0252` binds by alias and selection mode; `DOC-0202` needs visual recovery and binds
neither way. Forcing all three into one coordinate system would mean inventing coordinates for two
of them.

Evaluators call `CanonicalGoldRegistry.RequireCapability` and are refused the axes their Gold does
not carry. A source with no bindings is not a source that scores zero — it is one that cannot be
scored on that axis, and a zero would be a measurement nobody made.

## Fail closed

Resolution throws when the authority is unregistered, the file is missing, or the file no longer
matches its recorded `goldSha256`. There is deliberately no `try gold-current, then strict-gold-v4`
path: a fallback is how the older answer comes back.

## Provenance, not deletion

The legacy roots still exist and are **NON_CANONICAL_PROVENANCE_ONLY**. They may be read when
auditing history — `A99StrictGoldV4Tests` pins what the historical artifacts say, including totals
that differ from canonical — but never as scoring authority.

## Regenerating

Derived from the approved freezes; no provider or model call is involved.

```
A99_FREEZE_UPDATE=1 dotnet test -c Release --filter FullyQualifiedName~CanonicalGoldConsolidationTests
```
