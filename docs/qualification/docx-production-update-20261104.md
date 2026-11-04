# DOCX production: GROUP boundary guidance, 04/11/2026

## Phạm vi được đưa vào mặc định

Theo yêu cầu người dùng ngày 04/11/2026, `DocxSelector` mặc định gửi thêm một đoạn hướng dẫn tổng quát trong trường `instruction` của request GROUP. Đoạn này yêu cầu model xác định các phần được chọn có cùng đặt tên một đơn vị hay không; tách heading mở một đơn vị nội dung riêng, đồng thời cho phép nhãn và tên kết hợp thành một heading. Đây là hướng dẫn cho model, không phải rule code dựa vào tên `Opening`, định dạng đậm, số mục hay một tài liệu cụ thể.

- SELECT vẫn dùng `DocxAgentProtocol.Instruction` và schema extent cũ. GROUP system prompt, response schema và kiểm tra source/alias/thứ tự/group liền kề không đổi; GROUP không có quyền thay đổi membership.
- `DocxSelector.SelectorId` mang hậu tố `group-boundary-guidance-v1`. Khi dùng cache opt-in, GROUP request và key bao gồm instruction mới. Namespace cache DOCX có MVID của assembly, vì vậy build thay đổi có thể khiến cache cũ miss an toàn, kể cả SELECT; không hứa giữ hit cache sau cập nhật.
- `PromptCatalog.Docx.GroupInstruction` cung cấp instruction GROUP mặc định; trang web chỉ đọc hiển thị cả GROUP system prompt và instruction gửi trong payload. `agent-wire.json` khóa hash instruction GROUP mặc định mới `f42d5745650e51659ae9720ac743912e09bc494d0579a44cc8e4fbd3d7d268fd`.
- Hướng dẫn mới áp dụng với DOCX selector do ứng dụng tạo, kể cả khi caller cấp instruction SELECT riêng; caller qualification có thể chủ ý cấp `qualificationGroupingInstruction` riêng. Provider thay thế tự triển khai `IHeadingSelector` giữ quyền ưu tiên và không mặc nhiên nhận thay đổi này.
- Route PDF, scan và PLACE không được đổi trong đợt promote này. `qualificationFormatRunEvidence`, `qualificationFormatBoundaryEvidence`, `qualificationBatchExtentErrors` vẫn tắt mặc định; DOCX selection/grouping cache vẫn opt-in qua `DocxReuseSelection`, không bật kèm đợt này.
- Đây là thay đổi trên source mặc định, chưa phải một commit hay xác nhận bản build/service đang chạy đã được khởi động lại. Cần build/restart instance để source mới có hiệu lực.

## Bằng chứng trước và sau nối mặc định

Ở 074, SELECT đã được cố định bằng response hợp lệ, gồm `Governance/Voting Rights.` và `Subsequent Note:` theo xác nhận của người dùng. GROUP/PLACE gọi Qwen qua OpenRouter thật:

| Đối chứng | Control | Instruction GROUP mới |
| --- | --- | --- |
| 074: tách tiêu đề và `Opening:` | 2/4 | 4/4 |
| 075: tách các mục agenda đã chọn | 2/2 | 2/2 |
| Fixture nhân tạo: gộp `Chapter I` + `GENERAL PROVISIONS`, tách `Article 1. Scope` | 2/2 | 2/2 |

Sau khi nối mặc định, smoke 074 phát lại cùng SELECT, GROUP và PLACE chạy Qwen thật; GROUP request SHA-256 `8e1ca954ed52e012d9be026ea204622a7bc70d0b2467d9cded2db39e4f657bcd` đúng bằng candidate đã thử, và GROUP tách S0001 khỏi S0002. SELECT được phát lại, do đó smoke không chứng minh SELECT production mới chạy tốt. PLACE trong lượt smoke đề xuất tất cả năm heading là ROOT, `VerificationStatus=NOT_RUN`; quan hệ cha và ghép nội dung chưa được xác nhận bởi lượt này. Chi phí Qwen xác nhận của smoke là 0,000300314 USD; SELECT nội bộ mô phỏng 0 USD.

Các kết quả 4/4 và 2/2 là mẫu nhỏ, không phải cam kết ổn định hay thước đo recall/precision toàn corpus. Các lượt 075 trước đó từng bỏ sáu mục agenda hoặc `Attachment` ở SELECT. Bản thử format runs cho 075 và response SELECT chưa chứng minh giải quyết toàn bộ lỗi. Trên 074, hai heading `Governance/Voting Rights.` và `Subsequent Note:` được người dùng xác nhận là heading; các nhận xét cũ coi chúng là FP đã được đính chính.

## Kiểm thử và hồ sơ

- Đối chứng cố định SELECT và request/response: [báo cáo GROUP](../../artifacts/qualification/docx074-frozen-group-qwen-20261104/REPORT.md).
- Smoke sau nối mặc định: `artifacts/qualification/docx074-production-group-smoke-20261104/`.
- Bộ test liên quan đến DOCX selector, GROUP, prompt và wire golden: 21/21 qua. Full suite sau build cô lập: **804/804 qua** (2 phút 45 giây). Lượt đầu 803/804 do test fixture dò gốc repo bằng số cấp thư mục cố định; đã chuyển test sang `TestRepository.Path` và chạy lại toàn suite. Test SELECT/GROUP còn kiểm tra instruction SELECT không đổi và GROUP nhận guidance mới.
- Thay đổi source, test, Golden và hồ sơ chưa commit. Các thay đổi khác trong workspace không được xem là một phần riêng của lần promote này.

## Việc còn theo dõi

1. Chạy đối chiếu trên corpus DOCX đại diện và báo cáo tháng 8–10, kiểm tra SELECT, GROUP, parent, phạm vi content và vị trí chèn; không khẳng định không còn FP/FN.
2. Kiểm tra PLACE độc lập, nhất là trường hợp các đề xuất parent khác nhau với cùng request và trường hợp toàn bộ heading bị đề xuất ROOT.
3. Theo dõi số lượt repair offset và thời gian: validator hiện fail-fast ở mặc định, model có thể sửa nhiều lần. Không khẳng định hướng dẫn GROUP làm SELECT nhanh hơn.

## Bổ sung giao diện cache DOCX — 04/11/2026

Trang trích xuất và trang admin tổng hợp có checkbox `docxReuseSelection` gửi cùng tên form field đã được backend hỗ trợ. Mặc định không chọn; khi chọn, SELECT/GROUP có thể dùng lại cache nếu file và request khớp, còn PLACE vẫn gọi model. Đưa outline lên trực tiếp thì tài liệu đó không chạy extraction. Kết quả cold/warm và các bản sửa được ghi tại `artifacts/qualification/docx-cache-production-qwen-20261104/REPORT.md`; chưa kết luận chất lượng chung từ một file. Không thay đổi mặc định cache production bằng checkbox này.

## Bổ sung an toàn cache outline — 04/11/2026

Admin có thêm cache outline hoàn chỉnh, tách biệt với cache SELECT/GROUP; hit có thể bỏ qua cả PLACE. Chỉ dùng khi người dùng bật và SHA-256 toàn file cùng cấu hình khớp. Đã chặn ánh xạ outline cũ qua file thay đổi: test tái hiện đoạn heading mới bị `OutlineMap` coi là body, khiến nội dung sau nó có thể gắn sai mục. Mọi thay đổi byte hiện buộc chạy lại extraction; chưa có tối ưu incremental an toàn. Cache có thể lưu và tái dùng kết quả model còn thiếu hoặc sai, do đó cache hit không chứng minh chất lượng outline.

Thử nghiệm Qwen thực trên bản sao ba báo cáo tháng 8–10: cold 113,4 giây, warm 7,0 giây, cùng 19 ý tự động và 34 trường hợp chưa rõ; file sửa nội dung 112,5 giây, file sửa tên heading 106,1 giây, mỗi lượt có 0 ý tự động và 41 trường hợp chưa rõ. Các lượt sửa chỉ chạy model trên file thay đổi. Đây là thử nghiệm harness, không phải kiểm định end-to-end giao diện hay Gold. Sau thử nghiệm, phát lại outline đã lưu không gọi model và lưu plan vào `artifacts/qualification/gather-e2e-exact-cache-safe-20261104/`; các bản Word vẫn không phải bản tổng hợp đầy đủ. Lượt sửa nội dung có 142 ý nằm trong 41 mục chưa rõ: 11 tên gần giống, 10 khác đường dẫn, 8 trùng ứng viên, 12 do mục cha chưa rõ. Mục đích của phân loại là giữ lại để xem, không tự ý ghi. Ví dụ GROUP của văn bản đích gộp tên mục tổng quát với `Truyền thông:`, nên hai đoạn bổ sung nằm trong mục gần tên chưa được tự động ghép. Số đo chạy trong môi trường phát triển có tải, không phải benchmark SLA.

Sau thay đổi an toàn: 20/20 test outline và giao diện liên quan qua; một lượt full suite đạt 839/841, hai biến thể của `Transport_gate_admits` lỗi dưới tải, chạy riêng 2/2 qua. Chưa xác định được nguyên nhân lỗi và không coi lượt full suite đó đã qua.

Lượt kiểm tra lại trước khi commit rào chắn: full suite 848/849 (4 phút 46 giây). `SelectionConcurrencyTests.Out_of_order_scope_completions_preserve_all_aliases_requests_and_decisions` lỗi khi chạy full (13 thay vì 15) và qua khi chạy riêng. Lượt full chạy đồng thời các build/test đối chứng nên có tải; chưa chứng minh đó là nguyên nhân và không coi full suite đã qua.
