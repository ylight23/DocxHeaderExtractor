# HANDOFF — Trích xuất outline Heading từ DOCX tiếng Việt

Tài liệu bàn giao cho phiên làm việc mới (Claude Code hoặc chat mới).
Đọc file này trước, rồi đọc `spec-heading-outline-v2.md` khi cần chi tiết.

Cập nhật: 2026-08-10

---

## 1. Mục tiêu

Xây pipeline tự động trích cây outline Heading từ file `.docx` tiếng Việt.
Chạy local trên RTX 3060 12GB. Mục tiêu độ chính xác > 95% trên nhánh
auto-accept, phần còn lại chuyển người duyệt.

**Ràng buộc quan trọng:** tài liệu có yêu cầu bảo mật → xử lý trên máy,
không tải lên dịch vụ bên ngoài. Dùng file đã khử nội dung khi cần chia sẻ.

---

## 2. Trạng thái hiện tại

### Đã xong

| Hạng mục | File | Ghi chú |
|---|---|---|
| Spec đầy đủ | `spec-heading-outline-v2.md` | v2.3, ~1100 dòng, 9 chế độ tài liệu |
| Canonical block model | `canon.py` | gộp run ngữ nghĩa, quy tắc whitespace, phát hiện paragraph hỏng |
| Context builder | `ctx.py` | sinh request cho tầng LLM |
| Outline hành chính/pháp quy | `vn_outline.py` | suy cấp theo cha, bảng chữ cái tiếng Việt, ghép heading 2 dòng |
| Phân loại chế độ hàng loạt | `tier1_batch.py` | duyệt thư mục → CSV thống kê |
| Bộ kiểm thử Nghị định 30 | `test-nghidinh30.json` | 14 heading, ghép 3 cặp, 0 cảnh báo |
| Hướng dẫn chạy | `README-chay-tren-may-ban.md` | |

### Chưa xong — theo thứ tự ưu tiên

1. **Chạy `tier1_batch.py` trên 50–100 file thật** ← VIỆC TIẾP THEO
2. Cài Qwen + benchmark 4B vs 9B
3. Xây tập test có nhãn cho chế độ `vn-administrative`
4. Tầng 3 (LLM lọc ứng viên) — chưa viết dòng code nào
5. Tầng 4 vòng phản hồi Pass 2 → Pass 1 — chưa có
6. Tài liệu ghép nhiều chế độ — lỗ hổng kiến trúc, chưa có hướng giải

---

## 3. Việc tiếp theo, cụ thể

```bash
pip install lxml
python tier1_batch.py /duong/dan/tai-lieu --csv thongke.csv --recursive
```

Nhìn con số cuối cùng:

```
>>> KHÔNG PHÂN LOẠI ĐƯỢC: n/N = x%
```

- `< 10%` → spec gần đủ, chuyển sang việc số 2
- `10–30%` → xem cột `sample_markers` của các dòng `UNCLASSIFIED`, tìm mẫu chung
- `> 30%` → còn thiếu chế độ lớn, phải bổ sung trước khi làm gì khác

Cũng xem cột `flags`: `table_heavy`, `two_line_heading`, `tracked_delete`,
`sdt`, `field`, `textbox` — tần suất cao thì ưu tiên hoàn thiện luật tương ứng.

CSV chỉ chứa số liệu, không chứa nội dung tài liệu — an toàn để chia sẻ phân tích.
Thêm `--anonymize` nếu cần ẩn cả tên file.

---

## 4. Kết luận cốt lõi — đọc kỹ, đây là phần dễ làm sai lại

### 4.1 Không có luật deterministic dùng chung

Đo trên 5 tài liệu thật, hai tài liệu cùng loại cho kết quả **trái ngược**:

| | Báo cáo thực tập | Khóa luận |
|---|---|---|
| `pStyle` chính xác | **49%** | **100%** |
| `numPr` chính xác | **100%** | **0%** (toàn bullet) |

→ Bắt buộc phân loại chế độ tài liệu trước (tầng 1).

### 4.2 Luật lo RECALL, LLM lo PRECISION

Đo được: luật đạt recall 0,77–1,00 nhưng precision chỉ 0,38–0,56.
Sai của luật là **bắt nhầm**, không phải bỏ sót.

→ LLM chỉ trả lời một câu hỏi nhị phân hẹp: *"ứng viên này là heading
hay là nhãn/câu văn?"* — KHÔNG phân loại mọi block.

### 4.3 Mọi luật loại bỏ cứng đều nguy hiểm

Spec đã phải sửa **ba lần**, cả ba cùng một dạng lỗi:

1. `pStyle` là deterministic → sai 51%
2. Loại mọi block trong bảng → mất 40 heading (tài liệu D có 87% block trong bảng)
3. `len(text) > 150` → loại → mất heading dạng `heading_with_inline_body`

→ Mặc định khi thêm luật mới: **hạ confidence + gắn cờ**, không `loại`.

### 4.4 Khi thông tin không có thì abstain

Thử nghiệm: xóa `pStyle` + `numPr`, giữ định dạng thị giác → luật đạt tốt
nhất F1 = 0,65. Nguyên nhân đo được từ `styles.xml`:

- Báo cáo thực tập: heading và thân bài **cùng cỡ chữ 13pt**, chỉ khác bold
- Khóa luận: `Heading4` (12pt) và `Heading5` (11pt) **nhỏ hơn** thân bài, không bold

Khi tài liệu không lưu ranh giới heading dưới bất kỳ dạng nào, thông tin đó
**không tồn tại**. Không model nào tạo lại được. Abstain, đừng đoán.

Kết quả thử nghiệm đầy đủ (tài liệu A, đã xóa style):
tầng 2 recall 31/31 → tầng 3 (LLM) reject 31, uncertain 17, accept 31
→ **P=1,000 R=1,000, abstain rate 22%**.

---

## 5. Bẫy kỹ thuật — đã mất thời gian vì những cái này

### 5.1 Whitespace

`<w:t>` không có `xml:space="preserve"` bị cắt khoảng trắng rìa. Áp dụng
nguyên văn sẽ ra `Tìnhhìnhhoạtđộng…`. Quy tắc đúng (kiểm chứng bằng render PDF):

```python
if not preserve:
    txt = ' ' if txt and not txt.strip() else txt.strip(' \t\r\n')
```

### 5.2 Gộp run

`merge_runs` chuẩn chỉ gộp được 34/5464 run, vì phân mảnh do **kerning từng
ký tự** (`w:spacing`), không phải rsid. Gộp theo ngữ nghĩa (bỏ qua `spacing`,
`kern`, `position`, `lang`, `rFonts`) → giảm **89%**.

### 5.3 Paragraph hỏng có thật

Một paragraph render ra ký tự lặp đôi (`HHììnnhh`) do hai luồng run xen kẽ
phân biệt bởi `w:position`. **Word vẽ đúng như vậy** — đã kiểm chứng bằng
render PDF ra ảnh. Không có bước nhìn ảnh thì đã kết luận sai là lỗi parser.

### 5.4 Bảng chữ cái tiếng Việt

Nghị định 30 quy định điểm dùng "chữ cái tiếng Việt":
`a ă â b c d đ e ê g…` — KHÔNG phải Latin.

Nhưng **thực tế** tác giả dùng `a b c d đ e g…` (bỏ biến thể dấu).
→ Validator phải chấp nhận **cả hai** chuỗi, nếu không báo lỗi giả hàng loạt.

Regex `[a-z]` không bắt được `ă â đ ê ô ơ ư`. `f j w z` không có trong
bảng chữ cái tiếng Việt.

### 5.5 Heading trải 2 paragraph là BẮT BUỘC theo luật

Nghị định 30/2020: "Phần"/"Chương" + số trên **dòng riêng**, canh giữa,
in thường, đậm; tiêu đề ở **dòng ngay dưới**, canh giữa, IN HOA, đậm.

```
Chương II                          ← paragraph 1
SOẠN THẢO, KÝ BAN HÀNH VĂN BẢN     ← paragraph 2
```

→ Mọi văn bản hành chính nhà nước đều có ca này. Luật ghép ở tầng 2, đã
cài trong `vn_outline.py::merge_two_line_headings`.

### 5.6 Suy cấp theo ngữ cảnh cha

KHÔNG gán cứng `a)` = cấp 4. Trong tài liệu thật `a) b) c)` đứng ngay dưới
`1. 2. 3.` mà không qua cấp `x.y`.

```python
while stack and stack[-1][0] >= rank:
    stack.pop()
level = (stack[-1][1] + 1) if stack else 1
```

Và **rank của `num` (x.) phải CAO hơn `dec2` (x.y)** — dù regex vẫn kiểm
`dec2` trước để `3.1.` không bị `^\d+\.` bắt nhầm.

### 5.7 Tách heading/body — tìm chỗ NỘI DUNG bắt đầu

Dấu `:` không nhất quán ngay trong cùng dãy anh em:

```
b. KQ Mỹ: 0/0 (0/0).        ← CÓ
c. KQ Philippin 0/0 (0/0)   ← KHÔNG
```

Thuật toán: `cut = min(vị_trí_payload, vị_trí_dấu_hai_chấm)`.
Payload cứu ca không dấu câu, dấu `:` cứu ca payload là chữ.
Kiểm chứng 8/8 đúng.

Regex `PAYLOAD` phải **cấu hình theo domain** — danh sách đơn vị đo
(`tốp`, `tàu`, `l/c`) là đặc thù báo cáo quân sự.

### 5.8 Bảo mật khi giải nén

`.docx` là ZIP từ nguồn ngoài → untrusted. Chặn symlink, chặn path
traversal (`../`), parse XML với parser tắt entity (chống XXE).

---

## 6. Sai lầm đã mắc — đừng lặp lại

| Sai lầm | Hậu quả | Bài học |
|---|---|---|
| Tin `pStyle` là deterministic | sai 51% | luôn đối chứng bằng nguồn độc lập |
| Dùng `Counter.most_common(8)` để đếm style | báo nhầm "TOC = 0" cho tài liệu có 28 mục TOC | đừng cắt output khi đang đếm |
| `r_outline > 0` để chọn chế độ | 8/900 block cũng kích hoạt | ngưỡng phải là **độ phủ**, không phải sự tồn tại |
| Suy đoán từ 2 tài liệu | 3 file sau phá vỡ 1 chế độ | mở rộng mẫu trước khi kết luận |
| Regex `\s+\S` cuối marker | nuốt mất ký tự đầu (`- T ích hợp`) | dùng lookahead `(?=\S)` |

---

## 7. Lựa chọn model (chưa benchmark)

Card RTX 3060 12GB:

| Model | VRAM Q4 | Ghi chú |
|---|---|---|
| Qwen3.5-4B | ~2,5 GB | headroom lớn nhất cho batching |
| **Qwen3.5-9B** | ~6,6 GB | khuyến nghị mặc định |

Qwen3.5 dùng kiến trúc lai (Gated DeltaNet 3:1), chỉ 10/40 lớp dùng KV cache
→ context 4K→64K chỉ tốn thêm ~800MB. Ràng buộc VRAM cho context dài
gần như biến mất.

**Nhưng** kiến trúc nén bất lợi cho tác vụ liệt kê *đầy đủ* nhiều mục rải rác
(nhiều kim, không phải một kim) → giữ kiến trúc nhiều tầng, đừng nạp full-doc.

Cấu hình: constrained decoding (XGrammar/GBNF), `temperature=0`,
non-thinking cho tầng 3, bật thinking cho tầng 5b.

Tải LLM chênh rất mạnh theo chế độ:
`typed-numbering` 0 lượt · `numpr-driven` 2–4 lượt · `vn-administrative` 25–30 lượt.

---

## 8. Đo lường

Không dùng "% block phân loại đúng" — trivially cao vì đa số block là body.

Dùng: heading recall, heading precision, **exact span accuracy**,
level accuracy, tree edit distance, **accept precision** (mục tiêu ≥ 0,99),
abstain rate. **Báo cáo tách theo chế độ.**

Nguồn nhãn miễn phí: TOC field do Word sinh. Nhưng **TOC có thể lỗi thời** —
đo được ca tác giả sửa tiêu đề mà không refresh, pipeline lấy được bản
hiện hành còn TOC giữ bản cũ.

Ngân sách token đo được (ước lượng `len/3`, tiếng Việt thực tế tệ hơn 1,5–2×):
raw `document.xml` ~247K · text sạch ~12,5K · outline skeleton ~2,2K ·
1 request tầng 3 ~575.

---

## 9. Câu hỏi mở

1. Tỷ trọng 9 chế độ trong tập thật? → chạy `tier1_batch.py`
2. Bao nhiêu file `UNCLASSIFIED`? → còn thiếu bao nhiêu chế độ
3. Qwen 4B có đủ không, hay cần 9B? → benchmark cùng harness, cùng tập test
4. Tài liệu ghép nhiều chế độ giải thế nào? → có thể phải chuyển tầng 1
   từ "phân loại file" sang "phân loại từng vùng" (theo `<w:sectPr>` hoặc mốc `PHỤ LỤC`)
5. 5 thể loại ở spec 14.4 (biên bản, tài chính, giáo trình, dịch, sinh tự động)
   — hoàn toàn là giả thuyết, chưa có tài liệu mẫu nào

---

## 10. Cách dùng file này với Claude Code

```bash
# đặt tất cả file vào thư mục dự án
cd /duong/dan/du-an
claude
```

Rồi nói: *"Đọc HANDOFF.md và spec-heading-outline-v2.md. Việc tiếp theo là
mục 3."*

Lưu ý bảo mật: đặt thư mục **tài liệu** tách khỏi thư mục **code**. Mở Claude
Code ở thư mục code, truyền đường dẫn tài liệu qua tham số — đừng mở thẳng
trong thư mục chứa tài liệu nhạy cảm.
