# PDF072 production pipeline Luna run — blocked at provider seam

Date: 2026-10-25
Status: BLOCKED before model inference. No semantic output was frozen; no Gold or historical semantic response was read.

## Requested profile

Intended current production path: `PdfHandler.ExtractAsync` → `PdfPipeline.RunAsync` → `PdfLane.RunAsync` → `LaneHarness.DecideAndAssembleAsync` → registered PDF selector, binding, production placer, and hierarchy/materialization. Intended defaults: V12, raster geometry enabled, source-bound selection, generic grouping, production placement. No semantic VERIFY phase was to run.

## Interface inspection and blocking seam

The production orchestration accepts `HeadingAgents` (`src/DocxHeaderExtractor.Contracts/Agents/HeadingAgents.cs`). `IHeadingSelector.SelectAsync` and `IHeadingPlacer.ProposeAsync` are the only exposed agent seams. The registered `PdfSelector` supports a public `Func<HttpMessageHandler>` adapter, so model transport could be bridged there while preserving its own production scheduling and phases (`src/DocxHeaderExtractor.HeadingAgent/Selectors/PdfSelector.cs`; `AgentSelector.SelectAsync`).

The production `HeadingPlacer` does not expose an equivalent public transport injection: its production constructor creates its own `AgentModel`/HTTP handler; its `ModelTurn` and `HttpMessageHandler` adapter constructors are `internal` (`src/DocxHeaderExtractor.HeadingAgent/Placement/HeadingPlacer.cs`). `PdfPipeline` does accept an `IHeadingPlacer`, but substituting an external implementation would require reimplementing/delegating placement semantics rather than bridging the transport of the registered production placer. Therefore the required end-to-end Luna transport adapter cannot be installed through the public production interface without changing production code or substituting a harness/agent implementation. Both alternatives violate the request.

## Run records

- Production orchestration was inspected but not invoked because its required placer provider boundary cannot be bridged compliantly.
- Prepared requests: 0; request hashes: none.
- Responses: 0; response hashes: none.
- Provider/model calls: 0; timestamps: no calls.
- Production phase/call counts: not applicable; no phase began.
- Parallelism: not applicable; no production scheduling started.
- Adapter boundaries: no adapter installed. Selector has an injectable HTTP handler factory; placer has none on its public production constructor.
- No manual phase sequence, qualification semantic verifier, or surrogate harness was run.
- Gold scoring, FP identification, and geometry-versus-semantics conclusions were not performed because no frozen Luna semantic output exists.

## Evidence preserved

This report records the interface inspection and exact blocker. The run directory contains no fabricated requests/responses. Relevant source files are listed above; production code/defaults were not modified.
