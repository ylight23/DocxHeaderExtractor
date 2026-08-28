# VERIFY-1 Post-RFC Full-Suite Rebaseline

## Result

The post-RFC snapshot was executed in the separate worktree at base revision
`30befaad58ea5c73e8ebd56b051982e4f117a403`. Git status was clean before test
execution. ARCH-4E4 was not cherry-picked.

| Metric | Value |
| --- | ---: |
| Total | 1288 |
| Passed | 1257 |
| Failed | 31 |
| Skipped | 0 |
| Historical failures now passing | 5 |
| Historical failures still failing | 30 |
| New failure identities | 1 |

The complete occurrence-level failure list, with FQN, TRX-message fingerprint,
source file, assertion line, historical classification, and delta status is in
`eval/verification/post-rfc-full-suite.v1.json`. The 30 historical FQN matches
were classified from the C1 ledger. The single new identity is the MCP host
lookup failure; it is evidence only, not an automatic regression classification.

## RFC Guard

`RfcTocDictionaryOutlineTests` passed 5/5, including RFC 092. RFC-2 metrics
remain `67 dictionary entries / 67 body anchors / 0 TOC-only / 1.0 ratio`.
Therefore `RFC_LANE_REGRESSION = false` and RFC-1 through RFC-6 are not
reopened by this run.

The test run used `RuntimeIdentifier=win-x64` to avoid copying every
cross-platform native asset into the isolated output. This changed the MCP
test host lookup path, which is why the new MCP failure is recorded explicitly.

`PROVIDER_CALLS = 0`

`PRODUCTION_CODE_CHANGED = false`

`TEST_EXPECTATIONS_CHANGED = false`
