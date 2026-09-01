# Final Architecture R11-R15

Status: IMPLEMENTED / VERIFICATION PENDING

Base revision: `1053cb4b61e243dc1e4c96eb82909e6110701eb4`

Implementation revision: containing-implementation-commit

This document records the intended final architecture. It does not claim that the later final
verification has passed.

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
