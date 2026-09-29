# Reasoning response contract V2

Each model heading proposal must contain `start`, `end`, `semanticRole`, `proposedLevel`, `proposedParentLocalId`, `confidence`, and `decisionEvidence`. `proposedParentLocalId` may be null; a non-null value is local response metadata and never a source identity.

`semanticRole` is required and must use the existing closed vocabulary: `DOCUMENT_TITLE`, `PART`, `CHAPTER`, `SECTION`, `SUBSECTION`, `ARTICLE`, `CLAUSE_HEADING`, `ANNEX_HEADING`, `LOCAL_INDEX_TITLE`, `AGENDA_NAVIGATION_HEADING`, `TOC_ENTRY`, `FRONT_MATTER`, `CONTENT_HEADING`, or `OTHER_STRUCTURAL_LABEL`.

Projection consumes this role through the existing role materialization: `DOCUMENT_TITLE` is retained; `CONTENT_HEADING` is retained; `AGENDA_NAVIGATION_HEADING` becomes `LocalSubheading` and is excluded. Missing or unrecognized roles fail closed before projection. Projection must not infer a role from heading text.

## Level and hierarchy contract

Gold `level` is the approved structural depth in the applicable Gold taxonomy. A model `proposedLevel` is comparable only when the response contract explicitly defines the same root/depth convention and allows the relevant parent relation to be expressed and bound. `proposedParentLocalId` is evaluated only after local-to-canonical parent resolution and cycle validation.

When either condition is absent, existence/span metrics remain evaluable but level and hierarchy metrics are `LEVEL_CONTRACT_NOT_MEASURED_FAITHFULLY` and `HIERARCHY_NOT_MEASURED_FAITHFULLY`; such differences are not classified as model-level errors.
