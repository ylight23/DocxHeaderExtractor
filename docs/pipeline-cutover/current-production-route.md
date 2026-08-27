# Current Production Route Inventory

Baseline: `main@6debf01bdb59e8d46919e5c521481feaab3b15f1`

Status: inventory only. No extraction code is changed by this document.

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
- `src/DocxHeaderExtractor.Cli/Program.cs:2453-2471` constructs the selected text classifier/provider; this is not itself a final heading authority.
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
