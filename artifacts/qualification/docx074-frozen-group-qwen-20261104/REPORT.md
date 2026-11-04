# DOCX 074: GROUP ghép nhầm tiêu đề và Opening

Ngày 2026-11-04. Người dùng xác nhận `Governance/Voting Rights.` và `Subsequent Note:` là heading; các báo cáo trước gọi hai mục này là FP đã được đính chính. Không suy rộng thành mọi nhãn in đậm đều là heading.

## Nguyên nhân được quan sát

GROUP đã nhận riêng S0001 là tiêu đề tài liệu và S0002 là `Opening:`. Trong packet GROUP có selectedText, sourceText, style (S0001 căn giữa; S0002 căn trái) và `after` chứa câu khai mạc. Ở các run trước model trả `headings: [[S0001,S0002]]`. Parser GROUP kiểm tra mọi alias, thứ tự và kích thước nhóm, không tự xác minh ngữ nghĩa; đây là phân nhóm sai của model đã lọt kiểm tra protocol. Không có bằng chứng parser ghép sau response.

Đối chứng cố định cùng response SELECT hợp lệ đã ghi trước: transport qualification phát lại SELECT, GROUP và PLACE gọi Qwen/OpenRouter thật. Cùng source, selected aliases, extents, model và payload GROUP; candidate chỉ thêm vào `instruction` GROUP hướng dẫn tổng quát: chỉ nối các phần khi cùng đặt tên một đơn vị; tách phần mở đơn vị nội dung riêng, đồng thời vẫn cho phép label + title cùng đặt tên một đơn vị. System prompt, SELECT prompt, facts, code parser và hợp đồng response không đổi. Không thêm ví dụ hoặc quy tắc tên Opening.

Packet 074 control có GROUP request SHA-256 `4ff1bdbd5f41c8a0d559fc2f8e3b8757110aadec82c3773df4fb1895b80bf25e`; candidate `8e1ca954ed52e012d9be026ea204622a7bc70d0b2467d9cded2db39e4f657bcd`. So sánh nội dung JSON xác nhận khi khôi phục instruction và instructionSha256 control, các trường khác bằng nhau.

| Trường hợp | Control | Candidate |
|---|---|---|
| 074, S0001 / S0002 | Tách 2/4, gộp 2/4 | Tách 4/4 |
| 075, các mục liên tiếp | Tách 2/2 | Tách 2/2 |
| DOCX tổng hợp `Chapter I` + `GENERAL PROVISIONS`, rồi `Article 1. Scope` | Gộp hai phần đầu, tách phần sau 2/2 | Gộp hai phần đầu, tách phần sau 2/2 |

Bài tổng hợp là fixture nhân tạo, không chứng minh mọi heading nhiều dòng. GROUP control 074 cho kết quả khác nhau ngay cả khi cùng request SHA. Hai nhánh trên 075 không xử lý Attachment chưa được SELECT và không kiểm tra đầy đủ mọi heading. Các outline, response, request và selection hash được lưu cho từng lượt.

## Kết luận và giới hạn

Hướng dẫn GROUP qualification cho kết quả đúng 4/4 mẫu 074 và không gây sai ở các trường hợp thử còn lại. Đây là mẫu nhỏ, chưa chứng minh ổn định hay an toàn trên DOCX khác. Chưa bật production, chưa thay prompt đóng băng và chưa commit. Cần thêm corpus có heading nhiều đoạn và heading riêng liền kề, trong đó có báo cáo tháng 8–10, đo thêm membership/nhóm/parent trước promote.

Kiểm thử `DocxGroupingEvidenceTests`, `DocxAgentFlavorTests`, `AgentWireGoldenTests`: 9/9 qua, bao gồm khả năng giữ nhóm nhiều phần và giữ packet mặc định. Chưa chạy full suite. 16 lượt GROUP/PLACE thật trên 074, 075 và synthetic có tổng chi phí xác nhận 0,004562205 USD; SELECT được phát lại nội bộ, không tính là Qwen mới.

Artifacts 074 lượt lặp: `../docx074-frozen-group-repeat-qwen-20261104/`; 075: `../docx075-frozen-group-qwen-20261104/`; fixture: `../docx-synthetic-frozen-group-qwen-20261104/`. Công cụ `tools/qualification/GatherTrial/DocxFrozenGroupTrial.cs` ghi raw GROUP request, response và outline.
