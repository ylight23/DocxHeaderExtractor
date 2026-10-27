# PDF072 integrated production adaptive visual Luna qualification — 2026-10-26

## Status

**Partial; not frozen or scored.** I invoked the public `PdfHandler.ExtractAsync` path with the production `PdfSelector` and the newly integrated adaptive visual option. The process created six final SELECT request bodies, then waited at the local file bridge for authored replies. It was interrupted before any reply, remaining SELECT, GROUP, PLACE, freeze, or post-freeze score was produced.

A strict blind DOC-0252 scoring sequence is unavailable for this attempt: before running, I opened `docs/qualification/pdf072-adaptive-multimodal-luna-20261026.md` for integration context. That prior note contains post-Gold metrics. I did not open Gold or score files in this attempt. Consequently this attempt has no valid score-blind comparison and must not be scored.

## Route and options

The test-only entry point invokes `PdfHandler.ExtractAsync` with the production `PdfSelector` attached as the PDF agent. It runs the public handler, PDF pipeline/lane, production selection authority, grouping, and placement chain. `PdfSelector` was configured for source-bound selection, raster geometry, separate pairwise grouping, general boundary prompt, unchanged V12 instruction, and `adaptiveVisualSelection=true`. Both the selector's request policy and `PipelineOptions` set `PdfVisualSelectionMaxRequestBytes=786432`; the PDF selector parallelism is the production default of six. The production-default/source files were not edited for this qualification; only `NativePdf072Luna.cs` and its test-only mode dispatch in `Program.cs` were extended.

The production planner measures the prepared first request body with `RequestResources.Measure` inside its `Fits` callback before calling `OrderedScopePlanner.Plan` (`AgentSource.cs`, lines 52–64). The production `SelectionTask` attaches the page image to that prepared request (`SelectionTask.cs`, line 80). The captured requests confirm the image is an inline `data:image/jpeg;base64,...` `image_url` block.

## Captured evidence

The run captured 6 of the 15 expected first-round SELECT requests. Their serialized body sizes were 277,022–381,080 bytes, each had one inline JPEG, and each was below 786,432 bytes. The captured scopes contain 249 distinct aliases with no duplicate ownership among captured requests. This is partial evidence only: it does not establish the cap or unique ownership for all final SELECT requests because the other nine were never sent to the bridge.

Exact request JSONs and image/body digests are in `artifacts/qualification/pdf072-integrated-adaptive-luna-20261026/luna-requests/` and `integrated-wire-audit.json`. No response files exist. The bridge uses request-SHA-bound response JSON files and has no active Luna inference transport. No semantic response was fabricated to get past this gate.

## Timing and deviations

The interrupted local bridge attempt ran for approximately 26 seconds from command start to interruption, including local build/source preparation and waiting for response files. This is **local-bridge wall time**, not model latency; no model call occurred.

Deviations from the requested complete qualification:

- The process was stopped after six requests because no Luna response authoring session/tool was connected to the local response-file bridge. Therefore GROUP and PLACE were not run and no phases were frozen.
- The prior qualification note was read before freeze and included scores, invalidating any blind scoring sequence for this attempt.
- Full-request cap and complete alias-ownership checks could not be confirmed from the six bodies alone.

## Reproduction

The test-only invocation is `dotnet run --project tools/qualification/TargetRepresentationTrial/TargetRepresentationTrial.csproj -- <PDF072 path> <new output directory> integrated-adaptive-pdf072-luna`. It refuses to overwrite a nonempty output directory.
