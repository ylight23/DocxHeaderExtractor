# Hai bản chuẩn để test pipeline: 074 và 075

Trạng thái: đang dùng. Ngày: 2026-11-03.

## Quy định

- Mọi test và lần chạy pipeline ghép nội dung (074 → 075) dùng **hai file chuẩn** dưới đây.
- Không dùng các bản cũ: `todo10_8/heading_corpus_95_word/…` (pdf2docx, gộp dòng nhãn) và `todo10_8/generated-docx-v2/…` (converter v1, chỉ giữ chữ). Hai bản cũ này còn trong repo để đối chiếu, không dùng để test.
- Nếu PDF gốc hoặc converter thay đổi thì phải tạo lại cả hai file và cập nhật mã băm bên dưới.

## Hai file

| Văn bản | File chuẩn (DOCX) | SHA-256 DOCX | PDF gốc | SHA-256 PDF |
|---|---|---|---|---|
| 074 (họp 27/11/2024) | `todo10_8/standard-docx-v3/05_bien_ban_hop/074_FORTIS_GC_Minutes_Nov27_2024.docx` | `79a41bf07713edcc2bff32b8fa7518c19c457cfe3af2f13bb6b6812722e5da08` | `todo10_8/heading_corpus_100/05_bien_ban_hop/074_FORTIS_GC_Minutes_Nov27_2024.pdf` | `a72ce980c23f00196a257f63cf140edf700104f03fdd7fcfbd2d30d2c9f1eee8` |
| 075 (họp 21/11/2024) | `todo10_8/standard-docx-v3/05_bien_ban_hop/075_FORTIS_GC_Minutes_Nov21_2024.docx` | `1e03bd6986c228f2d097877c070b648fee695db70c37f38b01ee3c7948673d46` | `todo10_8/heading_corpus_100/05_bien_ban_hop/075_FORTIS_GC_Minutes_Nov21_2024.pdf` | `c48a9e9ac95820685c48def8b91df697484f81996cb6c4094234ed6c8319afc3` |

## Cách tạo

- Converter: `tests/DocxHeaderExtractor.Tests/PdfLineDocxConverter.cs` (v2). Giữ: chữ, đậm, gạch chân, font và cỡ chữ, căn lề (trái, giữa, đều), khoảng cách đoạn, đường kẻ ngang dưới dạng đoạn có viền dưới. Không ghi style heading, không ghi cấp heading.
- Test: `tests/DocxHeaderExtractor.Tests/StandardDocumentsTests.cs` kiểm tra file khớp đầu ra của converter, đủ mọi chữ của PDF, đủ số đường kẻ và số chữ gạch chân.
- Tạo lại: đặt biến môi trường `A99_REGENERATE_STANDARD=1` rồi chạy test `StandardDocumentsTests`.

## Đã kiểm tra

- Trang 1 của 074 và 075 đã so trực quan với PDF: đúng font Times New Roman, hai đường kẻ ở 074, gạch chân "Subsequent Note:", căn đều đoạn, căn giữa tiêu đề.
- Còn khác: danh sách "−" không có thụt lề treo; hai dòng tiêu đề giữa của 075 bị gộp thành một đoạn. Không ảnh hưởng chữ.
- Chưa kiểm tra các trang sau.

## Ghi chú về pipeline

- Heading của 074 và 075 chưa được xác nhận trên hai file chuẩn này. Đây là việc tiếp theo.
- Cập nhật 2026-11-04 theo quyết định của người dùng: `Governance/Voting Rights.` và `Subsequent Note:` trong 074 được coi là heading. Các đánh giá trước đây coi hai nhãn này là false positive không còn áp dụng; không suy rộng rằng mọi nhãn in đậm đều là heading.
