# VERIFY-4 Post-ARCH-4K Integrated Baseline

The clean combined tree was based on ARCH-4K `be8d9a4` and received the
MCP-2 test helper change from `e3e6edb`. The resulting combined revision is
`068af49c1565f10f5a5b27ca87bfef7a3781a52c`.

ARCH-4K focused gates passed `6/6`; the Release solution build passed with
zero errors. The full suite, however, did not preserve the VERIFY-3 universe:

| Metric | VERIFY-3 | Integrated |
| --- | ---: | ---: |
| Total | 1288 | 1317 |
| Passed | 1258 | 1282 |
| Failed | 30 | 35 |
| Skipped | 0 | 0 |

All 30 VERIFY-3 failures remain with unchanged fingerprints. Five new failure
identities appear on `be8d9a4`, including four RFC TOC tests and one
diagnostic runner test. The exact failure rows are persisted in the JSON
artifact; no failures are unjoined.

The RFC guard is not green on this exact architecture base: the focused RFC
lane is `1/5 PASS`, and RFC-2 invariants were not verified on this tree. The
reason is ancestry, not an inferred production regression: `be8d9a4` does not
contain the RFC-4/6 test-contract corrections, so stale route assertions are
present again. VERIFY-4 therefore records `RFC_LANE_REGRESSION = true` and
`ARCHITECTURE_THROUGH_4K_FULL_SUITE = NOT_STABLE`.

MCP-2 remains integrated and its failure identity is absent from the combined
failure set. No provider calls or production-code changes occurred.
