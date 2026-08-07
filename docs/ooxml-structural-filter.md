# Spec: tầng quyết định cấu trúc OOXML trước LLM

Bản viết lại của spec "lớp filter deterministic dựa trên OOXML style". Bản gốc đề xuất một bảng
luật R1–R6 chia mỗi block thành `auto_assign | route_pass1`. Phần lớn đề xuất đó **đã tồn tại trong
pipeline dưới dạng khác, hoặc đã được đo và bác**; tài liệu này giữ lại phần còn đúng, thay phần đã
bị số liệu bác, và nêu rõ phần duy nhất còn trống thật.

Mọi con số trích dẫn ở đây đều dẫn về `handoff.md` hoặc về dòng code cụ thể. Chỗ nào chưa đo thì ghi
**CHƯA ĐO** — không suy từ triệu chứng (bài học §7.5).

---

## 1. Vì sao không phải `auto_assign | route_pass1`

Bản gốc đặt mục tiêu "loại khỏi luồng LLM mọi block mà OOXML đã cho tín hiệu đủ tin cậy". Cờ làm
đúng việc đó đã tồn tại — `SkipStyledCandidates` tại
[HeaderExtractionPipeline.cs:52](../src/DocxHeaderExtractor.Core/Pipeline/HeaderExtractionPipeline.cs#L52)
— và doc-comment trên nó ghi kết quả đo:

> bỏ 32 câu hỏi ra khỏi khối làm đổi thành phần khối, và mô hình trả lời những đoạn **CÒN LẠI**
> khác đi: trên tài liệu thật, precision tụt từ 100% xuống 94,1% (nhận nhầm hai ô tiêu đề bảng)
> để đổi lấy 24% thời gian.

**R1 nay đã được đo TRỰC TIẾP** — xem `handoff.md` §10; bằng chứng dưới đây không còn chỉ là suy từ
một cờ lân cận. Tóm tắt: trên bench R1 cho F1 **tăng** 90,9% → 92,0% (6/8 → 7/8), nhưng truy nguyên
thì bốn heading nó gán thẳng đều đã đúng sẵn ở nhánh tắt, và toàn bộ lợi ích đến từ một đoạn
`styleId: Normal` mà R1 **không hề chạm tới** — nó lật chỉ vì thành phần khối đổi. Trên fixture style
bị áp sai, hai nhánh cho **cùng P/R/F1**, nhưng nhánh R1 **tự nhận 7 mục với confidence 1.0, trong
đó 3 mục sai** (độ chính xác auto_assign 57,1%) trong khi nhánh tắt đẩy cả 9 sang *cần duyệt*.

Hai hệ quả cho thiết kế:

1. **Rút một đoạn ra khỏi luồng LLM không miễn phí.** Nó đổi thành phần khối, và heading có style
   nằm xen kẽ đóng vai trò neo cho chuỗi sinh tự hồi quy. Cùng họ với bẫy §4.1: đổi thành phần
   batch làm mô hình trả lời khác đi cho những mục KHÔNG liên quan.
2. **`auto_assign` còn mạnh hơn cờ đó.** `SkipStyledCandidates` chỉ *không hỏi* nhưng vẫn giữ đoạn
   trong tập ứng viên để các lượt sau cắt được; `auto_assign` với `confidence 1.0` chốt luôn, không
   có gì phía sau bắt lại — đúng như §7 của chính bản gốc thừa nhận.

Áp R1 lên hai tài liệu thật đã đo:

| | style built-in nói gì | R1 sẽ làm gì |
|---|---|---|
| Khoá luận (§9.2) | 68 đoạn mang style, **68/68 là heading thật** | đúng — nhưng chỉ phủ 52% heading, 63 mục còn lại không style |
| Báo cáo thực tập (§7.1, §7.4) | tác giả gán Heading cho chú thích bảng, dòng bìa, khối chữ ký, mục liệt kê; **13 chú thích bảng mang Heading3** | chốt cứng cả nhóm rác với `confidence 1.0` |

**Đây chính là lý do tài liệu này tồn tại**: cùng một luật, hai tài liệu thật, hai kết quả ngược
nhau. Câu hỏi đúng không phải "block này có style không" mà **"style của TÀI LIỆU NÀY có đáng tin
không, và đáng tin cho việc gì"** — §7.1 và §9.7 đều dừng lại ở chỗ *"chưa có tín hiệu đo được"*.
Mục 4 đề xuất tín hiệu đó.

## 2. Thứ đã có, để không viết lại

Trước khi thêm gì, đây là những gì tầng OOXML đang làm — bản gốc đề xuất lại phần lớn:

| Bản gốc | Đã có ở đâu |
|---|---|
| R1/R2 nhận style Heading1..9/Title | [`HeadingHeuristics.BuiltInLevel`](../src/DocxHeaderExtractor.Core/OpenXmlLayer/HeadingHeuristics.cs#L347) — theo định danh ECMA-376, không phụ thuộc ngôn ngữ |
| R2 ngưỡng độ dài | `ExtractionOptions.MaxCandidateTextLength = 200`, cộng `Text.Length <= 80 → +0.10` |
| R2 kết thúc bằng dấu câu | `SentenceEndRx → -0.25` |
| R3 numbering + bold/cỡ chữ | nhánh `NumberingId → +0.60`, và `BodyFontSizePt` tính **theo tài liệu** chứ không lấy `docDefaults` ([SlimParagraph.cs:79](../src/DocxHeaderExtractor.Core/Models/SlimParagraph.cs#L79)) |
| R4 in_table / in_textbox | `TableDepth → -0.35`; textbox đã được `ParagraphWalker` đi vào và giữ đúng thứ tự |
| R6 mặc định | `PromoteStandaloneLine` — lớp ứng viên yếu nhất, đủ để lọt vào diện được hỏi |
| "giảm lượt gọi LLM" | critic bỏ qua đoạn có `NumberingStyleLevel`/style built-in ([:575](../src/DocxHeaderExtractor.Core/Pipeline/HeaderExtractionPipeline.cs#L575)); lượt gán cấp bỏ hẳn nếu cấu trúc đã quyết hết ([:794](../src/DocxHeaderExtractor.Core/Pipeline/HeaderExtractionPipeline.cs#L794)). Đo được 424 s → 144 s |

Khác biệt cốt lõi về hình thái: `HeadingHeuristics.Classify` không trả một **quyết định nhị phân**
mà trả một **vai trò + điểm số** (`ParagraphRole`, `Score`), rồi để các tầng sau cộng dồn bằng
chứng. R3 trong bản gốc là no-op vì những đoạn đó vốn đã là `HeadingCandidate`.

## 3. Ba trạng thái, không phải hai

Lược đồ `auto_assign | route_pass1` không diễn đạt được trạng thái đang gánh phần lớn giá trị của
pipeline. Kiểu đã có ở
[DocumentOutline.cs](../src/DocxHeaderExtractor.Core/Models/DocumentOutline.cs):

```csharp
enum HeadingSource          { Style, Model, Heuristic, Structure, HumanCorrection }
enum HeadingDecisionStatus  { RequiresReview, AutoAcceptedEvidence, AutoAcceptedCalibrated, HumanVerified }
bool Disputed
```

Nguyên tắc trục (§1, §9.5): **mô hình được quyền hạ độ tin cậy, không được quyền xoá bằng chứng
cấu trúc.** Đoạn bị bác mà có bằng chứng rơi vào *cần duyệt* (`Disputed`, confidence ≤ 0,5), không
biến mất. Số đo cho nguyên tắc này:

- +17,6 điểm recall khi ngừng cho mô hình xoá bằng chứng cấu trúc ở lượt critic (§3.1)
- +6,2 điểm recall **và** +1,7 điểm precision khi mở rộng đúng nguyên tắc đó sang lượt phân loại
  (§9.5) — cả 9 đoạn từng bị xoá đều đúng
- vòng 6 (§9.3) thêm một tầng bằng chứng YẾU HƠN (đậm + numbering Word): được cứu khỏi xoá nhưng
  **không bao giờ tự nhận** → recall 84,7% → 96,4%

Tức bậc thang đúng là **ba mức quyền**, không phải hai:

| Mức | Quyền | Ánh xạ kiểu |
|---|---|---|
| Tự nhận | vào kết quả không cần người duyệt | `AutoAcceptedEvidence` / `AutoAcceptedCalibrated` |
| Cần duyệt | vào kết quả, cờ đỏ, người/mô hình mạnh hơn xem lại | `RequiresReview` + `Disputed` |
| Bị loại | không vào kết quả | không sinh `HeadingRecord` |

**Tầng cấu trúc không được cấp mức "tự nhận" trực tiếp.** Mức đó do
`EvidenceConfidenceCalibrator` + `PrecisionAcceptanceGate` cấp, dựa trên 5 kiểm tra
`HeadingEvidence` (`numberingValid`, `siblingSequenceValid`, `formattingConsistent`,
`modelConfirmed`, `treeValid`) và cận dưới Wilson. Việc của tầng cấu trúc là **nộp bằng chứng**,
không phải tự chấm điểm mình.

## 4. Thay R1/R2: đo độ tin cậy của style ở mức TÀI LIỆU

Đây là phần thật sự mới, và là chỗ ý tưởng của bản gốc thuộc về — chỉ sai độ hạt: bản gốc hỏi
"block này" trong khi bằng chứng nằm ở "tài liệu này".

Style Word mang **hai quyền khác nhau**, và hai tài liệu thật hỏng ở hai quyền khác nhau:

| | quyền CHỌN (style ⇒ là heading) | quyền GÁN CẤP (style ⇒ cấp mấy) |
|---|---|---|
| Khoá luận (§9.2, §9.7) | **tin được** — 68/68 | **không tin được** — dùng H1→H3→H4, bỏ H2, đúng cấp ~28% |
| Báo cáo (§7.1) | **không tin được** — 13 chú thích mang Heading3 | **không tin được** — gần như mọi thứ là Heading2, đúng cấp 40,7% |

Hiện pipeline trao **cả hai quyền vô điều kiện** cho style built-in
([Classify:177-185](../src/DocxHeaderExtractor.Core/OpenXmlLayer/HeadingHeuristics.cs#L177-L185),
[ResolveLevel:1117-1119](../src/DocxHeaderExtractor.Core/Pipeline/HeaderExtractionPipeline.cs#L1117-L1119)).
Đó là thứ đưa bench từ 54,2% lên 100% đúng cấp — và cũng là thứ giữ báo cáo thật ở 40,7%.

### 4.1 Hai chỉ số, tính được không cần nhãn

**`StyleSelectionTrust`** — style có được áp đúng chỗ không:

- `r_caption` = tỉ lệ đoạn mang style built-in mà đồng thời khớp một mẫu **đã có** trong
  `HeadingHeuristics`: `ObjectLabelPrefixRx` + `PrecedesTable`, `InTableOfContents`,
  `BulletPrefixRx`, hoặc `SentenceEndRx`. Trên báo cáo thật nhóm này là 13/73 — chính nó là tín
  hiệu đo được rằng style bị áp bừa.
- `r_density` = số đoạn mang style built-in / số đoạn không rỗng. Vượt một ngưỡng thì "heading"
  mất nghĩa.

**`StyleLevelTrust`** — style có mang thông tin CẤP không:

- `distinct_style_levels` = số cấp riêng biệt mà style khai. Bằng 1 trên >20 đoạn ⇒ style không
  phân biệt cấp (ca báo cáo thật).
- `skipped_levels` = có cấp bị bỏ giữa chừng không (H1→H3 mà không H2) ⇒ con số trong tên style
  không phải độ sâu thật (ca khoá luận).
- `numbering_depth_conflict` = so `distinct_style_levels` với số bậc mà chuỗi đánh số gõ tay khai
  qua `StructuralHierarchyResolver.SignatureTiers`. Style khai 1 bậc trong khi đánh số khai 3 ⇒
  nguồn cấp đáng tin là đánh số.
- Ngoại lệ nâng hẳn tin cậy: có `w:lvl/w:pStyle` (`NumberingStyleLevel`). Người soạn cấu hình một
  lần cho cả tài liệu qua hộp thoại multilevel list nên nó không nhiễm lỗi copy định dạng — đây đã
  là bằng chứng mạnh nhất trong thứ tự quyền lực §1.

### 4.2 Hai chỉ số đó đổi được gì

Chỉ đổi **quyền**, không đổi tập ứng viên:

| Tình huống | Hành vi |
|---|---|
| `StyleLevelTrust` thấp | style vẫn CHỌN đoạn, nhưng **thôi cấm mô hình gán cấp** — bỏ chốt tại [:794](../src/DocxHeaderExtractor.Core/Pipeline/HeaderExtractionPipeline.cs#L794) và [:1119](../src/DocxHeaderExtractor.Core/Pipeline/HeaderExtractionPipeline.cs#L1119) cho tài liệu này; cấp lấy từ `SignatureTiers` rồi tới phiếu mô hình |
| `StyleSelectionTrust` thấp | đoạn mang style **không còn** thoát sớm với `Score = 1.0`; nó đi tiếp xuống phần tính điểm như mọi ứng viên khác, và critic được hỏi lại về nó thay vì bỏ qua ([:575](../src/DocxHeaderExtractor.Core/Pipeline/HeaderExtractionPipeline.cs#L575)) |
| Cả hai cao | y hệt hôm nay |

**Ràng buộc bắt buộc**: hai chỉ số này chỉ được **hạ quyền**, không được **xoá đoạn**. Đoạn mang
style built-in trong tài liệu bị chấm điểm thấp vẫn phải giữ được đường sống qua `RequiresReview` —
nếu không thì lặp lại đúng lỗi §3.1.

### 4.3 Cách đo — CHƯA ĐO, và cách đo là phần khó

Nguy cơ hiển nhiên: đây là một luật **mức tài liệu**, mà bench chỉ có 8 tài liệu tổng hợp đều dùng
style đúng. Trên bench cả hai chỉ số luôn cao ⇒ luật không kích hoạt ⇒ bench **không phát hiện được
gì**, tốt lẫn xấu. Đó chính là bẫy §9.6.2 ở chiều ngược lại.

Quy trình đo bắt buộc, theo kỷ luật §9.3 (một biến mỗi vòng):

1. Dựng fixture từ bench bằng cách **vá `styles.xml`/`document.xml`** để tái tạo hai chế độ hỏng —
   cùng cách §7.3 đã tái tạo ca `outlineLvl` lệch, giữ nguyên `.key`. Kiểm chứng fixture TRƯỚC khi
   đo: bản tắt luật cho `distinct_style_levels = 1`, bản bật thì chỉ số đổi.
2. Điều kiện giữ: holdout `bench/holdout/` tăng F1 **và** bench 8 tài liệu giữ 8/8. Bench ở đây
   đóng đúng vai nó sinh ra để đóng — chặn một luật đúng-với-một-tài-liệu bị nâng thành luật chung.
3. Backend tất định: Qwen `-ngl 20`, `top_k=1`, `temperature=0`, seed cố định, hai lượt mỗi bên.
   **Không dùng agent đóng vai LLM để nghiệm thu** — §7.2 đo được hai lượt cùng đầu vào chỉ đồng ý
   33% về cấp.

## 5. Thay R5: ranh giới heading không trùng ranh giới paragraph

Đây là phần duy nhất của bản gốc chỉ vào một lỗ hổng **có thật và chưa làm**. R5 như viết
("`same_paragraph_as_prev` hoặc `has_internal_line_break` ⇒ gộp với block liền trước") không dùng
được vì hai điều kiện đó nói về hai chuyện khác nhau và cả hai đều không tồn tại trong parser.

### 5.1 Ba kiểu xuống dòng, ba hành vi khác nhau

| | XML | Parser hiện tại | Cần gì |
|---|---|---|---|
| Word tự wrap | không có gì | một paragraph, đúng | không cần gì |
| **Shift+Enter** | `w:br` trong cùng `w:p` | [`case Break: sb.Append(' ')`](../src/DocxHeaderExtractor.Core/OpenXmlLayer/DocxSlimExtractor.cs#L230-L232) — **xoá không dấu vết** | ghi lại vị trí break |
| **Enter thật** | hai `w:p` | hai `SlimParagraph` rời, không có liên hệ | bước ghép ứng viên đa block |

Hệ quả của dòng giữa: trường hợp Shift+Enter **hiện không phân biệt được với Word tự wrap**.
`has_internal_line_break` của bản gốc không tồn tại và không suy lại được từ `Text`.

### 5.2 Chuẩn hoá khoảng trắng đã xảy ra, ánh xạ ngược thì chưa

[`BuildTextAndSpans`](../src/DocxHeaderExtractor.Core/OpenXmlLayer/DocxSlimExtractor.cs#L266-L277)
gộp mọi chuỗi whitespace — kể cả `\t` từ `w:tab` — thành một dấu cách. `SlimParagraph.Text` **là**
bản normalized; không giữ `raw_text`, và `SlimTextSpan` cũng đánh offset trên bản đã chuẩn hoá.
`StableId` định vị tới paragraph, không tới ký tự.

Hậu quả đã nhìn thấy được trong code, không phải giả định:
[`OutlineWriteback`](../src/DocxHeaderExtractor.Core/OpenXmlLayer/OutlineWriteback.cs#L132) từ chối
ghi ngược với lý do `"inline_body_not_splittable"` mỗi khi `InlineBody is not null`. Tức phần tách
heading/body **phát hiện được nhưng không ghi lại DOCX được** — đúng chỗ cặp `raw_text` /
`normalized_text` + offset mapping của bản gốc thuộc về.

### 5.3 Chiều tách đã làm, chiều ghép thì chưa

Cần ghi nhận để không viết lại:
[`InlineHeadingSplitter`](../src/DocxHeaderExtractor.Core/Pipeline/InlineHeadingSplitter.cs) đã tách
heading khỏi body trong **cùng** một paragraph, và đã tuân đúng nguyên tắc "không cắt máy móc tại
dấu `:`" — nó đòi hoặc chuyển tiếp bold→non-bold tại ranh giới
([:62-86](../src/DocxHeaderExtractor.Core/Pipeline/InlineHeadingSplitter.cs#L62-L86)), hoặc phần sau
dấu phân cách là payload thuần số/ký hiệu
([:88-108](../src/DocxHeaderExtractor.Core/Pipeline/InlineHeadingSplitter.cs#L88-L108)), và quét
**từ phải sang trái** nên dấu `:` bên trong tên mục vẫn sống.

Chạy ví dụ `2.1.2. Thành công: Tỉ lệ thành công: 20%` **toàn đậm** qua nó: `TryRunBoundary` trượt
(không có chuyển tiếp đậm), `TryNumericPayloadBoundary` trượt (payload còn chữ cái) ⇒ **không
tách**. Ví dụ đó nằm đúng vào khe hở, và cần đúng thứ bản gốc phác: quyết định của mô hình phải
mang được **span**, không chỉ một cờ heading/không-heading.

Cùng khe hở với hạn chế đã ghi ở §5 handoff: *"Lược đồ chỉ cho một quyết định trên một `i`"* — nên
`i=452` chứa hai heading dính nhau cũng không tách được.

### 5.4 Ba việc con, tách ra vì giá và rủi ro khác nhau

| | Việc | Đụng prompt? | Cách nghiệm thu |
|---|---|---|---|
| **a** | `SlimParagraph.LineBreakOffsets` — giữ vị trí `w:br` trên `Text`, **không** đổi ký tự | không, nếu chưa gửi cho model | unit test + kiểm đột biến; không cần bench |
| **b** | Offset mapping normalized → (run index, offset trong run) | không | test round-trip; mở khoá `inline_body_not_splittable` |
| **c** | Ứng viên đa block: một quyết định trải nhiều `i` | **có** — đổi lược đồ | bench + holdout, đo riêng |

**Không đổi `Text`.** Thay `w:br` bằng `\n` nghe gọn hơn nhưng `Text` đang là hợp đồng của mọi thứ
phía sau — `TextSpans`, `InlineHeadingSplitter`, `NeutralDocumentViewSerializer`, writeback đều
đánh offset trên nó. Thêm một mảng offset là phép cộng; đổi ký tự là đổi mọi offset cùng lúc.

**(a) và (b) rẻ vì chúng không đụng vào thứ mô hình đọc.** Ngay khi một trường mới được đưa vào
metadata gửi cho mô hình thì nó thành thay đổi phải đo bằng bench — §7.3 là tiền lệ trực tiếp: một
trường metadata mâu thuẫn đủ để lật đúng cấp từ 100% xuống 71,4%, và **bản vá "giấu trường đi" làm
bench tụt 8/8 → 7/8** vì san phẳng một tương phản có ích. Cách sửa thắng cả hai trục là **chuẩn hoá
chứ không giấu**. Áp dụng cho `LineBreakOffsets`: nếu gửi, gửi ở dạng nhất quán với những gì prompt
đã dạy, đừng gửi một trường mà mô hình phải tự đoán quy ước.

### 5.5 Luật ghép (c) — tín hiệu, không phải từ khoá

Giữ nguyên tinh thần bản gốc, viết lại theo kỷ luật "không luật nào chứa một từ tiếng Việt nào"
(§9):

- paragraph đầu mang numbering (`NumberingId` hoặc mẫu `DecimalPrefixRx`), paragraph sau **không**
  mang numbering mới;
- hai paragraph cùng `FontSizePt`, cùng `Bold`, cùng `Alignment`, cùng `TableDepth`, cùng
  `SectionIndex`;
- không có đoạn rỗng ở giữa, `Index` liền nhau;
- paragraph sau bắt đầu bằng chữ **thường** (`!char.IsUpper`) — tín hiệu mạnh nhất và độc lập ngôn
  ngữ;
- mục anh em cùng chữ ký `NumberToken.Signature` tồn tại ở chỗ khác trong tài liệu.

Ghép chỉ tạo một **ứng viên ghép** (`source_blocks`, `normalized_text`), **không xoá hai block
gốc** — cùng nguyên tắc §3.1: thêm giả thuyết, không xoá bằng chứng.

## 6. Output: dùng kiểu đã có

Không thêm schema mới. Kết quả của tầng này ghi vào `HeadingRecord` đã có:

| Bản gốc | Thay bằng |
|---|---|
| `decision: auto_assign` | `DecisionStatus` — do calibrator/gate cấp, **không phải** tầng cấu trúc tự cấp |
| `decision: route_pass1` | không tồn tại — mọi ứng viên đều đi qua luồng mô hình (mục 1) |
| `confidence: 1.0` | `Confidence` + `ConfidenceBasis`; 1.0 chỉ dành cho `HumanVerified` |
| `flags: [...]` | `HeadingEvidence` (5 kiểm tra) + `AcceptanceSignature` |
| `merged_from: [...]` | mới — cần cho mục 5.5, là trường duy nhất phải thêm |

Hai chỉ số mức tài liệu ở mục 4 thuộc về `DocumentOutline`, không thuộc `HeadingRecord`: chúng mô
tả tài liệu, không mô tả một đoạn. Ghi ra để đọc được lý do một lượt chạy trao quyền cho ai.

## 7. Test set — mỗi ca gắn với một số đo hoặc ghi CHƯA ĐO

Giữ các ca của bản gốc, bỏ những ca đã có luật trả lời, thêm ca đã đo được:

| Ca | Trạng thái |
|---|---|
| Heading style đúng nhưng dài bằng cả câu | có luật (`MaxCandidateTextLength`, `SentenceEndRx`); cần fixture khoá chiều "không auto-reject nhầm" |
| Heading + body chung một paragraph | `InlineHeadingSplitter` bắt được **khi** có chuyển tiếp đậm hoặc payload thuần số; **trượt** khi toàn đậm (5.3) |
| Numbering gõ tay `1.2.3` trên Normal, không đậm | có luật — `DecimalPrefixRx +0.55`, không cần thêm tín hiệu định dạng |
| Heading trong ô bảng phụ lục mang Heading2 | **hiện style thoát sớm TRƯỚC khi trừ điểm bảng** ([Classify:177](../src/DocxHeaderExtractor.Core/OpenXmlLayer/HeadingHeuristics.cs#L177)); mục 4.2 làm đúng ca này |
| Heading1 áp cho cả tiêu đề tài liệu lẫn dòng chú thích | đã đo, và có ca xấu hơn: **trang bìa lặp hai lần**, "BÁO CÁO THỰC TẬP" y hệt ở `i=56` và `i=74` (§5). Ràng buộc "tối đa MỘT document_title" sai ở đây — không sửa được bằng đổi mô hình |
| Style tự đặt tên gần giống "Heading" | có luật — `LocalizedHeadingTokens`, nằm sau `UseLexicalRules`, và giao diện **mặc định tắt cờ đó** |
| **Mới**: tài liệu dùng một cấp style duy nhất cho mọi mục | CHƯA CÓ FIXTURE — cần cho 4.3 |
| **Mới**: tài liệu bỏ cấp giữa (H1→H3→H4) | CHƯA CÓ FIXTURE — cần cho 4.3 |
| **Mới**: heading bị Shift+Enter cắt đôi | CHƯA CÓ FIXTURE, và parser hiện không phân biệt được (5.1) |
| **Mới**: heading bị Enter thật cắt thành hai paragraph | CHƯA CÓ FIXTURE (5.5) |

Mọi test mới phải qua **kiểm đột biến**: gỡ logic ra thì đỏ, gắn lại thì xanh. §9.6.4 là ca một
test không phân biệt được gì mà vẫn trông như đang bảo vệ điều gì đó — tiền lệ xử lý là gỡ hẳn test
thay vì để một test xanh giả.

## 8. Metric

Bỏ **"tỷ lệ auto_assign"**. Nó là một chỉ số **thưởng cho việc bỏ qua công đoạn**, và §4.5 đã ghi
đúng chế độ hỏng đó: một bản vá đưa recall lên 100% và F1 96,3% — vượt mọi ngưỡng — trong khi lặng
lẽ biến hai dòng mục lục thành heading.

Giữ và thêm:

| Chỉ số | Đọc thế nào |
|---|---|
| P / R / F1 / đúng cấp trên bench + holdout | thước chính; hai lượt, backend tất định |
| Tỉ lệ heading lọt vào tập ứng viên | **KHÔNG phải "trần recall"** — công văn có 66,7% mà recall cuối 88,9% nhờ `StructuralRecovery` (§7.1) |
| Phân bố bucket `AcceptanceSignature` | đã có; §6.1 cho thấy nó bắt được lỗi mà chỉ số tổng không thấy (5 heading chuyển đúng rổ, tổng không đổi) |
| `DisputedCount` | tăng mà P/R không đổi là **tốt** — nghi ngờ đúng chỗ, không phải hồi quy |
| **Mới**: `StyleSelectionTrust`, `StyleLevelTrust` | mức tài liệu; ghi vào `DocumentOutline` để đọc lại được lý do trao quyền |

Về calibration: ngưỡng `MinimumCalibrationSamples = 52` với `TargetPrecision = 0,93` cần n=52 cho
bucket **không có lỗi nào**, và nhảy lên **n=80** nếu có **một** lỗi. Bucket lớn nhất hiện có 28
mẫu (§5). Thêm bucket mới ở mục 4 làm phân mảnh thêm — **cân nhắc gộp thay vì tách** cho tới khi có
vài chục tài liệu thật đi qua bảng Review.

## 9. Thứ tự thực hiện

Xếp theo tỉ lệ (giá trị đã chứng minh) / (rủi ro đo lường):

1. **5.4(a) `LineBreakOffsets`** — parser-only, không đụng prompt, nghiệm thu bằng unit test. Mở
   đường cho mọi thứ còn lại mà không nợ một phép đo nào.
2. **5.4(b) offset mapping** — cũng parser-only, và có một lợi ích ngay: gỡ được
   `inline_body_not_splittable` ở writeback.
3. **Mục 4 — hai chỉ số tin cậy style.** Đây là thứ §7.1 và §9.7 cùng chỉ vào và cùng dừng ở
   *"chưa có"*. Đắt vì phải dựng fixture mới (4.3), nhưng nó nhắm thẳng vào lỗi lớn nhất còn lại:
   đúng cấp 40,7% và ~28% trên hai tài liệu thật.
4. **5.4(c) ứng viên đa block** — đắt nhất, đổi lược đồ, và không có tài liệu thật nào trong bộ đo
   hiện tại chứng minh nó đáng. Làm sau cùng, hoặc sau khi có một tài liệu thật hỏng đúng kiểu đó.

Ràng buộc xuyên suốt, lặp lại vì phiên trước mất số đo vì nó: **một biến mỗi vòng đo** (§4.1), và
mọi con số phải ghi kèm số lớp offload GPU (§3.7).
