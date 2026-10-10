# P7-F1Q held-out — Gold Visual Review Workspace (DRAFT, not approved)

Per document: `<id>.review.html` (self-contained, offline: zoom, every occurrence incl. OTHER, edit with reason, APPROVE, export JSON/commands) and `<id>.review.pdf` (annotated pages + full table). Review order: 044, 049, 087, 032, then the rest. Send back the exported `<id>.gold-review-decisions.json` or the commands (`APPROVE <id>`, `<id> <alias> -> E|R|O because …`). No Gold is approved and the provider stays locked until all 24 documents are approved.

**Privacy.** The repository is public. The `.review.html` / `.review.pdf` files embed 150 DPI page renders, so they are **not committed** (`.gitignore`). They live in the local worktree (this folder) and in the private store `~/.codex/diagnostic-captures/p7-f1q-heldout-private-20261010/gold-workspace.v1/`. `workspace-manifest.json` pins the SHA-256 of every file and of the draft it was built from. Regenerate byte-for-byte with:

    dotnet run --project scripts/P7F1QGoldWorkspace -c Release -- artifacts/web-pdf-semantic-diagnostic/p7.f1q.heldout.gold-drafts.v4 artifacts/web-pdf-semantic-diagnostic/p7.f1q.heldout.review-bundles.v1 artifacts/web-pdf-semantic-diagnostic/p7.f1q.heldout.gold-workspace.v1

| order | doc | source | pages | occurrences | E | R | O | excluded | flagged | HTML | PDF |
|---:|---|---|---:|---:|---:|---:|---:|---:|---:|---|---|
| 1 | 044 | `044_IDA_Financial_Statements_June_2024.pdf` | 4 | 292 | 11 | 0 | 281 | 0 | 109 | [044.review.html](044.review.html) | [044.review.pdf](044.review.pdf) |
| 2 | 049 | `049_IDA_Financial_Statements_September_2024.pdf` | 4 | 341 | 3 | 0 | 338 | 0 | 15 | [049.review.html](049.review.html) | [049.review.pdf](049.review.pdf) |
| 3 | 087 | `087_ND_53-2022_An_ninh_mang_EN.pdf` | 4 | 134 | 9 | 0 | 124 | 1 | 36 | [087.review.html](087.review.html) | [087.review.pdf](087.review.pdf) |
| 4 | 032 | `032_WB_Plant_TwoStage_2020.pdf` | 4 | 115 | 21 | 13 | 81 | 0 | 10 | [032.review.html](032.review.html) | [032.review.pdf](032.review.pdf) |
| 5 | 006 | `006_Luat_Dat_dai_31-2024-QH15.pdf` | 3 | 89 | 13 | 0 | 76 | 0 | 7 | [006.review.html](006.review.html) | [006.review.pdf](006.review.pdf) |
| 6 | 009 | `009_Luat_Giao_dich_dien_tu_20-2023-QH15.pdf` | 3 | 94 | 9 | 0 | 85 | 0 | 7 | [009.review.html](009.review.html) | [009.review.pdf](009.review.pdf) |
| 7 | 015 | `015_Luat_Cac_to_chuc_tin_dung_32-2024-QH15.pdf` | 3 | 114 | 17 | 0 | 97 | 0 | 8 | [015.review.html](015.review.html) | [015.review.pdf](015.review.pdf) |
| 8 | 017 | `017_ND_123-2020_Hoa_don_chung_tu.pdf` | 4 | 164 | 12 | 0 | 152 | 0 | 35 | [017.review.html](017.review.html) | [017.review.pdf](017.review.pdf) |
| 9 | 024 | `024_ND_15-2020_Xu_phat_BC_VT_CNTT.pdf` | 3 | 140 | 14 | 0 | 126 | 0 | 9 | [024.review.html](024.review.html) | [024.review.pdf](024.review.pdf) |
| 10 | 028 | `028_WB_RFB_Works_Without_Prequal_2017.pdf` | 4 | 113 | 15 | 0 | 98 | 0 | 20 | [028.review.html](028.review.html) | [028.review.pdf](028.review.pdf) |
| 11 | 034 | `034_WB_Plant_Without_Prequal_2016.pdf` | 4 | 88 | 15 | 13 | 60 | 0 | 18 | [034.review.html](034.review.html) | [034.review.pdf](034.review.pdf) |
| 12 | 051 | `051_WBG_Trust_Fund_FIS_June_2024.pdf` | 4 | 66 | 10 | 0 | 56 | 0 | 21 | [051.review.html](051.review.html) | [051.review.pdf](051.review.pdf) |
| 13 | 056 | `056_OpenStax_Business_Law_I_Essentials.pdf` | 4 | 94 | 3 | 33 | 58 | 0 | 6 | [056.review.html](056.review.html) | [056.review.pdf](056.review.pdf) |
| 14 | 060 | `060_Elements_of_Linear_Algebra_Lecture_Notes.pdf` | 4 | 150 | 5 | 30 | 115 | 0 | 11 | [060.review.html](060.review.html) | [060.review.pdf](060.review.pdf) |
| 15 | 064 | `064_Machine_Learning_with_Neural_Networks.pdf` | 4 | 119 | 2 | 39 | 78 | 0 | 9 | [064.review.html](064.review.html) | [064.review.pdf](064.review.pdf) |
| 16 | 067 | `067_Algorithms_for_Massive_Data_Lecture_Notes.pdf` | 4 | 149 | 4 | 39 | 106 | 0 | 5 | [067.review.html](067.review.html) | [067.review.pdf](067.review.pdf) |
| 17 | 073 | `073_FORTIS_GC_Minutes_Mar_2026.pdf` | 2 | 46 | 11 | 0 | 35 | 0 | 12 | [073.review.html](073.review.html) | [073.review.pdf](073.review.pdf) |
| 18 | 077 | `077_ICP_TAG_Minutes_Nov_2023.pdf` | 3 | 108 | 10 | 0 | 98 | 0 | 8 | [077.review.html](077.review.html) | [077.review.pdf](077.review.pdf) |
| 19 | 079 | `079_ICP_TAG_Minutes_Apr_2024.pdf` | 3 | 107 | 9 | 0 | 98 | 0 | 7 | [079.review.html](079.review.html) | [079.review.pdf](079.review.pdf) |
| 20 | 083 | `083_Luat_An_ninh_mang_2018_EN.pdf` | 3 | 87 | 8 | 0 | 78 | 1 | 14 | [083.review.html](083.review.html) | [083.review.pdf](083.review.pdf) |
| 21 | 088 | `088_ND_69-2018_Ngoai_thuong_EN.pdf` | 4 | 151 | 21 | 0 | 130 | 0 | 22 | [088.review.html](088.review.html) | [088.review.pdf](088.review.pdf) |
| 22 | 090 | `090_ND_155-2018_Sua_doi_Bo_Y_te_EN.pdf` | 3 | 93 | 8 | 0 | 85 | 0 | 15 | [090.review.html](090.review.html) | [090.review.pdf](090.review.pdf) |
| 23 | 093 | `093_RFC9112_HTTP_1_1.pdf` | 4 | 149 | 13 | 0 | 136 | 0 | 15 | [093.review.html](093.review.html) | [093.review.pdf](093.review.pdf) |
| 24 | 094 | `094_RFC9113_HTTP_2.pdf` | 4 | 148 | 7 | 24 | 117 | 0 | 12 | [094.review.html](094.review.html) | [094.review.pdf](094.review.pdf) |

Totals: 3151 occurrences on 86 pages — E 250 · R 191 · O 2708 · excluded 2.
