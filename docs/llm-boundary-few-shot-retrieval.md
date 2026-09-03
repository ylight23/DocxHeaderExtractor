# Thiết kế: retrieval few-shot cho tầng cắt ranh giới title/body

Bối cảnh đo được ghi đầy đủ ở `handoff.md` (mục "2026-08-14 follow-up - ICL đo trên 3 domain cho bài
toán ranh giới title/body"). Tài liệu này chỉ bàn KIẾN TRÚC: có nên xây retrieval-based few-shot
selection, và nếu có thì xây thế nào — không lặp lại số liệu, chỉ trích khi cần cho quyết định.

**Nguyên tắc áp dụng cho toàn tài liệu này** (đã trả giá ba lần trong dự án — whitelist tên style,
`TitleAbbreviations`, route `030` chỉ mở khoá 1/95 file): **đo baseline trước khi xây**. Mọi mục dưới
đây đều có một dòng "nghiệm thu bằng số nào" — chưa có số thì chưa được viết code retrieval thật.

## 1. Nhiệm vụ — phân biệt với tầng đã có trong spec

Spec `todo10_8/spec-heading-outline-v2.md` §6 định nghĩa **Tầng 3** cho câu hỏi nhị phân *"block này
là heading hay không"* (§6.1), request/response schema riêng (§6.3/§6.4), và đã có `split` field cho
ca heading dính body. Ba domain vừa đo (`handoff.md`) là một nhiệm vụ **hẹp hơn**, nằm bên trong
`split`: **cho một block ĐÃ BIẾT là heading dính body, cắt đúng điểm ranh giới**. Không hỏi lại
"có phải heading không" — câu đó tầng khác đã trả lời (ứng viên đến từ `MergedParagraphHeadings`,
`PartSectionOutline`, `PdfBoldLabelOutline`... đều đã tự tin block bắt đầu bằng heading).

Vì hẹp hơn, request/response có thể đơn giản hơn §6.3/§6.4 — không cần `sibling_candidates`/
`repeat_count`, chỉ cần: text đầu vào, few-shot ví dụ, trả về đúng phần heading (tương đương
`split.heading_text` + `split.boundary_char_offset` của §6.4, không cần trường nào khác).

## 2. Thứ đã có, để không viết lại

| Cần cho retrieval | Đã có ở đâu | Còn thiếu gì |
|---|---|---|
| Lưu trữ correction đã người xác nhận | [`CorrectionMemory`](../src/DocxHeaderExtractor.Infrastructure/Learning/CorrectionMemory.cs) — JSONL append-only, `VerifiedCorrection` record, `SaveChangedAsync`/`Load` | Record hiện tại là `PredictedLevel`/`CorrectedLevel` (sửa CẤP) — cần thêm biến thể cho sửa RANH GIỚI (`OriginalText`, `HeadingSpan`), tái dùng type có sẵn (`HeadingRecord.HeadingSpan`/`InlineBodySpan`/`InlineBody` đã đúng hình dạng cần) |
| Retrieval theo độ giống | `CorrectionMemory.FindExamples` — top-N theo `Similarity()` | **Gap thật, không phải tiểu tiết**: `Similarity()` bắt buộc `NumberingAudit.Parse` thành công ở CẢ HAI phía rồi so `Signature` (dòng 231-233); heading KHÔNG marker (domain "biên bản họp" — đúng nhóm giá trị cao nhất) luôn cho `NumberingAudit.Parse == null` → similarity luôn 0. Cơ chế hiện tại **không truy xuất được gì** cho đúng nhóm quan trọng nhất. |
| Chèn ví dụ vào prompt | `CorrectionMemory.InjectExamples` — chèn trước `END_DOCUMENT_VIEW`/`</doc>` | Đúng vị trí nối, tái dùng được nguyên vẹn cho nhiệm vụ ranh giới |
| Retrieval theo embedding (spec §6.3 nói tới) | **CHƯA CÓ** — `Similarity()` hiện tại là lexical/signature-based, không phải embedding | Cần quyết định: embedding thật (thêm model/thư viện) hay mở rộng lexical hiện có để phủ ca không-marker (mục 4 dưới) |

**Kết luận mục này:** không xây từ đầu. Mở rộng `CorrectionMemory` (hoặc một class song song cùng
khuôn) là đường đúng — nhưng `Similarity()` phải sửa trước khi retrieval dùng được cho domain không
marker, nếu không tính năng sẽ "có vẻ hoạt động" trên legal/RFC rồi im lặng vô dụng trên đúng nhóm
cần nhất.

## 3. Baseline bắt buộc trước khi xây retrieval

Bảng mode → 2 shot cố định, đã đo (`handoff.md`, cấu hình đầy đủ ở đó — Llama-3.2-3B-Instruct-Q4_K_M,
greedy, CPU):

| Domain | Shots | Kết quả |
|---|---|---|
| Pháp quy VN (`Điều N.`) | 2 shot cùng dạng (có dấu `:`) | 18/21 (85,7%) |
| RFC (`N.N.`) | 2 shot riêng domain (tiếng Anh) | 19/20 (95,0%) |
| Biên bản họp (không marker) | 2 shot riêng domain (1 có `:`, 1 không dấu câu) | 12/14 (85,7%) |

Đây LÀ baseline, không phải "số tham khảo". Bảng cứng domain → 2 shot này **rẻ hơn retrieval rất
nhiều** (không embedding, không lưu trữ, không cơ chế cập nhật) và đã có bằng chứng hoạt động trên
ba domain khác hẳn nhau.

## 4. Điều kiện retrieval được xây — không phải sở thích kiến trúc

Retrieval chỉ đáng xây nếu vượt qua CẢ HAI:

1. **≥ baseline trên ba domain đã đo** (85,7% / 95,0% / 85,7%) — dùng retrieval-2-example (top-2 từ
   pool khởi tạo bằng đúng 6 ví dụ đã dùng làm shot cố định) thay bảng cứng, đo lại y hệt 55 ca đã có
   sẵn (21+20+14). Nếu retrieval tự chọn sai domain (ví dụ lấy nhầm shot RFC cho ca legal vì tình cờ
   giống bề mặt) mà điểm tụt, đó là bằng chứng "phức tạp hơn mà không lợi hơn" — dừng lại đúng chỗ.
2. **≥ 70% trên MỘT domain chưa có shot nào từ trước** — domain đề xuất: 3-5 ca lấy từ nhóm 22 file
   no-route (`022_ND_01-2021_Dang_ky_doanh_nghiep`, hoặc một trong 16 file `FormatDriven` còn lại
   ngoài `073/074/080` đã đo). Đây là phép thử THẬT của "retrieval tổng quát hơn bảng cứng" — nếu
   retrieval chỉ giỏi bằng bảng cứng trên ba domain đã biết trước, nó không chứng minh được gì hơn
   một bảng tra cứu; phải tự chọn/pha trộn ví dụ tốt cho domain nó CHƯA từng thấy.

Nếu (1) đạt mà (2) không đạt: giữ bảng cứng, thêm domain mới vào bảng bằng tay (rẻ hơn debug
retrieval không tổng quát). Nếu cả hai đạt: retrieval có lý do tồn tại.

**ĐÃ ĐO (2026-08-15) — điều kiện 1 KHÔNG đạt, giữ bảng cứng.** Chi tiết đầy đủ + confound cần đọc
trước khi tin số: `handoff.md` mục "thí nghiệm retrieval theo §4 ... KHÔNG thắng bảng cứng".

```text
                baseline   retrieval
legal  (21 ca)  85,7%      81,0%   (-4,7)
rfc    (20 ca)  95,0%      95,0%   (bằng)
minutes(14 ca)  85,7%      57,1%   (-28,6)   <- điều kiện 1 thua rõ
unseen  (5 ca)     —       80,0%   (>= 70%, điều kiện 2 đạt)
```

Không xây retrieval production. Hai lỗ hổng cụ thể của BẢN THỬ NGHIỆM này (không phải kết luận
nguyên lý — chưa đo lại nên không được nói "retrieval không thể thắng"):

1. Wrapper (system prompt) dựng động dùng khung tổng quát, mất phần chú thích riêng từng ví dụ mà ba
   wrapper gốc có (`"Example 1 (ends with a colon)"`...). `minutes` — domain duy nhất có hai dạng
   ranh giới khác hẳn trong cùng pool — tụt nặng nhất, dù retrieval vẫn chọn đúng cặp shot ở phần lớn
   ca; `rfc` (domain đồng nhất) không tụt. Khớp đúng giả thuyết "chú thích mất đi mới là nguyên nhân",
   không phải "chọn sai ví dụ".
2. Shape-signature 24-ký-tự-thô đôi khi trộn domain (một ca `legal` lấy nhầm 1 shot legal + 1 shot
   minutes vì độ dài/mật độ dấu câu tình cờ giống nhau).

Nếu muốn thử lại: sửa CẢ HAI lỗ hổng là HAI biến, phải tách đo riêng, không gộp một lượt (đúng §0).
Không phải việc ưu tiên ngay — mục 8 (danh sách nợ kỹ thuật CÓ ĐIỀU KIỆN) do đó CHƯA mở khoá.

## 5. Correction pool — nguồn, định dạng, cơ chế xác nhận

**Định dạng.** Mở rộng theo khuôn `VerifiedCorrection` đã có, thêm biến thể ranh giới:

```csharp
public sealed record BoundaryCorrection(
    string Id, string SourceFile, string StableId,
    string OriginalText,      // nguyên văn đoạn glued title+body
    string HeadingText,       // phần đã xác nhận đúng là title
    int BoundaryCharOffset,   // == HeadingSpan.End, tái dùng TextOffsetSpan có sẵn
    DateTimeOffset CreatedUtc);
```

Không cần kiểu mới cho *dữ liệu* — `HeadingRecord.HeadingSpan`/`OriginalText` đã đúng hình dạng. Chỉ
thiếu tầng LƯU TRỮ RIÊNG (JSONL khác file, tránh lẫn với correction cấp) và tầng RETRIEVAL riêng.

**Nguồn khởi tạo pool.** Hai nguồn, không cần chờ UI review:
1. Sáu ví dụ đã dùng làm few-shot cố định trong ba lượt đo (mục 3) — chuyển thẳng thành pool ban đầu.
2. `keys/*-human/*.key` hiện có (`legal-human`, `typed-human`, `format-driven-human`) — mỗi heading
   trong các key này có `OriginalText` (paragraph nguồn) sẵn, tự động sinh `BoundaryCorrection` bằng
   cách so `HeadingSpan` (heading text kết thúc ở đâu trong paragraph) — không cần người duyệt lại
   những ca ĐÃ được người đọc PDF gốc xác nhận khi tạo key.

**Cơ chế người xác nhận cho ca MỚI (không có trong key có sẵn).** Tái dùng đúng luồng review hiện có
(`ReviewBundle`/`dhx review-key`), thêm trường `CorrectedBoundary: int?` song song `CorrectedLevel`
đã có — người dùng thấy ranh giới LLM đề xuất trong Web UI, xác nhận hoặc kéo lại điểm cắt. Không cần
UI mới, chỉ thêm một trường vào bundle đã có và một control kéo-thả hoặc input offset trong review
page hiện tại.

**Câu hỏi chưa trả lời, cần quyết định trước khi code:** một correction sai (người duyệt xác nhận
nhầm) có gỡ được không, và ai có quyền gỡ? `CorrectionMemory` hiện tại chỉ có `Load`/append — không
có xoá. Với correction MỨC CẤP, sai một correction chỉ làm sai gợi ý cấp cho ca giống nó. Với
correction MỨC RANH GIỚI dùng làm **few-shot**, một ví dụ sai có thể kéo sai nhiều ca khác qua cơ chế
bắt chước hình dạng — đúng hiện tượng vừa đo được (case `Điều 1. Ghi chú về từ ngữ` cho input tiếng
Anh). Rủi ro cao hơn correction cấp, cần cơ chế gỡ/đánh dấu-nghi-ngờ, không chỉ append-only.

## 6. Ngân sách

`spec-heading-outline-v2.md` §6.6 đo cho **Tầng 3 đầy đủ** (JSON schema có `sibling_candidates`):
~575 token/request. Request cho nhiệm vụ HẸP hơn ở tài liệu này (chỉ text + 2 shot, không sibling)
nhỏ hơn — ước lượng thô từ system prompt + 2 shot + input dùng trong ba lượt đo: 300-600 token/lượt
tuỳ domain (RFC dài hơn legal). **CHƯA đo bằng đúng tokenizer** — làm trước khi chốt batch size,
đúng cảnh báo đã có ở §6.6.

Số lượt/tài liệu: §6.7 đo cho Tầng 3 phân loại (không phải tầng ranh giới) — `toc-anchored`/
`numpr-driven` 2-4 ca/tài liệu, `vn-administrative` 25-30 ca/tài liệu, `typed-numbering` 0. Đây là
SỐ GẦN ĐÚNG NHẤT hiện có, không phải số đo trực tiếp cho tầng ranh giới — **CHƯA ĐO**: trong một tài
liệu thật, bao nhiêu ứng viên heading thật sự cần gọi tầng ranh giới (đã dính body) so với bao nhiêu
tự tách sạch bằng `InlineHeadingSplitter`/regex không cần LLM. Cần đo trên vài tài liệu thật trước
khi ước lượng throughput.

## 7. Chọn model

Ba lượt đo đều dùng **Llama-3.2-3B-Instruct-Q4_K_M**, CPU, và đạt 85-95% — tức đây nhiều khả năng là
**sàn, không phải trần** (theo Intelligence Index đã bàn trước đó, Qwen3.5-9B ăn điểm gấp đôi 2B).
Đề xuất: **Qwen3.5-4B trước**, đo lại đúng ba domain — nếu giữ hoặc vượt 85-95% thì 4B đủ dùng và có
headroom batching tốt hơn 9B cho nhóm tải cao (`vn-administrative`, §6.7). Chỉ lên 9B nếu 4B tụt điểm
rõ trên một domain.

## 8. Cái gì gỡ được nếu tầng LLM ranh giới chứng minh được — đo bằng cách tắt-và-so

Danh sách dưới đây là **nợ kỹ thuật CÓ ĐIỀU KIỆN** — chỉ gỡ sau khi tầng LLM ranh giới đã đo đạt
điều kiện mục 4, và gỡ từng cái một, đo lại, không gộp (đúng kỷ luật "một biến mỗi vòng đo"):

| Luật hiện tại | File | Vì sao có thể là nợ |
|---|---|---|
| `TitleAbbreviations` deny-list (Mr./Ms./Dr...) | [`PdfBoldLabelOutline.cs`](../src/DocxHeaderExtractor.DocumentProcessing/Pipeline/PdfBoldLabelOutline.cs) | Vá đúng MỘT lớp lỗi hình thái học (viết tắt danh xưng); LLM ranh giới đã đo đúng ca này khi có shot phù hợp |
| Gate "paragraph phải cho ≥2 entry PART/Section" | [`PartSectionOutline.cs`](../src/DocxHeaderExtractor.DocumentProcessing/Pipeline/PartSectionOutline.cs) `TextTocEntries` | Luật cấu trúc thay cho phán đoán ngữ nghĩa "đây có phải TOC thật không" — LLM có thể đọc ngữ cảnh trực tiếp |
| 5 exemption trong `InlineHeadingSplitter` (`pdf_bold_label`, `part_section_toc_text`, `pdf_textbook_layout`, `typed_number_depth`, `legal_marker_declared`) | [`InlineHeadingSplitter.cs`](../src/DocxHeaderExtractor.DocumentProcessing/Pipeline/InlineHeadingSplitter.cs) | Tồn tại vì splitter generic không tách được các dạng này — nếu tầng LLM thay hẳn splitter generic cho các route đó, exemption không còn ý nghĩa (không có gì để miễn trừ) |
| `PdfBoldLabelOutline` (bộ dựng cả file) | như trên | Nếu LLM ranh giới đạt ≥ điều kiện mục 4 trên đúng domain `FormatDriven`/biên bản họp, có thể thay bằng: ứng viên từ heuristic thường (không cần đọc PDF riêng) + LLM cắt ranh giới — bỏ hẳn phần đọc PdfPig song song |

**Đo bằng cách nào:** với mỗi dòng, tắt luật đó (feature flag hoặc comment tạm), thay bằng gọi tầng
LLM ranh giới tại đúng điểm quyết định, chạy lại đúng bộ test/eval đã có cho route đó (`073`/`074`
key, `030` eval, WB 9-file regression), so số. Giữ luật nếu tắt làm tụt điểm; gỡ nếu không đổi hoặc
tốt hơn. Không gỡ theo trực giác "chắc LLM giỏi hơn" — đúng bài học §10.4 của dự án.

## 9. Việc tiếp theo, đúng thứ tự

1. Sửa `CorrectionMemory.Similarity` (hoặc class song song) để không phụ thuộc `NumberingAudit.Parse`
   — không thì retrieval vô dụng trên đúng domain giá trị cao nhất trước khi thí nghiệm mục 4 chạy.
2. Sinh pool ban đầu từ 6 shot + 3 key `*-human/` hiện có (mục 5, nguồn 1+2 — không cần chờ UI).
3. Chạy thí nghiệm mục 4: retrieval-2-example so bảng cứng trên 55 ca đã có, cộng 3-5 ca domain mới.
4. Chỉ sau khi (3) cho retrieval thắng mới thiết kế UI xác nhận/gỡ correction (mục 5, câu hỏi chưa
   trả lời) — làm UI trước khi biết retrieval có đáng dùng là xây trước khi đo, đúng bẫy §10.4.
