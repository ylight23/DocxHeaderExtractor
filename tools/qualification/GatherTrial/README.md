# Gather trial (experimental)

`capture-docx <private-capture-directory> <docx>` runs one DOCX extraction with the same opt-in capture used by the web harness. It calls a paid provider and requires explicit authorization. The capture directory must be outside the repository or under ignored `scratchpad` / `.dhx-evidence`. The summary verifies saved file hashes and reports missing responses; PLACE captures model evidence rather than the complete wire body. Inspect the evidence and protocol failures before calling a diagnostic complete. This does not replace the blocked A/B and paired probes, and does not establish that their capture is complete.

The older `split-run`, `whole-run`, `split-capture`, `whole-capture`, `place-ab`, `paired-placement`, and `paired-frozen21` entry points are disabled until every model attempt can be captured and verified. `split-replay` and `docx-region-probe` remain available offline.

Runs one goal against one DOCX source. This is a qualification harness, not the production UI or an end-to-end writeback. Provide a JSON array of pipeline `HeadingRecord` objects for source headings. `--style-fallback` explicitly uses Word Heading styles instead; it is not equivalent to the pipeline's heading results.

```powershell
dotnet run --project tools/qualification/GatherTrial -- dry-run goal.txt source.docx outline.json unused-output
dotnet run --project tools/qualification/GatherTrial -- run goal.txt source.docx outline.json new-output
```

`dry-run` parses the DOCX and counts batches without reading an API key or calling a provider. `run` requires `OPENROUTER_API_KEY`, makes paid requests and requires a new output directory. Each invocation limits itself to eight attempts and a one-dollar budget using the existing conservative admission ledger; this limit does **not** include earlier invocations. Check the provider account and earlier logs, including calls with unknown billing, before setting a cumulative budget or resuming. Never assume an interrupted request cost zero.

`replay` requires a checkpoint directory as the final argument and never calls a provider. It rechecks cached responses with the current checker; a missing checkpoint is reported as an unreviewed batch. Its elapsed time is local replay time, not the original model's latency.

Set `GATHER_TRIAL_MAX_CALLS` to an integer from 1 to 8 to use a smaller call limit. A smaller limit can leave batches unreviewed; that must not be interpreted as a complete result.

The output includes `run-metadata.json`, `provider-events.jsonl`, checkpoints, and `run-result.json` if the runner finishes. A start event without a completion indicates unknown outcome and cost. Checkpoints and results contain information derived from the source: store them in a private directory. The event log contains metadata and usage, not document text or the API key.

To reuse successful checkpoints, give a **new** output directory and pass the previous checkpoint directory as a sixth argument. A new per-invocation budget applies. Reuse is limited to the identical serialized prompt and model identity; source content or settings changes invalidate the corresponding checkpoint.

```powershell
dotnet run --project tools/qualification/GatherTrial -- run goal.txt source.docx outline.json new-output previous-output/checkpoints
```
