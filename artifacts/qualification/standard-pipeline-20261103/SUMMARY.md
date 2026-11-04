# Tổng hợp: pipeline trên hai bản chuẩn 074 và 075 (2026-11-03)

Mục tiêu: chạy pipeline heading trên hai bản chuẩn, rồi ghép 074 → 075 theo tên heading. File gốc không bị sửa.

## File dùng

| Vai trò | File | SHA-256 (16 ký tự đầu) |
|---|---|---|
| Văn bản đích (mẫu) | `todo10_8/standard-docx-v3/05_bien_ban_hop/075_FORTIS_GC_Minutes_Nov21_2024.docx` | 1e03bd6986c228f2 (không đổi sau chạy) |
| Nguồn | `todo10_8/standard-docx-v3/05_bien_ban_hop/074_FORTIS_GC_Minutes_Nov27_2024.docx` | 79a41bf07713edcc |

Ghi chú: quy định trong `docs/qualification/standard-test-documents.md`.

## Kết quả pipeline heading (outline)

| Văn bản | Số heading | Các heading | Trạng thái |
|---|---|---|---|
| 074 | 5 | "Minutes… Meeting Governing Committee…" (tiêu đề), "Opening:" (cấp 1), "Present:" (cấp 2), "Governance/Voting Rights." (cấp 1), "Subsequent Note:" (cấp 1) | Lane ngữ nghĩa: complete |
| 075 | 4 | "MEETING MINUTES…" (cấp 1), "Opening:" (cấp 2), "AGENDA for FIRST MEETING…" (cấp 1), "Agenda Items" (cấp 2) | Lane ngữ nghĩa: complete |

Nhận xét:
- 074: "Opening:" và "Present:" đúng. "Governance/Voting Rights." và "Subsequent Note:" là nhãn in đậm, không phải heading theo quyết định của anh, nhưng pipeline vẫn nhận. Đây là lỗi của pipeline.
- 075: thiếu "Present:", "Attachment:Agreed Agenda", "Item 1"–"Item 6" và các mục đánh số 1–6. Đây là lỗi bỏ sót.
- Kết quả pipeline không ổn định giữa các lần chạy (đã thấy trên 075 trước đó).

## Kết quả ghép theo tên (74 → 75)

- Chỉ "Opening:" khớp tên. 1 ý được lấy: câu khai mạc của 074 (`074 body[1]/p[4]`).
- "Present:" không được ghép vì 075 chuẩn không có heading "Present:" trong outline này.
- Không ý nào được ghi vào mục 075 khác.
- File ghép (bản sao, không phải file gốc): `gather-by-name/075_FORTIS_GC_Minutes_Nov21_2024_by_heading.docx`.

## Việc cần làm tiếp

1. Sửa pipeline để không nhận nhãn in đậm đứng đầu đoạn thành heading (ví dụ "Governance/Voting Rights.", "Subsequent Note:").
2. Sửa pipeline để nhận đủ "Present:", "Attachment", "Item 1"–"Item 6" và mục đánh số trong 075.
3. Sau khi outline đúng, chạy lại ghép 074 → 075. Khi đó "Present" của 074 chỉ nhận danh sách người tham dự (theo cách đúng với cấu trúc của chính 074).

**Đính chính ngày 2026-11-04:** Người dùng xác nhận `Governance/Voting Rights.` và `Subsequent Note:` trong 074 là heading. Những nhận xét trước đây trong báo cáo này coi hai mục đó là không phải heading không còn áp dụng.
