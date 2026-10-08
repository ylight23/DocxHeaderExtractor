# S12–S15 production cleanup closure

Status: `ARCHITECTURE STABLE / CLEANUP CLOSED`.
Verified revision: `c5dec2ee700e7e04903e00d642d5d268b5e1b8e1`.

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

Windows-tested source revision: `2f59cd799d81884348b33024a527a76d6060da72`.
Only documentation changed between that revision and the verified `c5dec2e` checkpoint;
production source, tests, and project files are identical. Ubuntu CI tested `c5dec2e` directly.
The verification checkout contains only committed files, without the main workspace's modified
H3 preflight or untracked research files. Provider keys were blank and `A99_FREEZE_UPDATE=0`.

- Release solution build: PASS, 0 errors, 34 pre-existing warnings; no new diagnostic in this scope.
- Expanded focused regressions: PASS, 201/201, 0 failed/skipped (including 20 new S12–S15 cases).
- Official Windows `CoreDeterministic` tier: PASS, 1051/1051, 0 failed/skipped, 19m29s.
- `git diff --check`: PASS; verification checkout status after the suite: clean.
- Read-only Roslyn audit of DocumentProcessing: 0 unused imports, 0 compilation errors.
- Provider calls: 0; Gold mutations: 0; frozen artifact rebaselines: 0.
- GitHub publication: COMPLETE; `c5dec2e` is published on `main`.
- Ubuntu CI at `c5dec2e`: PASS, Release build, focused 51/51, deterministic 1051/1051,
  0 failed/skipped, and generated-artifact cleanliness. See
  [deterministic CI run 37718570871](https://github.com/ylight23/DocxHeaderExtractor/actions/runs/37718570871).

Commands: `dotnet build -c Release`; focused `dotnet test -c Release --no-build`;
`pwsh -NoProfile -File scripts/Invoke-A99TestTier.ps1 -Tier CoreDeterministic -Configuration Release -NoBuild`.
The tier used the unchanged official manifest, without ad-hoc exclusions.

Local ignored Windows verification records for `2f59cd7` (`.verify-build` in the clean checkout):

| Record | SHA-256 |
| --- | --- |
| `s12-s15-release-build.log` | `86a1de3dea4cbb081c9bf96587ad40bbae2056c294c27326820d1397571a013f` |
| `s12-s15-focused.trx` | `f2d4c818103a384d649e4bfeaffe478ee98619030ce8228b43003377584833cb` |
| `s12-s15-full.log` | `89d9d1f6da3284e1e3bff77a09e345da25477cfb4c5b8541b84a4f98ab4276fa` |

The Windows and Ubuntu verification gates are closed. The earlier publication/CI-pending status
is superseded by the successful CI run of the verified revision, not by a prior baseline's result.

No additional mass rename/move stage is planned. Accuracy/provider qualification remains separate.
Separating `FrozenInferenceResult` runtime response fields from qualification diagnostics is an
optional subsequent API refactor; no inference contract or runtime change is included in this closure.
