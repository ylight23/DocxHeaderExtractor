# DOCX 075: kiểm tra SELECT repair và PLACE

Ngày 2026-11-04. Đây là thử nghiệm qualification với Qwen qua provider thật, không bật thay đổi mặc định production. Các nhánh dùng cùng tài liệu DOCX 075 (SHA-256 `1e03bd6986c228f2d097877c070b648fee695db70c37f38b01ee3c7948673d46`), cùng GROUP và PLACE production; cache tắt. Mỗi nhánh chỉ một lần chạy, không phải đánh giá ổn định thống kê; 075 chưa có Gold được duyệt.

| Nhánh | Thay đổi SELECT | Thời gian toàn tuyến (s) | Phản hồi SELECT | Heading đầu ra |
|---|---|---:|---:|---:|
| Control | Contract hiện tại | 116,04 | 1 | 17 |
| Facts-only | Thêm biên hai phía của khoảng trắng giữa các format run | 146,19 | 2 | 17 |
| Instruction-only | Thêm hướng dẫn, có nhắc field formatBoundaries không có trong packet | 90,34 | 1 | 10 |
| Combined | Thêm facts và hướng dẫn | 105,88 | 1 | 9 |
| Trim-only | Chỉ nhắc text/offset chính xác và không có khoảng trắng đầu/cuối | 232,95 | 4 | 17 |

Số heading không tự chứng minh đúng: phải kiểm tra từng heading, nhóm và parent. Ở combined, SELECT bỏ `Opening:` (S0003), `Present:` (S0005) và sáu mục agenda S0032–S0037 so với control. Instruction-only cũng bỏ sáu mục agenda và GROUP ghép S0030 với S0031. `Attachment:Agreed Agenda` (S0029) không được SELECT trong các nhánh này. NOT_SELECT không mang lý do riêng nên không thể kết luận model giải thích vì sao bỏ. Instruction-only không cô lập hoàn toàn vì instruction nhắc field vắng mặt. Chênh lệch giữa các lần không chứng minh quan hệ nhân quả do mỗi nhánh chỉ chạy một lần.

## Nguyên nhân repair được kiểm chứng

Facts-only: phản hồi SELECT đầu có 12 extent chứa khoảng trắng ở mép; một lượt repair trả phản hồi hợp lệ. Thêm facts không bảo đảm tránh lỗi này.

Trim-only: phản hồi đầu trả text đã bỏ khoảng trắng nhưng 12 offset cuối vẫn chứa khoảng trắng. Chạy lại từng phản hồi qua parser production với cùng target source ghi nhận: lượt 1 `HEADING_EXTENT_TEXT_MISMATCH` tại S0011, lượt 2 tại S0020, lượt 3 tại S0032, lượt 4 hợp lệ. Ví dụ source `[0,27)` chứa `Item 1: Document Approval. `, còn text model gửi khớp `[0,26)`. Parser trả lỗi đầu tiên của mỗi phản hồi; các lỗi khác có thể còn trong cùng phản hồi. Đây là bằng chứng cụ thể cả lỗi phối hợp text/offset của model lẫn feedback fail-fast làm nhiều lỗi chưa được báo cùng lúc; chưa chứng minh một thay đổi feedback sẽ sửa được model. Không tự dịch offset hoặc cắt nội dung bằng code. Xem `075-validator-replay.json` và response JSONL cạnh báo cáo này.

## PLACE

Một đối chứng riêng trước đó giữ nguyên SELECT và GROUP trên cùng 075, chạy PLACE năm lần: bốn lần sáu mục agenda thuộc S0031 và một lần thuộc S0030. Request batch đầu có cùng SHA-256 `cee34df5bccd1c5e742c2044d13e8d0492800b05e952ff4d8bcccfa2e45d74d2`; các phản hồi khác nhau trên cùng input. Batch sau có input thay đổi theo kết quả trước. Đây là bằng chứng PLACE chưa ổn định cho trường hợp này, không phải đo tỷ lệ lỗi tổng quát. Xem `../docx075-repair-place-diagnostic-20261103/REPORT.md`. Ở facts-only, các mục agenda cuối ra level 2 thay vì level 3 như control; vì upstream/response khác nhau, không dùng riêng so sánh này để quy lỗi cho PLACE.

## Kết luận và điều kiện trước promote

Không promote candidate: chưa chứng minh cải thiện repair và có biến động chọn/group/parent. Cần thử feedback repair liệt kê nhiều lỗi hợp lệ trong cùng lượt, giữ nguyên source-bound và quyết định membership, đo số repair và so chất lượng trên nhiều lần/các tài liệu đại diện; cần kiểm tra ảnh hưởng giới hạn packet khi thêm facts sau khi chia segment. PLACE cần đối chứng lặp lại cùng request và đánh giá quan hệ cha trên dữ liệu có đáp án. Không suy ra chất lượng hay SLA toàn production từ các phép thử này.

Chi phí xác nhận ba nhánh mới facts-only, instruction-only, trim-only lần lượt 0,003597156; 0,002037480; 0,005345947 USD; không có khoản provisional trong ghi nhận. Các log đã lưu phản hồi cấu trúc, không lưu hidden reasoning.
