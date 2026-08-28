# ARCH-4B SourceFacts Compatibility Layer

## Status

ARCH-4B is complete as a compatibility layer. The immutable source contract is introduced and a one-way `SlimDocument` to `SourceDocument` adapter is available. Runtime caller cutover is intentionally deferred.

## Contract

The source-only namespace contains `SourceDocument`, `SourceParagraph`, `SourceTextRunSpan`, `SourceSegment`, `SourceStyleFacts`, `SourceNumberingFacts`, and `SourceLayoutFacts`. Properties are init-only and collections are exposed as read-only snapshots.

The contract contains source and normalized source facts: document identity, paragraph identity, text, formatting spans, line-break offsets, source segments, style facts, numbering facts, and layout facts. It does not contain candidate status, score, role, guessed level, style-trust decisions, demotion results, route decisions, model proposals, or validated hierarchy facts.

`SourceDocument` has no OpenXML SDK dependency and the source model namespace has no dependency on heading heuristics, ranking, validation, route policy, fallback policy, or provider clients.

## Adapter

`SlimSourceFactsAdapter.Adapt` performs a one-way projection:

```text
SlimDocument -> SourceDocument
```

It preserves `SlimDocument.SourcePath` as document identity, paragraph `StableId` and ordinal, duplicate text paragraphs, text spans, and `SlimSourceSegment` provenance. A deterministic `p:{Index}` identity is used only when a Slim paragraph has no stable id.

The adapter deliberately does not copy Slim derived facts, policy results, validated facts, or candidate/ranking state. No ambiguous field is silently promoted into the source contract.

## Runtime Boundary

`AuthorityExtractionPipeline` still consumes `SlimDocument`, and `DocxSlimExtractor` still returns `SlimDocument`. No normal production caller was cut over. Slim heuristics, candidate behavior, and routing therefore remain unchanged.

## Verification

- ARCH-4B focused tests: PASS, 3/3.
- F regression harness contract: PASS, 2/2.
- Release build: PASS, `DocxHeaderExtractor.sln`, 0 errors.
- Provider calls: 0.
- Production behavior changed: false.

The Release build reports 28 existing warnings; no new build errors were introduced.

Machine-readable results are in `eval/architecture/source-facts-compatibility.v1.json`.
