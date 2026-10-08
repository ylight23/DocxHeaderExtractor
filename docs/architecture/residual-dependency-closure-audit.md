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
