# R5-4C2 PDF Non-Heading Structural Lane

Status: PASS

This checkpoint activates only semantic roles that already exist in the PDF semantic pass:

- `PdfSemanticRole.TableTitle` becomes `StructuralElementType.TableTitle`.
- `PdfSemanticRole.FigureCaption` becomes `StructuralElementType.Caption`.
- `TableHeader`, `ListItem`, and `FigureTitle` remain inactive.

The lane builds a parser-owned PDF `StructuralCandidate`, creates a `StructuralProposal`, and sends it through `StructuralProposalValidator`. It then merges validated non-heading elements with the existing heading structure. It does not route through `PdfFinalHeading`, change semantic selection, or change product/heading output.

## Authority and projection

The source reference uses the PDF block identity, exact full-block text span, page, render block, and render line identities. The structural element id is independently namespaced as `structural:pdf:semantic:<blockId>`. Model semantic roles are proposals only.

`HeadingOutlineProjection` continues to accept only `Title`, `Subtitle`, and `Heading`, so the new elements remain outside the compatibility heading API.

## Execution

```ini
executionRevision = 4e1b27b3bb9d7e5683b66ff62dd018a69f4571e0
publicationRevision = containing-closure-commit
providerCalls = 0
expectedChanged = false
```

## Gates

```ini
PRODUCTION_TABLE_TITLE_EMISSION = 1
PRODUCTION_CAPTION_EMISSION = 1
PRODUCTION_LIST_ITEM_EMISSION = 0
PRODUCTION_FIGURE_TITLE_EMISSION = 0
NEW_STRUCTURAL_ELEMENT_COUNT = 2
NEW_TYPE_BYPASS_VALIDATOR = 0
NEW_TYPE_SOURCE_UNJOINED = 0
NEW_TYPE_SPAN_INVALID = 0
HEADING_PROJECTION_NEW_TYPES = 0

REPLAY_028_056_091 = 3/3
REPLAY_DELTAS = 0
HOST_E2E = 2/2
HOST_FINGERPRINT_CHANGED = false
HOST_FINGERPRINT = 16284414abee710236b27fe92f710b95efb32928e169ba0e1ede2e63891b8429
RELEASE_BUILD = PASS
DIFF_CHECK = PASS
```

The focused producer test proves one `TableTitle` and one `Caption` are emitted through the generic validator, while a `TableHeader` decision is not activated. The deterministic replay preserved all heading, structure, decision, product, and host observables.

## Full suite

The current tree measured 842 tests: 840 passed, 2 failed, and 0 skipped. The only failures remain frozen C1 and N15 with unchanged fingerprints.

```ini
FULL_SUITE = 842/840/2/0
NEW_FAILURES = 0
CHANGED_FINGERPRINTS = 0
UNJOINED = 0
FROZEN_FAILURES = C1,N15
```

R5-4C2 is closed. R5-4C3 is the next checkpoint for deliberate `ListItem` evidence, `FigureTitle` distinction, and semantic relations such as `CaptionOf`.
