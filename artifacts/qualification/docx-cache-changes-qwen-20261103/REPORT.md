# DOCX cache — thử thay đổi bản sao 074 (2026-11-03)

Ba bản sao được tạo từ 074 chuẩn. Cache được sao chép từ lượt đã chạy 074 gốc; các file cache có cùng namespace phiên bản triển khai. Mỗi bản thay đổi chạy một lần qua DocumentRunner, OpenRouter/Qwen thật; từng JSON được lưu trước khi đối chiếu. File chuẩn không bị sửa.

| Thay đổi trên bản sao | Giây | Lượt SELECT/GROUP | Lượt PLACE | Heading trả về | USD xác nhận |
|---|---:|---:|---:|---:|---:|
| Thêm đoạn thân bài sau Opening | 123,96 | 4 | 1 | 5 | 0,002581501 |
| Thêm heading Additional Test Section | 59,20 | 2 | 1 | 4 | 0,001099010 |
| Đổi Present thành Attendance | 81,84 | 3 | 1 | 4 | 0,001564926 |

- Cả ba lượt có 0 segment selection-reuse, dù có cache 074 gốc. Mỗi lượt có hash toàn file khác; cache hiện không tái dùng giữa phiên bản.
- Thêm thân bài: output gồm hai nhãn đã biết không phải heading, Governance/Voting Rights. và Subsequent Note:. Lượt 074 gốc lạnh có Governance/Voting Rights. nhưng không có Subsequent Note:.
- Thêm heading: Qwen chọn được Additional Test Section. Hai nhãn giả trên không xuất hiện trong lượt này.
- Đổi heading: Qwen chọn Attendance:, nhưng GROUP gộp tiêu đề tài liệu và Opening: thành một heading (aliases S0001 + S0002). GroupingObservation báo không fallback hoặc unresolved.
- Mỗi trường hợp chỉ chạy một lần và các request khác nhau. Các thay đổi kết quả có thể chịu ảnh hưởng biến thiên model; phép thử không xác định riêng ảnh hưởng của chỉnh sửa.
- Chưa có Gold heading được duyệt cho 074/075 chuẩn. Không suy TP/FP/FN tổng thể; hai nhãn giả dựa trên quyết định người duyệt đã ghi ở docs/qualification/standard-test-documents.md.
- Ba lượt dùng 12 lần transport và 0,005245437 USD xác nhận. Chưa thử riêng xóa hay di chuyển heading và chưa có lượt đối chứng cache tắt trên cùng từng bản sửa.

Lượt cold/warm file gốc cùng kết quả và giới hạn xem tại ../docx-cache-ab-qwen-20261103/REPORT.md.
**Đính chính 04/11/2026:** Người dùng xác nhận `Governance/Voting Rights.` và `Subsequent Note:` trong 074 là heading; các nhận xét phía trên gọi hai mục này là heading giả không còn áp dụng. Số đo cache và response thô không đổi.
