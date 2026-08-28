# VERIFY-3 Post-MCP Full-Suite Delta

VERIFY-3 ran the full test assembly in the clean MCP-2 worktree at commit
`e3e6edb218ff007f7194838873d5ae26b089fe75`.

| Metric | Result |
| --- | ---: |
| Total | 1288 |
| Passed | 1258 |
| Failed | 30 |
| Skipped | 0 |
| VERIFY-2 failures still failing | 30 |
| VERIFY-2 failures now passing | 1 |
| New failures | 0 |
| Changed fingerprints | 0 |
| Unjoined | 0 |

The exact MCP identity
`DocxHeaderExtractor.Tests.McpStdioIntegrationTests.Stdio_server_advertises_only_the_three_read_only_tools`
was present in VERIFY-2 and absent from the post-MCP failure set. The full
suite therefore records `MCP_FAILURE_RECOVERED = true` and
`MCP_DELTA = CLEAN_RECOVERY`.

The RFC guard remains green: `RfcTocDictionaryOutlineTests = 5/5 PASS`, with
RFC-2 invariants `67 / 67 / 0 / 1.0`. `RFC_LANE_REGRESSION = false`.

No production code, test expectation, or provider behavior was changed during
VERIFY-3. The only code under test is the previously committed MCP-2 test
helper change.

`PROVIDER_CALLS = 0`

`PRODUCTION_CODE_CHANGED = false`

`TEST_EXPECTATIONS_CHANGED = false`
