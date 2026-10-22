# Current Production Heading Authority

> **Superseded inventory notice.** The historical inventory below records the pre-cutover
> reachability state. It is retained as dated context only. The current production architecture
> is the source-adapter/heading-authority route described next.

## Current architecture

Names and folders as of 2026-10-22 (`DocumentProcessing/<Folder>/<Type>`); see
[../architecture/current.md](../architecture/current.md) for the full layout.

```text
DOCX -> DocxSourceAdapter -+
                           +-> DocumentSourceSnapshot
PDF  -> PdfSourceAdapter --+          |
scan -> ScanSourceAdapter -+          v
                    Selection/SelectionAuthority  (PDF since 2026-10-14, DOCX since 2026-10-14)
                          IHeadingSelector -> Selectors/DocxSelector / PdfSelector, Scan/ScanSelector
                          (V6 direct agent, D4.12 method: one conversation per layout segment,
                          get_line_facts first, evidence tools, repair turns)
                          F1 -> G2A -> H2-C V2 (FunctionAnchorExtentHeadingAuthority) retired
                                      |
                                      v
                    Structure/StructureAssembler (shared, format-blind)
      Binding/DecisionBinder -> Placement/ParentResolver -> Hierarchy/HierarchyResolver
                              -> Structure/Materializer
                                      |
                                      v
      Output/Final/FinalProjection -> Output/Product/ProductProjector
```

Hosts (2026-10-15): the CLI and the Web call `DocumentRunner` directly; there is no separate harness project
between them and the pipelines. The runner routes the file, converts `.doc`/`.rtf`/`.odt` first, and writes the
outline when asked.

Routes: `Lanes/Docx/DocxLane`, `Lanes/Pdf/PdfLane` and `Lanes/Scan/ScanLane` each build their source and hand
it to `Harness/LaneHarness`, which authorizes the run, lets the selected-lines authority decide, runs the one
shared assembler and records the route audit. Every lane runs under the shared lane lease (`LaneExecution`,
`LaneLease`); a run that calls the model takes its `ModelRoute` (model and key) from the host's `IModelProvider`
and refuses without one, and the placer's `ModelAccess` checks the lease before and after every model turn. The PDF route additionally owns its checkpoint. There is no canonical-text fallback and no local-model route:
the former DOCX text authority (`TextSemanticHeadingAuthority`, `CanonicalSemanticEngine`, the Core
canonical pipeline and binder) was removed when DOCX moved to the V6 agent.

Ownership:

- Format adapters own source facts and parser details (PdfPig layout, OOXML styles).
- A heading authority owns semantic membership, start and extent, and nothing downstream of it.
- Hierarchy owns parent and depth. The materializer owns canonical structure creation.
- Projection owns product shape. `Agents` owns the provider wire and the model/route policy; `App/OpenRouterProvider` hands it the key.

Heading extent (DOCX, 2026-10-15): a paragraph can hold a heading and the body text after it, on the same line
or after a `w:br` (which the reader writes as one space). The DOCX answer format (`P7_F1_V6_SELECT_OBJECTS_EXTENT_V1`)
lets a SELECT carry `headingExtent` `{start, end, text}`: UTF-16 offsets into the paragraph's text and that exact
text. The model decides the extent; the facts give it `lineBreakOffsets`, `pieces` and `formatRuns` with offsets
(`DOCX_AGENT_LINE_FACTS_V3`). The parser checks the offsets, the verbatim text, edge whitespace and word boundaries
and refuses a mismatch with what it measured; it never moves an extent. `SelectionAuthority` binds the
heading to that span. A SELECT without an extent is the whole paragraph; an extent that does not lie inside its
paragraph is rejected there and the paragraph makes no heading, never the whole paragraph. Writing the outline back is
still per paragraph (`w:outlineLvl`), so a heading that shares its paragraph with body text marks the whole
paragraph there; exact writeback for that case needs its own design. The PDF answer format, prompt and production
instruction (`InstructionStructuralV1`) are unchanged; `InstructionFormattingEvidenceV2` is a candidate to compare.

Compact facts and core coverage (DOCX): `get_line_facts` (`DOCX_AGENT_LINE_FACTS_V3`) gives every target its core facts
and only a summary of the heavy ones: line breaks with their offsets (at most 8), the number of format runs and the
offsets where the formatting changes, and a repetition summary (`otherOccurrences` and two `sampleAliases`). The pieces
between line breaks, every format run with its exact offsets and the full text come from `get_source_span`; the places of
a repeated text come from `get_repeated_occurrences`, `compare_source_locations` and `get_source_context`. The full facts
stay in the evidence store, so a model that asks gets the same measurements as before. The program decides how data is
shown, never which detail a paragraph needs.

Each unit keeps two ledgers in its audit: `coreFactsCoverage` (the targets whose core facts a facts call returned, and the
results that were cut) and `detailEvidenceCoverage` (the aliases the model chose to read through the other tools; not
required of any target). A DOCX unit is COMPLETE only when every target's core facts were returned: an answer that falls
short goes back once, naming the targets, while another facts result still fits the unit's tool bytes; if they stay
uncovered the unit is INCOMPLETE (`COVERAGE:CORE_FACTS_MISSING`), and if there is no room left to read them it is
INCOMPLETE too (`BUDGET:CORE_FACTS_UNCOVERED`). None of an INCOMPLETE unit's decisions is used. On PDF the ledger is
recorded only, so the qualified conversation keeps its request bytes.

Units (DOCX): a window holds at most 48 paragraphs and at most 28,000 bytes of compact facts (the size the tool would write
for them), and is never shorter than one paragraph. An ordinary document is cut into windows of 48; only a window of
unusually large paragraphs is cut shorter. The windows stay a partition and the context shown with a window crosses its
edges. Measured offline on the 72 readable documents of `todo10_8/generated-docx` (3,718 units): core facts p50 27.3 KB,
p90 28.9 KB, p99 29.1 KB, max 30.9 KB, none over 32 KB, two facts calls per unit, and no unit cut short by size (the 72
shorter units are the last unit of each document).

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
