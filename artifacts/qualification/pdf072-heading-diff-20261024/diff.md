# Diff từng heading PDF072

Bản cũ: full-page15 ngày 23/10, 42 heading. Bản đang mở: adaptive-production ngày 24/10, 41 heading. Lượt thử mới nhất: source-bound + chain-repair, 40 heading, chưa promote. Hai so sánh được tách riêng.

Đối chiếu theo alias nguồn, không theo số thứ tự. `∅` = không có heading riêng trong file; `—` = chưa có level. Parent là đề xuất của model; phải đọc cùng placement. `unresolved` chưa tạo quan hệ cha–con hợp lệ. Diff mô tả khác biệt, không chấm độ chính xác hierarchy vì Gold không có capability đó.

## 1. Bản cũ → file adaptive đang mở

42 → 41 heading; 4 heading giữ nguyên hoàn toàn. Trong các alias chung: 2 đổi nội dung, 2 đổi nhóm source, 32 đổi level, 12 đổi parent đề xuất, 15 đổi trạng thái placement. Các số đếm có thể trùng một heading.

| Alias | Trang | Heading: cũ → sau | Thay đổi | Level | Parent đề xuất | Placement |
|---|---:|---|---|---|---|---|
| L0000:S0 | 1 | MINUTES OF THE INTERNATIONAL COMPARISON PROGRAM TECHNICAL ADVISORY GROUP MARCH 7-8, 2025 New York - Hybrid | giữ nguyên | 1 | ROOT | model-root |
| L0004:S0 | 1 | Session I: Welcome and meeting objectives | level, parent đề xuất, placement | 2 → 1 | L0000:S0 → ROOT | model-parent-relation → model-root |
| L0025:S0 | 1 | Session II: Update on the ICP 2021 Cycle | level, parent đề xuất, placement | 2 → 1 | L0000:S0 → ROOT | model-parent-relation → model-root |
| L0026:S0 | 1 | 1. Global office update | level | 3 → 2 | L0025:S0 | model-parent-relation |
| L0078:S0 | 3 | 2. Regional updates | level | 3 → 2 | L0025:S0 | model-parent-relation |
| L0081:S0 | 3 | Africa | level | 4 → 3 | L0078:S0 | model-parent-relation |
| L0089:S0 | 3 | Asia and the Pacific | level | 4 → 3 | L0078:S0 | model-parent-relation |
| L0107:S0 | 3 | Commonwealth of Independent States (CIS) | level | 4 → 3 | L0078:S0 | model-parent-relation |
| L0123:S0 | 4 | Eurostat–OECD PPP Program | level | 4 → 3 | L0078:S0 | model-parent-relation |
| L0137:S0 | 4 | Latin America and the Caribbean | level | 4 → 3 | L0078:S0 | model-parent-relation |
| L0150:S0 | 4 | Western Asia | level | 4 → 3 | L0078:S0 | model-parent-relation |
| L0165:S0 | 5 | Session III: Short- and Long-Term Research and Development Agenda | level, parent đề xuất, placement | 2 → 1 | L0000:S0 → ROOT | model-parent-relation → model-root |
| L0166:S0 | 5 | 1. Research Topics Emerging from the Previous ICP Cycles | level | 3 → 2 | L0165:S0 | model-parent-relation |
| L0219:S0 | 6 | 2. Forthcoming Research Topics | level | 3 → 2 | L0165:S0 | model-parent-relation |
| L0271:S0 | 7 | Session IV: TAG Functioning and Terms of Reference for Task Forces | level, parent đề xuất, placement | 2 → 1 | L0000:S0 → ROOT | model-parent-relation → model-root |
| L0272:S0 | 7 | 1. TAG Composition and Terms of Reference | level | 3 → 2 | L0271:S0 | model-parent-relation |
| L0288:S0 | 7 | 2. Terms of Reference for a Task Force on Annual PPP Production | level | 3 → 2 | L0271:S0 | model-parent-relation |
| L0314:S0 | 8 | Cũ: ∅<br>Sau: annual PPPs with an emphasis on operational aspects and a strong involvement from NSOs. | mới được chọn | ∅ → 3 | ∅ → L0288:S0 | ∅ → model-parent-relation |
| L0315:S0 | 8 | Cũ: 3. Terms of Reference for a Task Force on ICP Classification Update<br>Sau: ∅ | không còn được chọn | 3 → ∅ | L0271:S0 → ∅ | model-parent-relation → ∅ |
| L0333:S0 | 8 | Cũ: ∅<br>Sau: beneficial. | mới được chọn | ∅ → 3 | ∅ → L0288:S0 | ∅ → model-parent-relation |
| L0334:S0 | 8 | Session V: Current Research | level, parent đề xuất, placement | 2 → 1 | L0000:S0 → ROOT | model-parent-relation → model-root |
| L0335:S0 | 8 | Cũ: 1. The Treatment of Import and Export Prices in International Comparisons<br>Sau: ∅ | không còn được chọn | 3 → ∅ | L0334:S0 → ∅ | model-parent-relation → ∅ |
| L0359:S0 | 9 | 2. A Survey Based Approach to Adjustment for Quality Differences in Services in International Price Comparisons | level | 3 → 2 | L0334:S0 | model-parent-relation |
| L0377:S0 | 9 | 3. Treatment of Scanner (Transaction) Data in the European Comparison Programme (ECP) | level | 3 → 2 | L0334:S0 | model-parent-relation |
| L0400:S0 | 10 | 4. Treatment of Negative Expenditures in ICP | level | 3 → 2 | L0334:S0 | model-parent-relation |
| L0420:S0 | 10 | 5. Fine Tuning Global Linking Procedures | level | 3 → 2 | L0334:S0 | model-parent-relation |
| L0470:S0 | 12 | 6. Improving Reliability of Price Comparisons | level | 3 → 2 | L0334:S0 | model-parent-relation |
| L0507:S0 | 12 | Session VI: Any Other Business | level, parent đề xuất, placement | 2 → 1 | L0000:S0 → ROOT | model-parent-relation → model-root |
| L0513:S0 | 13 | Cũ: International Comparison Program (ICP) TECHNICAL ADVISORY GROUP (TAG) Hybrid Meeting March 7-8, 2025 Bamboo Room, Ease 1345 1345 6th Ave, New York, NY 10105 Agenda<br>Sau: International Comparison Program (ICP) TECHNICAL ADVISORY GROUP (TAG) | nội dung, nhóm source | — | NONE | model-out-of-hierarchy |
| L0519:S0 | 13 | Cũ: ∅<br>Sau: Agenda | tách thành heading riêng từ L0513:S0 | ∅ → — | ∅ → L0513:S0 | ∅ → unresolved |
| L0520:S0 | 13 | DAY 1: Friday, March 7, 2025 &#124; Chair: Paul SCHREYER | parent đề xuất | — | L0513:S0 → L0519:S0 | unresolved |
| L0521:S0 | 13 | SESSION I: Welcome and Opening Remarks | giữ nguyên | — | L0520:S0 | unresolved |
| L0523:S0 | 13 | SESSION II: Update on the ICP 2021 and 2024 Cycles | giữ nguyên | — | L0520:S0 | unresolved |
| L0526:S0 | 13 | SESSION III: Short- and Long-Term Research and Development Agenda | giữ nguyên | — | L0520:S0 | unresolved |
| L0531:S0 | 13 | SESSION IV: TAG Functioning and Terms of Refence for Task Forces | level, parent đề xuất, placement | — → 1 | L0520:S0 → ROOT | unresolved → model-root |
| L0536:S0 | 14 | SESSION V: Current Research | level, parent đề xuất, placement | — → 1 | L0520:S0 → ROOT | unresolved → model-root |
| L0539:S0 | 14 | Cũ: DAY 2: Saturday, March 8, 2025 &#124; Chair: Prasada RAO<br>Sau: DAY 2: Saturday, March 8, 2025 &#124; Chair: Prasada RAO SESSION V: Current Research (Cont’d) | nội dung, nhóm source, level, parent đề xuất, placement | — → 1 | L0513:S0 → ROOT | unresolved → model-root |
| L0540:S0 | 14 | Cũ: SESSION V: Current Research (Cont’d)<br>Sau: ∅ | gộp vào heading khác L0539:S0 | — → ∅ | L0539:S0 → ∅ | unresolved → ∅ |
| L0550:S0 | 14 | Cũ: Improving Reliability of Price Comparisons<br>Sau: ∅ | không còn được chọn | — → ∅ | L0540:S0 → ∅ | unresolved → ∅ |
| L0553:S0 | 14 | SESSION VI: Closing | level, parent đề xuất, placement | — → 1 | L0539:S0 → ROOT | unresolved → model-root |
| L0556:S0 | 15 | Annex 2: List of Participants | level, parent đề xuất, placement | — → 1 | NONE → ROOT | model-out-of-hierarchy → model-root |
| L0557:S0 | 15 | ICP Technical Advisory Group members | level, placement | — → 2 | L0556:S0 | unresolved → model-parent-relation |
| L0566:S0 | 15 | ICP experts, guest speakers, and observers | level, placement | — → 2 | L0556:S0 | unresolved → model-parent-relation |
| L0576:S0 | 15 | ICP Inter-Agency Coordination Group (IACG) | level, placement | — → 2 | L0556:S0 | unresolved → model-parent-relation |
| L0587:S0 | 15 | World Bank | level, placement | — → 3 | L0576:S0 | unresolved → model-parent-relation |

### Những thay đổi về lựa chọn và grouping

- `L0314:S0`: source parts cũ ∅ → sau ['L0314:S0']. mới được chọn.
- `L0315:S0`: source parts cũ ['L0315:S0'] → sau ∅. không còn được chọn.
- `L0333:S0`: source parts cũ ∅ → sau ['L0333:S0']. mới được chọn.
- `L0335:S0`: source parts cũ ['L0335:S0'] → sau ∅. không còn được chọn.
- `L0513:S0`: source parts cũ ['L0513:S0', 'L0514:S0', 'L0515:S0', 'L0516:S0', 'L0517:S0', 'L0518:S0', 'L0519:S0'] → sau ['L0513:S0', 'L0514:S0']. nội dung, nhóm source.
- `L0519:S0`: source parts cũ ∅ → sau ['L0519:S0']. tách thành heading riêng.
- `L0539:S0`: source parts cũ ['L0539:S0'] → sau ['L0539:S0', 'L0540:S0']. nội dung, nhóm source, level, parent đề xuất, placement.
- `L0540:S0`: source parts cũ ['L0540:S0'] → sau ∅. gộp vào heading khác.
- `L0550:S0`: source parts cũ ['L0550:S0'] → sau ∅. không còn được chọn.

## 2. Bản cũ → lượt thử mới nhất, chưa promote

42 → 40 heading; 18 heading giữ nguyên hoàn toàn. Trong các alias chung: 0 đổi nội dung, 0 đổi nhóm source, 22 đổi level, 6 đổi parent đề xuất, 16 đổi trạng thái placement. Các số đếm có thể trùng một heading.

| Alias | Trang | Heading: cũ → sau | Thay đổi | Level | Parent đề xuất | Placement |
|---|---:|---|---|---|---|---|
| L0000:S0 | 1 | MINUTES OF THE INTERNATIONAL COMPARISON PROGRAM TECHNICAL ADVISORY GROUP MARCH 7-8, 2025 New York - Hybrid | giữ nguyên | 1 | ROOT | model-root |
| L0004:S0 | 1 | Session I: Welcome and meeting objectives | giữ nguyên | 2 | L0000:S0 | model-parent-relation |
| L0025:S0 | 1 | Session II: Update on the ICP 2021 Cycle | giữ nguyên | 2 | L0000:S0 | model-parent-relation |
| L0026:S0 | 1 | 1. Global office update | giữ nguyên | 3 | L0025:S0 | model-parent-relation |
| L0078:S0 | 3 | 2. Regional updates | giữ nguyên | 3 | L0025:S0 | model-parent-relation |
| L0081:S0 | 3 | Africa | giữ nguyên | 4 | L0078:S0 | model-parent-relation |
| L0089:S0 | 3 | Asia and the Pacific | giữ nguyên | 4 | L0078:S0 | model-parent-relation |
| L0107:S0 | 3 | Commonwealth of Independent States (CIS) | giữ nguyên | 4 | L0078:S0 | model-parent-relation |
| L0123:S0 | 4 | Eurostat–OECD PPP Program | giữ nguyên | 4 | L0078:S0 | model-parent-relation |
| L0137:S0 | 4 | Latin America and the Caribbean | giữ nguyên | 4 | L0078:S0 | model-parent-relation |
| L0150:S0 | 4 | Western Asia | giữ nguyên | 4 | L0078:S0 | model-parent-relation |
| L0165:S0 | 5 | Session III: Short- and Long-Term Research and Development Agenda | giữ nguyên | 2 | L0000:S0 | model-parent-relation |
| L0166:S0 | 5 | 1. Research Topics Emerging from the Previous ICP Cycles | giữ nguyên | 3 | L0165:S0 | model-parent-relation |
| L0219:S0 | 6 | 2. Forthcoming Research Topics | giữ nguyên | 3 | L0165:S0 | model-parent-relation |
| L0271:S0 | 7 | Session IV: TAG Functioning and Terms of Reference for Task Forces | giữ nguyên | 2 | L0000:S0 | model-parent-relation |
| L0272:S0 | 7 | 1. TAG Composition and Terms of Reference | giữ nguyên | 3 | L0271:S0 | model-parent-relation |
| L0288:S0 | 7 | 2. Terms of Reference for a Task Force on Annual PPP Production | giữ nguyên | 3 | L0271:S0 | model-parent-relation |
| L0315:S0 | 8 | 3. Terms of Reference for a Task Force on ICP Classification Update | giữ nguyên | 3 | L0271:S0 | model-parent-relation |
| L0334:S0 | 8 | Session V: Current Research | level, parent đề xuất, placement | 2 → 1 | L0000:S0 → ROOT | model-parent-relation → model-root |
| L0335:S0 | 8 | 1. The Treatment of Import and Export Prices in International Comparisons | level | 3 → 2 | L0334:S0 | model-parent-relation |
| L0359:S0 | 9 | 2. A Survey Based Approach to Adjustment for Quality Differences in Services in International Price Comparisons | level | 3 → 2 | L0334:S0 | model-parent-relation |
| L0377:S0 | 9 | 3. Treatment of Scanner (Transaction) Data in the European Comparison Programme (ECP) | level | 3 → 2 | L0334:S0 | model-parent-relation |
| L0400:S0 | 10 | 4. Treatment of Negative Expenditures in ICP | level | 3 → 2 | L0334:S0 | model-parent-relation |
| L0420:S0 | 10 | 5. Fine Tuning Global Linking Procedures | level | 3 → 2 | L0334:S0 | model-parent-relation |
| L0470:S0 | 12 | 6. Improving Reliability of Price Comparisons | level | 3 → 2 | L0334:S0 | model-parent-relation |
| L0507:S0 | 12 | Session VI: Any Other Business | level, parent đề xuất, placement | 2 → 1 | L0000:S0 → ROOT | model-parent-relation → model-root |
| L0513:S0 | 13 | Cũ: International Comparison Program (ICP) TECHNICAL ADVISORY GROUP (TAG) Hybrid Meeting March 7-8, 2025 Bamboo Room, Ease 1345 1345 6th Ave, New York, NY 10105 Agenda<br>Sau: ∅ | không còn được chọn | — → ∅ | NONE → ∅ | model-out-of-hierarchy → ∅ |
| L0520:S0 | 13 | DAY 1: Friday, March 7, 2025 &#124; Chair: Paul SCHREYER | level, parent đề xuất, placement | — → 1 | L0513:S0 → ROOT | unresolved → model-root |
| L0521:S0 | 13 | SESSION I: Welcome and Opening Remarks | level, placement | — → 2 | L0520:S0 | unresolved → model-parent-relation |
| L0523:S0 | 13 | SESSION II: Update on the ICP 2021 and 2024 Cycles | level, placement | — → 2 | L0520:S0 | unresolved → model-parent-relation |
| L0526:S0 | 13 | SESSION III: Short- and Long-Term Research and Development Agenda | level, placement | — → 2 | L0520:S0 | unresolved → model-parent-relation |
| L0531:S0 | 13 | SESSION IV: TAG Functioning and Terms of Refence for Task Forces | level, placement | — → 2 | L0520:S0 | unresolved → model-parent-relation |
| L0536:S0 | 14 | SESSION V: Current Research | level, placement | — → 2 | L0520:S0 | unresolved → model-parent-relation |
| L0539:S0 | 14 | DAY 2: Saturday, March 8, 2025 &#124; Chair: Prasada RAO | level, parent đề xuất, placement | — → 1 | L0513:S0 → ROOT | unresolved → model-root |
| L0540:S0 | 14 | SESSION V: Current Research (Cont’d) | level, placement | — → 2 | L0539:S0 | unresolved → model-parent-relation |
| L0550:S0 | 14 | Cũ: Improving Reliability of Price Comparisons<br>Sau: ∅ | không còn được chọn | — → ∅ | L0540:S0 → ∅ | unresolved → ∅ |
| L0553:S0 | 14 | SESSION VI: Closing | level, parent đề xuất, placement | — → 1 | L0539:S0 → ROOT | unresolved → model-root |
| L0556:S0 | 15 | Annex 2: List of Participants | level, parent đề xuất, placement | — → 1 | NONE → ROOT | model-out-of-hierarchy → model-root |
| L0557:S0 | 15 | ICP Technical Advisory Group members | level, placement | — → 2 | L0556:S0 | unresolved → model-parent-relation |
| L0566:S0 | 15 | ICP experts, guest speakers, and observers | level, placement | — → 2 | L0556:S0 | unresolved → model-parent-relation |
| L0576:S0 | 15 | ICP Inter-Agency Coordination Group (IACG) | level, placement | — → 2 | L0556:S0 | unresolved → model-parent-relation |
| L0587:S0 | 15 | World Bank | level, placement | — → 3 | L0576:S0 | unresolved → model-parent-relation |

### Những thay đổi về lựa chọn và grouping

- `L0513:S0`: source parts cũ ['L0513:S0', 'L0514:S0', 'L0515:S0', 'L0516:S0', 'L0517:S0', 'L0518:S0', 'L0519:S0'] → sau ∅. không còn được chọn.
- `L0550:S0`: source parts cũ ['L0550:S0'] → sau ∅. không còn được chọn.

Dữ liệu đầy đủ, số thứ tự và hash input: [diff.json](diff.json). Script chỉ đọc kết quả và ghi báo cáo, không sửa các file heading đầu vào.
