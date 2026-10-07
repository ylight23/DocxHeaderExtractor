# N15-N17 source ownership and reachability audit

Baseline: `main@3d05ee878aeb5ee649146c26b1ce9be46da0486d`.
Scope: ownership, dead source types, and misleading source vocabulary only.
Provider execution, Gold mutation, frozen artifact rebaseline, and semantic tuning: none.

## Ownership closure

- Shared `SourceMarkerFact` belongs to `Source/Common` and is used by DOCX/PDF marker parsing.
- `PdfSourceFacts` and `PdfSourceContextBuilder` belong to the `Source.Pdf` namespace.
- `HeadingHierarchyFactAudit` belongs to `Authority`, matching its existing folder.
- `SemanticLaneOptions` is execution policy in `Pipeline/PdfSemanticLaneOptions.cs`.
  The previous eight-file Pipeline allowlist is explicitly superseded by nine files, adding only
  this deadline policy. Helpers/parsers remain forbidden there.
- Source files have folder-aligned namespaces and no dependency on the Pipeline namespace.
- `PdfHierarchyFactsInventory` belongs to `Authority` and consumes already-validated headings.

## Dead-type closure

Before removal, the only external production uses of `PdfStyleClusterProfile` were two calls to
`StyleOf`: per-line source adaptation and layout grouping. There were no profile construction,
cluster-statistics, title/group collection, configuration, reflection, or test consumers found.
The profile and statistics records were removed, not replaced with heading heuristics.
The same `StyleOf` algorithm now belongs to `PdfStyleKey`; its half-point default, midpoint
rounding, font name, and fill identity are regression-tested. `PdfTextUtilities` is retained.

## Vocabulary and authority

`PdfLayoutBlock`/`PdfLayoutBlockGrouper` describe geometry/font/spacing grouping.
`PdfSourceContext`/`PdfSourceContextBuilder` describe parser context, not heading authority.
`DisplayReadable` is source display reconstruction; verbatim glyph projection remains distinct.
The obsolete comment describing two font/bold heading builders was removed.
No grouping, reconstruction, prompt, parser ledger, binding, or hierarchy behavior was changed.

The heading seam remains `IHeadingAuthority`: DOCX uses `TextSemanticHeadingAuthority`; PDF uses
`FunctionAnchorExtentHeadingAuthority`. Source preparation, review/writeback, post-processing,
audit, and qualification/history retain their existing responsibilities.

## Final dead-symbol inventory

A production-source scan of 187 C# files excluded V5Qualification, Eval, bin, and obj. It enumerated
public/internal type declarations and searched whole-symbol references in other production files.
No additional whole-file zero-external-type-reference candidates were found. Co-located records,
enums, helper types, and inferred return contracts with no external type-name mention were reviewed
as local implementation/contracts, not declared dead solely from this scan.

This is a static reference inventory with caller review, not a reflection/serialization reachability
proof or a claim that every private member is necessary. It authorizes no further broad deletion.
Architecture guards pin source dependency direction, namespace ownership, retired vocabulary,
provider neutrality, and the exact Pipeline allowlist. Existing frozen-wire tests remain unchanged.
