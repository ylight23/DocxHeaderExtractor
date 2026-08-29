# Round-3 Backlog Opening

Round-3 is opened from the verified Round-2 `main` baseline:

```text
main@e9b4c61a7fe7c34419352812f5d1216d06d81736
```

Round-2 is closed. The first Round-3 task is:

## LEGACY-1 — Exact legacy reachability census

Inventory production, tests, eval, replay, repair, output paths, and deprecated public APIs for references to:

- `HeaderExtractionPipeline`
- `DocxSlimExtractor`
- `SlimDocument` / `SlimParagraph`
- deprecated `Extract()` / `ExtractWithSourceFacts()` APIs
- `SlimCompatibilityBoundary` and related compatibility context
- legacy repair, eval, replay, and output paths

Each reference must be classified as `NORMAL_PRODUCTION`, `COMPATIBILITY_RUNTIME`, `REPAIR_RUNTIME`, `EVAL_ONLY`, `REPLAY_ONLY`, `TEST_ONLY`, `DEAD`, or `UNKNOWN`. The gate is `UNKNOWN = 0`.

LEGACY-1 is an audit only. It must not migrate production callers, delete legacy code, change benchmark expectations, or remove historical evidence. Its required outputs are:

```text
eval/architecture/final-legacy-reachability.v1.json
docs/architecture/final-legacy-reachability.md
```

Subsequent migration/removal tasks remain blocked until this census identifies replacement pipelines and blockers. No full-suite run is required merely to open this backlog.

Status: `ROUND-3 = OPEN`, `LEGACY-1 = READY`.
