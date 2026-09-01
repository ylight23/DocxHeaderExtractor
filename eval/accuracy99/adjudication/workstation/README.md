# Accuracy-99 local adjudication workstation

This is a static, offline editing surface for the canonical Phase C review packets. It does not run a server, call a provider, load a model, show production predictions, or assign semantic labels.

## Run

Open `index.html` directly in a modern browser. Select one or more files from:

```text
eval/accuracy99/adjudication/development/*.review.jsonl
```

The browser keeps edits in memory. Use `Export current` or `Export all` to download `*.review.completed.jsonl`; the original packet is never overwritten. To resume, load the exported packet again.

## Review flow

1. Load the packet(s).
2. Use `H`, `N`, `U`, or `X` to assign the human label. `Left` and `Right` navigate occurrences.
3. For `HEADING`, select the exact substring in **Raw source text**, then choose **Use selected text as heading span**.
4. Choose structural type, level/`LEVEL_NOT_REVIEWED`, parent state, reviewer, and optional notes.
5. Use **Validate packet**. Resolve every issue before changing the manifest `reviewStatus` to `REVIEW_COMPLETE` in the exported data.
6. Export the completed JSONL for Phase C2 import.

The workstation does not provide bulk semantic labeling. Historical provenance is displayed as evidence only; it never pre-fills a decision. Parent choices are limited to human-reviewed headings from the same loaded document.
