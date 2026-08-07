# bench — bộ test có đáp án

Mỗi `X.docx` cần một `X.key` đi kèm. Thiếu `.key` thì `dhx eval` bỏ qua file đó.

```powershell
dhx bench                  # sinh lại 9 tài liệu tổng hợp kèm đáp án (01..06, 07-chen-chi-thi,
                           # 08-danh-sach-da-cap, 09-style-ap-sai)
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
| `07-chen-chi-thi` | Dòng tiêm chỉ thị mang đủ bốn tín hiệu hình thức của tiêu đề — do `dhx bench` sinh |
| `08-danh-sach-da-cap` | Multilevel list gắn style Heading, đoạn không đậm không style — do `dhx bench` sinh |
| `07-mau-that` | File thật, gán nhãn tay — **trùng số thứ tự với `07-chen-chi-thi`** |
| `08-plph2` | File thật 898 đoạn — **`.docx` không có trong repo**, chép vào đây với tên `08-plph2.docx` |
| `09-style-ap-sai` | Style Heading áp nhầm cho dòng bìa, chú thích bảng, nhãn chữ ký — style nói dối |
| `10-…` trở đi | Chỗ thả tài liệu thật của riêng máy bạn; `.key` bị `.gitignore` chặn (§7.6) |

**Số thứ tự `07` và `08` bị dùng hai lần.** `dhx bench` sinh `07-chen-chi-thi`/`08-danh-sach-da-cap`,
còn git theo dõi `07-mau-that.key`/`08-plph2.key`. Chạy `dhx bench bench` vào đúng thư mục này thì
có đủ cả bốn và bộ đo thành 10 tài liệu. Nói "07" mà không nói tên đầy đủ là nói nước đôi.

## Bộ đo trên MÁY BẠN không chắc là bộ đo trong bảng số

Chỉ `07-mau-that.docx` được theo dõi bằng git. `01`–`06` sinh lại được nhưng phải chạy `dhx bench`;
`08` phải tự chép vào; `09+` là tài liệu riêng. Nghĩa là **hai máy chạy `dhx eval bench` có thể đang
chấm hai bộ khác nhau** trong khi cùng gọi nó là "bench".

Chuyện này đã xảy ra thật (handoff §10): một máy có `07-mau-that` thay vì `07-chen-chi-thi`, thiếu
hẳn `08-danh-sach-da-cap`, có `08-plph2.key` mà không có `.docx`, cộng thêm tài liệu thật `09`. Nên
`dhx eval bench` chấm 8 tài liệu trong đó **2 là tài liệu thật**, thiếu đúng tài liệu mà §5 và §7.3
bàn suốt — và cho F1 90,9% ở đúng bản code mà §7.3 ghi 100%. Không phải hồi quy, chỉ là khác bộ.

Hai hàng rào, cả hai đều là cơ chế chứ không phải quy ước:

- `dhx eval` **cảnh báo đáp án mồ côi** (có `.key`, không có tài liệu) — nhóm trước đây hoàn toàn
  vô hình vì phép duyệt đi từ tài liệu.
- Báo cáo eval in **chữ ký cấu hình** đầy đủ, kèm số lớp offload GPU và seed.

Trước khi so bất kỳ con số nào với một bảng trong `handoff.md`, đọc dòng liệt kê tài liệu ở đầu báo
cáo và đối chiếu với bảng ở trên.

Chín tài liệu tổng hợp do `BenchDocumentFactory` sinh ra: đáp án nằm ngay trong định nghĩa đoạn nên
`.docx` và `.key` luôn khớp, không có khâu gán nhãn tay để mà sai. `.docx` bị `.gitignore` chặn vì
sinh lại được bằng `dhx bench`.

## Giới hạn

9 tài liệu, ~45 tiêu đề là **rất nhỏ** — mỗi lỗi đơn lẻ đã dịch vài phần trăm. Chúng lại do
chính tác giả sinh ra *sau khi* đã biết mô hình hay sai ở đâu, nên chúng chỉ chắc chắn bắt được
lỗi hồi quy, không thay được tài liệu thật. Muốn con số nói được điều gì về tài liệu chưa từng
thấy thì phải thả thêm tài liệu thật vào đây.
