# LEGACY-1 — Exact Legacy Reachability Census

Status: `PASS_WITH_LEGACY_REMAINING` at `main@e9b4c61a7fe7c34419352812f5d1216d06d81736`.

This is an audit-only census. It did not change production code, tests, expected values, routes, or historical evidence. It did not call a provider or execute the full suite.

## Result

The repository-wide static scan covered `src`, `tests`, `eval`, and `docs`, excluding generated `bin`, `obj`, and `TestResults` roots. It matched 247 files and 1,284 lines for the legacy symbol set. Definitions, executable call-sites, adapters, tests, replay/evaluation artifacts, and documentation references were recorded separately.

`UNKNOWN = 0`.

No local reflection, `Activator`, assembly-load, plugin, or string-based dynamic call-site for the named legacy symbols was found. External invocation risk is recorded as a limitation of static analysis, not promoted to an `UNKNOWN` repository reference.

## Reachability map

The current normal path is:

```text
CLI / Web / MCP / AgentHarness
  -> PipelineDocumentExtractionTool
  -> AuthorityExtractionPipeline
```

The retained legacy paths are:

```text
dhx repair-key-package -> HeaderExtractionPipeline
Core.Repair workflows  -> HeaderExtractionPipeline
review/inspect/eval    -> DocxSlimExtractor and compatibility adapters
tests                  -> legacy pipeline and Slim model fixtures
```

`DocxSlimExtractor` and the Slim models are not classified as purely dead or purely normal: the census records their normal-authority, compatibility, repair, evaluation, replay, and test roles separately. Their definitions therefore do not by themselves imply that all normal production authority is legacy.

## Classification summary

| Classification | Census records |
|---|---:|
| `NORMAL_PRODUCTION` | 1 |
| `COMPATIBILITY_RUNTIME` | 9 |
| `REPAIR_RUNTIME` | 2 |
| `EVAL_ONLY` | 0 |
| `REPLAY_ONLY` | 1 |
| `TEST_ONLY` | 1 |
| `DEAD` | 0 |
| `DOCUMENTATION_ONLY` | 0 |
| `UNKNOWN` | 0 |

The detailed machine-readable records are in `eval/architecture/final-legacy-reachability.v1.json`.

## Removal dependency map

| Component | Replacement | Blocking work | Candidate next task |
|---|---|---|---|
| `HeaderExtractionPipeline` | `AuthorityExtractionPipeline` | repair cutover, eval/replay separation, test lineage | `LEGACY-2` |
| `DocxSlimExtractor` and Slim models | `SourceDocument` + `ValidatedStructure` + explicit ports | source boundary, ordered demotion contract, output adapter cutover | `LEGACY-2` |

No component is a removal candidate from this census. Legacy references remain intentionally present and must be migrated or retired with behavior and test lineage preserved before deletion.

## Gate

```ini
UNKNOWN = 0
all legacy definitions inventoried = true
all production call sites classified = true
all repair call sites classified = true
all compatibility call sites classified = true
all eval/replay/test references classified = true
migration performed = false
legacy deleted = false
expected changed = false
provider calls = 0
full suite executed = false
```

`LEGACY-1 = PASS`. The next scoped task is `LEGACY-2`, beginning with behavior-neutral repair workflow cutover.
