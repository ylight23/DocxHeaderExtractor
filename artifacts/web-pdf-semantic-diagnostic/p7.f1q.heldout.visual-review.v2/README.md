# P7-F1Q held-out — visual Gold review (DRAFT, not approved)

Each selected page is rendered at 110 DPI with PDFium. Every parser occurrence is drawn as a numbered box: **green** = drafted ESTABLISHES, **blue** = REPRESENTS, **grey** = OTHER, **orange fill** = EXCLUDED (source-corrupted, never scored); an **orange** outline = REVIEW_FOCUS / DECISION_NEEDED, **purple** = row decided by the user. The number in the box is the row number in the document's table, which lists **all** occurrences (not only non-OTHER rows). Boxes come from parser geometry (PDF points, bottom-left origin) and are evidence of what the parser saw, not labels.

Each document page ends with **OTHER rows to check for a missed heading**: OTHER rows that are bold, or larger than the page's most common body size, and short. They come from glyph fonts only. This is a checking aid, not a proposal: no label was changed because of it.

| doc | source | pages | occurrences | E | R | O | excluded | focus | user-decided | OTHER to check |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| [044](044.md) | `044_IDA_Financial_Statements_June_2024.pdf` | 1, 4, 49, 93 | 292 | 11 | 0 | 281 | 0 | 14 | 95 | 46 |
| [049](049.md) | `049_IDA_Financial_Statements_September_2024.pdf` | 1, 29, 39, 52 | 341 | 3 | 0 | 338 | 0 | 13 | 2 | 32 |
| [087](087.md) | `087_ND_53-2022_An_ninh_mang_EN.pdf` | 1, 11, 33, 38 | 134 | 9 | 0 | 124 | 1 | 24 | 12 | 18 |
| [032](032.md) | `032_WB_Plant_TwoStage_2020.pdf` | 1, 14, 29, 165 | 115 | 21 | 13 | 81 | 0 | 10 | 0 | 1 |
