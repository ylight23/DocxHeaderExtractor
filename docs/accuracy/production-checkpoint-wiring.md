# R1 Production Checkpoint Wiring

Status: `DETACHED_WRITER_LIFETIME_PASS`

`AuthorityExtractionPipeline` now creates a unique, production-owned checkpoint directory for the normal PDF authority route and passes its checkpoint path to the existing broad audit pipeline with `resume: false`. When a lane outlives its hard deadline, checkpoint admission closes, active writers drain, and cleanup proceeds without waiting for a detached provider that may hang forever. Late writes are blocked; complete lanes clean up immediately. Diagnostic callers still control their explicit checkpoint paths.

The change preserves the existing authority chain: checkpointed spans are only reused by `PreservePartialSpanResolutions`; validation, grounding, and output policy remain authoritative. Candidate construction, ranking, model configuration, timeout, and concurrency are unchanged.

Verification: focused checkpoint/lane/authority tests passed `31/31`; the complete suite recorded `1216/35/0` and matched the exact 35-test baseline failure set from `3b4e358` (`1214/35/0`), proving no new full-suite regression. Core, CLI, Web, and test projects built successfully; targeted `win-x64` restore/build also passed. No provider calls were made.

Final cancellation closure: focused checkpoint/lane/authority tests passed `31/31`, including admitted-write cancellation and fault drain. The current full suite recorded `1216/35/0` (`1251` total); the exact pre-remediation baseline at `3b4e358c2696190e2aafd5a609587ad335cb1eea` recorded `1214/35/0` (`1249` total). The 35 failed test identities matched exactly, so `NO_NEW_FULL_SUITE_REGRESSION = PROVEN`. Active writer cancellation drain and fault drain pass; a lost active-writer count cannot leave drain hanging. No provider calls were made.
