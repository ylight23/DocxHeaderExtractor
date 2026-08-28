# Source Facts Boundary Contract

Status: `PARTIAL` boundary, audit-only. This task defines the contract and migration map; it
does not refactor `DocxSlimExtractor` or change production behavior.

## Field Inventory

The inventory covers 47 properties in `SlimParagraph` and `SlimDocument`. The machine-readable
record includes each field's writer, readers, reachability, mutation state, current meaning, and
target owner.

| Class | Count |
| --- | ---: |
| `SOURCE_FACT` | 21 |
| `NORMALIZED_SOURCE_FACT` | 7 |
| `DERIVED_FACT` | 10 |
| `POLICY_RESULT` | 4 |
| `PROPOSAL` | 0 |
| `VALIDATED_FACT` | 3 |
| `DIAGNOSTIC_ONLY` | 1 |
| `AMBIGUOUS` | 1 |
| **Total** | **47** |

The most important boundary findings are:

- `StableId`, OOXML style/numbering/layout properties, and source segments are source-backed.
- `Text` and text spans are normalized source facts and remain recoverable through segments.
- `BodyFontSizePt`, `TableRole`, TOC/table adjacency, and numbering-style mappings are derived.
- `Role`, `GuessedLevel`, and `Score` are policy results, not source facts.
- `VerifiedHeadingEnd`, `VerifiedBodyStart`, and `VerifiedBoundarySource` are validated boundary
  facts and must not be treated as initial parser facts.
- `SlimDocument.Paragraphs` is `AMBIGUOUS` as a boundary field because its objects contain source,
  derived, and policy state together.

## Identity Contract

Authority identity is based on document identity, stable paragraph/source identity, and source
segment/range. `candidateId`, rank, and model-generated IDs are diagnostic linkage only. The
authority path preserves identity through:

`Source -> Candidate -> Proposal -> Validation -> Hierarchy -> Output`

The existing immutable `SourceFacts` / `SourceAnchor` / `MarkerFacts` contracts already express
the intended direction. `ModelProposal` carries a `SourceId` and proposal fields but no raw text or
source authority. `ValidatedHeading` is the only heading contract downstream output should consume.

## Responsibility Finding

`DocxSlimExtractor` currently spans `OPENXML_READ`, `SOURCE_NORMALIZATION`, `SOURCE_FACT_BUILD`,
`DERIVED_FEATURE`, `POLICY`, `POST_PROCESSING`, and `DIAGNOSTIC`. `ParagraphWalker`,
`StyleResolver`, and `NumberingResolver` provide useful extraction seams, but the facade still
coordinates style trust, table/TOC detection, heading heuristics, demotion, and diagnostics.

Therefore:

`SLIM_PRODUCTION_REACHABLE = true`

`SLIM_ARCHITECTURAL_LEGACY = true`

`SLIM_MIXES_SOURCE_AND_POLICY = true`

This is a boundary finding, not a runtime-legacy classification and not a reason to rewrite Slim
immediately.

## Decision

`SOURCE_FACT_BOUNDARY = PARTIAL`

`SOURCE_FACT_IMMUTABILITY = PARTIAL`

`IMMEDIATE_SLIM_REWRITE_JUSTIFIED = NO`

The safe path is incremental: introduce immutable source contracts, adapt the existing Slim output,
separate derived features, then move candidate policy and finally migrate the authority pipeline.
Every phase requires unchanged production behavior, the regression harness, and a rollback path.
Slim should only be deprecated after its caller count reaches zero.

Full field-level evidence, target ownership, responsibility blocks, and migration phases are in
`eval/architecture/source-facts-boundary.v1.json`.

`PROVIDER_CALLS = 0`

`PRODUCTION_CODE_CHANGED = false`

`PRODUCTION_BEHAVIOR_CHANGED = false`
