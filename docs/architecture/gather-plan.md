# Kế hoạch: gom ý liên quan từ nhiều tài liệu vào một mục của văn bản gốc

Trạng thái: gather là thử nghiệm. Đã có model giả, adapter OpenRouter và ghi bản sao DOCX; một lượt Qwen thật với contract offset cũ trả nhiều offset sai. Contract `gather-source-quotes-v2` dưới đây chưa được kiểm chứng với model thật. Bản đề xuất đáp án 075 là tiếng Anh và chưa duyệt. Chưa chứng minh chất lượng tiếng Việt hoặc độ đầy đủ trên nhiều nguồn.

## 1. Mục tiêu

Đầu vào:

1. **Văn bản gốc** và **một mục đích** trong đó, ví dụ "1. Thông tin chung".
2. **Các tài liệu nguồn** (DOCX hoặc PDF có lớp chữ).

Kết quả: danh sách các **ý** thuộc về mục đích, lấy từ **mọi nơi có chữ** trong tài liệu nguồn: đoạn văn, gạch đầu dòng, ô bảng. Một ý là một đoạn chữ **nguyên văn** trong một đoạn hoặc một ô (có thể chỉ là một phần của đoạn). Mỗi ý kèm tài liệu nguồn, vị trí đoạn hoặc ô (`sourceId`), và đường dẫn mục chứa nó. Ý trùng được gộp về một, ghi lại các nguồn còn lại; ý nghi mâu thuẫn được đánh dấu để người dùng kiểm tra.

Ví dụ: đoạn "Dự án được triển khai tại Hà Nội từ tháng 3 năm 2027. Sở A là đơn vị chủ trì." cho hai ý; một bảng có hàng "Tên dự án | Xây dựng hệ thống X" cho một ý, ghi nhãn hàng ("Tên dự án") cùng nguồn ô.

Số thứ tự mục không quyết định sự liên quan: "2. Thông tin chung" hay "3. Tổng quan dự án" ở tài liệu nguồn đều có thể khớp với "1. Thông tin chung" của văn bản gốc.

Ngoài phạm vi bước đầu: ghi vào file DOCX (để bước sau, xem §6); viết lại hoặc tóm tắt câu; dùng embedding hoặc BM25.

### Chế độ thử nghiệm: ghép heading cùng tên

`GatherByHeading` là một chế độ riêng cho yêu cầu lấy nội dung từ **các mục có cùng tên**, không dùng model để nhận biết mục đồng nghĩa. So tên sau khi chuẩn hóa Unicode, chữ hoa/thường, khoảng trắng, dấu cuối và tiền tố đánh số; ví dụ `1. Thông tin chung` khớp `3. THÔNG TIN CHUNG`. Tên giống nhau không bảo đảm cùng chủ đề; mục trùng tên nhiều lần ở tài liệu đích phải được phân biệt trước khi chèn.

Với mỗi cặp mục khớp, lấy nội dung trực tiếp sau heading, kể cả phần nằm cùng đoạn, đến heading tiếp theo. Các mục con được xử lý riêng theo tên, không tự nhập nội dung của chúng vào mục cha. Giữ thông tin nguồn và vị trí từng đoạn; nội dung trùng nguyên một đơn vị ở đích được loại khỏi phần chèn, không loại chỉ vì là chuỗi con. ID nguồn phải khác nhau. Nhiều mục cùng tên trong một nguồn được giữ và gắn cờ cần kiểm tra.

Khoảng giữa hai heading chỉ là **ứng viên**: trong 074, phần sau danh sách `Present` còn có thảo luận không thuộc danh sách đó. Các ứng viên được đánh dấu `SCOPE_UNREVIEWED`; `GatherByHeading.ReviewAsync` dùng semantic review kiểm tra theo tên mục và nguồn. Chỉ verdict `KEEP` hợp lệ bỏ cờ; `UNSURE`, thiếu verdict hoặc lỗi vẫn giữ cờ và chặn ghi mục. Verdict `EXCLUDE` được lưu cùng lý do. Review chỉ kiểm tra ứng viên đã tìm thấy, không chứng minh độ đầy đủ hay hai sự kiện khác ngày có thể trộn như cùng một sự kiện.

`GatherScopePipeline` là orchestration thử nghiệm khác: Qwen đề xuất quote; checker đối chiếu source/batch; `GatherQuoteRepair` thử sửa riêng quote bị từ chối trên đúng đơn vị gốc; Qwen review; sau đó review từng đơn vị không được đề xuất. Quote sửa vẫn phải khớp nguyên văn và không được đổi sang unit không thuộc đề xuất cũ. Thiếu verdict, lỗi gọi, quote sửa sai hoặc `UNSURE` không được tự chấp nhận. Bảo lưu đề xuất bị từ chối ban đầu và kết quả repair để kiểm toán. `Complete` chỉ mô tả xử lý/đếm đơn vị và vùng chưa đọc, không chứng minh recall ngữ nghĩa của các quote con. Chưa nối thành endpoint web hoặc promote.

Kết quả chỉ được ghi thành bản sao DOCX khi caller đã xác minh ranh giới heading của văn bản đích và gắn xác nhận với SHA-256 của chính bản đó; các vị trí không an toàn được báo bỏ qua. Chất lượng phụ thuộc outline của **cả nguồn lẫn đích**: nếu thiếu heading, nội dung có thể bị gán nhầm mục. Cần kiểm tra riêng độ phủ của cả hai trước khi coi kết quả đầy đủ; kiểm tra span hoặc hash không chứng minh được độ phủ đó. Chế độ này hiện mới được kiểm tra bằng fixture, chưa chứng minh toàn bộ kết quả 074 → 075.

## 2. Nguyên tắc

- **LLM chọn đoạn chữ nguyên văn.** Model trả `unitId`, `quote` và `role`; không trả offset. Code tìm chính xác quote trong text gốc, yêu cầu xuất hiện đúng một lần và tự tính `start`, `end` theo UTF-16. Quote rỗng, không tồn tại hoặc trùng vị trí bị từ chối. Text hiển thị được lấy lại từ nguồn; việc khớp chữ không chứng minh thông tin liên quan hoặc đầy đủ.
- **Ý được tách phải giữ nghĩa.** Model có thể chọn một phần đoạn (một câu, một mệnh đề), nhưng không được ghép chữ từ hai đoạn khác nhau trong cùng một ý.
- **Checker không gọi model.** Theo khung `IProposer` + `IChecker`: checker giữ những gì nguồn chứng minh được và ghi lý do cho những gì bị từ chối, không lặng lẽ bỏ.
- **Không tự chọn khi mâu thuẫn.** Hai dòng cùng trường nhưng khác giá trị (Sở A / Sở B) đều được giữ và đánh dấu.
- **Khớp heading với pipeline:** theo `stableId` (vị trí đoạn) trước; nếu vị trí không khớp chữ thì tìm theo chữ, tiến dần theo thứ tự, và báo `heading_located_by_text`. Không dùng chỉ số đoạn của outline: nó lệch so với bộ đọc sau bảng. Trên tài liệu 075 (outline đã lưu), 17 heading: một số phải khớp theo chữ, một heading (trải trên hai đoạn) không khớp và làm các đơn vị sau nó không chắc đường dẫn.
- **Heading từ pipeline khi có:** đường dẫn mục lấy từ kết quả heading của pipeline (`HeadingRecord`, khớp theo chỉ số đoạn và chữ). Heading không khớp file hoặc không có cấp được báo là gap; đơn vị sau heading không rõ cấp được gắn `PATH_UNCERTAIN`. Không có kết quả pipeline thì dùng style Heading và ghi `PathSource = style`.
- **Bảng là quy tắc gợi ý, không phải sự thật:** ô đầu hàng được coi là nhãn, hàng đầu là tiêu đề cột, nhưng bảng có thể đặt nhãn khác. Ghép nhãn–giá trị khác quy tắc, ghép xuyên hàng/bảng/nguồn, chạm ô gộp hoặc heading không chắc được gắn cờ để xem lại, không tự quyết định đúng hay sai.
- **Gold chỉ dùng để đánh giá**, không đưa vào lần chạy.

## 3. Luồng

```
Văn bản gốc + tài liệu nguồn
   │ 1. Pipeline heading hiện có: mỗi tài liệu → sections (heading, cấp, sourceIds) + catalog nguồn
   ▼
Mục đích: heading + nội dung (từ section của văn bản gốc)
   │ 2. Code: lấy đơn vị có chữ của từng nguồn (đoạn, gạch đầu dòng, ô bảng), kèm đường dẫn heading, nhãn hàng, tiêu đề cột
   ▼
   │ 3. LLM (proposer): theo lô, xét mục đích + đơn vị → unitId, quote nguyên văn, role
   ▼
   │ 4. Code (checker): tìm quote duy nhất trong đơn vị, tính span, loại trùng (giữ mọi nguồn), đánh dấu nghi mâu thuẫn
   ▼
Danh sách dòng được chọn (nguyên văn + nguồn) và danh sách bị từ chối kèm lý do
```

**Bước 2 — đơn vị nguồn.** Mỗi đoạn văn (kể cả trong ô bảng, và gạch đầu dòng) là một đơn vị, có `sourceId` theo vị trí `ParagraphWalker`. Với ô bảng, đơn vị kèm nhãn của hàng (ô đầu tiên của hàng) và tiêu đề cột khi có. Đường dẫn heading của từng đơn vị lấy từ heading gần nhất phía trên. Gạch đầu dòng là đoạn có numbering dạng `bullet` hoặc đoạn bắt đầu bằng ký hiệu gõ tay; chúng không phải loại riêng, chỉ là một cách trình bày. Danh sách đánh số vẫn là đoạn văn bình thường.

**Bước 3 — gửi gì cho LLM.** Gửi toàn bộ đơn vị có chữ của nguồn, không lọc theo tên mục trước, vì thông tin liên quan có thể nằm dưới mục có tên khác. Mỗi đơn vị gửi kèm đường dẫn heading và nhãn hàng (nếu là ô bảng). Mục đích gửi cả heading và nội dung hiện có. Chia lô theo số ký tự, giống `EntityProposer`.

**Bước 4 — loại trùng và mâu thuẫn (code, không model):**
- trùng tuyệt đối: cùng khóa chuẩn hóa (NFC, gộp khoảng trắng) → giữ ý đầu, ghi các nguồn còn lại;
- trùng với nội dung đã có trong mục đích → bỏ, lý do `ALREADY_IN_TARGET`;
- gần trùng (Dice trên token ≥ ngưỡng) và phần khác nhau ngắn → giữ cả hai, đánh dấu `POSSIBLE_CONFLICT` kèm đoạn chữ khác nhau. Đây là chỗ Myers có ích.

## 4. Kiến trúc

Khung bài toán hiện tại (`ProblemRunner`, `ProblemSource`) chỉ nhận **một** tài liệu. Bài toán này cần nhiều tài liệu, nên phải mở rộng khung (cần chốt, xem §7). Đề xuất:

- `ProblemSource` giữ nguyên. Thêm một bản ghi đầu vào nhiều tài liệu cho bài toán này (mục đích + danh sách nguồn, mỗi nguồn là `ProblemSource` cộng sections), và một runner nhỏ chạy pipeline heading cho từng file rồi gọi proposer/checker.
- Code bài toán đặt trong project riêng `DocxHeaderExtractor.Gather`, giống `DocxHeaderExtractor.Entities`.
- Model gọi qua `AgentModel` và `ModelRoute` như các agent hiện có; có trần chi phí mỗi lần chạy.

Quy tắc "agent heading chỉ lập luận trong một tài liệu" không đổi: nó áp dụng cho agent heading. Bài toán gom thông tin là bài toán khác, vốn đọc nhiều tài liệu.

## 5. Đánh giá

Hai loại kiểm thử, tách rõ:

- **Không phụ thuộc ngôn ngữ, chạy được trên corpus hiện có (kể cả tiếng Anh):** đọc heading và nội dung, nhận diện gạch đầu dòng, giữ `sourceId` và đường dẫn heading, chia lô, kiểm tra ID model trả về (có thật, đúng là gạch đầu dòng, không bịa). Dùng model giả để kiểm thử.
- **Phụ thuộc nhu cầu tiếng Việt thực tế, cần bộ có người đánh dấu:** recall và precision của bước chọn theo nghĩa. Bộ này gồm một văn bản gốc tiếng Việt và các tài liệu nguồn, người dùng đánh dấu dòng nào liên quan và không liên quan. Các tài liệu không cần có mục tên giống "Thông tin chung"; thông tin liên quan có thể nằm ở mục 2, mục 3 hoặc dưới tên khác, và đó chính là điều cần kiểm tra.

Chỉ số: recall (bỏ sót), precision (chọn nhầm), số dòng trùng còn sót, số mâu thuẫn bị bỏ lỡ; chi phí và thời gian mỗi lần chạy. Bộ mẫu được duyệt như Gold, độc lập với kết quả model. Chưa có bộ mẫu tiếng Việt thì không công bố số đo chất lượng cho bài toán tiếng Việt.

## 6. Lộ trình

| Bước | Nội dung | Gọi model |
|---|---|---|
| G0 | Dữ liệu: đơn vị nguồn (đoạn, ô bảng, gạch đầu dòng) + heading + nhãn hàng; lô; kiểm tra span; test đơn vị trên corpus hiện có | Không |
| G1 | Proposer trả quote và ID; checker xác định span, kiểm tra nguồn và ghi kết quả JSON | Có (test dùng model giả) |
| G2 | Chạy thật trên bộ mẫu, đo recall/precision/chi phí | Có |
| G3 | Ghi vào bản sao DOCX gốc dưới dạng Track Changes (`w:ins`), dùng định dạng danh sách của mục đích; nguồn ghi bằng comment | Không |
| G4 | Chỉ khi số đo G2 cho thấy cần: lọc trước bằng BM25/embedding, hoặc hai vòng (chọn mục rồi chọn dòng) để giảm chi phí | Tùy |

## 7. Quyết định

Đã chốt (2026-11-02):

1. Kiến trúc: runner riêng như §4; `ProblemRunner` chung không đổi.
2. Ứng viên: mọi đơn vị có chữ, gồm đoạn văn, gạch đầu dòng và ô bảng (thay quyết định cũ "chỉ gạch đầu dòng", đã đổi theo góp ý ngày 2026-11-02). Danh sách đánh số là đoạn văn bình thường.
3. Bộ mẫu: corpus hiện có dùng cho kiểm thử không phụ thuộc ngôn ngữ (§5). Bộ mẫu tiếng Việt để đo chất lượng do người dùng cung cấp và duyệt từng dòng.

Còn mở:

4. **Bảng.** Đã chốt: ô bảng là nguồn đầy đủ, không bỏ qua. Nhãn hàng và tiêu đề cột đi kèm. Bảng lồng nhau và ô gộp chưa đọc đầy đủ: phải báo trong kết quả là có thể thiếu (cùng cách `TableDiff` báo `merged_cells_present`).
5. **Model và trần chi phí.** Cấu hình được, không cố định. Chọn giá trị sau khi đo trên bộ mẫu, theo kích thước tài liệu và yêu cầu chất lượng.

6. **Đơn vị và nhiều span.** Một ý gồm một hoặc nhiều span, mỗi span là chữ nguyên văn trong một đơn vị (đoạn hoặc ô) và có vai trò (`label`, `value` hoặc `text`). Nhãn và giá trị ở hai ô hoặc hai đoạn được ghép thành một ý, mỗi phần giữ nguồn riêng. Khi hiển thị, dấu nối hoặc ": " do chương trình thêm, không coi là chữ nguyên văn; kết quả ghi rõ điều đó. Một ý có span không hợp lệ bị từ chối cả ý, không giữ phần đúng để tránh nhìn như đầy đủ.
7. **Báo thiếu có phạm vi.** Mỗi nguồn kèm danh sách thiếu: ô gộp, bảng lồng nhau, ảnh, ghi chú, tiêu đề/chân trang, theo đúng vị trí bảng khi biết (ví dụ `body[1]/tbl[3]`). Không để người dùng hiểu nhầm là đã tổng hợp đầy đủ.

Ghi chú: không đặt điều kiện nguồn phải có heading tương tự mục đích. Bước chọn theo ý nghĩa là điểm cần kiểm chứng, không phải giả định đầu vào.

Phép đo Myers (diff tài liệu) không thuộc bài toán này. Thay đổi sửa bộ nhớ đã làm đúng nhưng không làm nhanh hơn, nên không tiếp tục tối ưu Myers cho bước gom thông tin.


## Cập nhật 04/11/2026 — định tuyến mục con và so trùng

`GatherSubsectionRouter` nhận các mục đích đã được xác minh và các fact đã source-bound. Một fact có nhãn khớp duy nhất heading đích được tách nhãn, sau đó so trùng nguyên văn với nội dung trực tiếp của chính mục đó; nguồn và nhãn ban đầu vẫn nằm trong bản ghi duplicate/AlsoFrom. Nhãn không khớp, nhiều mục trùng tên hoặc thiếu căn cứ được giữ là unresolved. Một leaf đã được caller xác minh có thể nhận fact không có nhãn; không tự áp dụng tùy chọn này khi còn mục con. Đây là thành phần mới, **chưa được nối hoàn chỉnh vào luồng ghi Qwen tổng hợp**.

Với web admin `GatherByHeading`, heading cùng tên nhưng chuỗi cha khác nay được giữ `PATH_DIFFERS` để xác nhận, không tự chèn dưới parent suy đoán. Model phân loại uncertain vẫn chỉ đưa lời khuyên, không tự ghi. Điều này không chứng minh PLACE hay outline đầy đủ.

Công cụ writeback thử nghiệm tháng cũ từ chối tạo file khi các heading đích có mục con, thay vì ghi toàn bộ cuối mục cha; kiểm tra trùng được đặt sau bước tách nhãn và giới hạn trong scope. Audit 16 scope từ artifact Qwen đã đông cứng ở `artifacts/qualification/monthly-subsection-routing-20261104/audit-v2.json`: 34 fact đi vào scope leaf, 6 trùng nguyên văn, 112 chưa xác định mục con. Đây là kết quả kỹ thuật trước xác nhận outline/parent, không khẳng định các facts đúng đích hoặc mọi thông tin nguồn đã được chọn. Chưa tạo bản tổng hợp hoàn chỉnh mới.
