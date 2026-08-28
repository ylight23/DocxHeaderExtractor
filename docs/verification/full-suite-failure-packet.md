# C0 Full-Suite Failure Packet

## Scope

This packet reconstructs the failed-test evidence from TRX results for the
baseline and qualification revisions. The runs used separate worktrees and
separate result directories. No provider calls, production edits, or test
expectation edits were made.

## Results

| Run | Revision | Passed | Failed | Skipped | Total |
| --- | --- | ---: | ---: | ---: | ---: |
| Baseline | `3b4e358c2696190e2aafd5a609587ad335cb1eea` | 1214 | 35 | 0 | 1249 |
| Current | `a0a3638178e0b6092880abbce933a8954fa1780` | 1226 | 35 | 0 | 1261 |

The current revision has twelve additional passing tests; its failed-test
count remains 35.

## Exact Comparison

- `SAME_TEST_IDENTITY_SET = true`: all 35 failed test identities match.
- `SAME_FAILURE_FINGERPRINT_SET = true`: all normalized failure fingerprints
  match as well.
- Added failed identities: 0.
- Removed failed identities: 0.
- Added fingerprints: 0.
- Removed fingerprints: 0.

The identity is the TRX `testName` (fully qualified test name). Fingerprints
hash the identity, normalized error message, and first relevant stack frame.
Normalization removes only unstable absolute paths, GUIDs, and durations; it
does not remove assertion expected/actual content.

## Authority

The machine-readable source packets are:

- `eval/verification/full-suite-failure-packet.baseline.v1.json`
- `eval/verification/full-suite-failure-packet.current.v1.json`
- `eval/verification/full-suite-failure-packet-compare.v1.json`

Each failed test retains its TRX message, stack trace, duration, assembly,
fully qualified identity, and fingerprint. The packet is available at test
occurrence level; console output was not used as the authority.

`OCCURRENCE_LEVEL_PACKET_AVAILABLE = true`

`PROVIDER_CALLS = 0`

`PRODUCTION_CODE_CHANGED = false`

`TEST_EXPECTATIONS_CHANGED = false`
