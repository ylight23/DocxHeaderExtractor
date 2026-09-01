# R5-4C3A ListItem Structural Lane

R5-4C3A activates the first production `ListItem` lane. The PDF semantic analyst now has the
closed proposal role `list_item_topic`, which projects to the internal `ListItem` route. The
non-heading producer maps that proposal to `StructuralElementType.ListItem` and
`ProposedRole.ListItemTopic`, then materializes it only through `StructuralProposalValidator`.

The producer requires both independent inputs:

- semantic proposal: `PdfSemanticRole.ListItemTopic`;
- parser-owned source evidence: `document_body`, a parsed marker, a `marker:*` evidence entry,
  and a source layout shape (`standalone_line` or `multi_line_cluster`).

Consequently, numbering alone cannot authorize a `ListItem`, and a semantic proposal without
grounded list evidence is rejected. The source reference is the PDF block identity with its exact
source span; no model text or role is materialized directly. List items remain outside
`HeadingOutlineProjection`.

## Verification

- Execution revision: `7d4986f92a3b2a3192ae5780bff60a4759824844`
- Publication revision: `containing-closure-commit`
- Focused structural and semantic tests: `58/58` passed
- Host E2E: `2/2` passed
- Deterministic replay 028/056/091: all structure, decision, product, and heading deltas `0`
- Provider calls: `0`
- Release build: `PASS`
- `git diff --check`: `PASS`
- Contract-backed ListItem emission: `1`
- ListItem numbering-only authority: `0`
- ListItem validator bypasses: `0`
- Heading projection of ListItem: `0`

The full suite measured `846 total, 844 passed, 2 failed, 0 skipped`. The two failures are the
pre-existing frozen C1 and N15 diagnostic probes; `NEW_FAILURES=0`,
`CHANGED_FINGERPRINTS=0`, and `UNJOINED=0`.

No `FigureTitle`, `CaptionOf`, `Figure`, or `Table` container behavior is included. The existing
TableTitle and Caption lane remains unchanged. R5-4C3B is reserved for the semantic distinction
between figure titles and captions.

R5-4C3A is closed and R5-4C3B is authorized.
