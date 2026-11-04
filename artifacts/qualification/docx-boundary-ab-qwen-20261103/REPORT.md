# DOCX — thử nghiệm generic về ranh giới định dạng (2026-11-04)

## Thiết kế

Chạy DocumentRunner với provider OpenRouter/Qwen thật, cache SELECT/GROUP tắt; mỗi file chuẩn 074 và 075 chạy một lượt control và một lượt candidate. Hai file giữ nguyên SHA-256 theo docs/qualification/standard-test-documents.md. Output và response được ghi ngay trong từng lượt trước khi đối chiếu.

- Control: DOCX instruction và evidence hiện hành.
- Candidate: thêm formatBoundaries vào targetFacts, gồm precedingNonWhitespaceEnd, followingNonWhitespaceStart và gapIsWhitespace; thêm instruction tổng quát mô tả facts và yêu cầu headingExtent không có whitespace ở đầu/cuối.
- Candidate không đặt quy tắc chọn heading theo tên, nội dung hay loại tài liệu. Validator giữ nguyên, không tự dịch offset. GROUP được truyền instruction gốc; PLACE dùng mặc định.
- Đây là nhánh qualificationFormatBoundaryEvidence, mặc định tắt. Chưa promote.

Trên 075, request dựng lại của control và candidate khớp SHA với request đầu đã gửi. So sánh packet cho thấy cùng system, tools, targets và mọi trường còn lại sau khi loại phần instruction bổ sung và formatBoundaries. Candidate thêm boundary facts cho 13/38 target; request tăng từ 47.792 lên 49.922 byte. Ví dụ S0011: formatChangeOffsets cũ = 27; facts mới cho previous end = 26, next start = 27, gap whitespace = true.

## Kết quả thực tế

| File | Nhánh | Giây | Alias SELECT | Heading sau GROUP | SELECT request | GROUP request | PLACE request |
|---|---|---:|---:|---:|---:|---:|---:|
| 074 | control | 60,39 | 4 | 3 | 1 | 1 | 1 |
| 074 | candidate | 49,29 | 3 | 2 | 1 | 1 | 1 |
| 075 | control | 116,04 | 17 | 17 | 1 | 1 | 2 |
| 075 | candidate | 105,88 | 9 | 9 | 1 | 1 | 1 |

Cả control và candidate của 075 đều qua validator ngay SELECT đầu; không có repair ở lượt này. Replay response bằng validator production đã xác nhận. Vì control cũng không repair, phép thử không chứng minh candidate giảm số repair.

## Diff SELECT 075: 8 alias

| Alias / source | Control | Candidate | Nội dung |
|---|---|---|---|
| S0003 / body p4 | SELECT | NOT_SELECT | Opening: |
| S0005 / body p6 | SELECT | NOT_SELECT | Present: |
| S0032 / body p33 | SELECT | NOT_SELECT | 1. Document Approval. |
| S0033 / body p34 | SELECT | NOT_SELECT | 2. Operating Context. |
| S0034 / body p35 | SELECT | NOT_SELECT | 3. Strategic Directions. |
| S0035 / body p36 | SELECT | NOT_SELECT | 4. Work Plan, Budget, and Fees. |
| S0036 / body p37 | SELECT | NOT_SELECT | 5. Communications Strategy. |
| S0037 / body p38 | SELECT | NOT_SELECT | 6. Appointment of the Chair. |

Attachment:Agreed Agenda không được chọn ở cả hai lượt. Không có reason riêng cho các NOT_SELECT theo contract hiện hành; không suy diễn nguyên nhân ngữ nghĩa từ việc model trả NOT_SELECT.

## GROUP và PLACE

- 074: cả hai nhánh gộp S0001 + S0002, tức tiêu đề tài liệu với Opening:, vào cùng một heading. Candidate không chọn Governance/Voting Rights.; control chọn nhãn này. Đó là một FP đã được người duyệt xác định trong docs/qualification/standard-test-documents.md.
- 075: control chia 17 heading riêng, candidate chia 9 heading riêng. Sự khác nhau trước hết nằm ở SELECT, nên không dùng kết quả này để kết luận thay đổi cải thiện GROUP.
- 074: quan hệ cha của Present khác giữa hai lượt. 075: không có thay đổi cha giữa các heading chung, nhưng hai đầu vào PLACE khác nhau (17 so với 9 heading); không coi đó là kiểm chứng ổn định PLACE.
- Cả bốn lượt không có grouping fallback hoặc alias unresolved; điều này chỉ nói về protocol, không chứng minh đúng nghĩa.

## Giới hạn và kết luận

Chỉ có một lần độc lập mỗi nhánh trên mỗi file; baseline Qwen trước đó cũng từng bỏ các mục agenda, nên không thể quy nguyên nhân tám quyết định khác hoàn toàn cho evidence hoặc prompt. Candidate đồng thời thay hai yếu tố nên chưa tách được ảnh hưởng của từng yếu tố.

Kết quả candidate 075 thiếu các heading Opening, Present và sáu mục agenda so với control; cả hai nhánh 074 còn lỗi GROUP. Bản thử không đủ điều kiện promote. Không có Gold heading đã duyệt cho 074/075, nên không báo TP/FP/FN tổng thể. Cần tách đối chứng evidence-only và instruction-only, lặp nhiều lần và chấm trên Gold/cohort trước quyết định.

Bốn lượt có 13 transport attempt, tổng 0,006769664 USD xác nhận, provisional 0. Test code liên quan, bao gồm wire baseline: 16/16 qua. So sánh đầy đủ từng alias và cha: comparison.json. Response, packet được dựng lại và validator replay lưu cùng thư mục.
Giới hạn kỹ thuật bổ sung: formatBoundaries được thêm sau khi DocxAgentSource đã chia core dựa trên kích thước facts cũ. Packet 075 trong thử nghiệm vẫn ở 49.922 byte dưới cap 262.144 byte, nhưng chưa chứng minh các file lớn vẫn vừa budget; cần đo và xử lý packing trước mọi đề xuất promote. Việc build lại HeadingAgent cũng làm namespace cache DOCX dựa trên ModuleVersionId thay đổi, do đó các cache cũ sẽ miss sau rebuild; đây là invalidation an toàn, không phải giữ nguyên hiệu năng hit.
**Cập nhật 2026-11-04:** Người dùng xác nhận `Governance/Voting Rights.` và `Subsequent Note:` trong 074 là heading. Các nhận xét trước đây trong báo cáo này gọi chúng là FP không còn áp dụng; số liệu response và các quan sát khác vẫn giữ nguyên.
