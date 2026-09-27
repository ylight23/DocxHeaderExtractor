# Semantic-function contract design freeze v1

Status: offline design freeze only. This does not create a V4 request, change production code, or make a provider call.

The replay study showed that a single membership authority is necessary to eliminate contradictory valid states, but that projecting legacy `semanticRole` values is insufficient. In particular, 87 contents false positives were already classified as `section` or `section-heading`; a projection cannot recover information the raw classification omitted.

The next membership experiment must therefore require exactly one closed `semanticFunction` per occurrence:

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

The model-visible output of the first membership arm is deliberately only:

```json
{
  "sourceParts": ["...existing coordinate tuple..."],
  "semanticFunction": "REGION_STRUCTURE"
}
```

It excludes `occurrenceRole`, `scope`, and `titleRelation`. This isolates the question whether the model distinguishes `REGION_STRUCTURE` from `NAVIGATION`; descriptive axes and hierarchy are deferred to a later arm.

## Normative function definitions

| Function | Definition and boundary |
| --- | --- |
| `DOCUMENT_IDENTITY` | Identifies the artifact itself as its semantic title identity, rather than merely describing it as metadata. |
| `REGION_STRUCTURE` | Names or opens a semantic region whose subsequent content belongs beneath it; it is not navigation, a caption, or an internal table part. |
| `NAVIGATION` | Points to, lists, or leads to content elsewhere; it does not open a region whose following content belongs beneath it. |
| `PAGE_FURNITURE` | Serves repeated or page-positioned presentation, such as a running header, footer, or page number. |
| `OBJECT_CAPTION` | Names or describes an embedded object whose scope is that object itself, not a broader following region. |
| `TABLE_STRUCTURE` | Is a row, column, label, or header structure internal to a table, not a surrounding region opener. |
| `FOOTNOTE_OR_SOURCE` | Is a footnote, citation, attribution, or source note supporting other content, not a region opener. |
| `BODY_INFORMATION` | Is ordinary content or a statement without a region-opening function. |
| `METADATA` | Describes provenance, status, date, audience, or other attributes without constituting the artifact's title identity. |

Thus the same words can be navigation in a table of contents and a structural region in body text; decisions are about occurrences, not strings.

`occurrenceRole`, `scope`, and `titleRelation` remain possible descriptive axes in the target architecture only. None can override `semanticFunction` or alter membership.

The executable freeze is [SemanticFunctionContractDesignFreezeTests.cs](../../tests/DocxHeaderExtractor.Tests/SemanticFunctionContractDesignFreezeTests.cs). It proves the closed vocabulary, fail-closed behavior, non-member functions, occurrence sensitivity, coordinate/binder invariance, and V2 immutability. Its committed artifact is `eval/a99-closed-loop/semantic-function-contract-design-v1/contract-freeze.v1.json`.

After review and explicit provider authorization, the frozen prospective arm is `V4_SEMANTIC_FUNCTION_SINGLE_AUTHORITY`: `qwen/qwen3.7-flash`, V3 facts, packing 120, existing `sourceParts` binder, SRC-089 + SRC-095 only, and Gold unopened until raw predictions persist. No such arm is implemented or authorized by this freeze.

The gate remains: review this artifact and obtain explicit provider authorization before creating any V4 experimental transport or making a provider call.
