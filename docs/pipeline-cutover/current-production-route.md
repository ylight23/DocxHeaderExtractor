# Current Production Heading Authority

> **Superseded inventory notice.** The historical inventory below records the pre-cutover
> reachability state. It is retained as dated context only. The current production architecture
> is the source-adapter/heading-authority route described next.

## Current architecture

```text
DOCX -> DocxSourceOccurrenceAdapter -+
                                     +-> SourceOccurrenceUniverse
PDF  -> PdfSourceOccurrenceAdapter --+          |
                                                v
                                       IHeadingAuthority   (the replaceable seam)
                         DOCX and PDF: SelectedLinesHeadingAuthority  (PDF since 2026-10-14, DOCX since 2026-10-14)
                               IHeadingSelector -> DocxHeadingAgentSelector / PdfHeadingAgentSelector
                               (V6 direct agent, D4.12 method: one conversation per layout segment,
                               get_line_facts first, evidence tools, repair turns)
                               F1 -> G2A -> H2-C V2 (FunctionAnchorExtentHeadingAuthority) retired
                                                |
                                                v
                                     HeadingStructureAssembler (shared, format-blind)
        HeadingDecisionBinder -> HeadingParentResolver -> HeadingHierarchyResolver
                                -> HeadingStructureMaterializer
                                                |
                                                v
        CanonicalFinalStructure -> DocumentProductOutputProjector
```

Hosts (2026-10-15): the CLI and the Web call `V6DocumentRunner` directly; there is no harness between them and
the pipelines. The runner routes the file, converts `.doc`/`.rtf`/`.odt` first, and writes the outline when asked.

Routes: `DocxHeadingPipeline` and `PdfHeadingPipeline` each run their source adapter, the selected-lines
authority and the one shared assembler. Both run under the shared lane lease (`LaneExecution`) and the
fail-closed qualified transport (`IAgentRouteInferenceTransport`, `LeaseBoundInferenceTransport`); the
PDF route additionally owns its checkpoint. There is no canonical-text fallback and no local-model route:
the former DOCX text authority (`TextSemanticHeadingAuthority`, `CanonicalSemanticEngine`, the Core
canonical pipeline and binder) was removed when DOCX moved to the V6 agent.

Ownership:

- Format adapters own source facts and parser details (PdfPig layout, OOXML styles).
- A heading authority owns semantic membership, start and extent, and nothing downstream of it.
- Hierarchy owns parent and depth. The materializer owns canonical structure creation.
- Projection owns product shape. Infrastructure owns provider transport and the model/route policy.

Heading extent (DOCX, 2026-10-15): a paragraph can hold a heading and the body text after it, on the same line
or after a `w:br` (which the reader writes as one space). The DOCX answer format (`P7_F1_V6_SELECT_OBJECTS_EXTENT_V1`)
lets a SELECT carry `headingExtent` `{start, end, text}`: UTF-16 offsets into the paragraph's text and that exact
text. The model decides the extent; the facts give it `lineBreakOffsets`, `pieces` and `formatRuns` with offsets
(`DOCX_AGENT_LINE_FACTS_V2`). The parser checks the offsets, the verbatim text, edge whitespace and word boundaries
and refuses a mismatch with what it measured; it never moves an extent. `SelectedLinesHeadingAuthority` binds the
heading to that span; a SELECT without an extent is the whole paragraph, as before. Writing the outline back is
still per paragraph (`w:outlineLvl`), so a heading that shares its paragraph with body text marks the whole
paragraph there; exact writeback for that case needs its own design. The PDF answer format, prompt and production
instruction (`InstructionStructuralV1`) are unchanged; `InstructionFormattingEvidenceV2` is a candidate to compare.

Facts coverage: every unit keeps a ledger of the targets whose own facts a facts call returned, and the audit
(`factsCoverage` in each unit's model answer) lists the targets left without and the facts results that were cut.
On DOCX an answer that leaves targets uncovered goes back once, naming them, while another facts result still fits
the unit's tool bytes; the next answer is taken as it is. On PDF the ledger is recorded only, so the qualified
conversation keeps its request bytes.

The DOCX route has not been scored against Gold yet; scoring it is a separate step. Gold, replay,
qualification artifacts and diagnostics are never production authority.

## Historical inventory

## Route Summary

| Surface / route | Current construction path | Current authority | Classification | Cutover note |
|---|---|---|---|---|
| CLI normal `extract` | `RunExtractAsync` -> `PipelineDocumentExtractionTool` -> `HeaderExtractionPipeline` | `HeaderExtractionPipeline` selector/fallback chain | `LEGACY_AUTHORITY` | Must enter the shared authority orchestrator by default. |
| CLI `--pdf-first` | `RunExtractAsync` sets `PdfFirstValidatedFallback` -> `HeaderExtractionPipeline` PDF-first branch | PDF validated/product path, only when explicitly enabled | `NEW_AUTHORITY` (opt-in) | Promote as the normal path; remove silent legacy rescue. |
| CLI `review` / `eval` / repair commands | CLI-specific harnesses and evaluators; several construct `HeaderExtractionPipeline` | Review/evaluation outputs | `EVAL_ONLY` / `DIAGNOSTIC_ONLY` | Keep separate from production authority. |
| CLI `pdf-stage-*`, `pdf-hierarchy-*`, `pdf-visual-*`, `pdf-shadow-compare` | Direct stage/evaluator/probe calls | Frozen facts, diagnostics, or comparison outputs | `DIAGNOSTIC_ONLY` / `EVAL_ONLY` | Must never become a final heading authority. |
| Web extraction | `DocxHeaderExtractor.Web/Program.cs` builds `PipelineDocumentExtractionTool`; harness invokes its `HeaderExtractionPipeline` | `HeaderExtractionPipeline` | `LEGACY_AUTHORITY` | Web and CLI must share the cutover orchestrator. |
| MCP extraction | `McpExtractionService` -> `PipelineDocumentExtractionTool` -> `HeaderExtractionPipeline` | `HeaderExtractionPipeline` | `LEGACY_AUTHORITY` | Must use the same authority contract as CLI/Web. |
| AgentHarness extraction | `PipelineDocumentExtractionTool` constructs `HeaderExtractionPipeline` | `HeaderExtractionPipeline` | `LEGACY_AUTHORITY` | Make the tool delegate to the canonical orchestrator. |
| AgentHarness normal writeback | `OutlineWritebackTool` for non-PDF-first routes | Compatibility/legacy outline | `LEGACY_AUTHORITY` | Must consume product output from the canonical route after cutover. |
| AgentHarness PDF-first writeback | `PdfProductWritebackTool` -> `PdfProductWriteback` when `PdfFirstValidatedFallback` is set | `PdfProductOutput` | `NEW_AUTHORITY` (opt-in) | Retain mechanism; remove conditional production routing. |
| Web writeback | Web harness request may provide writeback target; current extraction still comes from `HeaderExtractionPipeline` | Conditional legacy/new depending on PDF-first flag | `LEGACY_AUTHORITY` by default | Unify extraction and writeback selection. |

## Evidence Map

- `src/DocxHeaderExtractor.AgentHarness/DocumentExtractionTool.cs:29-40` constructs `HeaderExtractionPipeline`.
- `src/DocxHeaderExtractor.Cli/Program.cs:134-155` constructs the extraction tool/harness; writeback selection at `137-142` is conditional on `PdfFirstValidatedFallback`.
- `src/DocxHeaderExtractor.Cli/Program.cs:743-744` directly invokes `HeaderExtractionPipeline` for a non-production evaluation path.
- `src/DocxHeaderExtractor.Cli/Program.cs:2244-2247` uses `PdfFinalStructureProjection` and `PdfOutputDecisionPolicy` in a diagnostic shadow/evaluation route.
- `src/DocxHeaderExtractor.Cli/Program.cs:2453-2471` constructs the selected text transport/provider; this is not itself a final heading authority.
- `src/DocxHeaderExtractor.Web/Program.cs:305-350` creates the extraction tool/harness and runs the request.
- `src/DocxHeaderExtractor.Mcp/McpExtractionService.cs:83-89` creates `PipelineDocumentExtractionTool`, then runs the harness.
- `src/DocxHeaderExtractor.Core/Pipeline/HeaderExtractionPipeline.cs:184` defines `PdfFirstValidatedFallback`; the normal route remains conditional.
- `src/DocxHeaderExtractor.Cli/CommandLineOptions.cs:506-507` documents `--pdf-first` as an explicit opt-in route and says output is still review-gated.

## Authority Findings

1. `DocxAuthorityPipeline` and the PDF `FinalStructure -> OutputDecision -> ProductOutput` components exist on `main`, but they are not yet the only normal production path.
2. The current normal path can still select headings through historical style, numbering, legal, bookmark, TOC, tagged, layout, analyst, textbook, financial, Docling, heuristic, or model branches.
3. The current PDF-first branch is a conditional authority path, not a completed global cutover.
4. `PdfLegacyValidatedOutputPolicy` is reachable from the hierarchy-facts diagnostic snapshot only; it is not the target production authority.
5. The next implementation gate is to make all normal CLI/Web/MCP/AgentHarness extraction requests enter one authority orchestrator, then add architecture guards before removing legacy production reachability.

## Cutover Constraints

- No production fallback from authority failure to the legacy selector chain.
- Deterministic parsers remain evidence/source-fact producers unless their output passes the shared validator.
- Gold, silver, census, replay, and diagnostic artifacts remain evaluation-only.
- `OpenRouterVisualQuestion` remains optional diagnostic/evaluation evidence until a separate production approval exists.
- Document `004` is a regression/trace case only; no tuning is performed during cutover.
