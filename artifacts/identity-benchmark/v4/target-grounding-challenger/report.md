# A99 V4G-A — target-grounding challenger freeze

Status: **READY_FOR_V4G_TARGET_GROUNDING_PROVIDER_EXPERIMENT**

Development status: **DEV_EXPOSED_CHALLENGER**. This request representation was informed by V4F-F target-grounding forensics; no independent generalization claim is made.

## Checkpoint

- Parent: `V4F-F@153204b`
- Candidate universe: **7,702/7,702**, unchanged.
- Frozen V4F-D sample: **128/128**, same candidate IDs.
- V4F-C historical projected target mismatch: **45/128**.
- V4F-C historical projected valid: **63/128**.

## Target contract

`TARGET PAIR` contains `LEFT_TARGET` and `RIGHT_TARGET`, each with the exact frozen occurrence ID, verbatim text, canonical comparison text, source order, and source/container location. `SUPPORTING EVIDENCE` is separately labeled and cannot become an endpoint by list position. The response relation vocabulary and parser are unchanged.

## Evidence lineage

- New source facts: **0**.
- Target fields duplicate existing pair-core facts only.
- Supporting evidence is the reconstructed frozen V4F-B packet with pair-core removed.
- Candidate generation, ranking, projection bounds, model, provider, and parser are unchanged.

## Size

- V4F-C projected total: **64,990,323 bytes**.
- V4G target-grounded total: **72,186,437 bytes**.
- Delta: **7,196,114 bytes (11.0726%)**.
- Mean increase/request: **934.3**; p95: **950**; max: **954**.

## Firewall

Gold reads: **0**; V4E evaluation reads: **0**; provider calls: **0**; model calls: **0**. V4F-C/F-D/F-E/F-F artifacts were read-only inputs and were not modified.

No provider execution was performed. This phase freezes an experiment-ready challenger only.
