# Thí nghiệm scan — tài liệu 023 (Nghị định 23/2015)

Tài liệu: `todo10_8/generated-docx/01_phap_quy/023_ND_23-2015_Chung_thuc.docx`, 34 trang scan, 1115 dòng hybrid (1084 dòng Tesseract).

Bảng số liệu đầy đủ từng lần chạy: [table-runs-s046-s060.md](table-runs-s046-s060.md).

## Cảnh báo khi đọc số

- **Gold đã được thay ngày 2026-10-21** theo yêu cầu của chủ dự án: `eval/scan-text/023_heading_lines.v1.json` nay là 111 dòng của run `s060` (chấp nhận theo nội dung, chưa duyệt từng dòng). Gold cũ 120 dòng (Claude đọc từ ảnh, alias Tesseract) được giữ ở `023_heading_lines.v0_replaced_2026-10-21.json`.
- **Số F1 trong bảng là theo Gold cũ.** Với Gold mới, `s060` đạt F1 1,0 theo định nghĩa, nên số đo lại chỉ là độ giống với `s060`, không đo được chất lượng.
- **F1 hybrid và Tesseract không so sánh được trên Gold cũ** vì alias lệch (Điều 1–7, 37, 38, 45, 47–49 không khớp dù đúng).
- **Gold không có khoản đánh số nào.** Theo định nghĩa đã được chủ dự án chốt ngày 2026-10-21 (xem memory `project_scan_023_accepted_output`), khoản trong thân văn bản không phải heading, mục đánh số và chữ cái của Phụ lục là heading. Gold chưa được cập nhật theo định nghĩa này.
- Số chi phí và thời gian là của đúng lần chạy đó, không gồm phần tạo dòng hybrid bằng Python (khoảng $0.056 và 51 phút cho 34 trang, đã trả một lần).
- Một lần chạy không đủ để kết luận về độ ổn định. Xem phần "Độ ổn định" bên dưới.

## Các cấu hình đã thử

| Thuật ngữ | Ý nghĩa |
|---|---|
| Cắt theo trang | Mỗi trang là một unit (`PageCores`, tối đa 48 dòng). Đây là cách cũ. |
| Cắt theo đoạn | Unit kết thúc ở ranh giới đoạn đo từ khoảng trống dòng; gộp qua trang đến tối đa 48 dòng (`ParagraphCores`). |
| Lượt hai (`precedingSelected`) | Chạy lại unit với alias dòng được chọn gần nhất trước unit. Đã tắt. |
| Ngữ cảnh 40 dòng | Hiển thị 40 dòng mỗi bên của unit (thay cho 20). |
| Kiểm tra nhất quán (`AgentSiblingReview`) | Dãy đánh số cùng hình thức và quyết định lẫn lộn được hỏi lại một lần. |
| `contextFacts` | Đưa fact của các dòng ngữ cảnh vào request (chỉ scan). Tắt. |
| Gate `get_line_facts` | Bắt model gọi tool đọc fact trước khi trả lời. Tắt. |
| Gate `get_scan_crop` | Bắt model xem ảnh crop trước khi trả lời. Tắt. |
| Transcript | Ghi request, raw response, `precedingSelected`, SHA của từng lượt gọi. Bật mặc định. |

## Kết quả chính

**Chia unit:** cắt theo đoạn cho 24 unit thay vì 33 và giảm lượt gọi (29–33 so với 103 ở cùng số lượt chọn). Hội thoại giảm từ 584 s (s049) xuống 210–363 s (s050–s054, s060).

**Lượt hai:** s049–s052 chạy hai lượt; s053 trở đi chạy một lượt. Lượt hai làm tăng gấp đôi lượt gọi và chi phí (s049 $0.081 so với $0.046 của một lượt) nhưng không cho thấy cải thiện F1 rõ ràng. Lượt hai đổi quyết định của P020-L014 nhiều lần, không ổn định.

**Ngữ cảnh 40 dòng:** tăng từ 20 dòng không sửa được P020-L014 (s049 vẫn chọn).

**Gate tool:**
- `get_line_facts` (s056): 77 lượt gọi, $0.092, không đổi kết luận P020-L014 (đổi sang N).
- `get_scan_crop` (s059): 79 lượt gọi, $0.081, 68 lượt crop. P020-L014 vẫn Y. Một unit bị lỗi (SEG-022, `INCOMPLETE:UNKNOWN`).
- Model chọn quyết định ngay ở lượt đầu rồi xác nhận lại khi bị gate; gate không đổi kết luận.

**`contextFacts`:**
- s055: 16/24 unit bị từ chối vì request vượt trần 128 KB (s055 không hợp lệ để đánh giá chất lượng).
- s057: request không chứa `contextFacts` do lỗi gọi trùng trong code. **Kết quả không hợp lệ, đã bỏ.**
- s058 (đã sửa lỗi, trần 256 KB): đúng `contextFacts` ở 28/28 request, P020-L014 N, nhưng chi phí $0.183 (gấp 3,6 lần) và mất 2 dòng Gold (P001-L007, P030-L001).

**Kiểm tra nhất quán:** s060 không kích hoạt lần nào vì không có dãy nào mâu thuẫn. Mô phỏng offline trên s059 cho thấy đúng một dãy (khoản 1–2–3 của Điều 43) sẽ được hỏi lại. Chưa kiểm chứng được bước này trên một lượt gọi thật có mâu thuẫn.

**Lựa chọn định nghĩa khoản:** trên 15 lần chạy, P020-L014 được chọn ở 5 lần và không được chọn ở 10 lần; khoản 1 và 2 của Điều 43 không được chọn ở lần nào. Không có chạy nào chọn khoản đánh số trong thân văn bản một cách có hệ thống.

## Độ ổn định

Trên ba lần cùng cấu hình (cắt theo đoạn, một lượt): tập heading trùng nhau 0,94–0,97 (Jaccard). Các dòng dao động: P020-L014/L015, P002-L022/L023, P023-L024, P032-L018, P033-L016. Trên tất cả 8 lần chạy đã thử: 100 dòng được chọn ở mọi lần và 129 dòng được chọn ít nhất một lần.

Chưa đủ để so sánh cắt theo trang với cắt theo đoạn về độ ổn định: cách cắt theo trang chỉ có những lần chạy cũ khác code. Cần ba lần cùng code cho mỗi cách.

## Trạng thái production sau khi promote

- Mặc định của scan: **cắt theo đoạn**, một lượt chọn, kiểm tra nhất quán bật, transcript và raw bật, gate tool tắt, `contextFacts` tắt.
- `--page-units` quay về cắt theo trang. `--paragraph-units` vẫn được chấp nhận và là mặc định.
- Các thí nghiệm không dùng vẫn nằm trong code, không bật: `precedingSelection` (tham số của `HeadingAgentSelector.Select`), `AgentUnit.ScanRequiresFactsTool`, `AgentUnit.ScanContextFacts`.
- Bộ test scan, pipeline và CLI: 132/132 pass sau khi promote.

**Điều chưa được kiểm chứng:**
- Cấu hình promote (cắt theo đoạn) chỉ được chạy trên nguồn dòng **hybrid** (store Python). Production vẫn dùng **Tesseract** làm nguồn dòng, và tổ hợp cắt theo đoạn + Tesseract **chưa được chạy lần nào**.
- Production Tesseract đạt F1 0.953 trên Gold cùng alias (`s046/prod`, code cũ). Chưa có lần nào của cấu hình mới trên Tesseract để so sánh. Đây là việc cần làm trước khi coi promote là an toàn.
- Chưa chạy toàn bộ bộ test của repo sau khi promote; mới chạy nhóm scan/pipeline/CLI.
- Chưa commit.

## Việc tiếp theo đề xuất

1. Chạy cấu hình promote trên Tesseract (`scan-extract` không dùng `-o` hybrid store), ít nhất ba lần, và so với `s046/prod` trên cùng Gold.
2. Cập nhật Gold theo alias đang dùng và theo định nghĩa khoản đã chốt, qua quy trình duyệt từng mục của bạn.
3. Chạy toàn bộ bộ test và commit khi bạn yêu cầu.
