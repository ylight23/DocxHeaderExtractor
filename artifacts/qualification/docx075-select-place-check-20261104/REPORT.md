# Kiểm tra SELECT và PLACE của DOCX 075

Kiểm tra lại các artifacts Qwen/OpenRouter hiện có; không chạy model mới. Tài liệu chuẩn 075 có SHA-256 `1e03bd6986c228f2d097877c070b648fee695db70c37f38b01ee3c7948673d46`.

## Sáu mục agenda mất ở SELECT

Request SELECT đầu dùng prompt DOCX production có SHA-256 `b4e8fba457679fa9ed0b162eb6e9b860b5f531359d7ab40eb14234db574675ff`. Packet chứa 38 target; các alias sau có text heading cùng nội dung phía sau:

| Alias | Phần đầu đoạn |
|---|---|
| S0032 | 1. Document Approval. |
| S0033 | 2. Operating Context. |
| S0034 | 3. Strategic Directions. |
| S0035 | 4. Work Plan, Budget, and Fees. |
| S0036 | 5. Communications Strategy. |
| S0037 | 6. Appointment of the Chair. |

Trong response đầu: control 075 chọn cả sáu; fresh-traced và batch-production đều `NOT_SELECT` cả sáu. Fresh-traced giữ sáu quyết định qua 2 response SELECT, batch-production giữ qua 3 response. Các response NOT_SELECT không có reason nên không biết model giải thích thế nào. Có thể kết luận bằng chứng cho thấy Qwen đã nhìn thấy từng target và tự quyết NOT_SELECT; không kết luận được nguyên nhân nhận thức, cũng không chứng minh điều này do cache, chunking, repair hay C# parser gây ra. Ba lần có cùng hash request đầu nhưng response khác nhau; cần lưu ý các lượt repair tiếp theo có lịch sử request khác nhau.

Raw GROUP: control trả riêng S0032–S0037; batch-production chỉ có S0030–S0031, do sáu alias đã NOT_SELECT. PLACE không thể tự khôi phục các heading không được SELECT. Fresh-traced còn GROUP gộp S0020 và S0021 (hai mục Item 2 và Item 3), là lỗi khác sau SELECT. Không có Gold toàn file được duyệt nên không suy ra precision/recall tổng thể.

## PLACE cùng request

Sáu artifacts cold, warm, place-1, place-2, place-3 và batch-repair đều có `placement.Resources.Attempts[0].RequestSha256` bằng `cee34df5bccd1c5e742c2044d13e8d0492800b05e952ff4d8bcccfa2e45d74d2`. Batch đầu chứa S0032–S0035. Năm lượt đề xuất parent S0031 `Agenda Items`; một lượt warm đề xuất S0030 `AGENDA for FIRST MEETING`. `VerificationStatus` của những đề xuất là `NOT_RUN`: hợp lệ giao thức không có nghĩa quan hệ cha đã được kiểm chứng ngữ nghĩa. S0036–S0037 ở batch kế, không lấy cùng-hash batch đầu để chứng minh riêng cho hai alias này. Đây là mẫu quan sát, không ước lượng tỷ lệ lỗi production.

## Tác động tổng hợp

Khi một heading bị bỏ, khoảng nội dung trực tiếp có thể kéo qua mục bị bỏ vào mục đứng trước; cần chặn hoặc báo không chắc khi không đủ căn cứ, thay vì coi outline thiếu là đầy đủ. Parent khác nhau có thể làm chuỗi mục khác, ảnh hưởng ghép các heading trùng tên. Các khả năng này là hệ quả thiết kế, không phải đã chứng minh sai nội dung trong một file tổng hợp cụ thể. Cần đối chiếu kết quả thực tế trên ba báo cáo tháng 8–10 và kiểm tra phạm vi chèn.

Nguồn: `../docx-boundary-ab-qwen-20261103/075_FORTIS_GC_Minutes_Nov21_2024-control-responses.jsonl`, `../docx075-repair-place-diagnostic-20261103/fresh-traced-responses.jsonl`, `../docx-production-batch-repair-qwen-20261104/075_FORTIS_GC_Minutes_Nov21_2024-batch-production-responses.jsonl`, cùng artifacts PLACE trong `../docx-cache-ab-qwen-20261103/`, `../docx075-repair-place-diagnostic-20261103/` và `../docx-batch-repair-qwen-20261104/`.
