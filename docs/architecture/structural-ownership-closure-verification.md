# S3-S5: clean tracked Windows verification

Verified source commit: `f2d68db892b606322d6baec4eed9c942a2ce527f`.
Date: 2026-10-08 (Asia/Saigon).

## Checkpoints

- S3 `1e10e40`: HeadingPipelineResult is the runtime envelope; Structure alone is
  structural authority. Public C# type rename is intentional; envelope wire shape is retained.
- S4 `757c311`: ParentId and OutlineElements are deliberately retained compatibility
  views. Production projections consume Elements/Relations, not a second authority.
- S5 `f2d68db`: pure structural proposal validation belongs to Core; unchanged class
  body, no reverse project dependency. Namespace/assembly API migration is intentional.

No additional cleanup stage, prompt/schema change, provider experiment, Gold change or
frozen artifact rebaseline is introduced by verification.

## Isolation and checkout policy

A separate local tracked clone at the exact commit excludes the user's modified H3 test
and all untracked research. Core.longpaths is enabled locally for historical artifact
paths. Core.autocrlf=false matches the main workspace; explicit .gitattributes rules,
including CRLF-bound provider captures, remain authoritative.

An initial run inherited system core.autocrlf=true and was interrupted after frozen
preflight hash mismatches. That run is invalid environment evidence, not a refactor
regression or a PASS. It is not included in the result below.

The checkout was reproduced using the repository's byte policy and verified against HEAD
before restarting. For example the G2B preflight SHA256 again matched frozen authority:
`b97130b4aa0fa09c9e4a3ad5a25aa24366ed5e2149efee08917915c6c6ca30db`.
No historical artifact was regenerated or changed in the main workspace.

## Completed verification

| Gate | Result |
| --- | --- |
| Clean tracked Release solution build | PASS, 0 errors; existing test/qualification warnings |
| Main workspace production project Release build | PASS, 0 errors, 0 warnings |
| S5 focused ownership/output/replay/lifecycle/parity suite | 135/135 PASS, 0 failed/skipped |
| Full manifest CoreDeterministic Windows Release tier | 1022/1022 PASS, 0 failed/skipped |
| CoreDeterministic duration | 16 minutes 41 seconds |
| Tracked/untracked checkout status after suite | CLEAN, 0 entries (ignored build/log files excluded) |
| git diff --check / staged graph against HEAD | PASS / no staged difference |
| Real provider calls / Gold mutation / frozen rebaseline | 0 / 0 / 0 |
| GitHub Ubuntu CI for S3-S5 | NOT RUN at this local verification checkpoint |

Official tier command:

```powershell
pwsh -NoProfile -File scripts/Invoke-A99TestTier.ps1 -Tier CoreDeterministic -Configuration Release -NoBuild
```

A99_FREEZE_UPDATE=0, provider credentials cleared in the child process, and experiment
execution gates closed. Suite membership comes from the committed test-tier manifest;
no failing test was excluded ad hoc. This is the complete CoreDeterministic tier, not
the All tier containing provider/long-running/historical/diagnostic campaigns.
The separately verified focused suite includes frozen replay and fake-transport PDF
lifecycle regressions that the tier otherwise quarantines.

Completed Windows console log SHA256:
`318347259999b3a28244382d304ded4fe966a2a36e7bc2e6667e512184e13d47`.
The log remains local under the verification clone's ignored .verify-build directory.
S5 focused TRX SHA256:
`afa6d2e54b7f0897410d59c014060552f2bd18ba79b30edc891c9540135c8e37`.

This closes local Windows verification of the S1-S5 ownership changes. It does not
claim new accuracy qualification, generalization, recursive graph immutability, or
Ubuntu CI success before the new commits are pushed and that job completes.
