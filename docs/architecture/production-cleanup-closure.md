# S12–S15 production cleanup closure

Baseline: `cafe32e` (S6–S11). This closes the residual ownership findings without changing
heading authority, prompts, schemas, provider selection, Gold, or frozen research artifacts.

## S12: host-owned inference composition

AgentHarness references only Application, DocumentProcessing, and Core. Its extraction tool requires
an explicitly supplied neutral factory or transport. Web/CLI/MCP already supply the factory.
The removed public one-argument `PipelineDocumentExtractionTool(PipelineOptions)` constructor is an
intentional source/binary API break: external callers must inject a factory or transport. No facade
or default provider construction remains in the harness. No-LLM tests supply a factory that throws
on any attempted creation, including PDF creation.

## S13: opt-in host/provider diagnostics

`ShowRawOutput` is removed from public `PipelineOptions` (intentional API/property-shape change),
and is owned by `CommandLineOptions` on CLI and the `showRaw` form flag on Web. CLI explicitly
configures the selected remote provider's `DebugLog`, including with `--quiet`; output goes to
stderr, not result stdout. Web retains its explicit form opt-in and UI log sink.

LM Studio and SGLang now invoke the existing callback with actual request/response payloads;
OpenRouter retains its existing callback implementation. This is a diagnostic behavior fix, not
a blanket behavior-parity claim. Diagnostic payloads can contain sensitive document text. Logging
is off by default, and request headers/API keys are not included by the added callbacks. Tests use
in-memory HTTP handlers to verify callback payloads and identical bodies with logging on/off.
Existing OpenRouter truncation behavior is unchanged; `--show-raw` is not an immutable capture service.

## S14: extraction policy reachability

DOCX extraction now passes `PipelineOptions.Extraction` to `OpenXmlDocumentSource`. `--no-tables`
therefore changes the source inventory as intended. Default `IncludeTables=true` is unchanged.
This is a behavior fix: excluding table paragraphs changes subsequent dense source ordinals, but
the structural XML-path source IDs remain stable. It is not a wire-parity claim for filtered input.

Regression fixtures include before-table, in-table, and after-table paragraphs. Extraction and
review use the same configured policy. Product and approved-review writeback both target the
after-table paragraph, preserve the source file, and leave the table paragraph unchanged.
CLI/Web extraction pass their extraction policy to their immediate writeback tools.

The separate Web human-review upload endpoint still uses the default extraction policy; Web
currently does not expose a table-exclusion form option. No new session/config serialization is
introduced here. A review plan built with excluded tables and applied using incompatible ordinals
is rejected by `ApprovedWritebackExecutor` before writing, rather than silently remapped. Changing
review-session policy persistence would require a separate deliberate contract change.

## S15: vocabulary and legacy governance

Private transport fields/methods/parameters and local variables use transport terminology.
Public constructor/method parameter names remain unchanged for named-argument callers.
Compatibility names `RawAnalystResponses`, `RouteAudit`, `DeterministicRoute`, JSON `route`, and
the `configured-analyst` provenance value remain unchanged. The neutral factory comment correctly
names DocumentProcessing, not Core, as its consumer. Review reader comments describe source-native
reading rather than the retired outline/Slim projection.

Production project references and source namespaces cannot depend on V5Qualification. Historical
`LegacyCore`/`LegacyPdf` and qualification `InternalsVisibleTo` remain available for replay.
Existing orchestration-folder allowlist and stage ownership guards remain in force.

## Verification gate

New focused regressions pass locally. Final Release, expanded focused regressions, the clean tracked
Windows `CoreDeterministic` tier, and GitHub Ubuntu deterministic CI are required before marking
the architecture status `ARCHITECTURE STABLE / CLEANUP CLOSED`. Until those gates complete, do
not treat the previous baseline's green suite as verification of this change.

No additional mass rename/move stage is planned. Accuracy/provider qualification remains separate.
