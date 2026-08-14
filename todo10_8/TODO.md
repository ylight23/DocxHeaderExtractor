# TODO

Sắp theo thứ tự làm. Việc sau phụ thuộc kết quả việc trước.

---

## P0 — Làm trước tiên, chặn mọi việc khác

- [ ] **Chạy `tier1_batch.py` trên 50–100 file thật**
  ```bash
  pip install lxml
  python tier1_batch.py /duong/dan/tai-lieu --csv thongke.csv --recursive
  ```
- [ ] Đọc con số `KHÔNG PHÂN LOẠI ĐƯỢC: n/N = x%`
  - [ ] `< 10%` → sang P1
  - [ ] `10–30%` → xem `sample_markers` của các dòng `UNCLASSIFIED`, tìm mẫu chung
  - [ ] `> 30%` → bổ sung chế độ mới vào spec §4.2 trước khi làm gì khác
- [ ] Đọc phân bố cột `flags` → ưu tiên hoàn thiện luật cho cờ tần suất cao
- [ ] Ghi kết quả vào `spec-heading-outline-v2.md` §1 (bảng dữ liệu thực nghiệm)

---

## P1 — Hoàn thiện tầng luật (không cần LLM)

- [ ] Thêm đầu vào PDF-first bằng `PdfPig` chính thức (`PdfPig` package, pin version; namespace
      `UglyToad.PdfPig`) cho nhóm PDF text-layout:
  - [ ] Ưu tiên prototype trên nhóm `font-strong` đã đo: 43/83 PDF có phân tách font/layout rõ
        (`02_hop_dong_mua_sam` 6/6, `04_giao_trinh` 15/15, `03_tai_chinh_ke_toan` 13/15,
        `06_dich_song_ngu` 5/8, `01_phap_quy` 4/24).
  - [ ] Làm trước nhánh typed textbook/OpenStax: audit tạm trên `056` cho thấy PDF layout prototype
        đạt `truth=46 returned=46 matched=46 fp=0` sau canonical matching.
    - [ ] Không match raw text PDF; cần normalize/canonical vì text layer có thể chèn khoảng trắng
          trong token (`Business` -> `Bu s i n ess`, `10.1` -> `1 0.1`).
    - [ ] Luật đầu tiên: section = dòng `fs≈15.6` bắt đầu marker typed compact `N.N`;
          chapter = dòng số chương `fs≈15.6` + dòng title kế tiếp `fs≈15.6` cùng trang.
  - [ ] Adapter `PDF -> lines/blocks -> SlimParagraph-like` với `text`, `fontSize`, `fontName/bold`,
        `indentX`, `pageY`, `pageNo`; không tự dựng cây trong adapter.
  - [ ] Dùng `BoundingBox` thay `GlyphRectangle`.
  - [ ] Prototype trên `030_WB_RFP_Consulting_Services_2019.pdf`: body heading `fs=16 bold`, body
        thường `fs=12`, page header `fs=10`, TOC indent `PART x≈72` / `Section x≈91`.
  - [ ] Với `glyph-only` 28/83 PDF, chưa claim tự động: `017` cho thấy glyph height có thể nhiễu
        do glyph/diacritic/font embedding; `092` có `FontSize=1.0` toàn dòng và text extraction vỡ.
  - [ ] Giữ DOCX path: chỉ 83/95 DOCX có PDF cùng stem; 6 PDF `empty` cần OCR/nguồn khác nếu muốn
        khai thác PDF.
- [ ] Cài luật phân loại bảng theo spec §5.5 (`layout` / `content` / `data`)
  - [ ] Đo `numeric_ratio`, `avg_cell_len`, `has_header`, số cột, số dòng
  - [ ] Xử lý merged cell (`gridSpan`, `vMerge`) — làm sai phép đếm cột
  - [ ] Bảng lồng bảng — luật hiện chỉ xét một cấp
- [ ] Cài luật loại trang bìa lặp (spec §5.1b) — dãy ≥ 3 block liên tiếp trùng
- [ ] Cài xử lý tracked changes (spec §3.2.1) — bỏ `<w:del>`, cờ `<w:ins>`
- [ ] Cài xử lý `<w:sdt>` (duyệt đệ quy) và field code (bỏ `<w:instrText>`)
- [ ] Cài `stream_id` riêng cho textbox (spec §3.2.4)
- [ ] Ngoại lệ reset numbering tại `<w:sectPr>`, chuyển chương, vào phụ lục
- [ ] Bổ sung số La Mã **thường** `i. ii. iii.` cho phần đầu sách
- [ ] Xử lý file `.doc` cũ, file mã hóa, ZIP hỏng (spec §13.2)

---

## P2 — Tập test có nhãn

- [ ] Lọc các file có TOC field → nhãn miễn phí
- [ ] Gán tay cho chế độ `vn-administrative` (chưa có ground truth nào)
- [ ] Tạo file **đã khử nội dung** cho tài liệu nhạy cảm: giữ khung
      `I./1./3.1./a)`, thay số liệu và danh từ riêng bằng dữ liệu giả
- [ ] Cài metric: heading recall/precision, exact span, level accuracy,
      tree edit distance, **accept precision**, abstain rate
- [ ] Báo cáo **tách theo chế độ**, không gộp chung

---

## P3 — Tầng LLM

- [ ] Cài Qwen3.5 (kiểm tra trang Qwen chính thức xem có bản mới hơn chưa)
- [ ] Cấu hình constrained decoding (XGrammar qua vLLM, hoặc GBNF qua llama.cpp)
- [ ] `temperature=0`, non-thinking cho tầng 3
- [ ] Viết tầng 3: câu hỏi nhị phân hẹp *"heading hay nhãn/câu văn?"*
      — KHÔNG phân loại mọi block
- [ ] Thêm `repeat_count` và "dãy đồng nhất" vào request schema
- [ ] **Benchmark 4B vs 9B** cùng harness, cùng tập test
  - [ ] Chênh < 2–3 điểm % → chọn 4B để lấy headroom batching
- [ ] Nhánh so sánh: một lượt full-doc, đo recall theo **vị trí** (đầu/giữa/cuối)
      → kiểm chứng "lost in the middle"
- [ ] Nhánh so sánh: chỉ luật, không LLM → đo giá trị gia tăng thật của LLM

---

## P4 — Tầng 4 và 5

- [ ] Vòng phản hồi Pass 2 → Pass 1 (tối đa 3 vòng, ghi log mọi thay đổi skeleton)
- [ ] Kiểm tra đối xứng anh em (spec §7.2)
- [ ] Tầng 5 leo thang: 5a non-thinking → 5b thinking → 5c thêm ảnh trang
- [ ] Validator bằng code thuần — **không dùng LLM validate LLM**
- [ ] Kiểm "không mất chữ, không thêm chữ" so với đầu vào

---

## P5 — Lỗ hổng kiến trúc chưa có hướng giải

- [ ] **Tài liệu ghép nhiều chế độ** — tờ trình + phụ lục nghị định + bảng biểu
      cần 3 chế độ trong 1 file, tầng 1 hiện gán 1 chế độ cho toàn file
  - [ ] Hướng thử: phân đoạn theo `<w:sectPr>` hoặc mốc `PHỤ LỤC`,
        chạy tầng 1 trên từng vùng
- [x] `vn-legal` đã có route tất định riêng (`LegalStructuredOutline`) và đã chạy trên corpus 95:
      23 file `VietnameseLegal/Normal` sinh 3.455 heading khi bật `--split-merged`. Đã có 2 full key
      nguồn ngoài pipeline (`010`, `025`): 121 heading, P/R/F1 gộp 82,6%, đúng cấp/cha 100%.
      Lỗi còn lại là 21 cặp ranh giới title/body cùng `Điều N`; `KHOAN` phủ 0/21, dấu câu đầu tiên
      phủ 0/21, độ dài >3× phủ 16/21 nhưng bắn nhầm 4 heading đúng, nên chưa vá auto-split.
- [ ] Các chế độ vẫn thiếu full key/đo precision ngoài phân phối cũ:
      `OutlineLevelDriven` (cần full key 1 file partial TOC), `TypedNumbering` (đã có 1 full key RFC
      `092`: exact P/R/F1 0%, nhưng marker diagnostic 62/64 và starts-with 61/64; đã sửa cấp
      starts-with 42/61 → 61/61; filter marker-last bị bác ở dạng marker-only vì registry/reference
      cuối tài liệu lặp marker; dấu câu blind exact 0/61; filter footer nhỏ đưa returned 300→293 và
      exact 0→3/64; đo trực tiếp `word/document.xml` cho 31 file Typed trong `03/04/07` cho thấy
      cả ba nhóm đều text-layout nặng, nên phải báo thêm metric `navigation-usable`; đã thêm key
      giáo trình `056` từ OpenStax: 46 truth, trên HEAD hiện tại returned 419, exact P/R/F1
      9,5%/87,0%/17,2%, navigation usable 46/46, level đúng 46/46, truth candidate 82,6%;
      câu hỏi cũ "24 candidate rơi ở đâu" không còn đúng trên HEAD, phần còn lại là giảm FP và
      exact span/writeback;
      còn cần 1 tài chính),
      `SemanticOnly/ConversionFailure` (không đo như mode trích xuất).
- [ ] Mỗi lần đo corpus phải ghi health check theo `Mode + Status`: `files`, `candidates`,
      `headings`, `avg candidates/file`, `avg headings/file`. Nếu một mode lệch mạnh khỏi kỳ vọng
      thể loại, kiểm route/builder trước khi gán tay thêm key.
- [ ] 5 thể loại ở spec §14.4 chưa có tài liệu mẫu nào

---

## Nguyên tắc khi thêm luật mới

> Mặc định là **hạ confidence + gắn cờ**, không phải `loại`.

Spec đã sai 3 lần, cả 3 cùng dạng: một luật loại bỏ cứng áp cho nhóm
thực tế có ngoại lệ.

1. `pStyle` deterministic → sai 51%
2. Loại mọi block trong bảng → mất 40 heading
3. `len > 150` → loại → mất `heading_with_inline_body`
