# R1 Production Checkpoint Wiring

Status: `DETACHED_WRITER_LIFETIME_PASS`

`AuthorityExtractionPipeline` now creates a unique, production-owned checkpoint directory for the normal PDF authority route and passes its checkpoint path to the existing broad audit pipeline with `resume: false`. When a lane outlives its hard deadline, checkpoint admission closes, active writers drain, and cleanup proceeds without waiting for a detached provider that may hang forever. Late writes are blocked; complete lanes clean up immediately. Diagnostic callers still control their explicit checkpoint paths.

The change preserves the existing authority chain: checkpointed spans are only reused by `PreservePartialSpanResolutions`; validation, grounding, and output policy remain authoritative. Candidate construction, ranking, model configuration, timeout, and concurrency are unchanged.

Verification: focused checkpoint/lane/authority tests passed `30/30`; the complete suite recorded `1215/35/0` and matched the exact 35-test baseline failure set from `a1c5b7d` (`1211/35/0`), proving no new full-suite regression. Core, CLI, Web, and test projects built successfully; targeted `win-x64` restore/build also passed. No provider calls were made.
