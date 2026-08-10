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
