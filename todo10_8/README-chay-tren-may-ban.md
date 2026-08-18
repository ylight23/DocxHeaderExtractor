# Chạy pipeline trên máy của bạn

Tài liệu **không rời khỏi máy**. Không cần mạng.

## Cài đặt

```bash
pip install lxml
```

## Bước 1 — Thống kê chế độ trên cả thư mục (làm trước tiên)

```bash
python tier1_batch.py /duong/dan/thu/muc --csv thongke.csv --recursive
```

Thêm `--anonymize` nếu muốn thay tên file bằng mã băm trước khi gửi CSV đi.

Đầu ra CSV **chỉ chứa số liệu**, không chứa nội dung tài liệu. Cột
`sample_markers` chỉ ghi *loại* ký hiệu (`roman:28|num:26|alpha:11`),
không ghi chữ trong tài liệu.

Con số quan trọng nhất ở cuối:

```
>>> KHÔNG PHÂN LOẠI ĐƯỢC: n/N = x%
    < 10%  : spec gần đủ
    > 30%  : còn thiếu chế độ lớn
```

## Bước 2 — Trích outline một file

```bash
# giải nén
mkdir unpacked && cd unpacked && unzip -q ../file.docx && cd ..

# dựng canonical block model
python canon.py            # -> blocks.json

# trích outline
python vn_outline.py blocks.json --json outline.json
python vn_outline.py blocks.json --tables      # kèm block trong bảng
```

## Bước 3 — Kiểm thử

```bash
python vn_outline.py test-nghidinh30.json
```

Bộ này mô phỏng văn bản theo Nghị định 30/2020: heading trải 2 paragraph
(`Chương II` + tiêu đề IN HOA dòng dưới) và chuỗi điểm `a) b) c) d) đ) e)`.
Kết quả đúng: 14 heading, ghép 3 cặp, 0 cảnh báo.

## Cần gửi lại gì để phân tích tiếp

Chỉ cần **`thongke.csv`**. Không cần gửi tài liệu.

Từ CSV có thể biết: tỷ trọng 9 chế độ, tỷ lệ file không phân loại được,
tần suất các cấu trúc khó (tracked changes, content control, textbox,
bảng dày, heading 2 dòng), và từ đó biết spec còn thiếu gì.

## Corpus 95 → 89 file (2026-08-18)

Quét route tất định trên cả 95 file, rồi phân loại nhóm **không rơi vào luật nào** (22 file):

| nhóm | số file | có heading để trích? | xử lý |
|---|--:|---|---|
| **A — rỗng thật** | 6 | **KHÔNG** — chỉ header chuyển đổi + chữ ký số | **đã xoá** |
| **B — biên bản họp** | 13 | CÓ, nhưng **không mốc đánh số nào** | giữ |
| **C — nhiều mốc mà không route** | 3 | CÓ, rất nhiều | giữ — đây là LỖ HỔNG |

### Nhóm A — đã xoá

`002` `011` `014` `016` `022` `023`. Mỗi file 242–304 ký tự, nội dung chỉ gồm dòng
`Converted to DOCX from PDF text-layout extraction` cộng khối chữ ký số của Cổng Thông tin điện tử
Chính phủ. `016` ghi thẳng `[No extractable text found by pdftotext]` — PDF gốc là ảnh, chuyển đổi
không ra chữ nào.

Không có gì để trích, và giữ chúng làm mọi tỉ lệ trên corpus bị pha loãng bởi mẫu số không thể đạt.

### Nhóm B — GIỮ, đừng xoá

13 biên bản họp (`072`–`080`, `073`–`075`) có 2.739–52.218 ký tự nhưng **0 mốc đánh số**. Tiêu đề
tồn tại dưới dạng nhãn ngữ nghĩa thuần (`Welcome address`, `Next meeting`), không có `1.`/`Điều`.

Đây là nhóm KHÓ NHẤT và cũng là nhóm đáng giá nhất: thí nghiệm LLM đo được **85,7%** trên chính
nhóm này, ngang với hai nhóm có mốc neo. Xoá chúng là xoá bằng chứng rằng bài toán giải được khi
không còn tín hiệu hình thức nào.

### Nhóm C — GIỮ, đây là lỗ hổng chưa vá

| file | mốc cấu trúc | ký tự |
|---|--:|--:|
| `063_Advanced_Linear_Algebra` | **797** | 1.028.266 |
| `019_TT_200-2014_Che_do_ke_toan_DN` | **542** | 1.102.570 |
| `020_TT_133-2016_Che_do_ke_toan_SME` | **302** | 833.856 |

Ba file này có **1.641 mốc cấu trúc trên 2,9 triệu ký tự** mà không route nào kích hoạt. Không phải
"không có heading" — mà là **cây quyết định chưa phủ tới**. Xoá chúng là giấu đi một lỗ hổng thật.

### Phân bố route sau khi dọn

```
auto:typed-numbering       39
auto:vietnamese-legal      23
(không route)              22 → còn 16 sau khi xoá nhóm A
auto:outline-level         10
auto:part-section-text-toc  1
```
