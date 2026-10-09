# P7-D1 parser-owned spatial evidence ledger

Qualification only. This command reads a PDF through the existing source adapter,
builds a sanitized immutable evidence inventory, validates its observed assertions,
and writes a **new** artifact. It has no provider, credential, Gold, or replay input.
It does not change production requests or semantic admission.

```powershell
dotnet run --project scripts/P7SpatialEvidence -c Release -- <source.pdf> <new-ledger.json>
```

## Source and identity contract

The source SHA-256 and alias-universe SHA-256 are retained. Each observation has
the existing alias, ordinal, exact full-occurrence UTF-16 span, source-ID hash,
text hash, page, and measured bounds. Raw source text is not published.
Bounds come from the parser line's glyph-union `Left/Right/Bottom/Top`, in PDF
points with bottom-left origin. The source context's degenerate `TopY/BottomY`
is not used as a bounding box. Missing vertical bounds are never reconstructed
from font size.

Facts have content-addressed IDs including version, type, ordered subjects, value,
availability, rule, and source/measurement provenance. Observations use source
order; facts use ID order. Source, parser-block, and query enumeration order do
not affect canonical bytes. Different measurements produce different identities.

## V1 physical predicates

| Fact | Exact rule |
| --- | --- |
| `SOURCE_BOUNDS` | Available finite, positive-width/height parser line bounds and page |
| `SAME_PAGE` | All available page numbers equal |
| `SAME_VISUAL_ROW` | Same page and strictly positive **common** vertical-band intersection |
| `DISTINCT_HORIZONTAL_REGIONS` | Same page and pairwise nonoverlapping horizontal intervals; touching allowed |

`SAME_VISUAL_ROW` is a **geometric row proxy**, not a native row identifier. It
does not mean same heading, continuation, or table header. Pairwise overlapping
bands do not imply a common band across three subjects. Separate horizontal
regions are not proof of semantic columns. Cross-page observed relations are
false; missing required measurements yield `NOT_AVAILABLE` with null value and
a reason, never a guessed false. Page-only evidence may remain available when
glyph geometry is missing.

The default preregistered query universe contains every consecutive source
window of two and three occurrences, with all three relation types, plus bounds
for each occurrence. This is linear coverage, **not** every spatial pair or full
page topology. Explicit additional queries must be frozen separately and supplied
to both builder and validator. No semantic query type is supported.

## Assertion validation

The validator rebuilds expected facts from the trusted snapshot and parser details;
it does not trust a submitted ledger's hash or verification status. It rejects
altered ledgers, invented IDs, duplicate references, wrong subjects, facts outside
issued aliases, unavailable facts, and false asserted values. A successfully
validated reference certifies evidence consistency, **not semantic truth** or
whether a model actually used that evidence.

## Limits and gates

Parser geometry can depend on font reconstruction. V1 does not carry an
independent PDF-rendering attestation or certify rotated/writing-mode-specific
row relationships. Typography, source-block topology, semantic roles, and inferred
heading predicates are absent. These limitations must not be hidden by the
`OBSERVED` or `RECOMPUTED_FROM_PARSER_FACTS` status.

P7-D2 treatments, P7-D3 semantic/generalization qualification, and P7-E production
promotion remain **LOCKED**. No F1 veto, overlap pruning, or semantic authority is
introduced. The existing F1/G2A/H2-C protocol responsibilities remain unchanged.

Regression tests: `P7SpatialEvidenceLedgerTests`. The reference PDF artifact is
`artifacts/web-pdf-semantic-diagnostic/p7.spatial-evidence-ledger.v1.json`; it
contains aliases, numeric measurements, and hashes, not source excerpts.
