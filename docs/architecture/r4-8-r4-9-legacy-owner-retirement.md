# R4-8/R4-9 Legacy Owner Retirement

## Result

`R4-8/R4-9 = PASS` for the legacy owner and compatibility boundary retirement.

The normal authority path remains `AuthorityExtractionPipeline`. The deleted
`HeaderExtractionPipeline` is not replaced by a new compatibility boundary.

## Revision authority

- Base revision: `0a13116352312f37903ad9667199d4e905328411`
- Execution revision: `833ee6e4336a3c753df9e28d93d96f9c31b3eb5d`
- Publication revision: assigned by the artifact commit

## Structural gate

- `HeaderExtractionPipeline` callers: `0`
- `HeaderExtractionPipeline` type: deleted
- `SlimCompatibilityBoundary` callers: `0`
- `SlimCompatibilityBoundary` type: deleted
- `SlimCompatibilityContext` references: `0` in source
- `AuthoritySourceExtractionResult` references: `0` in source
- `ForLegacyCompatibility` references: `0` in source
- `DocxSlimExtractor.ExtractForAuthority`: deleted
- `PipelineOptions`: preserved in `Pipeline/PipelineOptions.cs`
- `InferenceBackend`: preserved in `Pipeline/PipelineOptions.cs`

The remaining `DocxSlimExtractor`, `SlimDocument`, `SlimParagraph`, and
legacy `Extract`/`ExtractWithSourceFacts` APIs are intentionally retained for
the later physical Slim retirement milestone.

## Validation

- Release solution build: PASS, `0` errors
- Focused retirement/native authority tests: PASS, `56/56`
- Provider calls: `0`
- Expected behavior changes: `false`
- Raw runtime artifacts tracked: `0`
- Full suite: not run; scheduled after R4-10 physical removal

The removed test files were owner-only route/orchestration contracts that
asserted `HeaderExtractionPipeline` behavior. Native producer, authority,
compatibility-isolation, and low-level evidence tests remain in the focused
gate. No production fallback was added to preserve those retired routes.

## Next milestone

Proceed to R4-10 physical removal of the remaining Slim family only after all
executable compatibility consumers have been independently re-censused.
