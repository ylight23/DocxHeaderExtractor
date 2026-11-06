# Gather trial (experimental)

`capture-docx <private-capture-directory> <docx>` runs one DOCX extraction with private diagnostic capture. It calls a paid provider and requires explicit authorization; its per-run reservation ceiling is 0.50 USD, not an estimate or a cumulative spending limit. Run it from the main repository checkout. The capture directory must be outside the repository or under ignored `scratchpad` / `.dhx-evidence`. SELECT/GROUP protocol events are linked to wire requests by document, unit, turn and request hash; transport retries have separate index records. PLACE and repair record wire bodies as well as model evidence. The final check refuses a successful result if responses, metadata, protocol attribution or placement evidence are missing; inspect the audit and explicitly report failures. This entry point has been tested with a fake provider, not yet with a live provider. It does not replace the blocked A/B and paired probes.

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
