# S11: residual dependency closure verification

Verified source: `f5d8f0bab9bd4abdd2333cb80ecdf5818fee5f12`.
Date: 2026-10-08 (Asia/Saigon).

## Checkpoints

- S6 `44cf378`: remove unused PipelineOptions from inference factory signatures.
- S7 `234137b`: distinguish review snapshot/reader from common source IR.
- S8 `08e55a7`: retire unused materialization wrapper, preserving the audit-null empty output gate.
- S9 `e411bd8`: remove stale imports/global heading imports and guard explicit stage dependencies.
- S10 `f5d8f0b`: rename extraction request/result/format handlers; retain wire and routing behavior.

Public C# signature/type retirement is intentional and documented in
residual-dependency-closure-audit.md. It is not a source/binary compatibility claim.
ValidatedStructure remains the sole structural authority; ParentId/OutlineElements remain
the retained S4 compatibility views. No new authority, semantic layer or cleanup stage is added.

## Clean tracked verification

The isolated tracked checkout is at the exact source commit, core.autocrlf=false, with the
existing .gitattributes byte policy unchanged. User H3 edits and untracked research are excluded.
A99_FREEZE_UPDATE=0; provider credentials are cleared in the test child process.

| Gate | Result |
| --- | --- |
| Clean tracked Release solution build | PASS, 0 errors; 34 existing qualification/test warnings |
| Main workspace DocumentProcessing Release build | PASS, 0 warnings/errors |
| Focused ownership/API/output/byte parity/provider policy/replay/lifecycle/host suite | 159/159 PASS, 0 failed/skipped |
| Full manifest CoreDeterministic Windows Release tier | 1031/1031 PASS, 0 failed/skipped |
| CoreDeterministic duration | 20 minutes 2 seconds |
| Production retired names / unused factory option signatures | 0 live references; guards PASS |
| Explicit dependency / Pipeline exact-layout / unique source IR guards | PASS |
| DocumentProcessing source CS8019 import audit | 0 unused imports, 0 compilation errors; SDK-generated imports excluded |
| git diff --check / verification checkout status after suite | PASS / CLEAN, 0 entries except ignored build/log files |
| Corpus/Gold/frozen artifact diff | NONE |
| Real provider calls / Gold mutation / frozen rebaseline | 0 / 0 / 0 |
| GitHub Ubuntu CI | NOT RUN at this local verification checkpoint; requires push and separate completed run |

The increase from 1022 to 1031 is nine new deterministic ownership/API/wire regression cases.
Official full-tier command:

```powershell
pwsh -NoProfile -File scripts/Invoke-A99TestTier.ps1 -Tier CoreDeterministic -Configuration Release -NoBuild
```

Membership is the committed tier manifest, not an ad hoc failure exclusion. This is the full
CoreDeterministic tier, not the All tier containing provider/long-running/historical campaigns.
The separately run focused suite also covers frozen replay and fake-transport lifecycle cases
quarantined by the full-tier manifest. No failing test was removed to produce a PASS.

## Local evidence hashes

- Completed Windows console log SHA256:
  `d102dc168514dbf3dcffff2f244a8c5359ed8b23176dde0513a068de8678d72e`.
- Focused TRX SHA256:
  `2ff5111813b3296c8ac5b42946b3819f8884935a5d0cb44f6bf4caa2caeb2c21`.
- Clean Release build log SHA256:
  `0757bcc4bd584d54d841b9ab041e1b669fd504b15c0c202d5189c5e30d2e55b2`.

Logs remain local in the ignored verification .verify-build directory. Historical evidence
is not rewritten. The main workspace retains the user's H3 edit; a clean verification clone
does not imply the user's working tree was reset or that research was committed.

This closes the requested S6-S11 local cleanup gates. It does not claim new accuracy/provider
qualification, generalization, external API compatibility or Ubuntu success before CI completes.
