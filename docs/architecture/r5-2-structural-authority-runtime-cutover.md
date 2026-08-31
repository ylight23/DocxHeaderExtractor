# R5-2 Structural Authority Runtime Cutover

Status: PASS

R5-2 moves the normal outline owner at the common PDF final-structure choke point. The existing
`PdfProductOutputSerializer` remains the product serializer. `PdfProductOutlineAdapter` remains an
old-path parity oracle, but normal runtime no longer calls it.

## Runtime path

```text
PdfFinalStructure + PdfOutputDecision
        -> StructuralAuthorityMaterializer
        -> ValidatedStructure + EmittedElementIds
        -> HeadingOutlineProjection
        -> DocumentOutline.Headings
```

The materializer carries source identity, stable identity, exact source spans, text, level, parent
relations, decision provenance, and compatibility projection metadata. It does not select
candidates, resolve hierarchy, or re-derive product policy. Title, Subtitle, and Heading are all
accepted by the compatibility projection.

## Parity evidence

R5-2A parity compares the complete serialized `HeadingRecord[]` produced by the old adapter with
the generic materializer plus projection. The test covers canonical source identity, stable ID,
text, level, span, decision state, provenance, and nullable fields. Product output continues to be
serialized directly from the same final structure and decisions.

The deterministic host fixture also joins the existing R4-11 fingerprint across the canonical
tool, CLI, Web, MCP, and AgentHarness. No external provider is used.

## Scope

This checkpoint does not rewrite DOCX/PDF producers, hierarchy resolution, output policy, or the
public CLI/Web/MCP contracts. It changes only the internal final authority representation and
keeps `DocumentOutline` as the compatibility API.
