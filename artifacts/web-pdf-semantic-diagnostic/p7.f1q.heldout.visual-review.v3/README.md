# P7-F1Q held-out — visual Gold review (DRAFT, not approved)

Each selected page is rendered at 110 DPI with PDFium. Every parser occurrence is drawn as a numbered box: **green** = drafted ESTABLISHES, **blue** = REPRESENTS, **grey** = OTHER, **orange fill** = EXCLUDED (source-corrupted, never scored); an **orange** outline = REVIEW_FOCUS / DECISION_NEEDED, **purple** = row decided by the user. The number in the box is the row number in the document's table, which lists **all** occurrences (not only non-OTHER rows). Boxes come from parser geometry (PDF points, bottom-left origin) and are evidence of what the parser saw, not labels.

Each document page ends with **OTHER rows to check for a missed heading**: OTHER rows that are bold, or larger than the page's most common body size, and short. They come from glyph fonts only. This is a checking aid, not a proposal: no label was changed because of it.

| doc | source | pages | occurrences | E | R | O | excluded | focus | user-decided | OTHER to check |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| [006](006.md) | `006_Luat_Dat_dai_31-2024-QH15.pdf` | 1, 45, 57 | 89 | 13 | 0 | 76 | 0 | 7 | 0 | 2 |
| [009](009.md) | `009_Luat_Giao_dich_dien_tu_20-2023-QH15.pdf` | 1, 16, 22 | 94 | 9 | 0 | 85 | 0 | 7 | 0 | 2 |
| [015](015.md) | `015_Luat_Cac_to_chuc_tin_dung_32-2024-QH15.pdf` | 1, 44, 58 | 114 | 17 | 0 | 97 | 0 | 8 | 0 | 5 |
| [017](017.md) | `017_ND_123-2020_Hoa_don_chung_tu.pdf` | 1, 38, 39, 45 | 164 | 12 | 0 | 152 | 0 | 33 | 2 | 26 |
| [024](024.md) | `024_ND_15-2020_Xu_phat_BC_VT_CNTT.pdf` | 1, 15, 67 | 140 | 14 | 0 | 126 | 0 | 9 | 0 | 0 |
| [028](028.md) | `028_WB_RFB_Works_Without_Prequal_2017.pdf` | 1, 4, 51, 229 | 113 | 15 | 0 | 98 | 0 | 20 | 0 | 13 |
| [034](034.md) | `034_WB_Plant_Without_Prequal_2016.pdf` | 1, 17, 109, 241 | 88 | 15 | 13 | 60 | 0 | 17 | 1 | 0 |
| [051](051.md) | `051_WBG_Trust_Fund_FIS_June_2024.pdf` | 1, 6, 9, 15 | 66 | 9 | 0 | 57 | 0 | 20 | 1 | 18 |
| [056](056.md) | `056_OpenStax_Business_Law_I_Essentials.pdf` | 1, 7, 20, 106 | 94 | 3 | 33 | 58 | 0 | 6 | 0 | 4 |
| [060](060.md) | `060_Elements_of_Linear_Algebra_Lecture_Notes.pdf` | 1, 2, 21, 44 | 150 | 5 | 30 | 115 | 0 | 11 | 0 | 0 |
| [064](064.md) | `064_Machine_Learning_with_Neural_Networks.pdf` | 1, 7, 30, 191 | 119 | 2 | 39 | 78 | 0 | 9 | 0 | 0 |
| [067](067.md) | `067_Algorithms_for_Massive_Data_Lecture_Notes.pdf` | 1, 2, 21, 119 | 149 | 4 | 39 | 106 | 0 | 5 | 0 | 0 |
| [073](073.md) | `073_FORTIS_GC_Minutes_Mar_2026.pdf` | 1, 2 | 46 | 11 | 0 | 35 | 0 | 12 | 0 | 1 |
| [077](077.md) | `077_ICP_TAG_Minutes_Nov_2023.pdf` | 1, 4, 9 | 108 | 10 | 0 | 98 | 0 | 8 | 0 | 2 |
| [079](079.md) | `079_ICP_TAG_Minutes_Apr_2024.pdf` | 1, 4, 5 | 107 | 9 | 0 | 98 | 0 | 7 | 0 | 2 |
| [083](083.md) | `083_Luat_An_ninh_mang_2018_EN.pdf` | 1, 10, 32 | 87 | 8 | 0 | 78 | 1 | 13 | 1 | 5 |
| [088](088.md) | `088_ND_69-2018_Ngoai_thuong_EN.pdf` | 1, 35, 60, 64 | 151 | 21 | 0 | 130 | 0 | 22 | 0 | 14 |
| [090](090.md) | `090_ND_155-2018_Sua_doi_Bo_Y_te_EN.pdf` | 1, 24, 62 | 93 | 8 | 0 | 85 | 0 | 15 | 0 | 8 |
| [093](093.md) | `093_RFC9112_HTTP_1_1.pdf` | 1, 12, 18, 42 | 149 | 13 | 0 | 136 | 0 | 15 | 0 | 12 |
| [094](094.md) | `094_RFC9113_HTTP_2.pdf` | 1, 2, 16, 63 | 148 | 7 | 24 | 117 | 0 | 12 | 0 | 9 |
