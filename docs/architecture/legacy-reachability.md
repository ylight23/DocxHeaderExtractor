# Legacy Reachability And Ownership Audit

Status: audit-only. No production, test, route, or provider changes were made.

## Scope

This audit maps component-level reachability after the authority cutover. It is separate from
ARCH-2: it does not reclassify the 11 C2-P failures. It records callers, lifecycle ownership,
cutover residue, dependency direction, and the four existing C1 `LEGACY_ONLY_TEST` rows.

The central normal path is:

`CLI / Web / MCP / AgentHarness -> PipelineDocumentExtractionTool -> AuthorityExtractionPipeline`

The retained compatibility path is:

`CLI repair-key-package or Core.Repair -> HeaderExtractionPipeline`

## Classification Summary

| Class | Count |
| --- | ---: |
| `NORMAL_PRODUCTION` | 9 |
| `LEGACY_RUNTIME` | 2 |
| `COMPATIBILITY_ONLY` | 1 |
| `EVAL_ONLY` | 5 |
| `REPLAY_ONLY` | 3 |
| `TEST_ONLY` | 1 |
| `DEAD` | 0 |
| `MIXED` | 4 |
| `UNKNOWN` | 1 |
| **Total** | **26** |

No component is classified `DEAD`. Absence of a local caller is not sufficient proof when
reflection, configuration, plugin loading, or external invocation may exist.

## Important Findings

`HeaderExtractionPipeline` is `LEGACY_RUNTIME`, not normal production: `dhx repair-key-package`,
the Core Repair workflows, and tests construct it directly, while normal host extraction constructs
`AuthorityExtractionPipeline` through `PipelineDocumentExtractionTool`.

`PdfFirstValidatedFallback` is a nested policy of `HeaderExtractionPipeline`, not a separate
inventory component. It belongs to the `LEGACY_RUNTIME_REPAIR_COMPATIBILITY` lifecycle and is not
a standalone normal authority route. Its runtime reachability comes from the repair-key-package/
Repair callers that still construct that facade, plus their tests/eval paths.

`DocxSlimExtractor` is `MIXED` and `architecturalLegacy=true`, because it remains directly
reachable from the normal authority path as well as review/eval/test paths. The name or folder does
not make it runtime legacy.

`LegacyDocConverter` is `COMPATIBILITY_ONLY` despite its name. Explicit CLI/Web/AgentHarness
compatibility adapters may call it before normalized OOXML enters the canonical pipeline. It is an
infrastructure input adapter, not a legacy authority route.

`PdfLayoutEvidenceOutline` is normal-production reachable through the PDF authority path and is
also exposed by diagnostic CLI commands. `PdfLegacyValidatedOutputPolicy`, hierarchy artifact
evaluation, and shadow comparison are replay/diagnostic surfaces, not production authority.

## C1 Legacy Test Mapping

The four original C1 rows classified `LEGACY_ONLY_TEST` all map to
`PdfTaggedHeadingProbeTests.cs`. The tested `PdfTaggedHeadingProbe` is also called by
`PdfTaggedEvidenceOutline` on the current PDF authority path. This creates a recommendation to
review the C1 classification; this task does not edit C1 or test expectations. After ARCH-2 the
ledger contains 15 `LEGACY_ONLY_TEST` rows total: 4 original mappings plus 11 ARCH-2
reclassified tests. ARCH-2 mappings are reused here conceptually and are not duplicated.

## Cutover Residue

| Residue | Reachable from | Conflicts with current authority | Safe to remove now |
| --- | --- | --- | --- |
| `HeaderExtractionPipeline` compatibility construction | CLI, Repair, tests | Yes | No |
| nested `PdfFirstValidatedFallback` policy | `HeaderExtractionPipeline` | Yes | No |
| `OutlineWriteback` adapter | explicit action tool, tests | No | No |
| `PdfLegacyValidatedOutputPolicy` | hierarchy diagnostic/eval | No | No |

Removal is not justified while caller, migration, evaluation, replay, or compatibility obligations
remain. No code is deleted or renamed in this audit.

## Dependency Direction

Recorded architecture violations are:

- CLI `repair-key-package` and DocumentProcessing Repair reach `HeaderExtractionPipeline`.
- Application orchestration constructs concrete provider implementations.
- `Core` no longer depends on `OpenXmlLayer` types; source/parser implementation is owned by
  DocumentProcessing.

No normal-production-to-eval edge was found in the audited static call sites. The normal path's
use of `LegacyDocConverter` is retained only in explicit input-compatibility adapters before the
canonical pipeline; it is not counted as a legacy authority route.

## Lifecycle Decisions

Keep current authority components and diagnostic/replay artifacts. Move `DocxSlimExtractor` behind
a source port over time. Deprecate `HeaderExtractionPipeline`, `OutlineWriteback`, and the
historical tagged-route fixture only after migration and dependency obligations are closed.
No `REMOVE_CANDIDATE` is justified by this audit.

Machine-readable component evidence is in `eval/architecture/legacy-reachability.v1.json`.

`ORIGINAL_LEGACY_ONLY_TESTS_MAPPED = 4`

`POST_ARCH2_LEGACY_ONLY_TESTS_TOTAL = 15`

`ARCH2_RECLASSIFIED_TESTS = 11`

`ARCH2_TESTS_REUSED_NOT_REDUPLICATED = true`

`PROVIDER_CALLS = 0`

`PRODUCTION_CODE_CHANGED = false`

`TEST_EXPECTATIONS_CHANGED = false`
