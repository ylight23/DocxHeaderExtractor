# VERIFY-2 Current Full-Suite Failure Reconciliation

## Reconciliation

VERIFY-1 produced 31 current failures. All 31 are represented in the JSON
artifact with current assertion, expected/actual values where observable,
FQN, fingerprint, source location, historical join, and applicability status.

| Bucket | Count |
| --- | ---: |
| Historical exact still fail | 0 |
| Historical changed failure | 30 |
| Historical now pass | 5 |
| New failure | 1 |
| Unjoined failures | 0 |

The 30 historical FQNs are joined, but their current TRX-message fingerprints
do not literally match the fingerprints retained by the historical packet.
Consequently, the old classifications are recorded as `NOT_PROVEN` for
current applicability. They are not silently reclassified as current
production failures.

The five recovered identities are four RFC TOC cases and one diagnostic-runner
case. The four RFC cases have `PASS_RECOVERY_OWNER = RFC`; the diagnostic
runner owner remains `NOT_PROVEN` because identity alone does not prove an
RFC owner.

## MCP Counter-Check

The new failure is:

`McpStdioIntegrationTests.Stdio_server_advertises_only_the_three_read_only_tools`

The test calls `FindMcpDll`, which constructs the non-RID path
`src/DocxHeaderExtractor.Mcp/bin/Release/net9.0/dhx-mcp.dll`. Under the
VERIFY-1 `RuntimeIdentifier=win-x64` build, the artifact is emitted beneath
`net9.0/win-x64`, so lookup fails before MCP production host resolution.

Counter-check results:

- explicit `win-x64`: fail at test host lookup;
- default/no explicit RID: MCP integration test passes;
- `RID_SENSITIVE = true`;
- classification: `TEST_CONFIGURATION` / `PACKAGING_LOOKUP`;
- production regression: not proven.

No csproj, test, production, or expected-value changes were made.

`PROVIDER_CALLS = 0`

`PRODUCTION_CODE_CHANGED = false`

`TEST_EXPECTATIONS_CHANGED = false`
