# Spec: Trích xuất outline Heading từ DOCX

Phiên bản 2.3 — năm tài liệu tiếng Việt thật (2 học thuật, 3 hành chính) + chuẩn thể thức chính thức. Bổ sung mục 14: phân loại thể loại văn bản Việt Nam theo Nghị định 30/2020/NĐ-CP.
Thay thế `spec-ooxml-style-filter.md` (v1), vốn dựa trên giả định sai rằng `pStyle` là tín hiệu deterministic.

---

## 0. Nguyên tắc thiết kế

Ba nguyên tắc rút ra từ đo đạc thực tế, không phải từ lý thuyết:

**N1 — Không tồn tại một luật deterministic dùng chung.** Hai tài liệu cùng loại (báo cáo học thuật tiếng Việt) cho kết quả trái ngược hoàn toàn với cùng một luật. Bắt buộc phải phân loại chế độ tài liệu trước.

**N2 — Luật lo recall, LLM lo precision.** Đo được: luật đạt recall 0,77–0,91 nhưng precision chỉ 0,38–0,56. Sai của luật là bắt nhầm, không phải bỏ sót. Nên nhiệm vụ của LLM là *lọc*, không phải *tìm*.

**N3 — Khi thông tin không tồn tại thì abstain, không đoán.** Nếu tài liệu không lưu ranh giới heading dưới bất kỳ dạng nào (style, numbering, định dạng, số gõ tay), thì kết quả là suy luận chứ không phải dữ liệu. Đánh dấu `uncertain` và chuyển người duyệt tốt hơn là auto-accept một phán đoán không có căn cứ.

---

## 1. Dữ liệu thực nghiệm nền

Mọi ngưỡng và luật trong spec này bắt nguồn từ **năm** tài liệu đã đo. Ghi lại để sau này biết luật nào có căn cứ, luật nào là phỏng đoán.

### 1.1 Hai tài liệu học thuật

| | A (báo cáo thực tập) | B (khóa luận) |
|---|---|---|
| Tổng paragraph | 1.184 | 1.211 |
| Trong bảng | 471 (40%) | 188 (16%) |
| Heading thật | 33 | 68 |
| Số cấp | 4 | 5 |
| `pStyle` chính xác | **49%** | **100%** |
| `numPr` chính xác | **100%** (numId∈{3,4}, ilvl∈{1,2}) | **0%** (toàn bullet, ilvl=0) |
| Số gõ tay trong text | không | có, đủ 4 cấp |
| `w:outlineLvl` | 0/1.184 | 0/1.211 |
| Có TOC field | có (28 mục, dùng làm ground truth) | có nhưng rỗng |
| Phân mảnh run | 5.464 → 590 sau gộp (giảm 89%) | giảm 26% |
| Paragraph hỏng | 1 (ký tự lặp do `w:position`) | 0 |
| Chênh lệch cỡ chữ heading vs thân bài | **0pt** (13 vs 13, chỉ khác bold) | H4/H5 **nhỏ hơn** thân bài |

Hai dòng cuối là lý do quan trọng nhất khiến luật thuần định dạng không đủ.

### 1.2 Ba tài liệu hành chính — phá vỡ giả định của v2 bản đầu

| | C (báo cáo ngày) | D (thiết kế phân hệ) | E (báo cáo ngày 2) |
|---|---|---|---|
| Tổng block | 273 | 866 | 131 |
| Ngoài bảng | 43 | 109 | 112 |
| `pStyle` heading | **0** | 33 (H1–H4) | **0** |
| `numPr` | **0** | **0** | **0** |
| TOC field | **0** | **0** | **0** |
| Ký hiệu La Mã `I. II.` | có | 28 | 8 |
| Ký hiệu chữ cái `a) b)` | có | 11 | 12 |
| Heading trích được | 12 | 41 | 35 |
| Chế độ | `vn-administrative` | `vn-administrative` + style | `vn-administrative` thuần |

Cả ba đều **không có bất kỳ tín hiệu OOXML nào** (style/numPr/TOC/outlineLvl) ngoài định dạng in đậm. Tín hiệu duy nhất là ký hiệu đánh số gõ tay theo hệ hành chính Việt Nam.

Đây là lý do phải bổ sung chế độ `vn-administrative` (xem 4.2) — regex `^\d+(\.\d+)+` của bản đầu bỏ sót **toàn bộ** cấp La Mã và cấp chữ cái ở cả ba file.

**Đặc điểm quyết định:** phần lớn mục trong tài liệu hành chính viết theo dạng `N. Tiêu đề: nội dung…` — heading và body **luôn dính trong cùng một paragraph**. Xem 6.7 về hệ quả chi phí.

---

## 2. Kiến trúc tổng thể

```
DOCX
 │
 ├─ Tầng 0: Ingest an toàn + Canonical Block Model      [code]
 ├─ Tầng 1: Phân loại chế độ tài liệu                    [code]
 ├─ Tầng 2: Sinh ứng viên (tối ưu RECALL)                [code]
 ├─ Tầng 3: Lọc ứng viên (tối ưu PRECISION)              [LLM]
 ├─ Tầng 4: Dựng cây + kiểm tra nhất quán                [code]
 ├─ Tầng 5: Reasoning sâu tại điểm mâu thuẫn             [LLM]
 └─ Tầng 6: Validator + quyết định accept/review         [code]
```

Tầng LLM chỉ có 2/7. Đây là cố ý.

---

## 3. Tầng 0 — Ingest và Canonical Block Model

### 3.1 Bảo mật (bắt buộc, không tùy chọn)

DOCX từ người dùng là **untrusted input**. File .docx là ZIP:

- Từ chối entry là symlink (`stat.S_ISLNK(m.external_attr >> 16)`)
- Từ chối entry có đường dẫn thoát thư mục (kiểm tra `target.is_relative_to(dest)` sau `resolve()`)
- Parse XML bằng `defusedxml` (chống XXE, billion-laughs)
- Kiểm tra relationship target không thoát khỏi package

### 3.2 Phạm vi quét và các cấu trúc XML phải xử lý

Không chỉ `word/document.xml`. Heading có thể nằm ở:

- `word/document.xml` — luồng chính
- `word/header*.xml`, `word/footer*.xml` — hiếm nhưng có
- Bảng (`<w:tbl>`) — xem 5.5, **không được loại vô điều kiện**

**Các cấu trúc XML đo được trên ba tài liệu, tất cả đều chưa có luật ở bản trước:**

| Cấu trúc | A | B | D | Xử lý bắt buộc |
|---|---|---|---|---|
| `<w:ins>` tracked insert | 42 | 21 | 55 | xem 3.2.1 |
| `<w:sdt>` content control | 4 | 3 | 4 | xem 3.2.2 |
| `<w:instrText>` field code | 0 | 1 | 41 | xem 3.2.3 |
| `<w:txbxContent>` textbox | 4 | 0 | 2 | xem 3.2.4 |
| `<w:sectPr>` section break | 41 | 3 | 3 | xem 3.2.5 |
| `<w:bookmarkStart>` | 72 | 68 | 39 | không ảnh hưởng, bỏ qua |

#### 3.2.1 Tracked changes — nguy hiểm nhất

Văn bản trong `<w:del>` là nội dung **đã bị xoá nhưng chưa accept**. Parser ngây thơ đọc `<w:delText>` như text thường → **heading đã xoá vẫn vào outline**.

```
văn bản trong <w:del>/<w:delText>  → BỎ HOÀN TOÀN
văn bản trong <w:ins>              → GIỮ, gắn cờ pending_insert
```

Gắn cờ vì heading đang trong trạng thái đề xuất chèn có thể bị reject sau; nếu tài liệu dùng cho quy trình phê duyệt, nên báo cáo riêng nhóm này.

Lưu ý phối hợp với 3.3: không gộp run nằm trong hai wrapper `<w:ins>`/`<w:del>` khác nhau — làm vậy sẽ hợp nhất các lần sửa riêng biệt thành một.

#### 3.2.2 Content control

`<w:sdt>` bọc ngoài paragraph hoặc run, đẩy chúng xuống sâu thêm một cấp. Duyệt bằng `p.findall(w:r)` (con trực tiếp) sẽ **bỏ sót**. Dùng duyệt đệ quy có nhận biết `<w:sdtContent>`, hoặc "làm phẳng" `<w:sdt>` trước khi dựng block.

#### 3.2.3 Field code

`<w:instrText>` chứa mã lệnh (`SEQ`, `REF`, `PAGEREF`, `TOC`), **không phải nội dung hiển thị**. Kết quả hiển thị nằm trong `<w:fldSimple>` hoặc giữa `fldChar begin/separate/end`.

```
instrText            → BỎ (là mã, không phải chữ người đọc thấy)
kết quả field        → GIỮ
```

Tài liệu D có 41 field code — chủ yếu `SEQ` cho caption. Không lọc thì outline sẽ nhiễm chuỗi kiểu ` SEQ Bảng \* ARABIC `.

#### 3.2.4 Textbox

`<w:txbxContent>` là **luồng tài liệu riêng**, thứ tự của nó không nối tiếp luồng chính. Đưa block textbox vào cùng mảng `document_order` sẽ phá vỡ ngữ cảnh prev/next của tầng 2 và tầng 3.

```
gán stream_id riêng cho từng textbox
ngữ cảnh anh em chỉ tính TRONG cùng stream_id
mặc định: nội dung textbox KHÔNG phải heading (thường là ghi chú, nhãn hình)
```

#### 3.2.5 Section break

Mỗi `<w:sectPr>` có thể **khởi động lại đánh số**. Tài liệu A có 41 section. Validator ở 7.2 phải cho phép numbering reset tại ranh giới section thay vì báo lỗi đứt quãng.

### 3.3 Gộp run theo ngữ nghĩa

**Đây là bước quyết định, chạy trước mọi thứ khác.**

`merge_runs` tiêu chuẩn so sánh `rPr` nguyên văn nên chỉ gộp được 34/5.464 run trên tài liệu A. Nguyên nhân: phân mảnh do **kerning từng ký tự** (`w:spacing`), không phải rsid.

Phân loại thuộc tính `rPr`:

```python
IGNORE = {'spacing','kern','position','noProof','lang','rFonts','w','rtl','em','shd'}
KEEP   = {'b','i','u','sz','szCs','color','caps','smallCaps','strike','vertAlign'}
```

Gộp run liền kề khi chữ ký gồm **chỉ các thuộc tính KEEP** giống nhau. Kết quả đo: giảm 89% số segment trên tài liệu A.

Trước đó vẫn nên chạy bước dọn chuẩn: xóa `<w:proofErr>`, xóa mọi thuộc tính chứa `rsid`.

### 3.4 Quy tắc whitespace (bẫy nguy hiểm nhất)

Theo OOXML, `<w:t>` không có `xml:space="preserve"` bị cắt khoảng trắng rìa. Áp dụng nguyên văn sẽ **xóa mất mọi dấu cách** trong tài liệu A, cho ra `Tìnhhìnhhoạtđộng...`.

Quy tắc đúng (đã kiểm chứng bằng render PDF):

```python
def rendered(text, preserve):
    if preserve:
        return text
    # w:t toàn khoảng trắng vẫn render thành 1 dấu cách
    if text and not text.strip():
        return ' '
    return text.strip(' \t\r\n')
```

Sai ở đây làm hỏng toàn bộ offset và mọi `source_span` phía sau.

### 3.5 Cấu trúc block

```json
{
  "block_id": "p452",
  "para_index": 452,
  "raw": "văn bản giữ nguyên \x0b cho w:br, \t cho w:tab",
  "text": "văn bản đã chuẩn hóa khoảng trắng",
  "style": "Heading2 | BodyText | null",
  "numId": "3", "ilvl": "1",
  "jc": "both|center|left|right", "indent": "711",
  "vis_bold": true, "vis_size_pt": 13.0,
  "uniform_format": true, "n_segments": 1,
  "has_break": false,
  "in_table": false, "table_depth": 0,
  "corrupt": false,
  "segments": [
    {"text": "...", "bold": true, "size_pt": 13.0, "start": 0, "end": 42}
  ]
}
```

`vis_bold` / `vis_size_pt` là **định dạng hiệu dụng** — phải resolve kế thừa từ `styles.xml`, không chỉ đọc `rPr` của run. Trong tài liệu A, toàn bộ bold/size nằm trong định nghĩa style, `rPr` của run rỗng.

`segments` giữ offset để ánh xạ ngược về XML khi cần sửa file.

### 3.6 Phát hiện paragraph hỏng

Tài liệu thật có paragraph hỏng render ra ký tự lặp đôi (`HHììnnhh`), do hai luồng run xen kẽ phân biệt bởi `w:position`. Đã kiểm chứng bằng render PDF: Word vẽ đúng như vậy, đây không phải lỗi parser.

```python
def is_doubled(t, thresh=0.55):
    c = [ch for ch in t if ch.strip()]
    if len(c) < 12: return False
    pairs = sum(1 for i in range(0, len(c)-1, 2) if c[i].lower() == c[i+1].lower())
    return pairs / (len(c)//2) >= thresh
```

Block `corrupt=true` → loại khỏi luồng ứng viên, ghi log, chuyển người duyệt. Không đưa vào LLM (model sẽ cố suy luận trên rác).

---

## 4. Tầng 1 — Phân loại chế độ tài liệu

**Tầng quan trọng nhất trong spec này.** Thiếu nó, mọi luật phía sau đều sai trên một nửa số tài liệu.

### 4.1 Các chỉ số đo trên toàn tài liệu

Gọi `H` = tập block có `pStyle` thuộc họ Heading/Title.

| Ký hiệu | Định nghĩa |
|---|---|
| `n_toc` | Số mục trích được từ TOC field (style TOC1–TOC9), nếu có |
| `r_numpr` | Tỷ lệ block trong `H` có `numPr` với `ilvl ≥ 1` |
| `r_typed` | Tỷ lệ block trong `H` có text khớp `^\d+(\.\d+)+` |
| `c_depth` | Độ nhất quán ánh xạ (mức style) ↔ (độ sâu số gõ tay), đo bằng: với mỗi style, tỷ lệ block có cùng độ sâu |
| `d_format` | Chênh lệch định dạng heading vs baseline thân bài (bool bold khác nhau, hoặc chênh cỡ chữ ≥ 1pt) |

Baseline thân bài = mode của `(vis_bold, vis_size_pt)` trên các block có `len(text) > 200`.

### 4.2 Cây quyết định

```
if r_outline > 0:
    → CHẾ ĐỘ "outlinelvl-driven"   (thẩm quyền cao nhất trong OOXML)

elif n_toc >= 5 and TOC khớp được >= 80% mục với block trong body:
    → CHẾ ĐỘ "toc-anchored"

elif r_vnadmin >= 0.5:
    → CHẾ ĐỘ "vn-administrative"   (tài liệu C, D, E)

elif r_typed >= 0.6 and c_depth >= 0.9:
    → CHẾ ĐỘ "typed-numbering"     (tài liệu B)

elif r_numpr >= 0.20:              # ĐÃ SỬA, xem ghi chú bên dưới
    → CHẾ ĐỘ "numpr-driven"        (tài liệu A)

elif r_custom >= 0.5:
    → CHẾ ĐỘ "custom-style"

elif d_format == True:
    → CHẾ ĐỘ "format-driven"

else:
    → CHẾ ĐỘ "semantic-only"       (tải LLM nặng nhất, kỳ vọng thấp nhất)
```

**Ba chỉ số bổ sung:**

| Ký hiệu | Định nghĩa |
|---|---|
| `r_outline` | Số block có `w:outlineLvl` đặt tường minh. Đo được **0/2.395** trên năm tài liệu — hiếm, nhưng khi có thì thẩm quyền cao nhất |
| `r_vnadmin` | Tỷ lệ block in đậm ngoài bảng khớp một trong bốn lớp ký hiệu ở 4.4 |
| `r_custom` | Tỷ lệ block dùng style **tên tự đặt** (không thuộc họ `Heading*`) mà lặp ≥ 5 lần, định dạng lệch baseline, và độ dài trung bình < 90 ký tự |

**Sửa lỗi ngưỡng `r_numpr`.** Bản đầu đặt `≥ 0.6`. Đo thực tế trên tài liệu A chỉ được **0,24** — vẫn là chế độ `numpr-driven` đúng. Nguyên nhân: mẫu số là *toàn bộ* block có style Heading, mà 51% trong số đó là dương giả (caption, nhãn) nên pha loãng tỷ lệ. Hạ ngưỡng xuống **0,20**, hoặc tính `r_numpr` sau khi đã áp luật loại trừ X1–X5.

Chế độ được ghi vào metadata kết quả. Mọi báo cáo độ chính xác phải tách theo chế độ — gộp chung sẽ che mất tài liệu nhóm khó.

### 4.3 Ghi chú từng chế độ

**`toc-anchored`** — TOC field do Word sinh chứa đúng ý định của tác giả. Dùng làm ground truth chính: khớp từng mục TOC về block trong body bằng so khớp chuỗi đã chuẩn hóa (bỏ dấu chấm dẫn, bỏ số trang, bỏ khoảng trắng). Tài liệu A thuộc nhóm này — 28 mục TOC khớp hoàn hảo. **Lưu ý:** TOC có thể lỗi thời (tác giả sửa heading mà không refresh) → vẫn phải validate, không tin tuyệt đối.

**`typed-numbering`** — số mục nằm trong text nên độc lập với style. Luật `^\d+(\.\d+)+` cho precision 1,00 trên tài liệu B. Level = độ sâu số. Phần front/back matter (`MỤC LỤC`, `MỞ ĐẦU`, `KẾT LUẬN`…) không có số → xử lý bằng từ khóa, xem 5.3.

**`numpr-driven`** — numbering do Word sinh, không có trong text. Chỉ tin `numPr` khi `ilvl ≥ 1`; `ilvl = 0` thường là bullet list. Cần lọc thêm theo `numId`: xác định tập `numId` nào thực sự dùng cho heading bằng cách xem `numId` nào xuất hiện cùng block có style Heading với tỷ lệ cao.

**`format-driven`** — dựa vào lệch khỏi baseline. Cảnh báo: tài liệu A có heading **cùng cỡ chữ** với thân bài (13pt vs 13pt), chỉ khác bold. Tài liệu B có H4/H5 **nhỏ hơn** thân bài và không bold. Nên không được giả định "heading luôn to hơn/đậm hơn".

**`semantic-only`** — không còn tín hiệu cấu trúc. Đặt kỳ vọng thấp, tăng tỷ lệ abstain, cân nhắc bổ sung thị giác (mục 7.3).

**`outlinelvl-driven`** *(mới)* — `w:outlineLvl` khai báo thẳng cấp heading, độc lập với style. Chưa gặp trong năm tài liệu đã đo, nhưng khi có thì tin trước cả TOC. Ghi lại ở đây để không bỏ sót do mẫu nhỏ.

**`vn-administrative`** *(mới)* — hệ đánh số hành chính Việt Nam. Khác `typed-numbering` ở chỗ **cấp suy từ loại ký hiệu, không từ độ sâu dấu chấm**. Chiếm 3/5 tài liệu đã đo. Xem 4.4.

**`custom-style`** *(mới)* — người dùng tự tạo style tên riêng (`Tieu de 1`, `Muc cap 2`, `Chuong`), phổ biến với template cơ quan. Mọi luật khớp theo tên họ `Heading*` sẽ trượt hoàn toàn. Phát hiện bằng: style lặp nhiều lần + định dạng lệch baseline + block ngắn + đứng trước đoạn dài.

**`vn-legal`** *(mới, CHƯA GẶP nhưng khả năng cao có trong tập)* — văn bản quy phạm pháp luật dùng hệ `Phần / Chương / Mục / Điều / Khoản / Điểm`, khác hoàn toàn `I./1./a)`:

```python
LEGAL = [(1, r'^Phần\s+(thứ\s+)?[IVXLC\dMột-Mười]+'),
         (2, r'^Chương\s+[IVXLC\d]+'),
         (3, r'^Mục\s+\d+'),
         (4, r'^Điều\s+\d+\.?'),        # Điều là đơn vị chính, KHÔNG phải cấp 1
         (5, r'^\d+\.\s'),              # Khoản
         (6, r'^[a-zđ]\)\s')]           # Điểm
```

Đặc điểm riêng cần lưu ý:

- **`Điều` đánh số liên tục xuyên suốt toàn văn bản**, không reset theo Chương. `Điều 47` có thể nằm trong `Chương V`. Validator không được coi đây là nhảy cấp.
- Tiêu đề của `Điều` thường nằm **cùng dòng**: `Điều 5. Nguyên tắc quản lý` → tách tại dấu `.` sau số, không phải dấu `:`.
- `Khoản` chỉ có số, không có tiêu đề — thường là body, không phải heading.

Căn cứ đưa vào spec dù chưa gặp: tài liệu D **trích dẫn** 6 văn bản loại này (Luật Đất đai, Nghị định, Thông tư, Quyết định) trong mục căn cứ pháp lý, nên khả năng cao chúng nằm trong tập tài liệu cần xử lý.

### 4.4 Hệ ký hiệu hành chính Việt Nam

Bốn lớp ký hiệu, **kiểm tra từ cấp sâu đến cấp nông** — nếu không, `3.1.` sẽ bị luật `^\d+\.` bắt trước và gán nhầm cấp 2:

```python
LV = [(3, r'^(\d{1,2}\.\d{1,2})\.?\s'),   # 3.1.  3.2.     ← kiểm TRƯỚC
      (4, r'^([a-zđ])[\.\)]\s'),          # a)  b)  c.
      (2, r'^(\d{1,2})\.\s*\D'),          # 1.  2.  3.
      (1, r'^([IVXLC]+)\.\s*\S')]         # I.  II.  III.
```

Còn một lớp thứ năm dưới cùng: gạch đầu dòng phân cấp `-`, `+`, `*`. Đo được 44 và 68 lần trên tài liệu D và E. Chúng thường là **nội dung**, không phải heading — nhưng đôi khi là mục con thật. Mặc định coi là body; đưa lên tầng 3 nếu in đậm và đứng trước nhiều dòng cùng cấp.

**Cấp phải suy theo ngữ cảnh cha gần nhất, không gán cứng theo loại ký hiệu.** Đo trên tài liệu D và E: `a) b) c)` đứng ngay dưới `1. 2. 3.` mà không qua cấp `x.y` — gán cứng `a)` = cấp 4 tạo ra cây nhảy cấp 2→4 sai. Đúng: `a)` dưới `3.` là cấp 3; `a)` dưới `5.1.` mới là cấp 4.

**Ký hiệu gõ tay thắng `pStyle` khi hai nguồn mâu thuẫn.** Tài liệu D có `- Sơ đồ luồng nghiệp vụ:` mang style `Heading4` lọt vào cùng cấp với `a) b) c)`.

---

## 5. Tầng 2 — Sinh ứng viên (tối ưu RECALL)

Mục tiêu: **recall ≥ 0,95**, chấp nhận precision thấp (~0,5). Thà thừa còn hơn thiếu, vì tầng 3 lọc được thừa nhưng không cứu được thiếu.

### 5.1 Loại trừ trước (chạy đầu tiên, ưu tiên cao nhất)

Thứ tự quan trọng — kiểm tra loại trừ **trước** mọi luật thu nhận:

| # | Điều kiện | Hành động |
|---|---|---|
| X1 | `corrupt == true` | loại, `flag=corrupt_paragraph` |
| X2 | `text` khớp `^(Bảng\|Hình\|Biểu\s*đồ\|Sơ\s*đồ\|Table\|Figure)\s*\d` | loại, `flag=caption` |
| X3 | `in_table == true` | **KHÔNG loại** — phân loại bảng trước, xem 5.5 |
| X4 | `len(text.strip()) == 0` | loại |
| X5 | `text` chỉ gồm số/số La Mã (số trang) | loại |
| X6 | thuộc khối trang bìa lặp | loại bản trùng, xem 5.1b |
| X7 | nằm trong `<w:del>` (tracked delete) | loại, xem 3.2.1 |
| X8 | nằm trong `<w:txbxContent>` | loại khỏi luồng chính, xem 3.2.4 |

X2 là luật có căn cứ mạnh: tài liệu A có 12 caption bảng bị gán `Heading3`, chiếm gần 1/3 số dương giả.

**X3 đã sửa.** Bản trước ghi "tách sang luồng riêng" ở 5.1 nhưng thực thi thành loại vô điều kiện, mâu thuẫn với 5.5. Hậu quả đo được trên tài liệu D: **757/866 block nằm trong bảng (87%)**, trong đó **40 block khớp ký hiệu heading** bị mất — gồm `I.1`, `I.2` và các danh mục `1. Văn bản đề nghị giao đất;`, `2. Sơ đồ vị trí, ranh giới…`. Xem 5.5 để có luật phân loại bảng cụ thể.

### 5.1b Khối trang bìa lặp (X6)

Đo được ở **cả hai** tài liệu có trang bìa: tài liệu A lặp `BÁO CÁO THỰC TẬP` + toàn bộ thông tin sinh viên; tài liệu D lặp `BỘ TỔNG THAM MƯU`, `PHÂN HỆ QUẢN LÝ…`, `HỆ SINH THÁI SỐ…`. Tổng 24 và 51 đoạn text lặp.

Nguyên nhân: người soạn nhân đôi trang bìa (một bản có logo, một bản không; hoặc bìa ngoài + bìa trong).

```
B1. Tìm mọi dãy ≥ 3 block liên tiếp mà text (đã chuẩn hóa) trùng
    với một dãy liên tiếp khác trong tài liệu
B2. Nếu cả hai dãy nằm trong 15% đầu tài liệu → là trang bìa lặp
B3. Giữ dãy XUẤT HIỆN SAU (thường là bìa trong, đầy đủ hơn),
    loại dãy trước, gắn flag=duplicate_cover
```

Không dùng `repeat_count` đơn lẻ để loại, vì nhãn lặp hợp lệ (`Nhận xét:`) cũng có `repeat_count` cao — điều kiện phải là **dãy liên tiếp trùng nhau**, không phải block đơn lẻ trùng.

### 5.2 Luật thu nhận theo chế độ

**Chế độ `toc-anchored`:**
```
ứng viên = mọi block khớp một mục TOC
         ∪ mọi block có pStyle họ Heading
         ∪ mọi block có numPr với ilvl ≥ 1
```

**Chế độ `typed-numbering`:**
```
ứng viên = block có text khớp ^\d+(\.\d+)+  (level = độ sâu)
         ∪ block có pStyle họ Heading
         ∪ block khớp từ khóa cấu trúc (5.3)
```

**Chế độ `numpr-driven`:**
```
ứng viên = block có numPr, ilvl ≥ 1, numId ∈ tập numId-heading
         ∪ block có pStyle họ Heading
         ∪ block khớp từ khóa cấu trúc
```

**Chế độ `outlinelvl-driven`:**
```
ứng viên = mọi block có w:outlineLvl đặt tường minh   (level = outlineLvl + 1)
         ∪ block khớp từ khóa cấu trúc
```

**Chế độ `vn-administrative`:**
```
ứng viên = block khớp một trong bốn lớp ký hiệu ở 4.4
         ∪ block có pStyle họ Heading
         ∪ block khớp từ khóa cấu trúc
level    = suy theo ngữ cảnh cha gần nhất, KHÔNG gán cứng theo loại ký hiệu
```

**Chế độ `custom-style`:**
```
B1. Xác định tập style-ứng-viên: lặp ≥ 5 lần, định dạng lệch baseline,
    độ dài trung bình < 90 ký tự, ≥ 60% đứng ngay trước đoạn > 200 ký tự
B2. ứng viên = block dùng style trong tập đó ∪ block khớp từ khóa cấu trúc
B3. level = thứ hạng cỡ chữ giảm dần giữa các style-ứng-viên
```

**Chế độ `format-driven` / `semantic-only`:** dùng bộ điểm ở 5.4.

### 5.2b Độ dài KHÔNG được là điều kiện loại bỏ cứng

**Lỗi đã mắc và đã sửa.** Bản đầu có `if len(text) > 150: loại`. Trên tài liệu E, luật này loại mất mục `5.` — một heading dài 166 ký tự vì thuộc dạng `heading_with_inline_body`. Ba mục lân cận (`4.`, `6.`) chỉ dài 20 và 37 ký tự nên lọt qua, khiến cây trông như đứt quãng `4. → 6.`.

Nghịch lý: ngưỡng độ dài loại bỏ **chính xác nhóm cần xử lý nhất**.

Luật đúng:

```
nếu ký hiệu đánh số đã khớp:
    độ dài KHÔNG loại bỏ
    len > 120  →  hạ confidence 0.1  +  bật cờ needs_split
nếu KHÔNG có ký hiệu đánh số:
    độ dài mới được dùng làm tín hiệu cộng điểm (xem 5.4)
```

### 5.2c Hiệu chỉnh ngưỡng điểm — 0,62 → 0,58

Đo trên tài liệu A đã xóa style: **cả 7 heading bị bỏ sót đều nằm ở đúng 0,59**. Không phải trùng hợp — chúng đều là heading đậm, ngắn, không kết câu, nhưng block kế tiếp là *caption bảng* nên mất 0,15 điểm của tín hiệu "đứng trước đoạn dài".

Hai sửa đổi:

1. Ngưỡng **0,62 → 0,58**. Kết quả: recall **0,77 → 1,00** (31/31), số ứng viên 52 → 79.
2. Tín hiệu "đứng trước đoạn dài" phải tính cả trường hợp block kế tiếp là **caption** hoặc **bảng**, không chỉ đoạn văn > 200 ký tự.

### 5.3 Từ khóa cấu trúc (tiếng Việt)

Luật rẻ, precision rất cao cho phần front/back matter — nhóm mà mọi luật số học đều bỏ sót:

```
^(MỤC LỤC | DANH MỤC .* | LỜI (CAM ĐOAN|CẢM ƠN|MỞ ĐẦU|NÓI ĐẦU) | MỞ ĐẦU
 | ĐẶT VẤN ĐỀ | TỔNG QUAN | KẾT LUẬN | KIẾN NGHỊ | TÀI LIỆU THAM KHẢO
 | PHỤ LỤC | TÓM TẮT | ABSTRACT | CHƯƠNG\s+[\dIVX]+ | PHẦN\s+[\dIVX]+)
```

Áp dụng không phân biệt hoa thường, nhưng cộng điểm thêm nếu toàn chữ hoa. Trong thử nghiệm, thêm luật này kéo recall tài liệu B từ 0,69 lên 0,91.

Level mặc định cho nhóm này: `CHƯƠNG/PHẦN` và front/back matter → level 1.

### 5.4 Bộ điểm cho chế độ format-driven / semantic-only

```python
score = 0.0
if vis_size_pt > baseline_size:        score += 0.45
if vis_bold and not baseline_bold:     score += 0.35
if re.match(r'^\d+(\.\d+)+', text):    score += 0.45
if KEYWORD_RE.match(text):             score += 0.45
if len(text) <= 90:                    score += 0.12
if not re.search(r'[.!?;,]$', text):   score += 0.12
if text.isupper() and len(text) > 6:   score += 0.15
if jc == 'center':                     score += 0.10
if next_block_len > 200 and len(text) < 90:  score += 0.15
if in_table:                           score -= 0.50

ứng viên nếu score >= 0.62
```

Ngưỡng 0,62 cho recall 0,77 (tài liệu A) và 0,91 (tài liệu B) trong thử nghiệm. **Phải hiệu chỉnh lại trên tập của bạn**, và theo dõi định kỳ vì phân phối tài liệu trôi theo thời gian.

Tín hiệu `next_block_len > 200` (dòng ngắn đứng ngay trước đoạn dài) rẻ mà hiệu quả — heading gần như luôn đứng trước thân bài.

### 5.5 Phân loại bảng — luật cụ thể

**Đây là chỗ mất dữ liệu lớn nhất nếu làm sai.** Tài liệu D có 87% block nằm trong bảng; loại vô điều kiện làm mất 40 heading thật.

Phân loại **từng bảng** (không phải từng block) thành ba nhóm:

| Nhóm | Điều kiện | Xử lý block bên trong |
|---|---|---|
| **layout** | 1 cột, HOẶC ≤ 2 dòng, HOẶC nằm trong 15% đầu tài liệu (khung trang bìa) | coi như luồng chính, áp mọi luật thu nhận bình thường |
| **content** | ≥ 2 cột và ≥ 3 dòng, nhưng < 30% ô chứa số, và độ dài ô trung bình > 40 ký tự | **cho phép heading**, nhưng ngữ cảnh anh em chỉ tính trong cùng cột |
| **data** | ≥ 2 cột, ≥ 3 dòng, ≥ 30% ô chứa số HOẶC độ dài ô trung bình ≤ 40 ký tự | loại toàn bộ, `flag=data_table` |

Nhóm **content** là nhóm bị bỏ sót ở bản trước. Tài liệu D dùng bảng để trình bày quy trình nghiệp vụ — mỗi ô chứa một bước có đánh số (`1. Văn bản đề nghị giao đất;`). Đây là nội dung có cấu trúc, không phải dữ liệu bảng biểu.

Chỉ số phụ giúp phân biệt:

```python
numeric_ratio = số ô khớp ^[\d\s\.,%/\-]+$ / tổng số ô
avg_cell_len  = độ dài trung bình text trong ô
has_header    = dòng đầu toàn bộ in đậm
uniform_cols  = độ lệch chuẩn số ký tự theo cột thấp  → nghiêng về data
```

Bảng có `has_header = true` và `numeric_ratio` cao gần như chắc chắn là **data** → loại.

**Ngữ cảnh cho block trong bảng:** không dùng prev/next theo `document_order` toàn cục. Dùng thứ tự **trong cùng ô**, rồi **trong cùng cột**. Gán `stream_id = f"tbl{n}_col{c}"` tương tự cách xử lý textbox ở 3.2.4.

---

## 6. Tầng 3 — Lọc ứng viên bằng LLM (tối ưu PRECISION)

### 6.1 Nhiệm vụ

**Một câu hỏi nhị phân hẹp trên mỗi ứng viên**, không phải phân loại mở:

> Ứng viên này là *tiêu đề mục* (heading), hay là *nhãn nội dung / câu văn / phần tử danh sách*?

Đây là thay đổi quan trọng so với thiết kế ban đầu. Nhiệm vụ hẹp → prompt ngắn → mô hình 4B–9B làm tốt → rẻ.

### 6.2 Vì sao chỉ LLM giải được

Ví dụ có thật từ tài liệu A. Hai block **giống hệt nhau về mọi tín hiệu định dạng** (ngắn, in đậm, không kết câu, đứng trước đoạn dài):

| Text | Sự thật |
|---|---|
| `Tình hình cho vay` | **heading** |
| `Nhận xét:` | **nhãn nội dung** |

Phân biệt được nhờ ngữ cảnh: `Nhận xét:` xuất hiện 3 lần rải rác, mỗi lần ngay sau một bảng số liệu. `Tình hình cho vay` nằm trong dãy anh em `Tình hình huy động vốn` / `Kết quả hoạt động kinh doanh`.

### 6.3 Request schema

```json
{
  "document_mode": "numpr-driven",
  "candidate": {
    "block_id": "p544",
    "text": "Tình hình cho vay",
    "flagged_because": ["bold_vs_baseline", "short", "precedes_long_body"],
    "metadata": {
      "style": "Heading2", "numId": null, "ilvl": null,
      "bold": true, "size_pt": 13.0, "align": "both", "indent": "711",
      "uniform_format": true, "has_line_break": false, "in_table": false
    }
  },
  "immediate_neighbours": {
    "previous_block": {"block_id": "p543", "text": "...", "len": 412},
    "next_block":     {"block_id": "p545", "text": "...", "len": 388}
  },
  "sibling_candidates": {
    "before": [{"block_id": "...", "text": "...", "style": "...", "numId": "..."}],
    "after":  [{"block_id": "...", "text": "...", "style": "...", "numId": "..."}]
  },
  "repeat_count": 1,
  "corrections": []
}
```

`repeat_count` = số lần text này (đã chuẩn hóa) xuất hiện trong tài liệu. Tín hiệu mạnh: `Nhận xét:` có `repeat_count = 3` → nhiều khả năng là nhãn lặp lại, không phải heading.

`corrections` = tối đa 3 ví dụ đã người duyệt xác nhận, lấy bằng embedding retrieval trên `(text + ngữ cảnh)`. Không nhét toàn bộ correction memory.

### 6.3b Tách heading/body — luật code chạy TRƯỚC LLM

Với chế độ `vn-administrative`, phần lớn ứng viên có dạng `<ký hiệu> <tiêu đề>[: ]<nội dung>`. Đa số tách được bằng code; chỉ phần còn lại mới đưa lên LLM.

**Nguyên tắc: tìm chỗ NỘI DUNG bắt đầu, không tìm chỗ tiêu đề kết thúc.**

Lý do — dấu `:` không nhất quán ngay trong cùng một dãy anh em (ví dụ thật, tài liệu C):

```
b. KQ Mỹ: 0/0 (0/0).        ← CÓ dấu hai chấm
c. KQ Philippin 0/0 (0/0)   ← KHÔNG có
```

Trong thể loại hành chính, phần body có hình dạng rất đều, nhận diện được bằng regex:

```python
PAYLOAD = re.compile(
    r'(?='
    r'\d+/\d+'                                    # 0/0,  5.121/2.281
    r'|\d[\d\.,]*\s*(tốp|tàu|chiếc|lượt|l/c|l/m|giàn|công dân|đơn vị)'
    r'|\((tăng|giảm|như ngày)'
    r'|Bình thường'
    r'|Không ghi nhận'
    r')')

MARK = re.compile(r'^\s*([IVXLC]+\.|\d{1,2}\.\d{1,2}\.?|\d{1,2}\.|[a-zđ][\.\)]|[-+*])\s*')
```

Thuật toán:

```
1. Tách ký hiệu đầu dòng bằng MARK
2. cut_payload = vị trí PAYLOAD khớp đầu tiên   (None nếu không có)
3. cut_colon   = vị trí dấu ':' ĐẦU TIÊN         (None nếu không có)
4. cut = min(các giá trị không None)
5. không có cut nào  → toàn bộ là heading
```

Lấy `min()` là điểm mấu chốt: payload cứu ca không có dấu câu (`c.`), dấu `:` cứu ca payload là chữ (`Bình thường`).

**Kết quả kiểm chứng: 8/8 đúng** trên bộ mẫu gồm cả ba ca khó:

| Input | Heading | Body | Ranh giới từ |
|---|---|---|---|
| `b. KQ Mỹ: 0/0 (0/0).` | `KQ Mỹ` | `0/0 (0/0).` | dấu `:` |
| `c. KQ Philippin 0/0 (0/0)` | `KQ Philippin` | `0/0 (0/0)` | **payload** |
| `6. Ngoại biên: Tình hình A: Quân đội B…` | `Ngoại biên` | `Tình hình A: Quân đội B…` | dấu `:` **đầu tiên** |
| `2. Vùng biển miền Trung, QĐHS` | toàn bộ | — | không có |

Regex `PAYLOAD` phải **tùy biến theo thể loại tài liệu**, không dùng chung. Danh sách đơn vị đo (`tốp`, `tàu`, `l/c`…) là đặc thù báo cáo quân sự; tài liệu tài chính sẽ cần (`tỷ đồng`, `%`, `triệu`), tài liệu hành chính dân sự cần (`hồ sơ`, `trường hợp`, `vụ`). Đây là điểm cấu hình theo domain, nên tách ra file riêng.

**Vai trò còn lại của LLM:** những ca luật không quyết được — payload là câu văn tự do không khớp mẫu, hoặc tiêu đề chứa dấu `:` trong chính nó (`Quyết định số 562/QĐ-TTg: …`).

### 6.3c Nhãn lặp lại (`inline_label`) — quyết định một lần cho cả tập

Ca `* Nhận xét: Không ghi nhận hoạt động bay quân sự.` không có đáp án khách quan. Nó phụ thuộc **mục đích của outline**:

| Mục đích | Xử lý `Nhận xét` |
|---|---|
| Điều hướng (mục lục, nhảy mục) | **không** phải heading — mục lục sẽ đầy dòng `Nhận xét` vô nghĩa |
| Tái dựng cấu trúc đầy đủ (JSON, CSDL, đánh chỉ mục) | **là** ô cấu trúc cố định, giữ lại |

Ba tín hiệu nhận diện, đều tính bằng code, không cần LLM:

1. `repeat_count` cao và các lần xuất hiện ở vị trí tương đương trong cây
2. Ký hiệu đầu dòng (`*`, `-`) **không thuộc** dãy đánh số nào
3. Không có mục anh em cùng ký hiệu **liền kề** trong cùng mục cha

Mặc định: `role = "inline_label"`, giữ trong cây có cờ riêng, **loại khỏi outline điều hướng**. Đây là quyết định cấu hình một lần cho cả tập, không phải phán đoán từng ca.

### 6.4 Response schema

```json
{
  "block_id": "p544",
  "is_heading": true,
  "role": "section_heading",
  "confidence": 0.86,
  "level_hint": 4,
  "split": null,
  "evidence": ["nằm trong dãy anh em cùng chủ đề với p452b, p601"]
}
```

`role` ∈ `document_title | section_heading | heading_with_inline_body | inline_label | list_item | body_text | caption | uncertain`

`split` khác `null` khi block chứa heading dính body hoặc hai heading dính nhau:

```json
"split": {
  "heading_text": "Tình hình hoạt động kinh doanh trong 3 năm",
  "remainder_text": "Tình hình huy động vốn",
  "remainder_role": "section_heading",
  "boundary_char_offset": 42
}
```

Trường hợp này **có thật và không có tín hiệu định dạng nào**: trong tài liệu A, ranh giới nằm *bên trong một run duy nhất*, không có `<w:br/>`, không đổi định dạng, không dấu câu.

### 6.5 Cấu hình inference

- **Constrained decoding bắt buộc** (XGrammar qua vLLM/SGLang, hoặc GBNF qua llama.cpp). Ép JSON hợp lệ ở tầng decoding, không dựa vào validator bắt lỗi format sau.
- **temperature = 0**, greedy. Bài toán cần tái lập cho audit trail, không phải sinh sáng tạo.
- **Non-thinking mode** cho tầng này (phân loại hàng loạt, có metadata rõ).
- **Batch nhiều ứng viên mỗi request** khi chúng liền kề, miễn giữ đủ metadata để tách kết quả.

### 6.6 Ngân sách token đo được

| Payload | Token (ước lượng) |
|---|---|
| Nạp thẳng `document.xml` | ~247.000 |
| Toàn bộ text đã sạch | ~12.500 |
| Outline skeleton | ~2.200 |
| 1 request tầng 3 | ~575 |

Ước lượng bằng `len/3`; tiếng Việt có dấu thường tệ hơn tỷ lệ này 1,5–2× với tokenizer kiểu cl100k. **Đo lại bằng đúng tokenizer của model đã chọn** trước khi chốt batch size.

### 6.7 Tải LLM chênh lệch rất mạnh theo chế độ

Đây là hiệu chỉnh quan trọng so với ước tính ban đầu ("~4 lượt/tài liệu").

| Chế độ | Ca cần LLM / tài liệu | Nguyên nhân |
|---|---|---|
| `typed-numbering` (B) | **0** | style + số gõ tay nhất quán 100%, không có heading gộp |
| `toc-anchored` / `numpr-driven` (A) | **2–4** | chỉ vài heading bị gộp |
| `vn-administrative` (C, D, E) | **25–30** | **hầu như mọi heading đều cần tách** |

Nguyên nhân của cột thứ ba: thể loại hành chính viết mục theo dạng `N. Tiêu đề: nội dung…`, heading và body luôn nằm chung một paragraph. Ví dụ thật từ tài liệu E:

```
1. An ninh chính trị: Bình thường.
4. Xuất, nhập cảnh trái phép: BĐBP phát hiện, xử lý 16 công dân…
V. KHÔNG GIAN MẠNG: Thông tin liên quan đến Quân đội phát tán…
```

**Không được cắt máy móc tại dấu `:`** — đúng nguyên tắc đã nêu ở 6.2. Ba dạng cùng tồn tại trong một tài liệu:

| Text | Xử lý |
|---|---|
| `1. An ninh chính trị: Bình thường.` | tách tại `:` đầu tiên |
| `6. Ngoại biên: Tình hình Campuchia-Thái Lan: Quân đội…` | **hai** dấu `:`, ranh giới ở cái đầu |
| `2. Vùng biển miền Trung, QĐHS` | không có `:`, toàn bộ là heading |

Chi phí vẫn thấp về tuyệt đối (~575 token × 30 ≈ 17K token/tài liệu), nhưng cần tính đúng khi ước lượng throughput và chọn kích thước model. Với chế độ này, **tầng 3 không còn là thiểu số** — nó là phần chính của pipeline.

Hệ quả cho việc chọn model: nếu tập tài liệu của bạn nghiêng về hành chính, ưu tiên **Qwen3.5-4B** để lấy headroom batching hơn là 9B, vì số lượt gọi mới là nút cổ chai chứ không phải chất lượng từng lượt (nhiệm vụ tách `Tiêu đề: nội dung` đơn giản hơn nhiều so với phân loại heading/nhãn).

---

## 7. Tầng 4 — Dựng cây và kiểm tra nhất quán

### 7.1 Gán level

Thứ tự ưu tiên nguồn level:

1. Độ sâu số gõ tay (`1.1.1.` → level 3 + offset chương)
2. `ilvl` từ `numPr` (đã kiểm chứng: ilvl=1 ↔ cấp 2, ilvl=2 ↔ cấp 3 trên tài liệu A)
3. Cấp mục tương ứng trong TOC (`TOC3` → cấp 2, `TOC4` → cấp 3)
4. `level_hint` từ LLM
5. Bậc style (`Heading1..5`) — **nguồn kém tin cậy nhất**, chỉ dùng khi không còn gì khác

Lý do xếp `pStyle` cuối: trong tài liệu A, `Heading2` ánh xạ tới **hai** cấp TOC khác nhau; `Heading1` cũng vậy.

**Với chế độ `vn-administrative`: cấp suy theo ngữ cảnh cha, KHÔNG gán cứng theo loại ký hiệu.**

Đây là lỗi đã mắc ở bản trước. Gán cứng `a) b) c)` = cấp 4 tạo ra cây nhảy cấp 2→4 sai, vì trong tài liệu D và E chúng đứng ngay dưới `1. 2. 3.` mà không qua cấp `x.y`.

```
level(block) = level(mục cha gần nhất theo document_order) + 1

trong đó "mục cha gần nhất" = ứng viên gần nhất phía trước có
loại ký hiệu ở lớp CAO HƠN theo thứ tự:  La Mã > số > số.số > chữ cái > gạch
```

Ví dụ: `a)` dưới `3.` → cấp 3. Cùng ký hiệu `a)` nhưng dưới `5.1.` → cấp 4.

**Ký hiệu gõ tay thắng `pStyle` khi mâu thuẫn.** Tài liệu D có `- Sơ đồ luồng nghiệp vụ:` mang style `Heading4` lọt vào cùng cấp với `a) b) c)` — sai, vì gạch đầu dòng ở lớp thấp hơn chữ cái.

### 7.2 Kiểm tra toàn cây

| Kiểm tra | Xử lý khi vi phạm |
|---|---|
| Numbering liên tục (`3.1 → 3.2 → 3.4` thiếu `3.3`) | escalate vùng đó lên tầng 5 |
| Không nhảy cấp (`2` → `2.1.1` thiếu `2.1`) | escalate |
| Không có node mồ côi (con không cha) | escalate |
| Level đơn điệu theo document order | escalate |
| Text heading không trùng lặp bất thường | cảnh báo nếu `repeat_count > 2` |
| Span nằm trong nguồn, không mất chữ | lỗi cứng → review |
| **Đối xứng anh em** — heading các mục cùng cha có độ dài và hình dạng tương đồng | escalate, xem bên dưới |

**Ngoại lệ cho kiểm tra numbering liên tục** — ba trường hợp reset hợp lệ, không được báo lỗi:

1. **Ranh giới `<w:sectPr>`** — tài liệu A có 41 section, mỗi section có thể đánh số lại
2. **Chuyển chương** — `Chương 1` có `1.1, 1.2`; `Chương 2` lại bắt đầu `1.1` thay vì `2.1`. Phổ biến trong tài liệu tiếng Việt
3. **Vào phần phụ lục** — `Phụ lục 1` rồi bên trong lại `1., 2., 3.` từ đầu

**Kiểm tra đối xứng anh em** — tín hiệu rẻ và mạnh, phát hiện lỗi tách sai mà không cần LLM:

```
với mỗi nhóm anh em cùng cha:
    nếu độ dài heading của một mục lệch > 3× trung vị nhóm
    hoặc một mục có payload còn các mục khác thì không
    → escalate lên tầng 5
```

Ví dụ thật: `b. KQ Mỹ` và `c. KQ Philippin` là anh em, đều là tên lực lượng. Nếu luật tách cho ra `KQ Mỹ` nhưng `KQ Philippin 0/0 (0/0)` thì bất đối xứng đó chính là dấu hiệu tách sai — bắt được trước khi cần LLM.

**Vòng phản hồi:** khi phát hiện bất thường, chạy lại tầng 2+3 tại vùng đó với cửa sổ ngữ cảnh rộng hơn, sinh skeleton v2. Giới hạn **tối đa 3 vòng lặp** và **ghi log mọi thay đổi giữa các phiên bản skeleton** — vòng lặp có thể tự củng cố lỗi, sửa các mục đúng cho khớp với một mục sai.

---

## 8. Tầng 5 — Reasoning sâu tại điểm mâu thuẫn

Chỉ chạy trên các vùng tầng 4 đánh dấu. Số lượng rất nhỏ: trên tài liệu A là **4 ca**, tài liệu B là **0 ca**.

Ba tầng leo thang, chỉ lên tầng sau khi tầng trước vẫn `uncertain`:

| Tầng | Cấu hình | Tỷ trọng dự kiến |
|---|---|---|
| 5a | text + metadata + skeleton, non-thinking | đa số |
| 5b | như trên, **bật thinking mode** | ít |
| 5c | thêm **ảnh trang render** | rất hiếm |

**Về 5b:** đo trên Artificial Analysis Intelligence Index, cùng một model bật thinking tăng điểm khoảng 60% so với non-thinking. Đáng dùng cho nhóm khó, không đáng cho phân loại hàng loạt.

**Về 5c:** chuỗi render `DOCX → LibreOffice → PDF → pdftoppm → ảnh`. Tốn thời gian và ~0,5–1,5GB VRAM cho vision encoder. Chỉ dùng cho chế độ `semantic-only` hoặc khi 5b vẫn bế tắc — ảnh cho biết khoảng cách dòng, thụt lề, vị trí trên trang, nhưng **không** cho mapping về paragraph/run để sửa file.

Ngữ cảnh cấp thêm ở tầng này: mục cha, 3 mục anh em mỗi bên, các heading cùng bậc trong toàn tài liệu, correction đã xác nhận cho ca tương tự.

---

## 9. Tầng 6 — Validator và quyết định

### 9.1 Kiểm tra bằng code thuần

**Không dùng LLM để validate LLM** — cùng lớp model sẽ lặp lại cùng thiên lệch.

- JSON đúng schema (đã được constrained decoding đảm bảo, vẫn kiểm lại)
- Mọi `source_span` nằm trong block gốc, offset hợp lệ
- Tổng text của heading + body = text gốc, **không mất chữ, không thêm chữ**
- Cây hợp lệ: không mồ côi, không vòng, level đơn điệu
- Nếu tài liệu có TOC: đối chiếu, báo cáo độ lệch

### 9.2 Ngưỡng quyết định

```
accept nếu:
    confidence >= 0.85
    AND không vi phạm kiểm tra cây nào
    AND (chế độ ∈ {toc-anchored, typed-numbering, numpr-driven}
         OR có ít nhất 2 tín hiệu độc lập ủng hộ)

review nếu:
    confidence < 0.85
    OR role == 'uncertain'
    OR có split (mọi ca tách ranh giới đều phải người xem)
    OR chế độ == 'semantic-only'
    OR block corrupt
```

**Nguyên tắc N3:** sai ở nhánh `accept` nghiêm trọng hơn sai ở nhánh `review`, vì không còn cơ chế nào bắt lại. Thà đẩy nhiều sang review.

Một hệ thống đạt 95% trên 70% khối lượng (30% chuyển review) **hữu ích hơn** hệ thống ép auto-accept 100% để đạt 95% — vì ở trường hợp sau, 5% lỗi rải rác không biết ở đâu, phải kiểm lại toàn bộ.

---

## 10. Metric và benchmark

### 10.1 Metric bắt buộc

Không dùng "% block phân loại đúng" — chỉ số này trivially cao (đa số block là body) và gây ảo tưởng.

| Metric | Định nghĩa |
|---|---|
| **Heading recall** | % heading thật được phát hiện |
| **Heading precision** | % ứng viên được accept thực sự là heading |
| **Exact span accuracy** | % heading khớp hoàn toàn text (quan trọng nhất cho việc sửa DOCX) |
| **Level accuracy** | % heading đúng cả text lẫn cấp |
| **Tree edit distance** | khoảng cách sửa cây so với cây chuẩn — chuẩn của học thuật (HRDoc, ToC extraction) |
| **Document-level exact** | % tài liệu có outline hoàn toàn đúng |
| **Abstain rate** | % chuyển review |
| **Accept precision** | độ chính xác *riêng* nhánh auto-accept — mục tiêu ≥ 0,99 |

**Báo cáo tách theo chế độ tài liệu.** Gộp chung sẽ che mất nhóm `semantic-only`.

### 10.2 Nguồn nhãn

1. **TOC field** — nhãn miễn phí, chất lượng cao. Tài liệu A cho 28 mục khớp hoàn hảo. Ưu tiên xây tập test từ các file có TOC.
2. Gán tay cho nhóm không có TOC, đặc biệt chế độ `semantic-only`.

### 10.3 Bước đo trước tiên

Trước khi chọn model hay tối ưu prompt, chạy **chỉ tầng 1** trên 50–100 file thật để biết tỷ trọng 5 chế độ. Con số này quyết định mục tiêu 95% có khả thi không, nhiều hơn việc chọn model 4B hay 9B.

### 10.4 Nhánh so sánh nên chạy

| Nhánh | Mục đích |
|---|---|
| A | Pipeline đầy đủ (multi-pass) |
| B | Một lượt, nạp toàn tài liệu | đo recall theo **vị trí** (đầu/giữa/cuối) để kiểm chứng "lost in the middle" |
| C | Chỉ luật, không LLM | đo giá trị gia tăng thật của tầng LLM |
| D | Model nhỏ hơn (4B) vs lớn hơn (9B) | nếu chênh < 2–3 điểm %, chọn nhỏ để lấy headroom batching |

---

## 11. Lựa chọn model

Ràng buộc: RTX 3060 12GB.

| Model | VRAM (Q4_K_M) | Ghi chú |
|---|---|---|
| Qwen3.5-4B | ~2,5 GB | headroom lớn nhất cho batching |
| **Qwen3.5-9B** | ~6,6 GB | **khuyến nghị mặc định** |
| Qwen3-VL-8B | ~12 GB | kín VRAM, không còn chỗ KV cache |

Qwen3.5 dùng kiến trúc lai (Gated DeltaNet 3:1 với full attention), chỉ 10/40 lớp dùng KV cache — tăng context 4K→64K chỉ tốn thêm ~800MB. Ràng buộc VRAM cho context dài gần như biến mất.

**Nhưng** kiến trúc nén này bất lợi cho tác vụ liệt kê *đầy đủ* nhiều mục rải rác toàn tài liệu (nhiều kim, không phải một kim) — thêm một lý do giữ kiến trúc nhiều tầng thay vì một lượt full-doc. Nhánh B ở 10.4 để kiểm chứng điểm này.

Qwen3.5 natively multimodal → không cần model VL riêng cho tầng 5c.

Toàn bộ dòng Qwen là Apache 2.0, dùng thương mại tự do.

---

## 12. Giới hạn đã biết — nói rõ để không hứa sai

**Không đảm bảo khôi phục 100% khi tài liệu không lưu ranh giới.** Đo thực nghiệm: xóa `pStyle` và `numPr`, giữ nguyên định dạng thị giác, luật đạt tốt nhất F1 = 0,65 (tài liệu A) và 0,54 (tài liệu B). Ngay cả khi LLM nâng precision lên đáng kể, một số ca vẫn không có căn cứ khách quan để phân xử.

Lý do gốc rễ, đo được từ `styles.xml`:

- Tài liệu A: heading và thân bài **cùng cỡ chữ 13pt**, khác biệt duy nhất là bold — một bit thông tin, mà thân bài cũng có chỗ bôi đậm.
- Tài liệu B: `Heading4` (12pt) và `Heading5` (11pt) **nhỏ hơn và không đậm** — nhạt hơn cả thân bài. Chỉ số gõ tay cứu được chúng.

Khi cả style, numbering, số gõ tay và định dạng đều vắng mặt, ranh giới heading **không tồn tại trong file**. Không model nào — 9B, 235B, hay bất kỳ — tạo lại được từ hư không. Thiết kế đúng trong tình huống đó là abstain và chuyển người duyệt, không phải đoán.

**Tập chế độ chưa chắc đã đầy đủ.** Bản đầu của spec này có 5 chế độ; ba tài liệu hành chính tiếp theo phá vỡ ngay một trong số đó và buộc phải thêm ba chế độ mới. Không có cơ sở nào để tin rằng 8 chế độ hiện tại là đủ. Mỗi thể loại tài liệu mới đều có khả năng lộ ra một chế độ chưa biết.

**Mức kiểm chứng rất khác nhau giữa các chế độ:**

| Chế độ | Đã đo trên | Có ground truth độc lập? |
|---|---|---|
| `toc-anchored` | A | có (TOC, 28 mục) |
| `numpr-driven` | A (bỏ TOC) | có |
| `typed-numbering` | B | **không** — chỉ kiểm chứng nội tại |
| `vn-administrative` | C, D, E | **không** |
| `format-driven` | C | **thất bại có chứng cứ** (thiếu mục) |
| `outlinelvl-driven` | — | **chưa bao giờ** |
| `custom-style` | — | **chưa bao giờ** |
| `semantic-only` | — | **chưa bao giờ** |

Con số "68/68" của tài liệu B và các outline của C, D, E **chưa được đối chứng bằng nguồn độc lập** — nên đọc dè dặt.

**TOC có thể sai hơn pipeline.** Đo trên tài liệu A: TOC ghi `CHƯƠNG 2. GIỚI THIỆU CÁC DỊCH VỤ…` trong khi thân bài ghi `…CÁC SẢN PHẨM DỊCH VỤ…` — tác giả sửa tiêu đề sau khi tạo mục lục và không refresh. Pipeline lấy được văn bản hiện hành, TOC giữ bản cũ. Không tin TOC tuyệt đối, kể cả khi dùng làm nhãn cho tập test.

Ngoài ra:

- Tiếng Việt chưa có benchmark công khai cho bài toán này. Mọi ngưỡng phải tự hiệu chỉnh.
- Ngưỡng trong spec bắt nguồn từ **năm** tài liệu. Vẫn là mẫu rất nhỏ.
- Phân phối tài liệu trôi theo thời gian → cần theo dõi định kỳ tỷ trọng chế độ và phân phối flag, không tune một lần rồi để yên.

**Việc cần làm tiếp, theo thứ tự:**

1. Chạy **chỉ tầng 1** trên 50–100 file thật. Đếm tỷ trọng 8 chế độ, và đặc biệt đếm số file **không rơi vào chế độ nào** — con số đó cho biết còn thiếu bao nhiêu chế độ.
2. Sửa phần suy cấp theo ngữ cảnh cha (4.4) — hiện đang gán cứng và tạo cây nhảy cấp sai.
3. Xây tập test có nhãn cho `vn-administrative`, vì đây là chế độ chiếm tỷ trọng lớn nhất trong mẫu hiện có mà lại chưa có ground truth nào.

---

## 13. Các trường hợp còn để ngỏ

Ghi lại để không quên, phân theo mức chắc chắn.

### 13.1 Chưa gặp trong 5 tài liệu, nhưng gần như chắc chắn tồn tại

| Ca | Vì sao tin là có | Ảnh hưởng |
|---|---|---|
| Văn bản quy phạm `Điều/Khoản/Điểm` | tài liệu D trích dẫn 6 văn bản loại này | đã thêm chế độ `vn-legal` ở 4.3 |
| Heading bị tách bằng **Enter thật** (2 paragraph) | tài liệu gốc mô tả kỹ; 5 file đều `has_break = 0` nên chưa kiểm chứng được | cần luật ghép block ở tầng 2, hiện chỉ có ở 3.3 mức run |
| Numbering reset theo chương | phổ biến trong tài liệu tiếng Việt | đã thêm ngoại lệ ở 7.2 |
| Phụ lục có hệ đánh số riêng | tài liệu A có `Phụ lục 1–7` | đã thêm ngoại lệ ở 7.2 |
| Số La Mã **thường** `i. ii. iii.` cho phần đầu sách | chuẩn xuất bản | regex hiện chỉ bắt `[IVXLC]` hoa — cần bổ sung |
| Tài liệu song ngữ Việt–Anh | phổ biến với tài liệu dự án | chưa có luật |

### 13.2 Ca vận hành chưa xử lý

| Ca | Xử lý đề xuất |
|---|---|
| File `.doc` (binary cũ, không phải OOXML) | phát hiện bằng magic bytes → chuyển LibreOffice sang `.docx` trước, hoặc từ chối có thông báo rõ |
| File đặt mật khẩu | `zipfile` báo lỗi → trả `status=encrypted`, không cố đoán |
| ZIP hỏng / thiếu `word/document.xml` | trả `status=corrupt`, ghi log, không crash pipeline |
| File rất lớn (> 50MB, > 10k paragraph) | xử lý theo luồng (streaming), đặt trần bộ nhớ; tài liệu D đã 34MB |
| Tài liệu quét (chỉ có ảnh, không có lớp text) | phát hiện: `< 50` block text nhưng nhiều `w:drawing` → cần OCR, ngoài phạm vi spec này |

### 13.3 Nguyên tắc khi gặp ca mới

Spec này đã phải sửa **ba lần** vì mẫu nhỏ:

1. Bản v1 giả định `pStyle` là deterministic → sai 51% trên tài liệu đầu tiên
2. Bản v2 bản đầu có 5 chế độ → ba tài liệu hành chính phá vỡ ngay một trong số đó
3. Bản 2.1 loại vô điều kiện block trong bảng → mất 40 heading trên tài liệu D

Bài học chung: **mọi luật loại bỏ cứng đều nguy hiểm hơn luật hạ điểm.** Ba lỗi trên đều cùng dạng — một điều kiện `loại` áp cho nhóm mà thực tế có ngoại lệ. Khi thêm luật mới, mặc định nên là *hạ confidence + gắn cờ*, chỉ dùng `loại` khi có bằng chứng trên nhiều tài liệu.

---

## 14. Thể loại văn bản Việt Nam — phân loại theo chuẩn

Mục này tổng hợp từ **quy định pháp lý chính thức**, không phải suy đoán. Mỗi mục ghi rõ nguồn và mức tin cậy.

### 14.1 Văn bản hành chính — Nghị định 30/2020/NĐ-CP

**Nguồn: Phụ lục Nghị định 30/2020/NĐ-CP. Đây là chuẩn bắt buộc cho toàn bộ cơ quan nhà nước Việt Nam.** Mức tin cậy: cao nhất.

Bố cục 7 cấp: **Phần → Chương → Mục → Tiểu mục → Điều → Khoản → Điểm**

Quy định trình bày (chính là tín hiệu nhận diện):

| Cấp | Ký hiệu số | Trình bày | Tín hiệu OOXML |
|---|---|---|---|
| Phần, Chương | **La Mã** | từ "Phần"/"Chương" + số trên **dòng riêng**, canh giữa, in thường, đậm. **Tiêu đề ở dòng NGAY DƯỚI**, canh giữa, IN HOA, đậm | `jc=center`, `b=1`, **2 paragraph** |
| Mục, Tiểu mục | **Ả Rập** | như trên — từ + số một dòng, tiêu đề dòng dưới, canh giữa, IN HOA, đậm | `jc=center`, `b=1`, **2 paragraph** |
| Điều | Ả Rập + dấu `.` | "Điều" + số + tiêu đề **CÙNG một dòng**, lùi đầu dòng 1cm hoặc 1,27cm, đậm | `ind≈567` hoặc `720` twips, `b=1` |
| Khoản | Ả Rập + dấu `.` | kiểu chữ **đứng, KHÔNG đậm**. Nếu khoản có tiêu đề thì tiêu đề trên dòng riêng, đậm | `b=0` (trừ khi có tiêu đề) |
| Điểm | **chữ cái tiếng Việt** + `)` | in thường, đứng, **không đậm** | `b=0` |

**Ba hệ quả quan trọng cho pipeline:**

**(a) Heading Phần/Chương/Mục BẮT BUỘC trải qua hai paragraph.** Đây là ca "heading tách bằng Enter" mà spec xếp vào nhóm "chưa gặp, chưa kiểm chứng" ở 13.1 — hóa ra nó **được quy định bắt buộc** trong mọi văn bản hành chính:

```
Chương II                          ← paragraph 1: từ + số La Mã
SOẠN THẢO, KÝ BAN HÀNH VĂN BẢN     ← paragraph 2: tiêu đề IN HOA
```

Luật ghép bắt buộc phải có ở tầng 2, không chỉ ở tầng run (3.3):

```
nếu block khớp ^(Phần|Chương|Mục|Tiểu mục)\s+[IVXLC\d]+\s*$  (không có gì thêm)
   VÀ block kế tiếp canh giữa + IN HOA
→ ghép hai block thành MỘT heading
```

**(b) Căn giữa (`jc=center`) là tín hiệu mạnh** cho Phần/Chương/Mục — mạnh hơn in đậm, vì thân bài hành chính hầu như không bao giờ canh giữa.

**(c) Thụt lề 1cm/1,27cm phân biệt Điều với Khoản.** Cả hai đều dùng số Ả Rập + dấu chấm, nhưng `Điều` có `w:ind left≈567–720` twips và **đậm**, còn `Khoản` thì không đậm. Đây là cách duy nhất tách hai cấp này khi chỉ nhìn ký hiệu.

### 14.2 Thứ tự bảng chữ cái tiếng Việt — bẫy cho validator

Nghị định 30 quy định điểm dùng **"các chữ cái tiếng Việt theo thứ tự bảng chữ cái tiếng Việt"**. Bảng chữ cái tiếng Việt **không phải** bảng Latin:

```
a  ă  â  b  c  d  đ  e  ê  g  h  i  k  l  m  n  o  ô  ơ  p  q  r  s  t  u  ư  v  x  y
```

Hệ quả: chuỗi điểm hợp lệ là `a) ă) â) b) c) d) đ) e) ê) g)…`, **không phải** `a) b) c) d) e) f) g)`.

Hai lỗi sẽ xảy ra nếu dùng thứ tự Latin:

1. Validator báo "đứt quãng" sai khi gặp `d)` → `đ)` (tưởng thiếu `e`)
2. Regex `[a-z]` **không bắt được** `ă â đ ê ô ơ ư` — phải dùng lớp ký tự Unicode tiếng Việt tường minh

```python
VN_ALPHA = 'aăâbcdđeêghiklmnoôơpqrstuưvxy'
DIEM = re.compile(r'^([' + VN_ALPHA + r'])\)\s')
def next_letter(c): return VN_ALPHA[VN_ALPHA.index(c)+1]
```

Cũng lưu ý: `f`, `j`, `w`, `z` **không có** trong bảng chữ cái tiếng Việt — nếu gặp thì là ký hiệu ngoại lai, đáng nghi.

### 14.3 Hợp đồng

Nguồn: khảo sát mẫu hợp đồng kinh tế phổ biến. Mức tin cậy: trung bình (không có chuẩn pháp lý bắt buộc về thể thức).

Cấu trúc ba khối:

```
KHỐI MỞ ĐẦU (không đánh số)
  - Quốc hiệu / Tiêu ngữ
  - Tên hợp đồng (IN HOA, canh giữa)
  - "Căn cứ ..." (nhiều dòng, thường gạch đầu dòng)
  - "Hôm nay, ngày ... tại ..."
  - "BÊN A:" / "BÊN B:"  ← nhãn, không phải heading mục
KHỐI ĐIỀU KHOẢN
  - Điều 1: … / ĐIỀU 1. …    ← hoa/thường và dấu KHÔNG nhất quán
    - 1.1  1.2  hoặc  1. 2. 3.
      - a) b) c)  hoặc  + gạch đầu dòng
KHỐI KÝ
  - "ĐẠI DIỆN BÊN A" / "ĐẠI DIỆN BÊN B"  ← nhãn chữ ký, KHÔNG phải heading
```

Ba lưu ý:

- **Hoa/thường không nhất quán ngay trong một file**: quan sát được cả `Điều 9:` lẫn `ĐIỀU 5.` trong cùng loại mẫu. Regex phải `re.IGNORECASE` và chấp nhận cả `:` lẫn `.`
- **Tham chiếu chéo gây dương giả**: câu thân bài như *"theo quy định tại Khoản 4.3 Điều 4 của Hợp đồng này"* chứa chuỗi `Điều 4` giữa dòng. Luật phải neo đầu dòng (`^`), không tìm ở giữa
- **`BÊN A:` / `ĐẠI DIỆN BÊN A` là nhãn**, xử lý như `inline_label` ở 6.3c

### 14.4 Các thể loại còn lại — mức tin cậy thấp, cần kiểm chứng

Chưa có tài liệu mẫu để đo. Ghi lại giả thuyết để khi gặp thì kiểm, **không dùng làm luật production trước khi có bằng chứng**.

| Thể loại | Cấu trúc dự đoán | Rủi ro riêng |
|---|---|---|
| **Biên bản họp** | `I./1./a)` như hành chính; nhiều nhãn cố định (`Thời gian:`, `Địa điểm:`, `Thành phần:`, `Nội dung:`, `Kết luận:`) | nhãn cố định lặp ở **mọi** biên bản → nếu xử lý cả tập, `repeat_count` xuyên tài liệu là tín hiệu mạnh |
| **Tài chính/kế toán** | Bảng chiếm đa số; chỉ tiêu có mã số (`Mã số 110`, `A. TÀI SẢN NGẮN HẠN`, `I. Tiền và tương đương tiền`) | `A.` `B.` `I.` là **chỉ tiêu trong bảng**, không phải heading — luật phân loại bảng ở 5.5 quyết định |
| **Giáo trình** | `Chương N` + `N.1` + `N.1.1`; thêm khối phụ (`Mục tiêu`, `Câu hỏi ôn tập`, `Bài tập`) | khối phụ lặp mỗi chương → như biên bản |
| **Tài liệu dịch** | giữ cấu trúc gốc (thường `1.1`, `A.`, `I.`) nhưng tiêu đề tiếng Việt | trộn hệ ký hiệu Latin với hệ Việt |
| **Sinh tự động từ web/phần mềm** | thường CÓ `pStyle` chuẩn hoặc `w:outlineLvl`, ít nhiễu | ngược lại: có thể có `w:sdt` dày đặc, style tên máy (`Heading1Char`, `style21`) |

### 14.5 Bảng tra cứu tín hiệu theo thể loại

Dùng cho tầng 1 khi phân loại chế độ:

| Thể loại | `pStyle` | `numPr` | Ký hiệu gõ tay | Tín hiệu đặc trưng nhất |
|---|---|---|---|---|
| Hành chính (NĐ30) | hiếm | hiếm | La Mã + Ả Rập + chữ Việt | **`jc=center` + heading 2 paragraph** |
| Quy phạm pháp luật | hiếm | hiếm | `Chương/Điều/Khoản/Điểm` | **từ khóa "Điều" đầu dòng** |
| Hợp đồng | hiếm | đôi khi | `Điều N` + `N.M` | **khối `BÊN A/BÊN B`** |
| Học thuật (khóa luận) | **có, tin cậy** | hiếm | `1.1.1` | style ↔ độ sâu nhất quán |
| Báo cáo thực tập | có nhưng **sai 51%** | **có, tin cậy** | không | `numPr` với `ilvl ≥ 1` |
| Tài chính | hiếm | hiếm | `A./I./1.` trong bảng | **tỷ lệ ô số cao** |
| Biên bản | hiếm | hiếm | `I./1./a)` | nhãn cố định lặp |
| Sinh tự động | **có** | có | tùy | `w:outlineLvl` hoặc `w:sdt` dày |

### 14.6 Điều mục này KHÔNG giải quyết

Vẫn còn ba khoảng trống, và cần nói rõ:

1. **Chỉ 14.1 và 14.2 có căn cứ pháp lý.** 14.3 dựa trên khảo sát mẫu, 14.4 là giả thuyết thuần túy.
2. **Chưa có tài liệu mẫu nào thuộc 5 thể loại ở 14.4** để kiểm chứng. Bảng 14.5 là điểm khởi đầu để thử, không phải luật đã xác nhận.
3. **Tài liệu ghép nhiều chế độ vẫn chưa xử lý được.** Một bộ hồ sơ gồm tờ trình (hành chính) + phụ lục trích nghị định (pháp quy) + phụ lục bảng biểu (tài chính) cần **ba chế độ trong một file**, trong khi tầng 1 hiện gán *một* chế độ cho toàn file. Đây là lỗ hổng kiến trúc lớn nhất còn lại — cần chuyển tầng 1 từ "phân loại file" sang "phân loại từng vùng", phân đoạn theo `<w:sectPr>` hoặc theo mốc `PHỤ LỤC`.

### 14.7 Cập nhật đo trên corpus 95: pháp quy cần builder riêng

Sau khi bỏ `dec1` khỏi `adminMarks`, nhóm song ngữ/pháp quy về `VietnameseLegal` đúng hướng,
nhưng phép đo candidate lộ ra một bẫy khác: `VietnameseLegal` chỉ có khoảng 7 candidate/file trong
corpus 95. Với văn bản pháp quy thật, candidate thấp **không được đọc là output thấp** vì nhiều
marker `Điều`/`Article` nằm trong paragraph gộp và không đi qua tập `HeadingCandidate` heuristic.

Lỗi production đã đo được: `auto:vietnamese-legal` dùng chung `AdministrativeOutline.Build`.
Builder hành chính đòi ít nhất hai chữ ký phân cấp để tránh bắt nhầm; văn bản pháp quy chỉ có một
chữ ký lặp (`Điều` hoặc `Article`) vẫn là cấu trúc hợp lệ. Vì vậy không được tái dùng builder hành
chính cho route pháp quy.

Builder pháp quy tất định phải đọc hệ nhãn riêng:

```
Phần|Part       -> level 1
Chương|Chapter  -> level 2
Mục|Section     -> level 3
Điều|Article    -> level 4
```

Ràng buộc đã chốt:

- Chấp nhận marker có dấu ngắt (`Điều 5.`, `Article 7:`, `Chapter II -`).
- Chấp nhận marker không dấu ngắt do PDF-convert (`Chương II QUY ĐỊNH CHUNG`) chỉ khi phần sau
  bắt đầu bằng chữ hoa; không bắt tham chiếu giữa câu như `Điều 3 của Bộ luật này`.
- Chuẩn hoá Unicode Form C trước regex để bắt nhãn dấu tổ hợp.
- Khi bật `--split-merged`, một paragraph có thể sinh nhiều heading cùng `Index`; validator/key
  phải phân biệt bằng `(Index, Text)`, không chỉ `Index`.
- `1.`/`2.` sau tiêu đề `Điều` là khoản/payload, không phải heading pháp quy.
- Không cho `StructuralHierarchyResolver` generic ghi đè cấp của route này. `Điều 4/5/...` nhìn
  giống list số thường, nhưng cấp của `Điều` đã được hệ pháp quy khai báo là level 4; suy từ chữ ký
  số sẽ làm sai cấp.

Đo lại toàn corpus 95 với `--no-llm --split-merged`:

| mode/status | files | headings | avg heading/file |
|---|--:|--:|--:|
| VietnameseLegal / Normal | 23 | 3.455 | 150,2 |
| SemanticOnly / ConversionFailure | 6 | 6 | 1,0 |

Kết luận: route pháp quy không còn mất trắng outline. Nhưng đây mới là đo coverage/output; chưa có
full answer key nên chưa phát biểu precision. Cần gán tay ít nhất một file pháp quy gộp nặng
(`001` hoặc `025`) để đo đúng/sai heading và cấp.
