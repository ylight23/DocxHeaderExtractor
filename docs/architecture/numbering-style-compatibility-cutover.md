# LEGACY-4 — Numbering/style compatibility cutover

Status: `PASS` (scoped boundary cutover; Round-3 cumulative regression/full-suite gate remains pending)

Base: `e1f3172bb059eb7eda187f8e70245ce3d7bf28bd`  
Cutover: `e1ee02a5c97bdf4be126220361ef35af1a2e4185`

LEGACY-4 moved the remaining executable numbering/style read in the scoped repair audit to the existing immutable boundary:

```text
SourceDocument
    -> NumberingStyleFeatures
       -> ParagraphNumberingFeatures
       -> ParagraphStyleFeatures
```

`RepairCorpusAudit.StructureSourceAudit` now reads built-in heading style counts from `features.Styles`; it no longer reads `SlimParagraph.HasBuiltInHeadingStyle` for that source-fact counter. The boundary remains source-only and contains no role, score, candidate, guessed-level, or demotion state.

The three ordered operations were intentionally not migrated:

```text
DemoteCoverPageBlock
DemoteInlineEmphasis
DemoteRunsWithoutOwnProse
```

They remain compatibility/demotion-boundary blockers because of ordered mutable semantics. `DocxSlimExtractor`, Slim models, writeback, and legacy deletion were not changed.

Gate result:

```ini
EXECUTABLE_SLIM_NUMBERING_STYLE_READERS_BEFORE = 1
EXECUTABLE_SLIM_NUMBERING_STYLE_READERS_AFTER = 0
NUMBERING_ID_DELTA = 0
NUMBERING_LEVEL_DELTA = 0
NUMBER_LABEL_DELTA = 0
NUMBERING_FORMAT_DELTA = 0
STYLE_ID_DELTA = 0
BUILTIN_HEADING_STYLE_LEVEL_DELTA = 0
OUTLINE_LEVEL_DELTA = 0
STYLE_EMPHASIS_DELTA = 0
POLICY_STATE_LEAKAGE = 0
DEMOTION_STATE_LEAKAGE = 0
EXPECTED_CHANGED = false
LEGACY_DELETED = false
DOCX_SLIM_REMOVED = false
PROVIDER_CALLS = 0
FULL_SUITE_EXECUTED = false
```

Validation: Release build passed with `0` errors. The focused numbering/style, repair, outline, and demotion-boundary suite passed `77/77`. Raw TRX remains local and is not published; its SHA256 is recorded in the JSON summary.

Next task: `LEGACY-5`.
