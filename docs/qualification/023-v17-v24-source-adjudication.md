# Offline source adjudication: V17 and V24

Review date: 2026-10-19. **Recommendation: partition A is correct for both scopes. D's B proposals are false merges; E's structurally grounded rejection agrees with the source.** This is an assistant source-review finding, not user-finalized Strict Gold. No Gold reference was changed and no model/provider call was made.

## Evidence and authority

The source is [023_ND_23-2015_Chung_thuc.pdf](../../todo10_8/heading_corpus_100/01_phap_quy/023_ND_23-2015_Chung_thuc.pdf), SHA256 `762b29b5c61411349d08858bba9eb3be3f417f00dc02a4c4179ca8400ec006a8`. The PDF hash matches the retained OCR evidence. Pages 8 and 11 were inspected visually as full-page rasters, with surrounding body text visible, then compared with the alias-indexed primary OCR. Both raster file hashes match their OCR page records:

| PDF page | Raster SHA256 |
|---|---|
| 8 | 377525861198aaba01515b253dcac40e63e2032c33ea634b523d6ec01755ddaa |
| 11 | 0e160763c00342cf4612deacea1b2115070c936a3123b9275866a77134342e40 |

Local page evidence: [page 8](../../.dhx-evidence/ocr/762b29b5c6141134/326f99521990/pages/page-008.png), [page 11](../../.dhx-evidence/ocr/762b29b5c6141134/326f99521990/pages/page-011.png). No document content or images were sent to another model for this review.

No user-finalized Strict Gold for this exact PDF hash was found in the repository Gold registries examined. `eval/scan-text/023_heading_lines.v1.json` explicitly declares model transcription without human review, references a generated-DOCX hash and is line-text evidence, not a heading-partition authority. The historical `todo10_8/outline_json/023_ND_23-2015_Chung_thuc.json` is marked `insufficient_text` and does not establish these boundaries. Neither was promoted or used as a substitute for source inspection. The [Strict Gold authority policy](../accuracy/accuracy99-strict-gold-authority-policy-v2.md) requires user finalization and exhaustive document review; reviewing two local scopes does not meet that requirement.

## V17: PDF page 8, P008-L013..L018

| Aliases | Visible source content and function |
|---|---|
| L013 | Chương II |
| L014–L015 | The chapter title, ending CHỨNG THỰC CHỮ KÝ; one title wrapped across two lines |
| L016–L017 | Mục 1 and its title CẤP BẢN SAO TỪ SỔ GỐC |
| L018 | Điều 16. Cá nhân, tổ chức có quyền yêu cầu cấp bản sao từ sổ gốc |

The chapter number/title form one centered heading. Below it, Mục 1 and its title form a second centered heading with a separate vertical block. Điều 16 is a distinct article heading, aligned like the following Điều 17 heading, followed immediately by its own numbered body clauses 1, 2 and 3. It introduces a different structural unit from the section title. The decision uses the visible content function, body ownership and neighboring heading structure as well as layout; it is not merely a numbering heuristic.

```text
A — correct:
[[P008-L013,P008-L014,P008-L015],[P008-L016,P008-L017],[P008-L018]]

B — incorrect false merge:
[[P008-L013,P008-L014,P008-L015],[P008-L016,P008-L017,P008-L018]]
```

B incorrectly turns the section heading and the first article heading into one heading. A has three headings: chapter, section, article. This establishes a local boundary judgment only; it does not authorize or test runtime hierarchy placement.

## V24: PDF page 11, P011-L001..L004

| Aliases | Visible source content and function |
|---|---|
| L001–L002 | Mục 3 / CHỨNG THỰC CHỮ KÝ |
| L003–L004 | Điều 23. Trách nhiệm của người yêu cầu chứng thực chữ ký và / người thực hiện chứng thực chữ ký |

Mục 3 and its title form the centered section heading at the top of the page. Điều 23 follows as a separate article heading whose long title wraps onto L004. Its numbered clauses 1 and 2 follow beneath it; Điều 24 begins later as the next article. The section title organizes these articles, while Điều 23 introduces its own body. They are not four lines of one structural heading.

```text
A — correct:
[[P011-L001,P011-L002],[P011-L003,P011-L004]]

B — incorrect false merge:
[[P011-L001,P011-L002,P011-L003,P011-L004]]
```

## Consequences

For these two scopes, D proposed false corrections and E did not falsely reject B. The A↔B exchange was an observable structural disagreement, but the source resolves this local comparison in favor of A. E's counterproposal equalling A was never authority to restore or emit A automatically; the runtime correctly stopped unresolved under the canary rule.

This finding is separate from the demonstrated contract failures on P021, P001 and P009. It supports the minimal production change: reject self-contradictory REGROUP within the verifier's bounded protocol conversation, retain valid different suggestions for audit only, and preserve the grouper repair path, no-progress guard, two-cycle bound and hierarchy fail-closed. No source-specific phrase, numbering rule or hand-coded partition was added to the production algorithm.
