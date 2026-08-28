# Clean Architecture and Authority Routing Review

**Revision:** `521ab902d89d4f3c8c7a68cb528b84ca6ebccfb2`  
**Mode:** audit-only; no provider calls and no production changes.

## Verdict

The repository is a modular monolith with useful namespace seams, but it is not
currently compliant with strict Clean Architecture dependency direction.
The verdict is **FAIL** for strict compliance. This is an architectural finding,
not a recommendation for a big-bang rewrite. `IMMEDIATE_REFACTOR_JUSTIFIED` is
`NO`; the safe next move is incremental boundary extraction protected by the
existing regression/provenance contracts.

## Actual call graph

The normal host path is:

```text
CLI / Web / MCP / AgentHarness
        -> PipelineDocumentExtractionTool
        -> AuthorityExtractionPipeline
        -> LegacyDocConverter
        -> DocxSlimExtractor
        -> DocumentModeClassifier / PDF discovery
        -> DocxAuthorityPipeline or PdfLayoutEvidenceOutline
        -> proposal/span resolution
        -> validator -> canonical grounding -> hierarchy/output policy
```

The CLI still has a direct compatibility/evaluation construction of
`HeaderExtractionPipeline`. Tests and repair/evaluation commands also reach it.
That makes `HeaderExtractionPipeline` legacy-reachable, not dead. It contains
source preparation, candidate and ranking-related logic, route selection, model
passes, critic/rolling behavior, hierarchy, and output concerns.

Provider implementations are also constructed from orchestration/host code
(`OpenRouter`, `LMStudio`, `SGLang`, and `LlamaSharp`). `IHeaderClassifier` is a
useful port, but composition is not consistently kept at a host/infrastructure
boundary.

## Authority routing

`AuthorityExtractionPipeline` owns the normal product route. However,
`PdfFirstValidatedFallback` is a boolean application policy embedded in
`HeaderExtractionPipeline`. Its predicate combines an application option with
source availability and then diverts to `RunPdfFirstAuthorityPipelineAsync`.

The policy consequence is large: it changes source authority and can bypass the
legacy candidate/split/critic/rolling path. The C2-P evidence traced 11 failures
to this route diversion. PDF discovery itself is a source capability concern;
whether PDF-first authority should supersede the other route is an application
policy concern. They are currently mixed.

The correct future owner is an explicit application `RoutePolicy`, consuming
capability facts and returning a route decision. It should not be implemented
by a PDF adapter, and it should not remain an implicit boolean in the legacy
orchestrator.

## `DocxSlimExtractor` and source facts

`DocxSlimExtractor` is production reachable but architecturally legacy. It
currently combines:

- OpenXML source I/O and paragraph walking;
- text normalization, source segments, and line-break mapping;
- style, numbering, font, alignment, table, and source-span extraction;
- style trust, TOC/table interpretation, and document-mode derivation;
- heading heuristic classification, candidate scoring, and level guessing;
- cover/inline/body demotion and other post-processing;
- diagnostic state such as corruption, table role, and mode reports.

Therefore `SLIM_SINGLE_RESPONSIBILITY = FAIL`. The `SlimParagraph` model also
mixes normalized source facts (`Text`, `StyleId`, `SourceSegments`) with derived
facts (`BodyFontSizePt`, TOC/table relationships), policy results (`Role`,
`IsCandidate`, `Score`, `GuessedLevel`), and diagnostic/interpreted state.
`SlimDocument` directly imports `OpenXmlLayer` types. The source boundary is
therefore **PARTIAL**, not absent.

The authority contract should make the following distinction explicit:

```text
immutable SourceFacts
    -> application candidate/policy decisions
    -> ModelProposal
    -> canonical mapping and deterministic validation
    -> ValidatedStructure / hierarchy
    -> OutputDecision
```

The model must never create a source fact, and hierarchy/output must not
resurrect rejected candidates.

## Dependency findings

The main violations are:

- `Core.Models` imports `OpenXmlLayer` types;
- the Core project owns OpenXML, PDF, HTTP, and LLamaSharp infrastructure;
- application orchestration constructs concrete provider implementations;
- `DocxSlimExtractor` embeds source interpretation and heading policy;
- route policy is embedded in a legacy orchestrator;
- Eval and Repair are colocated with runtime pipeline code and reach legacy paths;
- hosts partly duplicate provider composition.

`Core.Output` is comparatively clean as an output adapter, but it is still
invoked by pipelines that mix the other concerns. No separate Domain,
Application, Ports, or Infrastructure projects currently enforce the desired
dependency direction.

## Migration phases

1. Freeze current behavior and keep the regression/provenance harness authoritative.
2. Define immutable `SourceFacts` and occurrence identity contracts.
3. Wrap `DocxSlimExtractor` behind `IDocumentSource` without changing behavior.
4. Separate raw extraction from derived features.
5. Move heuristic and policy logic out of the source adapter.
6. Extract `RoutePolicy` from `AuthorityExtractionPipeline` and
   `PdfFirstValidatedFallback`.
7. Move OpenXML, PDF, and inference implementations behind ports and composition roots.
8. Deprecate `HeaderExtractionPipeline` only after its caller count reaches zero.

This sequencing preserves the current authority chain and makes each move
measurable. The audit does not justify immediate refactoring, provider calls, or
production behavior changes.

## Machine-readable record

The full node classification, route branches, field classification, dependency
map, legacy/target components, and migration phases are in
`eval/architecture/clean-architecture-review.v1.json`.

```text
PROVIDER_CALLS = 0
PRODUCTION_CODE_CHANGED = false
TEST_EXPECTATIONS_CHANGED = false
```
