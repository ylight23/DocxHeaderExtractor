# LEGACY-6 — Legacy runtime facade reachability closure

Status: `PASS_SCOPED_WITH_COMPATIBILITY_BLOCKERS`

LEGACY-6 closes executable reachability of the historical `HeaderExtractionPipeline` facade from the normal authority route. It does not remove the facade, Slim types, deprecated APIs, compatibility adapters, tests, or historical evidence.

## Result

- `HeaderExtractionPipeline` executable callers in `src`: `0` before and after.
- External executable callers of `ExtractWithSourceFacts`: `0`.
- Normal authority Slim API callers: `0`.
- Unknown reachability: `0`.
- `Extract()` compatibility/diagnostic callers retained: `17`.
- Behavior unexpected delta: `0`.
- Provider calls: `0`.
- Full suite: not run by design.

The remaining `Extract()` callers are classified, not deleted: Web inspect, CLI audit/PDF/benchmark probes, repair evidence, replay evidence, and deterministic PDF diagnostics. They still consume Slim-shaped compatibility output and are blockers for physical retirement, but are not normal authority callers.

## Cutover

`RepairGateCalibration` now reads source facts and candidate indexes through `AuthorityEvaluationSourceReader`. This removes its direct Slim extraction dependency without changing expected values or the normal production route.

`SlimCompatibilityBoundary` remains an internal compatibility boundary. `SlimDocument` and `SlimParagraph` remain valid compatibility/test lineage and are intentionally outside this task.

## Verification

Base: `c60ced1967f89b11c080146624705aab3d8341a6`

Cutover commit: `b719d9f` (`refactor: route repair calibration through source boundary`)

Release build passed with `0` errors. Focused tests passed `30/30`; raw TRX remains local only. Its SHA-256 is recorded in the JSON artifact.

## Decision

`LEGACY-9` physical removal is not authorized. The next task is the Round-3 cumulative regression/full-suite gate. Only after that gate should the remaining compatibility blockers be reconsidered.
