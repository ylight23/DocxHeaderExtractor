# Core ownership closure

Source baseline: `a2fece331b05c9a59e3e998207be2b2596c3e745`.

Implementation checkpoint: `d062012`.
Current-doc/comment checkpoint and full-suite tested revision:
`96225b6f521ba02186ff31e26273cd1d7590fe69`.

## Decisions

- Provider carrier, envelope and body types move from Core to
  `Infrastructure/AI/QualifiedInference`. Their wire encoding and payload hashes remain frozen.
- Neutral prompt/protocol/occurrence/context and inference input/interface contracts stay in
  `Core/Models/Inference`; neutral historical namespaces remain source-compatible.
- Production entrypoint, request composer and context packing move to
  `DocumentProcessing/Semantics/Canonical`. Core never references that implementation project.
- Pure proposal normalization, deterministic binding/identity orchestration and global conflict
  detection remain in Core, under `Semantics/Canonical`; their DTOs stay in Models.
- The mixed runtime file is split; validators and exact source binding are retained.
- Current documentation no longer presents retired classes/scripts or old unmerged-branch status
  as the active architecture. Historical artifacts/docs are not rewritten.

This is ownership cleanup, not semantic promotion or accuracy qualification. Relocated public
implementation types require imports/assembly references to be updated and consumers rebuilt;
binary compatibility for those types is not claimed. There is no compatibility facade that
reintroduces a concrete provider into Core.

## Verification

| Check | Tested revision | Result |
|---|---|---|
| Whole solution Release build, clean tracked checkout | `96225b6` | PASS, 0 errors |
| Focused ownership, wire/composer, validation and semantic replay tests | `d062012` | 131/131, 0 failed/skipped |
| Full Windows tracked Release suite | `96225b6` | 981/981, 0 failed/skipped, 16m05s |
| Status and diff check after full suite | `96225b6` | clean checkout, no artifact drift |
| Implementation comparison for six extracted/moved classes | baseline vs `96225b6` | unchanged, excluding comments/ownership |

Local TRX hashes (not raw provider captures):

- `core-ownership-focused.trx`: `0b3693d0b8a3c178df0922b48c484401c79c931c3db9ddbe4979cb87032e7bde`
- `core-ownership-full.trx`: `9a41aabe2656fdebbf74be66b67e527c336f8e3993c163c1cc611cb01d0eae33`

The TRX files remain in the isolated verification worktree's ignored `.verify-build/test-results`.
These results do not claim a new GitHub Ubuntu CI run or a full suite on a later revision.

Provider calls: 0. Gold mutation: 0. Frozen artifact rebaseline: 0.
The author's existing H3 test diff and 238 untracked research files are preserved and excluded
from these commits. The dirty workspace solution build encountered two untracked research tests
with old provider-type imports; those user-owned files were not edited or removed. The clean
tracked build/suite above does not include them.
