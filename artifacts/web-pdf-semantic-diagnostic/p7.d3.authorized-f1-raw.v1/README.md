# P7 F1 original raw provider responses

Publication addendum to `main@80f56b9`, authorized explicitly by the user after the result push.
The earlier receipts remain immutable: `responseTextPublished=false` records the state when
those receipts were created, not the state of this subsequent publication.

All 21 attempts are included: 14 primary calls and 7 retries, including all 14 rejected B
responses. `manifest.json` maps call handles, attempts, validator failure codes and relative
paths, and hashes every original file. Nothing has been redacted or regenerated.

For each `NN/attempt-N/`:

- `response.txt`: original completion text, before parser validation.
- `response.sse`: original captured SSE text, including provider-generated reasoning when present.
- `observation.json`: original transport observation, usage, completion and SSE.
- `raw-freeze.json`: immutable pre-validation raw hashes.
- `attempt-receipt.json`: immutable outcome, cost and retry-lineage receipt.
- `parsed-decision.json`: only the seven originally accepted Control outputs.

`capture-freeze.json` closes the original execution. The publication manifest also binds the
previously pushed capture and forensics V2 receipts. All files are byte-preserved in Git via
the narrowly scoped `-text` rule. README and manifest are new publication metadata, not
additional provider outputs.

Request bodies/headers, API keys, private exception text and authorization files are NOT
published. Known active OpenRouter key and credential-pattern scans found no matches in the
exported files; this is not a claim of exhaustive sensitive-data detection.

Git now permits independent inspection/reassembly of provider responses and retry accounting.
Full evidence assertion validation still requires the matching frozen source/evidence inputs;
this package does not publish the original PDFs or frozen request bodies.

Publication sends zero provider calls and changes no production code, Gold or scoring. All B
attempts remain contract failures; their stage-decision ledgers are not silently adopted.
Paired semantic accuracy remains NOT_EVALUABLE. See the separate result summary and Gold score.

Publication verification: 3/3 new raw-response tests PASS (every file hash, frozen receipts,
rejected-attempt retention, SSE reassembly, provider/model identity, original observations).
Working-tree P7 focused regression: 448/448 PASS. This is not a claim that the full test suite
or the subsequent GitHub CI has passed.
