# LEGACY-9 - Final legacy physical-removal readiness

Status: `PASS_REMOVAL_READINESS_AUDIT`

This is an audit-only closure at publication head
`f4db8ee1dea3fa55dfe5f0568dec588044632ab6`. It does not change `src` or
`tests`, delete legacy code, or alter the Round-3 canonical execution
authority at `32bb000343361516078f37da63943e5073b678fe`.

## Census result

The LEGACY-6 executable census is carried forward and re-adjudicated at the
publication head:

| Surface | Result | Classification | Blocker or owner |
| --- | ---: | --- | --- |
| `HeaderExtractionPipeline` executable callers in `src` | 0 | `KEEP_FOR_API_LINEAGE` | Legacy definition, explicit compatibility/evaluation test lineage; owner: pipeline migration |
| `DocxSlimExtractor.ExtractWithSourceFacts` external callers | 0 | `REMOVE_CANDIDATE` | Internal implementation remains behind the current compatibility adapter; owner: OpenXml boundary migration |
| Normal authority Slim callers | 0 | `REMOVE_CANDIDATE` | None on the normal authority route; owner: authority boundary |
| `DocxSlimExtractor.Extract` compatibility callers | 17 | `KEEP_COMPATIBILITY` | Web inspect, CLI audit/PDF/benchmark, repair evidence, replay, and deterministic diagnostics; owners: respective command/workflow owners |
| `SlimCompatibilityBoundary` callers | 5 | `KEEP_COMPATIBILITY` | Authority compatibility context and legacy runtime bridge; owner: OpenXml/authority boundary |
| `SlimDocument` / `SlimParagraph` | nonzero | `KEEP_COMPATIBILITY` | Demotion, compatibility, diagnostic, writeback, and test lineage; owners: demotion and compatibility migration |

The 17 count is an executable caller count, not a file count. Test-only and
historical documentation/evaluation references are retained as separately
classified lineage and are not silently treated as dead code.

## Gates

```text
UNKNOWN = 0
HEADER_EXTRACTION_PIPELINE_EXECUTABLE_CALLERS = 0
EXTRACT_WITH_SOURCE_FACTS_EXTERNAL_CALLERS = 0
NORMAL_AUTHORITY_SLIM_CALLERS = 0
EXTRACT_COMPATIBILITY_CALLERS = 17
SLIM_COMPATIBILITY_BOUNDARY_CALLERS = 5
EVERY_REMAINING_CALLER_CLASSIFIED = true
EVERY_REMAINING_CALLER_HAS_BLOCKER_OR_OWNER = true
HISTORICAL_EVIDENCE_DELETION = false
BENCHMARK_EXPECTED_CHANGED = false
PROVIDER_CALLS = 0
SOURCE_DELTA_FROM_CANONICAL_EXECUTION = 0
TEST_DELTA_FROM_CANONICAL_EXECUTION = 0
```

## Decision

`HeaderExtractionPipeline` has no executable caller on the normal production
route, but it remains part of compatibility/API lineage. The deprecated
`Extract()` surface and `SlimCompatibilityBoundary` are not removable while
their 17 and 5 callers remain. `SlimDocument` and `SlimParagraph` are also
not removal candidates because demotion, compatibility, diagnostic,
writeback, and test contracts still consume them.

Therefore:

```text
LEGACY-9 = PASS_REMOVAL_READINESS_AUDIT
PHYSICAL_SLIM_RETIREMENT_READY = false
DO_NOT_DELETE_LEGACY_BEFORE_ZERO_REFERENCE_PROOF = true
```

No full-suite rerun is required for this audit-only publication. The Round-3
canonical execution remains frozen at `32bb000...`; this publication is a
later documentation/evaluation revision only.
