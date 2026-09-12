# A99 canonical semantic Gold vNext

Current authority: `freeze-registry.v4.checked.json`.

The registry freezes semantic document totals for 13 sources (aggregate 1503). It does not
freeze an exhaustive occurrence list or character spans: every source currently has
`exactOccurrenceFreeze=false`. Therefore this directory intentionally contains no occurrence
or binding artifact. Coordinates may be materialized later only from authoritative source-backed
lists through the exact UTF-16 binder.

Runtime boundary:

`source -> source-faithful evidence -> stable aliases -> candidate attention hints -> route/context packing -> semantic proposal -> semantic validation -> exact UTF-16 binding -> hard binding validation -> global graph -> semantic boundary -> intent normalization -> deterministic projection`

The model supplies meaning (`sourceAlias/sourceAliases`, `isHeading`, exact verbatim text/parts,
role/type/scope and optional relation hints). The harness supplies coordinates. Repeated and
continuation headings remain canonical occurrences; projection may collapse them without changing
canonical truth. Legacy strict Gold and review artifacts are provenance only and are preserved.
