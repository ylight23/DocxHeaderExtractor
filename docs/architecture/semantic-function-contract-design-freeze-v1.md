# Semantic-function contract design freeze v1

Status: offline design freeze only. This does not create a V4 request, change production code, or make a provider call.

The replay study showed that a single membership authority is necessary to eliminate contradictory valid states, but that projecting legacy `semanticRole` values is insufficient. In particular, 87 contents false positives were already classified as `section` or `section-heading`; a projection cannot recover information the raw classification omitted.

The next experimental contract must therefore require exactly one closed `semanticFunction` per occurrence:

- `DOCUMENT_IDENTITY`
- `REGION_STRUCTURE`
- `NAVIGATION`
- `PAGE_FURNITURE`
- `OBJECT_CAPTION`
- `TABLE_STRUCTURE`
- `FOOTNOTE_OR_SOURCE`
- `BODY_INFORMATION`
- `METADATA`

Membership is derived solely from that function. Only `DOCUMENT_IDENTITY` and `REGION_STRUCTURE` are members. An unknown function fails closed. `isHeading`, generic `heading`, and free-text role-based membership are not part of the proposed shape.

`REGION_STRUCTURE` names or opens a semantic region whose subsequent content belongs beneath it. `NAVIGATION` points to, lists, or leads to content elsewhere. Thus the same words can be navigation in a table of contents and a structural region in body text; decisions are about occurrences, not strings.

`occurrenceRole`, `scope`, and `titleRelation` may remain optional descriptive axes for future hierarchy work. None can override `semanticFunction` or alter membership.

The executable freeze is [SemanticFunctionContractDesignFreezeTests.cs](../../tests/DocxHeaderExtractor.Tests/SemanticFunctionContractDesignFreezeTests.cs). It proves the closed vocabulary, fail-closed behavior, non-member functions, occurrence sensitivity, coordinate/binder invariance, and V2 immutability. Its committed artifact is `eval/a99-closed-loop/semantic-function-contract-design-v1/contract-freeze.v1.json`.

The gate remains: review this artifact before creating any V4 experimental transport or making a provider call.
