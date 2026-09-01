# Accuracy-99 human adjudication guide

Edit the five `eval/accuracy99/adjudication/development/*.review.jsonl` files source-first. The first line is an immutable manifest; every later line is one parser-owned occurrence. Do not remove, duplicate, reorder, or edit source identity/text fields.

- `HEADING`: the occurrence contains a heading. Set the exact zero-based, end-exclusive `headingStart`/`headingEnd`, copy the exact substring into `headingText`, set `structuralType`, and record level/parent review status.
- `NON_HEADING`: reviewed source content that is not a heading.
- `UNCERTAIN`: source evidence is insufficient for a defensible semantic decision.
- `EXCLUDED`: occurrence is outside the benchmark's eligible source universe for an explicit protocol reason.

For `HEADING`, coordinates may cover only part of `rawSourceText`. Never normalize text when selecting the span. Set `levelReviewStatus` to `REVIEWED` with a positive `level`, or `LEVEL_NOT_REVIEWED` with `level=null`. Set `parentReviewStatus` to `ROOT`, `PARENT_REVIEWED`, or `PARENT_UNKNOWN`; `PARENT_REVIEWED` must use another heading's deterministic `goldHeadingId` in the same document.

Every reviewed row requires `reviewer`. Non-heading labels must leave every heading, level, parent, and gold-heading field null. Historical provenance is evidence only and does not pre-fill the human decision. Production predictions are intentionally absent.

After all rows are complete, change the manifest `reviewStatus` to `REVIEW_COMPLETE` and run the importer with `--import-reviews=<directory>`. A discrepancy pass may preserve `initialAdjudicatedLabel`, then set `finalAdjudicatedLabel` plus `resolutionReason`; it must never overwrite the initial label silently.

`--refresh-review-packets` is only for regenerating untouched blank packets. The runner refuses to refresh any packet containing human input. A frozen `development-gold.v1.json` is immutable; corrections require a new explicit dataset version.

## Local workstation

For practical review, open `eval/accuracy99/adjudication/workstation/index.html` in a modern browser. Load one or more `*.review.jsonl` packets; the workstation keeps edits in memory and never overwrites the source packet.

1. Use `H`, `N`, `U`, or `X` to classify the current occurrence. Use the arrow keys to move.
2. For `HEADING`, select the exact substring in the raw source panel and apply it as the heading span.
3. Set structural type, level state, parent state, reviewer, and optional notes.
4. Use the filters and progress counters to find unreviewed or special-provenance rows; filters only change navigation and never remove rows from the packet.
5. Run **Validate packet**, resolve every issue, then mark `REVIEW_COMPLETE` and export the completed JSONL.
6. Return the exported files for Phase C2 import. Do not create or infer labels for rows that have not been reviewed.
