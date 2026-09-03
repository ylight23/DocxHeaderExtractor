# AUTO-HARNESS PHASE 2 — Runtime Verification & Activation

Status: `VERIFICATION_COMPLETE`

Phase 1 is frozen at `main@5678b454dc28c8bab811c5ce35a789d540fa82be`. This phase activates and
verifies the prepared generic seams without Accuracy-99 tuning, Human Gold work, corpus-specific
rules, or model output becoming authority.

Invariant:

```text
MODEL PROPOSES → CODE VALIDATES → SOURCE MATERIALIZES → POLICY AUTHORIZES
```

## Frozen baseline

- Branch: `verification/auto-harness-phase2`
- Worktree: `C:\DocxHeaderExtractor-auto-harness-phase2`
- Baseline main/tree: `5678b454dc28c8bab811c5ce35a789d540fa82be` /
  `4b53ec1f88dfc8cacb71cf4e0d48be4da4b2e441`
- Phase-1 audit: PASS
- Phase-1 final gate: PASS
- Restore: PASS
- Release build: PASS, 0 errors
- Full baseline suite: `923 PASS / 7 FAIL / 0 SKIP / 930 total`
- Baseline failures: four stale source/type paths after Phase-1 relocation, one stale retained
  reachability expectation, one stale N1.5 artifact hash, and one duplicate inventory expectation.

The baseline failures are verification debt, not production behavior changes. They must be repaired
or explicitly classified before the Phase-2 regression gate can pass.

## Workstreams

- [x] P2-WS1 — Baseline and test inventory frozen; Phase-1 gates pass unchanged.
- [x] P2-WS2 — Provider-neutral conversational intent producer and all intent states.
- [x] P2-WS3 — Microsoft Agent Framework adapter outside Core with approval boundary.
- [x] P2-WS4 — Skill catalog runtime verification.
- [x] P2-WS5 — Semantic registry runtime verification.
- [x] P2-WS6 — Capability execution matrix.
- [x] P2-WS7 — Source-grounding end-to-end verification.
- [x] P2-WS8 — Generic multi-prompt functional matrix.
- [x] P2-WS9 — Web/CLI/MCP host parity.
- [x] P2-WS10 — Approval, policy, security, and prompt-injection resistance.
- [x] P2-WS11 — Retry, cancellation, deadline, and idempotency.
- [x] P2-WS12 — Durable state and resume.
- [x] P2-WS13 — Provider interchangeability.
- [x] P2-WS14 — Reliability/performance profile.
- [x] P2-WS15 — Full regression.
- [x] P2-WS16 — Phase-2 verification gate and publication.

## Verification evidence

The deterministic verification matrix is recorded in
[`phase2-verification-evidence.md`](phase2-verification-evidence.md) and the machine-readable
gate input is `eval/verification/phase2-final-evidence.v1.json`. The matrix covers the generic
runtime contracts, all three production hosts, source-grounded execution/projection, policy and
security boundaries, lifecycle persistence, provider-neutral composition, and the full test
regression. No provider call or Accuracy-99 mutation is part of this phase.

## Product boundary

The Microsoft Agent Framework, if enabled, is an outer adapter. It may orchestrate sessions and
function tools, but it cannot bypass `IntentValidator`, `CapabilityCatalog`, `PolicyDecision`, or
source validation/materialization. Provider/framework verification is functional verification, not
Accuracy-99 measurement.

## Completion contract

Phase 2 completes only when every mandatory workstream is evidenced by deterministic tests or
recorded provider limitations, Phase-1 gates still pass, no Accuracy-99/Human Gold files changed,
and the final gate has published and remote-verified the branch in `main`. Then this file becomes
`Status: VERIFICATION_COMPLETE`, [phase2-seams.md](phase2-seams.md) becomes `Status: VERIFIED`,
and the phase stops without opening Human Gold or Accuracy-99.
