# Current architecture contract

Status: `ACTIVE — SOURCE CLEANUP AND CORE OWNERSHIP CLOSURE`

Source-cleanup baseline: `main@a2fece331b05c9a59e3e998207be2b2596c3e745`.
The subsequent Core ownership changes are described below; this is a current contract, not a frozen qualification artifact.

The source-tree hygiene changes are integrated into `main`. Historical Phase-1/Phase-2 branch
reports remain historical evidence, not the current ownership or publication status.

## Current host routes

```text
Web / CLI / MCP
  -> DocumentAgentHarness
  -> PipelineDocumentExtractionTool
  -> DocxExtractionPipeline / PdfExtractionPipeline
  -> DocxHeadingPipeline / PdfHeadingPipeline
  -> HeadingPipelineResult (runtime envelope; Structure = ValidatedStructure authority)
  -> PromptDrivenProjection
  -> GenericTaskResult
```

All three hosts currently use the same `DocumentAgentHarness`; Web, CLI, and MCP consume the
validated `TaskResult.Value` projection. The compatibility `DocumentAgentRunResult.Outline` is
retained for existing library/test callers and is not a second authority route.

## Production ownership after N15

`DocumentProcessing/Pipeline/` is orchestration-only. Its exact, recursively checked allowlist is
`DocxExtractionPipeline.cs`, `DocxHeadingPipeline.cs`, `PdfExtractionPipeline.cs`,
`PdfHeadingPipeline.cs`, `PdfLaneExecution.cs`, `PdfStageCheckpoint.cs`,
`ProductionCheckpointScope.cs`, `PipelineOptions.cs`, and `PdfSemanticLaneOptions.cs`. The last file
owns only the semantic lane's execution deadline, not source facts or heading decisions. Helpers, validators and parsers may not
be added there.

PDF parsing/adapters belong to `Source/Pdf`; DOCX adapters to `Source/Docx`; shared marker parsing
to `Source/Common`. `DocumentSourceCatalogBuilder` belongs to the `Source` root because it builds
both generic and PDF-facing catalogs. Canonical semantics belong to `Semantics/Canonical`, heading
authority/decision contracts to `Semantics/HeadingAuthority`, and hierarchy/binding/materialization
to `Materialization`. Output projections/policies belong to `Projection`, audit/result contracts
to `Authority`, neutral transport/wire contracts to `Inference`, DOCX product writeback to
`OpenXmlLayer`, and binary revision discovery to `Provenance`.

The C# audit vocabulary is `PipelineExecutionAudit`, `LaneExecutionAudit`, `SourceBlockAudit`,
`SourceBlockDecisionAudit`, and `ExecutionAuditBoundary`. `PipelineExecutionAudit.PipelineId`
retains the JSON name `route`; serialized/API names and frozen experiment artifacts are unchanged.
`HeadingAuthorityArchitectureTests` guards the exact layout, retired symbols, and provider-neutral
DocumentProcessing vocabulary. This cleanup changes neither prompts nor semantic authority.

Source ownership is namespace-aligned: shared `SourceMarkerFact` lives in `Source/Common`, PDF
facts/context construction in `Source/Pdf`, and `HeadingHierarchyFactAudit` in `Authority`.
`Source/**` may not depend on the `Pipeline` namespace; orchestration depends on source, never
the reverse. The architecture guard checks both that dependency direction and source namespaces.

PDF appearance grouping uses `PdfStyleKey.StyleOf` with the unchanged half-point default bucket.
Readable text reconstruction belongs to `PdfTextUtilities`. The unused style-profile/statistics
records and title/group collections are retired; these source utilities do not classify headings.

`PdfLayoutBlockGrouper`/`PdfLayoutBlock` describe geometric layout grouping, not semantic decisions.
`PdfSourceContextBuilder`/`PdfSourceContext` describe parser context supplied to inference, and
`PdfTextUtilities.DisplayReadable` is display reconstruction, not heading detection.
`Authority/PdfHierarchyFactsInventory` inventories already-validated headings for observability;
it cannot create headings or call a provider. Review, writeback, output projection, audit, and
qualification/history subsystems retain their distinct live responsibilities.

## Trust and authority boundaries

- Model output is a proposal, never authority.
- Input documents and tool output are untrusted until deterministic validation.
- Parser-owned source coordinates are the only materialization source.
- `ValidatedStructure` is structural authority.
- S4 retains `ValidatedStructuralElement.ParentId` and `ValidatedStructure.OutlineElements`
  as public/wire compatibility views, not independent authority. Graph admission derives
  ParentId only from explicit validated relations. Production outline/grounding/section
  projections read Elements directly; section hierarchy reads Relations.
- `HeadingPipelineResult` is the DOCX/PDF runtime envelope, not a second authority. It carries
  Structure plus projection context, source catalog, audit, emitted IDs and reason. It remains
  in the existing DocumentProcessing/Authority audit/result-contract owner, outside orchestration
  source files and outside Core. ProjectionContext stays JsonIgnore. S3 renames the public C#
  envelope API; it does not change default/Web JSON field names or graph semantics.
- Application plan compilation creates stable `PlanId` values from task/resource identity and
  capability metadata; explicit idempotency keys override the resource identity when supplied.
- Capability metadata is registered and resolved by the provider-independent Application catalog;
  host tool selection cannot silently overwrite duplicate capability names.
- Policy/guardrails authorize transfer and mutation; a model cannot grant permission. A remote
  capability with an exhausted provider-call budget is denied before execution.
- Retry is typed and policy-driven: only explicitly transient `ProviderCallException` failures may
  be retried, and cancellation/untyped failures remain fail-closed.
- Projection and formatting cannot create authority.
- Accuracy-99 gold, human adjudication, and provider-quality tuning are outside Phase 1.

## Project dependency baseline

| Project | Current role | Current references |
|---|---|---|
| `Core` | source/structure contracts, authority value objects, validators/binders and pure deterministic domain algorithms | no project or parser/render/provider package references |
| `Application` | provider-independent intent, plan compiler, policy, projection, task/resource, capability, semantic-registry and runtime contracts | `Core` |
| `DocumentProcessing` | DOCX/PDF source adapters, authority pipeline implementations, bounded review/repair compatibility | `Application`, `Core`; owns OpenXML/PdfPig/PDFtoImage |
| `AgentHarness` | host-neutral orchestration, registry, guardrails, validators, task envelope | `Application`, `DocumentProcessing`, `Core` |
| `Web` | HTTP host and UI composition root | `AgentHarness`, `Core`, `DocumentProcessing`, `Infrastructure` |
| `Cli` | command host and explicit evaluation/repair commands | `AgentHarness`, `Core`, `DocumentProcessing`, `Infrastructure`, explicit `Eval` plugin bridge |
| `Mcp` | MCP host and async job adapter | `AgentHarness`, `Core`, `DocumentProcessing`, `Infrastructure` |
| `Eval` | evaluation/replay-only adapters | `Core`, `DocumentProcessing` |
| `Infrastructure` | provider implementations, prompt/cache adapters, and source infrastructure ports | `Application`, `Core`, `DocumentProcessing` |

`Application`, `DocumentProcessing`, and `Infrastructure` project boundaries now exist. Package
versions are centrally declared in `Directory.Packages.props` without changing the pinned versions.
DocumentProcessing now owns source/parser/rendering and authority pipeline implementations; Core
contains package-free contracts/value objects/validators and pure deterministic domain algorithms. Infrastructure now contains provider
contracts, heading-provider implementations, prompt/cache
adapters, fact-provider adapters, LLamaSharp/SGLang VLM adapters, and an allowlisted file resource
resolver. Core exposes no concrete provider or inference orchestration. Web/MCP wire normal and review paths; CLI evaluation commands use an explicit Eval project boundary and the CLI normal path does not activate Eval. The hosts wire the resolver
and trusted semantic registry into the common harness; the MCP subprocess worker composes the same
source boundary plus runtime state adapters. The normal extraction route never activates the Eval
project. `EvaluationProjectionBridge` was the adapter that carried an outline across that boundary;
it was removed once nothing called it, and no evaluation command had been reaching it.

## Persisted artifacts and ownership

- correction memory: Web writes through Application `IHumanFeedbackStore` and the Infrastructure
  `CorrectionMemoryFeedbackStore`; the append-only implementation is owned by Infrastructure at
  `src/DocxHeaderExtractor.Infrastructure/Learning/CorrectionMemory.cs` and remains compatible
  with existing JSONL files
- skill policy: versioned `skills/heading-extraction/SKILL.md`, parsed into the provider-independent
  Application `SkillCatalog` before harness creation; framework-specific adapter remains deferred
  without adding a runtime dependency in Phase 1
- semantic definitions: provider-independent concept/schema registry in Application; trusted
  generic defaults are composed by Web/MCP, while external configuration registration and feature
  consumers remain open
- run lifecycle: versioned persistence/telemetry ports and secret redaction contract in Application;
  Web/MCP compose `JsonFileTaskRunStore` and `JsonLinesTaskTelemetrySink` from the configurable
  `DHX_RUNTIME_STATE_DIR` boundary, and the common harness records Running and terminal states;
  persistence failures remain non-authoritative and provider payloads are not part of these artifacts
- MCP job state: temporary `McpJobStore` snapshots owned by the MCP host
- generated/writeback files: request-owned temp directories and explicit writeback adapters
- Accuracy-99 review/gold: evaluation-owned and excluded from this cutover

The extension seam is executable-tested in
`tests/DocxHeaderExtractor.Tests/AutoHarnessExtensionProofTests.cs`: a custom capability, semantic
definition, allowlisted source, compiled task plan, and provider-neutral transport can compose
without adding a second authority route or making a provider call.

## Core semantic and inference ownership

`Core/Models` owns DTOs, value objects and neutral contract descriptors, not domain services.
Value-object invariants and wire schema/encoding helpers remain valid contract behavior; this is
not a blanket ban on methods in Models. `SemanticCoordinateContract` remains a descriptor that
composes delegates from the separately owned parsing, validation and binding services.

Materialized production structure is heading-only: `StructuralElementType.Heading = 2`,
`ProposedRole.HeadingTopic = 0`, and `StructuralRelationType.ParentChild = 0`. These numerical
identities and the live JSON field/enum names are retained. Title/subtitle/list/caption/table/figure
materialization and non-parent relations are retired, not silently remapped to headings. This
intentionally narrows the public contract; downstream consumers of retired members must migrate.
It does not narrow the model's upstream semantic-role vocabulary or prevent title extraction.

The former `StructuralContracts.cs` monolith is split into `SourceSelectionContracts.cs`
(source identity/spans), `HeadingStructuralContracts.cs` (heading proposals/results),
`HeadingHierarchyContracts.cs` (parent relation DTOs), and `ValidatedStructure.cs` (passive graph result).
Graph construction/validation belongs to `Core/Semantics/Validation/ValidatedStructureFactory`;
relations must be explicit and ParentId is derived only from the admitted relation graph.
Projection metadata and outline stable-ID compatibility belong to the separate
`DocumentProcessing/Projection/HeadingProjectionContext`, keyed by element/source identity and
carried by the runtime envelope, not the Core authority graph. Heading/product output compatibility
is tested separately from the intentional raw structural DTO/API retirement; see
`structural-projection-context-audit.md`.
Exact source/multipart spans, hierarchy, provenance, source catalogs, sections and body-backed chunks
remain live. Generic synthetic graph tests are replaced with heading/output and retirement guards.
Neutral inference input/interface/context and occurrence contracts live in `Core/Models/Inference`.
The neutral `Core.V5` namespaces are retained for source compatibility; the retired
`Models/QualifiedInference` folder contains no current files.

`Core/Semantics/Canonical` owns the pure deterministic `CanonicalSemanticPipeline`,
`SemanticConflictNormalizer`, and `CanonicalSemanticGlobalConflictDetector`. These operate on
supplied proposals/source aliases, perform no IO or inference, and cannot select a semantic winner.
Their result/conflict DTOs remain in Core Models. Domain processing is not provider orchestration.

`Core/Semantics/Binding`, `Validation`, `Parsing` and `Identity` own exact/source-part binding,
coordinate-binding strategies, proposal and relation validators, proposal parsing/decoding and
semantic identity resolution. Their contract/result DTOs keep the `Core.Models` namespace and
serialized shapes preserved by that ownership move. Later DTO retirement/API changes are
documented separately in the cleanup contract compatibility and S1/S2 audits. File-backed source hashing belongs to
`DocumentProcessing/Provenance`, not the pure semantic services.

`DocumentProcessing/Semantics/Canonical` owns `CanonicalSemanticTextProductionEntryPoint`,
`CanonicalSemanticRequestComposer`, and `SemanticContextPacker`. The entrypoint invokes the
Core `ICanonicalSemanticTextModel` boundary; its input contracts remain Core-owned, so Core has
no reverse reference to DocumentProcessing. DOCX uses this text path; PDF uses F1 → G2A → H2-C V2.

`Infrastructure/AI/QualifiedInference` owns the frozen provider carrier and its provider envelope/body
types. Moving them does not change provider payload bytes, prompt wording, token limits, or parser
semantics. Assembly/namespace relocations require consumers to rebuild/update imports; this cleanup
does not claim binary compatibility for relocated public implementation types.

`IFrozenInferenceTransport` only executes supplied frozen bytes; it does not select a model-specific
composer. The authorized `IPdfProductionAuthorizedInferenceTransport` owns the qualified PDF
composer, and the production heading pipeline requires this capability before starting calls.
Generic OpenRouter and qualification transports can target other models without implicitly
advertising the qualified PDF composer. Qualification supplies its composer explicitly.

The mixed `CanonicalSemanticVnextRuntime.cs` is split into context contracts, runtime packing,
contract validation and hard-binding validation. No validation/binding logic is deleted.
`CoreOwnershipArchitectureTests` guards this split and the absence of concrete providers/reverse
project dependencies in Core; frozen wire and replay parity are tested separately.

The retired extraction/slim/converter classes are not current production routes. Input conversion,
where requested, happens at the source preparation boundary before normalized OOXML extraction.

The current mechanical checks are the architecture/layout tests in
`tests/DocxHeaderExtractor.Tests` and `.github/workflows/deterministic.yml`, with
`scripts/Invoke-A99TestTier.ps1` selecting deterministic tiers. Historical architecture audit scripts
are not current repository entrypoints. Historical publication gates must not be presented as
evidence for a newer commit. Verification results belong to the exact tested revision and the
ownership closure audit, not to stale branch status.

The latest local Core ownership verification is recorded in
[`core-ownership-closure-audit.md`](core-ownership-closure-audit.md), including the exact tested
revision and clean tracked checkout caveat. The subsequent C1/C2 ownership closure is recorded in
[`core-domain-service-ownership-audit.md`](core-domain-service-ownership-audit.md).

## Phase control

`HUMAN_ADJUDICATION = NOT_STARTED_IN_PHASE1`
`ACCURACY_TUNING = NOT_STARTED_IN_PHASE1`
`PROVIDER_QUALITY_TUNING = NOT_STARTED_IN_PHASE1`
`MULTI_PROMPT_FUNCTIONAL_VERIFICATION = NOT_STARTED_IN_PHASE1`
