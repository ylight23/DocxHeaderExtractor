# Final Architecture R11-R15

Status: VERIFIED / PUBLICATION PENDING

Base revision: `1053cb4b61e243dc1e4c96eb82909e6110701eb4`

Implementation revision: `de5d33103b1fd44282445c0be4fa46d961dce66c`

This document records the implemented final architecture and its verification result. Publication
and the final no-ff merge remain pending.

## Final Verification

The execution revision was `de5d33103b1fd44282445c0be4fa46d961dce66c`. Focused authority, replay,
consumer, and host checks measured `88/88`; the deterministic `028/056/091` replay joined `3/3`.
The Release build completed with zero errors and `git diff --check` was clean.

The canonical suite measured `910 total / 908 passed / 2 failed / 0 skipped`. The only failures were
the frozen `C1` and `N15` lineage, with no new FQNs, changed fingerprints, or unjoined failures.
The C1 fingerprint is `383ec4a94b319a5969fc69e042ae286ef96fefd2045ecf098016c1356603caad`; the N15
fingerprint is `197b86a209a2236047e386d256589434f927ac41d9467cceb48835034a7e7ac6`.

Provider calls were `0`. Accuracy-99 benchmarking was not run. This verification does not itself
close the architecture; the publication commit and final merge must still preserve the execution
parent and publication tree.

## Canonical Flow

```text
SOURCE
  |
Parser
  |
SourceDocument / SourceFacts
  |
StructuralCandidate
  |
StructuralProposal
  |
StructuralProposalValidator
  |
ValidatedStructure
  |--------------------> HeadingOutlineProjection
  |                              |
  |                          HeadingRecord
  |                        COMPATIBILITY ONLY
  |
  |--> StructuralSectionProjection --> SectionChunkProjection --> Chunks
  |                                      |
  |                                      +--> Retrieval/Search
  |                                      +--> IE Context
  |                                                |
  |                                           Schema discovery
  |                                                |
  |                                      SchemaSelectionProposal
  |                                           UNTRUSTED
  |                                                |
  |                                      SchemaSelectionValidator
  |                                                |
  |                                      ValidatedSchemaSelection
  |                                                |
  |                                      Registered schema packs
  |                                                |
  |                                      Fact proposal production
  |                                                |
  |                                           FactProposal
  |                                           UNTRUSTED
  |                                                |
  |                                      FactProposalValidator
  |                                                |
  |                                      Semantic Authority
  |                                                |
  |                                           ValidatedFact
  |                                                |
  |                                      DocumentProcessingResult
  |                                                |
  |                                      CLI / Web / MCP / Agent
```

## R11 Boundary

The obsolete `DocumentDomainPolicy` authority-shaped members and the obsolete
`ValidatedStructure.Headings` view are removed. `HeadingRecord` remains only at the explicit
compatibility projection, document-outline API, formatting/output, repair, diagnostic, and
historical evaluation boundaries. It is not a generic structural, section/chunk, retrieval,
search, IE, fact, schema, or product-fact authority.

Historical PDF legacy projection is isolated in `DocxHeaderExtractor.Eval`, which references Core;
Core does not reference that project. `SlimXmlChunker` remains model-input chunking, and
`LegacyDocConverter` remains `.doc` to `.docx` support.

## R12 Enforcement

`ArchitectureBoundaryGuards` provides a small dependency-free rule catalog and fail-closed helper
methods for proposal validation, parser-owned source catalogs, and direct authority materialization.
The later verification suite can inspect these declarations and source boundaries without a
third-party architecture framework.

The protected directions are:

- source/provider adapters to source and proposal contracts;
- proposals through their validators to authority contracts;
- projections from validated authority;
- application services over authority and projections;
- hosts over the application service;
- Eval over Core.

Direct model/provider materialization of structural or fact authority, source-catalog
reconstruction, and compatibility-to-generic reverse flow are forbidden.

## R13 Schema Discovery

`SchemaDiscoveryContext` contains document identity, bounded structural types, section count,
bounded source excerpts, and model-safe descriptors of registered schema packs. It never exposes
semantic-authority instances. `SchemaSelectionProposal` is untrusted. `SchemaSelectionValidator`
deduplicates and orders keys, rejects unknown packs, handles empty proposals as `NO_SCHEMA_MATCH`,
and never falls back to an arbitrary schema.

`DocumentFactExtractionRuntime` remains the multi-schema execution owner. One
`DocumentExtractionResult` and one IE context projection are reused across selected packs.

## R14 Unified Application Surface

`DocumentProcessingService` is the common application surface for structure-only, explicit-schema,
and auto-schema-discovery requests. Its request accepts only an input path, mode, and optional
schema keys. Its result exposes the generic extraction, validated structure, source-backed
sections/chunks, validated facts, validated schema selection, schema results, audit, and the
explicit compatibility outline projection.

Facts exposed by the primary result are `ValidatedFact` values only. Proposals, rejections, and
producer failures remain audit data. Existing heading host APIs remain compatibility surfaces and
must continue to be projections of canonical authority.

## Dependency Map

Allowed:

```text
source/provider adapters -> source/proposal contracts
validators               -> authority contracts
projections              -> validated authority
application              -> authority + projections
hosts                    -> application
Eval                     -> Core
```

Forbidden:

```text
Core                     -> Eval
authority                -> host
generic authority        -> HeadingRecord
validator                -> provider concrete implementation
source authority         -> structural reconstruction
fact authority           -> search ranking score
```

## Freeze Policy

This architecture becomes closable only after the separate final verification run. Later changes
require either a concrete product requirement or Accuracy-99 evidence that a frozen boundary blocks
quality. The freeze is a governance boundary, not a claim that the implementation is perfect
forever.
