# VERIFY-6A — RFC-Preserving Architecture Replay

Status: **BLOCKED_PRE_CANONICAL_FULL_SUITE**.

The replay was derived from merge-base `fbc8c0bc3763a51a7f27d0a2ebcdc8e847561cd2` and replayed 18 ordered architecture commits through `d173feb92b4bcf69e9371518e8f22ee6b6fa020b`, followed by MCP-2 `e3e6edb218ff007f7194838873d5ae26b089fe75`. The replay worktree retained the RFC-closed `RfcTocDictionaryOutlineTests.cs` contract. The architecture target differs at four RFC assertion sites, but no architecture-range commit owns that file; the divergence is the pre-RFC file state at the architecture branch base. Therefore the four correction provenances are identified without rolling back assertions.

## Gates

- RFC lane: 5/5 PASS
- RFC-2 invariants: 67 / 67 / 0 / 1.0 PASS
- MCP default RID: PASS
- MCP `win-x64`: PASS
- Latest architecture-focused tests: 40/40 PASS
- F regression: 2/2 PASS
- Release build: PASS, 0 errors

## Full Suite Evidence

The run produced `1326 total, 1256 passed, 70 failed, 0 skipped`. It is not a valid canonical baseline: the clean replay tree did not contain non-versioned benchmark artifacts required by existing probes, and the run exhausted temporary disk space. Several failures are consequently missing-artifact, repository-root, or temp-I/O failures. The complete raw failure rows are persisted in the JSON artifact.

Accordingly, `CURRENT_INTEGRATED_FAILURE_UNIVERSE` is **NOT_FROZEN** and `UNJOINED` is not claimed as zero. No production semantics were changed and no provider calls were made.

Artifact: `eval/verification/rfc-preserving-architecture-replay.v1.json`.

## Checkpoint causal conclusion

Checkpoint A at `a1c5b7d` reproduced the exact `DocumentDiagnosticRunner`
failure before architecture replay. The lineage is therefore:

- `FAILURE_PREDATES_ARCHITECTURE_REPLAY = PROVEN`
- `FAILURE_PREDATES_MCP2 = PROVEN`
- `INTRODUCED_BY_ARCHITECTURE = false`
- `INTRODUCED_BY_MCP2 = false`
- `CLASSIFICATION = PREEXISTING_RELATIVE_TO_INTEGRATION`
- `ROOT_CAUSE = UNKNOWN`
- `REMEDIATION_JUSTIFIED = false`

This answers who did not introduce the failure, not why the expectation fails.
It must not be relabeled `STALE_ASSERTION` without separate evidence.

`VERIFY-6E = NOT_APPLICABLE`: its required A-pass/B-fail shape is absent.
The next valid step is VERIFY-6C environment reconstruction, followed by
VERIFY-6B only after the canonical execution gates pass. Any later 6B failure
with this fingerprint joins this pre-existing lineage rather than the new
architecture-failure bucket.
