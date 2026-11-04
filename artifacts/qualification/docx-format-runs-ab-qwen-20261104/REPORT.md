# Đối chứng generic: format runs cho DOCX SELECT

Ngày 2026-11-04. Chạy Qwen/OpenRouter thật qua DocumentRunner, cache tắt, nguyên instruction DOCX production, nguyên GROUP/PLACE và validator fail-fast. Candidate chỉ bổ sung `formatRuns` đo từ parser vào targetFacts khi có nhiều run: start/end UTF-16, bold, italic, underline; có `formatRunsNotShown` nếu parser giới hạn. Không có nhãn heading hay rule theo tài liệu. Flag qualification mặc định tắt.

## Đối chiếu evidence

Packet 075 hiện tại cho cả Item 1–6 trong biên bản và 1–6 trong agenda có `style.bold=false` ở cấp đoạn, `formatRunCount=2`, `boldCharRatio` thấp và một `formatChangeOffsets`. Đây không trực tiếp chỉ rõ run nào in đậm. Trong packet candidate, S0011 có run `[0,26)` bold và `[27,507)` không bold; S0032 có `[0,21)` bold và `[22,226)` không bold. Các số là dữ kiện formatting, không tự chứng minh vai trò heading. Hai nhóm có cấu trúc evidence tương tự; không có reason NOT_SELECT để xác định vì sao model quyết định khác.

Đã dựng lại hai request đầu của 075: SHA control `b4e8fba457679fa9ed0b162eb6e9b860b5f531359d7ab40eb14234db574675ff`, candidate `5ced37913588bb58331e94c1e4bd5441b90d17f4a9cdbe57cb4b0b33e1b40f11`, trùng hash đã gửi. Sau khi bỏ hai trường bổ sung `formatRuns` và `formatRunsNotShown` khỏi candidate, mọi trường JSON còn lại tương đương control. Packet 47.792 → 50.108 byte; thêm facts cho 13/38 đoạn. Control/candidate 074 cũng được dựng lại, khớp hash đã gửi, chỉ khác facts format runs của S0010 và S0012, 28.373 → 28.716 byte.

## Kết quả end-to-end

| File / nhánh | Giây | SELECT replies | Heading đầu ra | Sáu mục agenda |
|---|---:|---:|---:|---|
| 075 control A | 153,8 | 2 | 10 | 0/6 |
| 075 candidate A | 147,7 | 2 | 17 | 6/6 |
| 075 control B | 676,2 | 13 | 17 | 6/6 |
| 075 candidate B | 158,1 | 2 | 17 | 6/6 |
| 074 control A | 59,8 | 1 | 3 | không áp dụng |
| 074 candidate A | 99,3 | 3 | 4 | không áp dụng |

Trên 075 chỉ 7/38 quyết định khác nhau giữa bốn lượt: S0005 `Present` và sáu mục agenda; control A không chọn, ba lượt còn lại chọn. `Attachment:Agreed Agenda` S0029 không được chọn ở cả bốn. Ba lượt có sáu mục agenda đều tách chúng riêng qua GROUP và PLACE đề xuất parent S0031; PLACE có biến thiên trong thí nghiệm khác, chưa được kiểm chứng ngữ nghĩa. Hai lượt candidate bắt đầu đều có 12 mismatch text/offset và sửa trong một lần; control B sửa 12 mismatch qua 12 lượt repair. Những quan sát ít mẫu này không chứng minh độ chính xác hay tốc độ ổn định.

Trên 074, control SELECT S0010 `Governance/Voting Rights.`; candidate SELECT cả S0010 và S0012 `Subsequent Note:`. Hai nhãn này trước đó người dùng xác định không phải heading. Cả hai lượt GROUP gộp tiêu đề tài liệu với `Opening:`. Candidate làm tăng số nhãn sai đã biết từ 1 lên 2 trong hai lượt cụ thể; không thể quy toàn bộ khác biệt cho formatRuns vì model không xác định.

Chi phí xác nhận các lượt 075 control A/B và candidate A/B lần lượt USD 0,003548956; 0,022415757; 0,003681305; 0,0035835. Lượt 074 control/candidate USD 0,001043814 và 0,001816534; provisional 0.

## Kết luận

Chưa promote: candidate không chứng minh ổn định trên cohort, vẫn bỏ Attachment ở 075, còn nhãn sai trên 074 và lỗi GROUP. Gold toàn tài liệu chưa được duyệt; không báo precision/recall tổng. Với tài liệu lớn, facts được thêm sau khi phân đoạn nên chưa chứng minh request luôn dưới byte/token budget; phải đo serialized packet/packing trước khi cân nhắc bật. Build mới đổi MVID nên cache DOCX hiện có sẽ miss an toàn.

Test DOCX facts, wire golden và architecture: 18/18; chưa chạy full suite. Dữ liệu thô response và outline nằm cùng thư mục; 074 ở `../docx-format-runs-074-qwen-20261104/`; `selected-decisions.json` lưu đủ 38 quyết định của bốn lượt 075. Ba báo cáo tháng 8–10 chưa được chạy trong thử nghiệm này.

**Cập nhật 2026-11-04:** Người dùng xác nhận `Governance/Voting Rights.` và `Subsequent Note:` trong 074 là heading. Các nhận xét trước đây trong báo cáo này gọi chúng là FP không còn áp dụng; số liệu response và các quan sát khác vẫn giữ nguyên.
