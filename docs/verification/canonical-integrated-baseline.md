# VERIFY-5 Canonical Integrated Tree Reconstruction

VERIFY-5 was audited from the requested revisions:

- RFC-closed snapshot: `30befaad58ea5c73e8ebd56b051982e4f117a403`;
- MCP-2: `e3e6edb218ff007f7194838873d5ae26b089fe75`;
- latest architecture: `d173feb92b4bcf69e9371518e8f22ee6b6fa020b`.

The architecture series was applied onto the RFC-closed snapshot and MCP-2
was then applied. The combined commit is
`ae304304d69d2ae0b621f414cd6e0cdeeb7bda55`, with a clean worktree before
verification.

## Gate Result

The reconstruction is **not canonical**. ARCH-4 focused gates passed `6/6`
and the Release build passed with zero errors, but the mandatory RFC gate
failed: `RfcTocDictionaryOutlineTests = 1/5 PASS`. RFC-2 invariants were not
verified on this tree.

The architecture patch series modified the RFC test file and restored stale
route assertions. Four RFC failures therefore reappeared, while the
`DocumentDiagnosticRunner` failure is not covered by an RFC correction and is
recorded as `UNKNOWN`. No assertion was edited during VERIFY-5.

## Full Suite

The integrated run produced `1317 total`, `1282 passed`, `35 failed`, and `0`
skipped. All 30 VERIFY-3 failures still fail with unchanged fingerprints. Five
new identities are present, so the integrated universe is `35`, not `30`.
Those five rows and their exact assertion locations are persisted in the JSON
artifact.

The correct next action is to reconcile the architecture patch with the
already-closed RFC test contracts before attempting another integrated
baseline. VERIFY-5 is not closed and the 30-failure freeze is not valid for
this tree.

`PROVIDER_CALLS = 0`

`PRODUCTION_BEHAVIOR_CHANGED_BY_INTEGRATION = false`
