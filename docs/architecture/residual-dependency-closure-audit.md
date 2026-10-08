# S6-S10: residual dependency and vocabulary closure

Baseline: `bdcdb8c`. These checkpoints do not reopen structural authority design.
Gold, provider prompts/bodies, frozen artifacts and research H3 are outside scope.

## S6: factory input ownership

IInferenceTransportFactory, Infrastructure and Web factories now take only an optional
CancellationToken. PipelineOptions was unused beyond null-check/delegation; provider
selection remains constructor-owned. No replacement configuration DTO is introduced.
DOCX/PDF callers retain their own pipeline options and forward the same cancellation token.
PDF provider authorization, capability rejection and transport ownership are unchanged.

This is an intentional public C# signature change; callers/implementers must remove the
PipelineOptions argument. No legacy overload is retained. No wire contract is affected.
InferenceFactoryOwnershipTests checks both signatures and absence of the reverse dependency;
PdfProductionProviderPolicyTests retains provider-policy behavior coverage without network calls.

## S7: review source naming

Review.DocumentSourceSnapshot becomes ReviewDocumentSourceSnapshot and its reader becomes
ReviewDocumentSourceReader. The common source IR retains DocumentSourceSnapshot; it is not
renamed or conflated with the review Document/SourceIndexes envelope. Read/conversion/cleanup
behavior and constructors remain unchanged; Web review callers use the new names.
This is an intentional public C# type/API rename, not binary compatibility. Record property
names and serialization shape are unchanged. ReviewSourceOwnershipTests checks snapshot
shape, unique common IR naming and source/index parity against the existing OOXML reader.

## S8: retire the unused materialization envelope

StructuralMaterializationResult had one production consumer, no tests/serialization/config
consumer in the repository, and two zero-only counters. DOCX now carries structure and emitted
IDs as local variables. The audit-null branch still uses an empty validated graph and empty
emitted IDs; only the audit-present branch admits authority.Structure and the identical ID
fallback. Projection/product/sections/chunk construction retain their ordering and inputs.
HeadingStructureMaterialization (graph + projection sidecar) remains live and is not removed.

Removing the public wrapper is an intentional public API retirement; repository reachability
does not prove absence of external clients. This is not a wire-removal claim. The guard tests
the retired type and preserves the audit admission gate; existing output/replay parity tests
remain authoritative for live serialized behavior.

## S9: explicit dependencies and stale imports

Unused Pipeline imports outside the legitimate Routing/Review composition consumers are removed.
The format-neutral materializer no longer imports the DOCX source adapter. HeadingAuthorityGlobalUsings
is retired; actual consumers import the heading/protocol contracts explicitly. IDE0005 cleanup is
limited to DocumentProcessing, not user research or historical artifacts. A local read-only
Roslyn compilation audit (using SDK assemblies and existing project references, 0 compiler errors)
found 60 further CS8019 unnecessary imports after the first pass; they were removed. The repeat
audit reported 0 unnecessary imports. The audit tool stays ignored under .verify-build; it is
not a production/test dependency or a new runtime component.

The architecture guard prohibits global usings in current processing source and checks qualified
Pipeline namespace references as well as whole-symbol references to pipeline-owned orchestration
and option types. Routing, Pipeline and Review are the explicit composition exceptions; Source,
Projection, Materialization, Semantics, Authority and Inference are not exceptions. Build and wire/
replay regressions separately verify that import cleanup did not alter type resolution or payloads.

## S10: routing vocabulary

AuthorityPipelineExecutionResult becomes DocumentExtractionExecutionResult; AuthorityExtractionRequest
becomes DocumentExtractionRequest; ICanonicalSourceExtractor becomes IDocumentExtractionHandler;
DocxCanonicalSourceExtractor/PdfCanonicalSourceExtractor become DocxExtractionHandler/PdfExtractionHandler.
The two handlers have separate files; routing, lazy inference lifetime, provider policy and file-only
request construction are unchanged. Hosts/tests are migrated, no legacy facade remains. These are
intentional public C# source/binary API renames. Existing route identities and canonical technical
terms, JSON property names/order and type-independent payloads are not renamed.

ExtractionRoutingContractTests pins default/Web execution-envelope and request hashes captured
provider-free from actual pre-rename types at e411bd8. These are new regression fixtures, not
historical artifact rebaselines. Boundary, PDF lifecycle and byte/replay parity regressions are
retained. The production retired-symbol guard includes the old names; historical docs/evidence
are preserved rather than rewritten to pretend they used the new vocabulary.
