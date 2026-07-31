# bench — bộ test có đáp án

Mỗi `X.docx` cần một `X.key` đi kèm. Thiếu `.key` thì `dhx eval` bỏ qua file đó.

```powershell
dhx bench                  # sinh lại 6 tài liệu 01..06 kèm đáp án
dhx eval bench --no-llm    # chấm riêng tầng luật OpenXML, vài giây
dhx eval bench -m models\Qwen2.5-7B-Instruct-Q4_K_M.gguf --ctx 8192
```

`dhx eval` trả exit code 1 khi còn sai sót, cắm thẳng vào CI được.

## Định dạng .key

Mỗi dòng một mục, `<chỉ số đoạn> <cấp>`; phần sau `#` là chú thích. Bỏ trống cấp thì chỉ chấm
việc chọn đúng đoạn, không chấm cấp.

```
95 1     # I. THÔNG TIN CHUNG
96 2
101      # không ghi cấp ⇒ chỉ chấm việc chọn
```

Chỉ số `i` lấy từ `dhx xml <file> --compact`.

## Tài liệu trong bộ

| File | Nhắm vào |
|---|---|
| `01-style-chuan` | Heading1–3 chuẩn — bài nền, sai ở đây là hỏng nền |
| `02-dinh-dang-thu-cong` | Không style nào, chỉ đậm/hoa/canh giữa |
| `03-bang-ma-hieu` | Ô bảng `II.1`, `III.1` đậm/hoa/canh giữa — dữ liệu, không phải tiêu đề |
| `04-bia-muc-luc-chu-thich` | Trang bìa, dòng mục lục có neo `_Toc`, chú thích `Hình 1.1.` |
| `05-outline-sai` | Gạch đầu dòng mang `outlineLvl` sai + tiêu đề mất style |
| `06-style-ban-dia` | Style `"Tiêu đề 1"` không kèm `outlineLvl` |
| `07-mau-that` | File thật, gán nhãn tay |
| `08-plph2` | File thật 898 đoạn — **`.docx` không có trong repo**, chép vào đây với tên `08-plph2.docx` |

Sáu file đầu do `BenchDocumentFactory` sinh ra: đáp án nằm ngay trong định nghĩa đoạn nên `.docx`
và `.key` luôn khớp, không có khâu gán nhãn tay để mà sai. `.docx` bị `.gitignore` chặn vì sinh
lại được bằng `dhx bench`.

## Giới hạn

8 tài liệu, ~40 tiêu đề là **rất nhỏ** — mỗi lỗi đơn lẻ đã dịch vài phần trăm. Sáu file lại do
chính tác giả sinh ra *sau khi* đã biết mô hình hay sai ở đâu, nên chúng chỉ chắc chắn bắt được
lỗi hồi quy, không thay được tài liệu thật. Muốn con số nói được điều gì về tài liệu chưa từng
thấy thì phải thả thêm tài liệu thật vào đây.
