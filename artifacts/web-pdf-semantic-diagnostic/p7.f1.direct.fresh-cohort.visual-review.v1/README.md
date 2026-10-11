# P7-F1Q held-out — visual Gold review (DRAFT, not approved)

Each selected page is rendered at 110 DPI with PDFium. Every parser occurrence is drawn as a numbered box: **green** = drafted ESTABLISHES, **blue** = REPRESENTS, **grey** = OTHER, **orange fill** = EXCLUDED (source-corrupted, never scored); an **orange** outline = REVIEW_FOCUS / DECISION_NEEDED, **purple** = row decided by the user. The number in the box is the row number in the document's table, which lists **all** occurrences (not only non-OTHER rows). Boxes come from parser geometry (PDF points, bottom-left origin) and are evidence of what the parser saw, not labels.

Each document page ends with **OTHER rows to check for a missed heading**: OTHER rows that are bold, or larger than the page's most common body size, and short. They come from glyph fonts only. This is a checking aid, not a proposal: no label was changed because of it.

| doc | source | pages | occurrences | E | R | O | excluded | focus | user-decided | OTHER to check |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| [075](075.md) | `075_FORTIS_GC_Minutes_Nov21_2024.pdf` | 1, 3, 4 | 84 | 14 | 0 | 70 | 0 | 16 | 0 | 3 |
| [078](078.md) | `078_ICP_IACG07_Minutes_May_2023.pdf` | 1, 6, 8 | 106 | 10 | 0 | 96 | 0 | 2 | 0 | 2 |
| [071](071.md) | `071_ICP_IACG_Minutes_Oct_2025.pdf` | 1, 7, 25 | 102 | 6 | 0 | 96 | 0 | 2 | 0 | 2 |
| [003](003.md) | `003_Luat_Doanh_nghiep_59-2020-QH14.pdf` | 1, 18, 79 | 112 | 13 | 0 | 99 | 0 | 0 | 0 | 5 |
| [007](007.md) | `007_Luat_Nha_o_27-2023-QH15.pdf` | 1, 13, 49 | 102 | 7 | 0 | 95 | 0 | 0 | 0 | 2 |
| [008](008.md) | `008_Luat_Kinh_doanh_BDS_29-2023-QH15.pdf` | 1, 35, 36 | 96 | 15 | 0 | 81 | 0 | 0 | 0 | 2 |
| [010](010.md) | `010_Luat_An_ninh_mang_24-2018-QH14.pdf` | 1, 5, 26 | 94 | 10 | 0 | 84 | 0 | 0 | 0 | 2 |
| [019](019.md) | `019_TT_200-2014_Che_do_ke_toan_DN.pdf` | 1, 226, 428, 437 | 233 | 16 | 0 | 217 | 0 | 7 | 0 | 11 |
| [021](021.md) | `021_TT_78-2021_Hoa_don_dien_tu.pdf` | 1, 14, 20 | 13 | 0 | 0 | 13 | 0 | 0 | 0 | 1 |
| [048](048.md) | `048_IBRD_Financial_Statements_March_2025.pdf` | 1, 7, 19, 60 | 263 | 6 | 0 | 257 | 0 | 4 | 0 | 26 |
| [045](045.md) | `045_IBRD_Financial_Statements_June_2023.pdf` | 1, 36, 64, 84 | 271 | 8 | 0 | 263 | 0 | 1 | 0 | 18 |
| [046](046.md) | `046_IBRD_Financial_Statements_September_2024.pdf` | 1, 3, 36, 49 | 216 | 10 | 13 | 193 | 0 | 0 | 0 | 22 |
| [050](050.md) | `050_IDA_Financial_Statements_March_2025.pdf` | 1, 7, 33, 38 | 269 | 11 | 0 | 258 | 0 | 6 | 0 | 16 |
| [053](053.md) | `053_IDA_Information_Statement_FY25.pdf` | 1, 7, 32, 123 | 410 | 4 | 0 | 406 | 0 | 1 | 0 | 44 |
| [055](055.md) | `055_IDA_External_Review_FY25.pdf` | 1, 28, 107, 119 | 197 | 3 | 0 | 194 | 0 | 3 | 0 | 50 |
| [062](062.md) | `062_Lectures_on_Probability_Theory.pdf` | 1, 5, 52, 141 | 156 | 4 | 42 | 110 | 0 | 0 | 0 | 0 |
| [063](063.md) | `063_Advanced_Linear_Algebra.pdf` | 1, 21, 176, 387 | 115 | 3 | 0 | 112 | 0 | 0 | 0 | 0 |
| [068](068.md) | `068_SDEs_for_Finance_Course_Notes.pdf` | 1, 2, 27, 90 | 132 | 4 | 28 | 100 | 0 | 0 | 0 | 0 |
| [070](070.md) | `070_Lectures_on_Optimization_Yale.pdf` | 1, 2, 30, 143 | 155 | 3 | 44 | 108 | 0 | 0 | 0 | 22 |
| [081](081.md) | `081_Luat_Doanh_nghiep_2020_EN.pdf` | 1, 58, 140 | 101 | 12 | 0 | 89 | 0 | 3 | 0 | 3 |
| [085](085.md) | `085_Luat_Nha_o_2023_EN.pdf` | 1, 50, 76 | 111 | 16 | 0 | 95 | 0 | 4 | 0 | 6 |
| [086](086.md) | `086_Luat_Dau_tu_2020_EN_alt.pdf` | 1, 21, 46 | 76 | 12 | 0 | 64 | 0 | 3 | 0 | 4 |
| [091](091.md) | `091_RFC9110_HTTP_Semantics.pdf` | 1, 12, 76, 116 | 147 | 10 | 20 | 117 | 0 | 1 | 0 | 10 |
