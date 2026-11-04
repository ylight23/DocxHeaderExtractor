# Đối chiếu 075: PDF (heading_corpus_100) với DOCX (heading_corpus_95_word)

Cách làm: `compare.py` (chạy lại được) đọc trực tiếp `word/document.xml` của DOCX, lấy văn bản PDF bằng `pdftotext -layout`, rồi so chuỗi bằng difflib. Kết quả đầy đủ nằm trong `compare-output.txt`.

## Kết quả theo ba mức so sánh

1. **Chỉ chữ và số, không phân biệt hoa thường, bỏ khoảng trắng và dấu câu:** 7.640 ký tự mỗi bên, khớp tuyệt đối (ratio 1,00000, 0 vùng khác).
2. **Giữ dấu câu, bỏ khoảng trắng, đổi dấu nháy cong và ngoặc kép cong thành thẳng:** 7.920 ký tự mỗi bên, khớp tuyệt đối. Bước đổi dấu nháy che đi khác biệt sau:
   - Khi giữ nguyên dấu nháy cong, DOCX dùng U+2019 và U+201C/U+201D, còn PDF dùng ' và ". Có khoảng 12 vùng như vậy (ví dụ "Ukraine’s", "“For the avoidance…”").
3. **Giữ cả dấu câu và khoảng trắng:** chênh lệch chủ yếu là ngắt dòng và thụt đầu dòng của PDF (`\r\n`, khoảng trắng bố cục), không phải nội dung. Có một khác biệt nội dung thật: DOCX ghi "theFacilitation" (thiếu khoảng trắng) ở tiêu đề và đoạn Agenda; PDF ghi "the Facilitation".

## Số trang

Cả hai file có các số trang 1–4. DOCX lưu chúng thành các đoạn riêng trong thân tài liệu; PDF in chúng ở chân trang. Trong mức 1, số trang khớp nhau.

## Kết luận

- Phần chữ và số của hai file giống nhau tuyệt đối.
- Khác biệt về dấu câu chỉ là kiểu dấu nháy (cong và thẳng).
- Khác biệt về khoảng trắng: "theFacilitation" trong DOCX.
- Khác biệt về bố cục: ngắt dòng và thụt đầu dòng của PDF.
- Không so được level heading vì PDF không có style Heading trong văn bản thô.
