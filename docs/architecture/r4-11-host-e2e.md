# R4-11 Host Runtime E2E

Status: PASS

R4-11 proves that the four normal host surfaces execute the same authority route on one
deterministic DOCX. The test uses `DisableLlm=true`; no provider or network call is part of this
gate.

## Revision Authority

- `baseRevision`: `5f59d46fce7397d6606c1df3e4c992bbc58265f8`
- `executionRevision`: `12c105e40fdf022eb69513db80b1d5f209131c27`
- `publicationRevision`: the commit containing this closure pair
- `fullSuite`: `NOT_RUN`

## Runtime Routes

- CLI normal extraction runs the normal command and reaches `PipelineDocumentExtractionTool`.
- Web posts the fixture to `/api/extract` through `WebApplicationFactory`.
- MCP calls public `McpExtractionService.ExtractAsync` with a rules-only host configuration.
- AgentHarness uses a real `DocumentAgentHarness` and a real `PipelineDocumentExtractionTool`.

All outputs are normalized to `StableId`, `HeadingSpan.Start`, `HeadingSpan.End`, `Level`, and
`Text`, then joined by SHA-256 fingerprint.

Observed fingerprint for every host:

`16284414abee710236b27fe92f710b95efb32928e169ba0e1ede2e63891b8429`

## Gates

- `CANONICAL_TOOL_FINGERPRINT` = `CLI_FINGERPRINT` = `WEB_FINGERPRINT` = `MCP_FINGERPRINT` = `AGENT_HARNESS_FINGERPRINT`
- `UNJOINED_HOST_RESULTS`: `0`
- `CLI_NORMAL_DIRECT_EXTRACTION_BYPASS`: `0`
- `WEB_API_EXTRACT_DIRECT_EXTRACTION_BYPASS`: `0`
- `MCP_EXTRACT_DIRECT_EXTRACTION_BYPASS`: `0`
- `AGENT_HARNESS_DIRECT_EXTRACTION_BYPASS`: `0`
- `HOST_LEGACY_FALLBACK`: `0`
- `UNKNOWN_HOST_EXTRACTION_ROUTE`: `0`
- `providerCalls`: `0`
- `expectedChanged`: `false`
- Release build: `PASS`
- Focused host suite: `2/2 PASS`

`NeedsHumanReview` is a valid AgentHarness terminal outcome for the fixture; the host gate joins
the returned validated outline and does not incorrectly require the human-review disposition to be
`Completed`.

R4-12 is authorized. The 1338-test canonical full suite remains deferred to R4-13.
