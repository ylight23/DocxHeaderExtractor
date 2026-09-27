# Current semantic reachability audit — 2026-09-27

## Scope and checkpoint

This is a source-level reachability audit of current `HEAD` (`6a055022742cbe974bde04ddef58da9742215a64`, branch `refactor/a99-semantic-control-plane-vnext`). It deliberately did not invoke a provider, set `A99_LLM_PILOT_RUN`, modify an experiment artifact, or alter an extraction decision.

- `git status --short` before work: four untracked artifacts only: `docs/accuracy/accuracy99-provider-completion-integrity-v1.md`, `docs/accuracy/reasoning-response-contract-v2.md`, and the two `eval/a99-closed-loop/structured-v2-target-baseline-v1/DOC-0252/...` artifacts. They were not read, staged, changed, renamed, or regenerated.
- Release build before edits: succeeded, 0 warnings / 0 errors.
- Test discovery without execution: 1,609 tests.

The audit follows executable composition roots and direct source references. A symbol is not considered dead merely because it has an old name. `COMPATIBILITY_ONLY` means a public API, CLI/UI option, provider interface, or output/replay format still exposes it even if the canonical lane does not consume it.

## Current canonical roots and paths

Normal host path (Web, CLI, MCP) is `PipelineDocumentExtractionTool` in AgentHarness. It constructs one `CanonicalExtractionDispatcher` with the DOCX and PDF extractors. No host constructs a deterministic outline strategy after dispatch.

```text
Web / CLI / MCP
  -> PipelineDocumentExtractionTool
  -> CanonicalExtractionDispatcher
     -> DocxCanonicalSourceExtractor
        -> AuthorityExtractionPipeline
        -> OpenXmlDocumentSource / DocxPolicyStateBuilder / DocxAuthorityPipeline
        -> CanonicalSemanticDocxAuthorityAdapter
     -> PdfCanonicalSourceExtractor
        -> PdfCanonicalExtraction
        -> CanonicalSemanticPdfAuthorityAdapter
  -> CanonicalSemanticEngine.HeaderClassifierCanonicalTextModel (only when a transport is supplied)
  -> CanonicalSemanticProductionEntryPoint
  -> CanonicalSemanticPipeline / exact source-part binding / hard binding validation
  -> CanonicalSemanticIdentityResolver + ModelRelationHierarchyResolver
  -> CanonicalSemanticPlacementCoordinator (only unresolved, only with supplied transport)
  -> CanonicalStructureMaterializer
  -> CanonicalProjectionBoundary / HeadingOutlineProjection / product projection
```

The source names in the request that do not exist as current types (`CanonicalSemanticExactBinder`, `CanonicalSemanticHardBindingValidator`, `PdfCandidateRanking`, `PdfProductionOccurrenceResolver`, `PdfSemanticClusterContracts`, `LlmBoundaryCutter`, `StructuralHierarchyResolver`, and `TableOfContentsAnchor`) have zero source hits on this HEAD. Their present responsibilities live under `CanonicalSemanticPipeline`, `SemanticSourcePartBinder`, `SemanticSourcePartsContractV2`, and `ModelRelationHierarchyResolver`; no identically named superseded implementation remains.

Alternate roots are separated as follows:

| Root | Entry point | Effect on canonical truth |
| --- | --- | --- |
| diagnostics | `EnableDocumentDiagnostics` -> `DocumentDiagnosticRunner.Analyze` | report-only, attached after source facts are built |
| repair | agent feedback quarantine -> rerun `AuthorityExtractionPipeline` | explicitly requested new run; not a post-model filter |
| writeback | Web action tool -> `PdfProductWriteback` / OOXML writeback | writes an already materialized result; does not select headings |
| review | `AuthorityDocumentSourceReader`, `AuthorityOutlineReviewProjection`, `ReviewBundle` | projects existing source/outline data only |
| eval/tests | test transport/replay helpers and frozen experiment gates | no normal host composition root |
| historical replay | explicit `HistoricalRequest` / `AttentionLegacyV1` selection | exact request reproduction, not production default |

## Reachability matrix

Abbreviations: `P` production reachable; `M` model-visible; `T` may change semantic truth; `+/-` may add/drop a claim; `M/S` may merge/split; `B` changes binding/source parts; `H` changes hierarchy. `—` means no. Evidence is current direct caller evidence, not an inference from a filename.

| File / class | Current callers / runtime root | P | M | T (+/−, M/S, B, H) | Special role | Disposition | Evidence |
| --- | --- | ---:| ---:| --- | --- | --- | --- |
| `Routing/CanonicalExtractionDispatcher` | AgentHarness tool, extraction boundary tests | yes | no | — | host routing | LIVE_REQUIRED | Dispatcher is built only with DOCX/PDF canonical extractors. |
| `Pipeline/AuthorityExtractionPipeline` | DOCX extractor, explicit repair package | yes | no | drop only via explicit quarantine rerun | DOCX orchestration | LIVE_REQUIRED | Calls `CanonicalSemanticDocxAuthorityAdapter`; diagnostics are gated. |
| `Pipeline/PdfCanonicalExtraction` | PDF extractor; Web PDF diagnostics endpoint | yes | no | projection only after authority | PDF orchestration | LIVE_REQUIRED | Calls `CanonicalSemanticPdfAuthorityAdapter`. |
| `Pipeline/CanonicalSemanticDocxAuthorityAdapter` | Authority pipeline, direct adapter tests | yes | yes | +/−, M/S, B | DOCX semantic adapter | LIVE_REQUIRED | Builds source evidence then calls the production entry point. |
| `Pipeline/CanonicalSemanticPdfAuthorityAdapter` | PDF extraction, PDF tests/replay | yes | yes | +/−, M/S, B, H | PDF semantic adapter | LIVE_REQUIRED | Calls semantic production, placement, hierarchy and materialization. |
| `Pipeline/CanonicalSemanticEngine` | both adapters; placement coordinator | yes | yes | model proposal meaning only | prompt/request owner | LIVE_REQUIRED | `HeaderClassifierCanonicalTextModel` invokes `BoundaryCutAsync`. |
| `Core/CanonicalSemanticProductionEntryPoint` | both adapters | yes | yes | +/−, M/S, B | semantic contract/control-plane entry | LIVE_REQUIRED | Canonical production result feeds both lanes. |
| `Core/CanonicalSemanticPipeline`, `SemanticSourcePartBinder`, `SemanticSourcePartsContractV2` | production entry point | yes | no | drop invalid, bind exact parts, merge multipart | exact binding / hard validation | LIVE_REQUIRED | Rejects non-verbatim/invalid pointers rather than inventing text. |
| `Core/CanonicalSemanticIdentityResolver` | canonical pipeline, hierarchy resolver | yes | no | merge/repeat identity | identity owner | LIVE_REQUIRED | only model `same-node` identity is consumed; no text-equality identity. |
| `Pipeline/ModelRelationHierarchyResolver` | both adapters, placement coordinator | yes | no | H only | global hierarchy | LIVE_REQUIRED | validates model parent relation and derives depth; reads no style/numbering. |
| `Pipeline/CanonicalSemanticPlacementCoordinator` | both adapters when a transport exists | yes | yes | H only | unresolved placement retry | LIVE_REQUIRED | cannot add/remove/rewrite a bound heading. |
| `Pipeline/CanonicalStructureMaterializer` | both adapters | yes | no | materializes accepted claim, H projection | structural boundary | LIVE_REQUIRED | consumes validated headings and resolved relations. |
| `Pipeline/CanonicalProjectionBoundary` | DOCX/PDF product builders | yes | no | final projection only | output boundary | LIVE_REQUIRED | delegates existing materialized facts to product projection. |
| `Pipeline/DocumentSourceCatalogBuilder` | Authority pipeline | yes | no | source representation only | source catalog | LIVE_REQUIRED | builds source units for result/traces. |
| `Pipeline/DocxAuthorityPipeline` | DOCX adapter | yes | yes | source facts / routing context | source representation | LIVE_SUSPICIOUS | derives scope/domain context; it must remain observation-only. |
| `OpenXmlLayer/StyleTrustAudit` | `DocxPolicyStateBuilder`, diagnostic runner | yes | historical V1 attention only | routing/candidate observation | derived observation | LIVE_SUSPICIOUS | selection trust affects policy state; V2 must not serialize attention. |
| `OpenXmlLayer/TableRole`, `Pipeline/SourceFactsBuilder` | policy state, DOCX source facts | yes | yes | source representation | raw source fact | LIVE_REQUIRED | table role is parser-owned evidence, not a heading decision. |
| `Pipeline/DocumentDomainPolicy` | `DocxAuthorityPipeline`, PDF projection/contracts | yes | yes | routing/model prior risk | derived observation | LIVE_SUSPICIOUS | domain evidence is derived before model context; no cleanup change made. |
| `Pipeline/StructuralScopeTracker` | `DocxAuthorityPipeline` | yes | yes | source scope only | routing signal | LIVE_REQUIRED | supplies structural scope context, not level/claim selection. |
| `OpenXmlLayer/HeadingHeuristics` | policy/source feature builders | yes | historical candidate attention | candidate observation | LIVE_SUSPICIOUS | a V1 attention producer; verify V2 serialization remains attention-free. |
| `Pipeline/ParagraphHeadingSplitter` | diagnostics and legacy deterministic strategy helpers | no canonical semantic use | no | could split only within diagnostic strategy output | diagnostic helper | DIAGNOSTIC_ONLY | no adapter call; used by `DocumentDiagnosticRunner` closure. |
| `Pipeline/DocumentDiagnosticRunner` | only gated in Authority pipeline | conditional only | no | — | diagnostic report | DIAGNOSTIC_ONLY | `EnableDocumentDiagnostics` defaults false; result only assigned to `DocumentOutline.Diagnostics`. |
| `Pipeline/StyleDeclaredOutline` | diagnostic runner; public option setters | no | no | +/−/H inside diagnostic candidate report | deterministic diagnostic strategy | DIAGNOSTIC_ONLY | no pipeline branch reads `StyleDeclaredOutline` option. |
| `Pipeline/TypedNumberingOutline` | diagnostic runner | no | no | +/−/H inside diagnostic candidate report | deterministic diagnostic strategy | DIAGNOSTIC_ONLY | only `DocumentDiagnosticRunner.CandidateSignals` calls `Build`. |
| `Pipeline/Strategies/AdministrativeOutline.Build` | no current caller | no | no | +/−/H if externally invoked | public legacy strategy | COMPATIBILITY_ONLY | method has zero in-repository callers; helpers remain used by typed diagnostics. |
| `Strategies/AdministrativeOutline.NguongNhanDe`, `SplitHeadingBody` | `TypedNumberingOutline`; focused tests | diagnostic only | no | diagnostic text split | shared diagnostic helper | DIAGNOSTIC_ONLY | removing them would break the live typed diagnostic closure. |
| `Strategies/BookTocDictionaryOutline` | diagnostic runner, `NumberingAudit` | no | no | +/−/H inside diagnostic report | diagnostic strategy | DIAGNOSTIC_ONLY | no canonical adapter/host call. |
| `Strategies/RfcTocDictionaryOutline` | diagnostic runner; residual diagnosis tests | no | no | +/−/H inside diagnostic report | diagnostic/test strategy | DIAGNOSTIC_ONLY | no canonical adapter/host call. |
| `Pipeline/PartSectionOutline` | `TypedNumberingOutline`; Web suggested-route diagnostic | no | no | +/−/H inside diagnostic path | diagnostic/compat strategy | DIAGNOSTIC_ONLY | Web only suggests a route; it does not execute this builder. |
| `Pipeline/HistoricalContracts/AttentionLegacyV1` | semantic engine/version selection; historical tests | no | yes, historical only | historical request bytes only | immutable replay contract | HISTORICAL_REPLAY | explicit V1 selection; default is `V2_ATTENTION_FREE`. |
| `Pipeline/CanonicalSemanticExperiment` | adapters, engine, historical and experiment tests | baseline path yes; arms explicit | yes | changes request only when explicitly selected | experiment configuration | LIVE_REQUIRED | baseline is supplied to normal semantic transport; non-baseline arms are not host-configured. |
| `Inference/IHeaderClassifier`, `ChunkResult`, `HeadingClassificationProposal`, `ClassifierContracts` | current providers, wrappers, provider tests | `BoundaryCutAsync` yes; old methods no | transport only | no canonical decision after parse | public provider compatibility | COMPATIBILITY_ONLY | canonical adapters call only `BoundaryCutAsync`; legacy `Classify*` members remain exposed by the public interface/providers. |
| `Inference/PdfExperimentControl` | frozen experiment/replay tests | no ordinary host path | no | provider-call gate only | eval/replay gate | TEST_EVAL_ONLY | no normal composition root supplies an experiment gate. |
| `Repair/PartialKeyPackage`, `TextLayoutLineProbe` | explicit repair/analysis workflows | no | no | may create a subsequent requested run/package | repair only | REPAIR_ONLY | no canonical host path calls either. |
| `Review/*` | Web review projection and explicit review calls | yes, projection only | no | — | review/output | WRITEBACK_ONLY | reads source and completed outline; never writes semantic authority. |
| `OpenXmlLayer/ApprovedWritebackExecutor`, `OutlineWriteback`, `Pipeline/PdfProductWriteback` | explicit Web writeback action | yes, explicit action only | no | may alter OOXML file, not truth | writeback | WRITEBACK_ONLY | writes the already projected/materialized result. |

## Authority graph

| Mutation / decision | Current owner | Constraint |
| --- | --- | --- |
| source text, aliases, parts, layout/table facts | `OpenXmlDocumentSource` / PDF source-universe builders | SOURCE_REPRESENTATION; parser-owned facts only |
| claim creation/drop, semantic role/type/scope, multipart claim proposal | provider via `CanonicalSemanticEngine` -> `CanonicalSemanticProductionEntryPoint` | SEMANTIC_MODEL / SEMANTIC_CONTRACT |
| proposal schema, owned aliases, verbatim and source-part validation | `SemanticSourcePartsContractV2`, `SemanticSourcePartBinder`, canonical pipeline | EXACT_BINDER / HARD_VALIDATOR; can reject but not synthesize a heading |
| claim merge/repeat identity | `CanonicalSemanticIdentityResolver` | IDENTITY_RESOLVER; only model-provided same-node keys |
| parent and level | model parent relation + `ModelRelationHierarchyResolver` | GLOBAL_RESOLUTION; resolver validates and counts depth only |
| unresolved-heading placement | `CanonicalSemanticPlacementCoordinator` + model | SEMANTIC_MODEL for relation; coordinator cannot change membership |
| final elements/outline/product | materializer and projections | PROJECTION; consumes accepted facts, does not reclassify |
| diagnostics/repair/writeback | their separate roots | DIAGNOSTIC / LEGACY; no feedback edge to canonical adapter |

The contract still carries logically independent `isHeading` and `semanticRole` fields. That is observed as a remaining semantic-contract debt, not redesigned here.

## Diagnostic non-interference proof

`AuthorityExtractionPipeline.ExecuteDocumentAsync` computes diagnostics only under `EnableDocumentDiagnostics`; it retains the report in the compatibility outline's `Diagnostics` property. `CanonicalSemanticDocxAuthorityAdapter.RunAsync` receives only policy state, mode, analyst, cancellation token and replay capture; it receives neither a diagnostic report nor a strategy result. The result of `DocumentDiagnosticRunner` has no call edge to `isHeading`, source parts, semantic role, identity, hierarchy, `CanonicalStructureMaterializer`, or `CanonicalProjectionBoundary`.

An explicitly invoked repair may use source/diagnostic data to request a new quarantined run. That is a distinct user-requested operation, not a diagnostics-to-authority feedback path.

## Candidate ranking audit

There are no current `PdfCandidateRanking`, `PdfProductionOccurrenceResolver`, `PdfSemanticClusterContracts`, `RankedCandidate`, `top-K`, or candidate-admission types/calls in source. The canonical PDF adapter builds a full source universe and passes it to `CanonicalSemanticProductionEntryPoint`; no ranking closure gates provider recall on this route.

## Deletion manifest and decision

No deletion batch is safe on this HEAD.

| Batch | Candidate | Why it was not deleted |
| --- | --- | --- |
| 1 | named obsolete types from the request | absent already; nothing to delete |
| 2 | `AdministrativeOutline.Build` | zero repository callers, but public static API; helpers are live in typed diagnostics |
| 2 | legacy deterministic strategies | each has a diagnostic caller behind explicit opt-in; deleting would remove supported diagnostics |
| 3 | `HeadingClassificationProposal`, `ChunkResult`, `IHeaderClassifier.Classify/Critique/Hierarchy` | no canonical caller, but public provider/interface compatibility and direct provider tests remain |
| 3 | unused legacy `PipelineOptions` flags and CLI/Web setters | functionally dormant in canonical extraction, but a public CLI/UI/API compatibility surface; no evidence permits a breaking removal |
| replay | `AttentionLegacyV1` | immutable historical request contract, needed for reproducibility |

Therefore this audit intentionally makes no semantic or API deletion. A deletion would either break an explicit diagnostic/repair/replay/compatibility closure or exceed the evidence available from the repository.

## Inventory counts

Counts cover the matrix rows/classes above; grouped rows count once per stated disposition.

| Disposition | Count |
| --- | ---: |
| LIVE_REQUIRED | 14 |
| LIVE_SUSPICIOUS | 3 |
| DIAGNOSTIC_ONLY | 9 |
| REPAIR_ONLY | 2 |
| WRITEBACK_ONLY | 2 |
| HISTORICAL_REPLAY | 1 |
| TEST_EVAL_ONLY | 1 |
| COMPATIBILITY_ONLY | 3 |
| DEAD | 0 |
| PARTIALLY_DEAD | 0 |

## Remaining debt, separated by kind

- Dead-code debt: no repository-dead source type was found in the named high-priority set. The absence of the previously named types is itself confirmed.
- Compatibility debt: public provider methods and many legacy `PipelineOptions`/CLI/UI switches are no longer consumed by the canonical lane.
- Diagnostic coupling: diagnostics share parsing/strategy helpers with obsolete deterministic builders; the authority edge is absent, but the code is still compiled and public options advertise it.
- Semantic-authority duplication: no legacy deterministic or diagnostic authority is on the provider-backed route. The remaining concern is the independent `isHeading`/`semanticRole` representation in the semantic contract.
- Hierarchy debt: placement has a second provider request for unresolved relations; it is membership-safe, but makes hierarchy depend on a separate model transaction.
- Source-representation debt: `StyleTrustAudit`, `HeadingHeuristics`, and `DocumentDomainPolicy` are derived observations feeding policy/context. They require an explicit V2 model-visible evidence audit before any future removal/refactor.

## Critical answers

1. **No.** No old deterministic/legacy classifier is production-reachable on the provider-backed canonical route. `DocumentDiagnosticRunner` is explicitly gated and its result does not enter either canonical adapter.
2. **No.** A legacy/diagnostic component cannot alter `isHeading` or claim membership after the LLM. The binder may reject invalid source pointers; that is the canonical hard-validation owner, not a legacy filter.
3. **The SRC-095 precision issue is not plausibly caused by a legacy runtime path.** The current provider-backed path is the semantic contract/model plus its canonical binding/validation; diagnostics are non-interfering. This audit did not run or tune a provider experiment.
4. **Four semantic authority stages remain:** semantic model/contract (membership), exact binding/hard validation, identity resolution, and model-relation hierarchy resolution. Projection is intentionally non-semantic.
5. **Next issue without provider calls:** add deterministic tests/a static contract audit proving that V2 request composition cannot serialize `CandidateAttention`, style-trust-derived selection, or other derived priors as semantic truth. Do not add a post-model heading filter or change prompts/models.
