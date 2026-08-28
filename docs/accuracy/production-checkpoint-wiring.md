# R1 Production Checkpoint Wiring

Status: `WIRED_FOCUSED_VERIFICATION_PASS`

`AuthorityExtractionPipeline` now creates a unique, production-owned checkpoint directory for the normal PDF authority route and passes its checkpoint path to the existing broad audit pipeline with `resume: false`. The checkpoint is disposed before the temp directory is cleaned up. Diagnostic callers still control their explicit checkpoint paths.

The change preserves the existing authority chain: checkpointed spans are only reused by `PreservePartialSpanResolutions`; validation, grounding, and output policy remain authoritative. Candidate construction, ranking, model configuration, timeout, and concurrency are unchanged.

Verification: focused checkpoint/authority tests passed `24/24`; Core, CLI, Web, and test projects built successfully without a RID. A `win-x64 --no-restore` build was not available because the existing assets files do not contain `net9.0/win-x64` targets. No provider calls were made.
