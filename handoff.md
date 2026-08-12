# Handoff — chuyển trích xuất heading sang hướng cấu trúc quyết định

Tài liệu này ghi lại một phiên làm việc: đổi kiến trúc quyết định heading, đo lại từng bước, và
những chỗ suýt kết luận sai. Viết cho người tiếp nhận, nên phần "vì sao" quan trọng hơn phần
"đã sửa gì".

---

## 0. Trạng thái hiện tại — đọc mục này trước

**Cập nhật 2026-08-11.**

### Số đo hiện tại — chỉ những gì có ĐÁP ÁN

| bộ đo | P | R | F1 | đúng cấp | đúng cha | tuyệt đối |
|---|--:|--:|--:|--:|--:|--:|
| **bench + mô hình** (7 tài liệu) | **100%** | **100%** | **100%** | **100%** | **100%** | **7/7** |
| bench `--no-llm` (7 tài liệu) | 92,3% | 100% | 96% | 100% | 100% | 6/7 |
| khoá luận thật (1.498 đoạn, `key-human.key` 105 mục) | 79,5% | 96,2% | 87,1% | **96,0%** | 96,0% | — |
| khoá luận `--style-outline` (đáp án người, 68 mục) | 100% | 100% | 100% | 100% | 100% | — |
| báo cáo TT `--numbering-outline` (đáp án người, 29 mục) | 100% | 100% | 100% | 100% | 100% | — |

**452 test xanh** (build sạch — xem §50.1 về cách đếm).

Cấu hình đo khoá luận: `--style-trust --chunk-tokens 28000 --ctx 32768 -ngl 99 --no-reuse-prefix`,
Qwen3.5-9B-Q4_K_M. Pipeline **tất định** — hai lượt y hệt cho trùng khít từng chữ số (§33.1).

### Ba điều phải biết trước khi trích bất kỳ con số nào khác

1. **Corpus 95 file `todo10_8` KHÔNG có đáp án.** Mọi bảng ở §45.3, §47.2, §48.1 chỉ nói *luật nào
   kích hoạt*, không nói *gán đúng hay sai*. Đọc §45.4, §46.3, §48.4 trước khi dùng chúng.
2. **Mọi con số `--no-llm` ghi TRƯỚC §51 đều thiếu bộ suy cấp tất định** — kể cả bảng phân bố cấp
   ở §45.3. Con số còn hiệu lực nằm ở §51.3 và §55.9.
3. **`bench --no-llm` không đi qua đường có mô hình.** §55.12 liệt kê năm lỗi mà bench xanh suốt
   trong khi chúng đang tồn tại. Bench xanh không nói gì về nhánh bench không chạy.

### Việc đã xong và còn hiệu lực

| | |
|---|---|
| §51 | bộ suy cấp tất định chạy cả trên `--no-llm` — bench đúng cấp 86,1% → 100%. **Mặc định bật** |
| §53 | `TableOfContentsAnchor` pin đúng cấp cho heading `numPr` — đúng cấp 44,8% → 96,6% |
| §56.3 | luật **chuỗi mồ côi** (`2.1` dưới `PHỤ LỤC A`) — bench có mô hình 6/7 → **7/7** |
| §56.4 | context **tự đọc từ GGUF** thay cho allowlist theo tên: 4.096 → 32.768 |
| §45.1 | ba bảng chữ cái tiếng Việt cho `NumberingAudit` (`d) → đ)` không còn báo đứt quãng sai) |
| §45.2 | `--split-merged` cắt tiêu đề lọt giữa paragraph — **mặc định tắt**, xem TODO mục 10 |
| §55.2 | nhãn + số **không dấu ngắt** (`Chương II QUY ĐỊNH CHUNG`), chốt in-hoa chặn chú thích |
| §52 | `dhx toc-keys` — mục lục Word làm đáp án miễn phí |

### Kết luận lớn nhất của dự án


### Kết luận lớn nhất của dự án

**Mọi tiến bộ đo được đều đến từ việc đọc dữ kiện cấu trúc có sẵn trong tài liệu.**
Đúng cấp đi `26,5% → 37,2% → 51,9% → 66,0% → 81,1% → 91,5% → 96,0%` qua sáu luật tất định
(§17, §22, §24.1, §28, §31) cộng một lần sửa đáp án (§37).

**Năm hướng "cho mô hình nhiều hơn" đều cho số không:**

| Hướng | Kết quả |
|---|---|
| One-pass 129.546 token một khối (§19) | R 83,2% — kém nhất |
| Thinking toàn bộ (§24.2) | mất 10 điểm recall; +4,5 cấp là ảo giác (§25.1) |
| Thinking riêng lượt gán cấp (§25) | không đổi một chữ số |
| Khung outline tăng dần (§30.2) | −1,3 precision |
| Thị giác làm tầng lọc (§32) | +2,6 F1 nhưng gấp ba thời gian |

### Việc tiếp theo, theo thứ tự giá trị

1. **Người duyệt tiếp** (TODO 4) — cổ chai dưới mọi con số. Một câu "thừa này" của người dùng vừa
   bắt được một cổng quyết định chạy ngược suốt từ đầu (§37).
2. **Recall**: 2/4 mục đã cứu được (`PHỤ LỤC 1/2`, TODO 7 nửa đầu); 2 mục còn lại (bullet kết thúc
   bằng `:` bị trừ điểm kép) chưa có hướng — không phải do mô hình (§36.3).
3. **Precision 79,5%** — con số kém nhất; bốn luật đã thử và đo là hỏng, đừng thử lại (TODO 8).
4. **Hai bộ cấu hình khác nhau** giữa CLI đã đo và mặc định Web (TODO 9, §35).
5. **Đo bằng LLM cần đúng Qwen3.5-9B, máy hiện chỉ có Qwen2.5-7B/Llama-3.2-3B** (TODO mục 2, 7 nửa
   sau, và 13 phần LLM) — ba câu hỏi, một lượt LLM nếu tách đúng biến: (a) bản sửa
   `TableOfContentsAnchor` có hồi quy trên đường đầy đủ không (phần `--no-llm` đã đo xong, 44,8% →
   96,6%, xem trên), (b) recall của `StructuralRecovery` sau khi thêm `Labelled` có tăng trên
   `key-human.key` không, (c) đo nhánh `LevelTrusted`/"hạ quyền chuyển chỗ trống" — nay có 2 tài liệu
   thật (báo cáo thực tập + fixture `10-cap-style-thoai-hoa`) sẵn sàng để đo cùng lúc.

### Bốn kỷ luật đã trả giá để học

- Xác minh **môi trường** trước khi tin con số: log phải nói `GPU N lớp` (§27).
- Dump dùng để suy luận phải **sinh lại bằng đúng cờ** của lượt đang bàn (§33.3, §36.1 — sai hai lần
  liên tiếp).
- Chỉ số tính trên **tập được giữ lại** tự đẹp lên khi tập bị lọc bớt (§25.1).
- Cờ bật để **làm sạch phép đo** có thể che lỗi ở đường mặc định (§35.3).

---

## 1. Kiến trúc mới

Trước: LLM quyết định đoạn nào là heading **và** cấp của nó; các luật cấu trúc chạy sau như lưới
cứu, và có quyền bị LLM phủ quyết.

Sau: **cấu trúc quyết định, LLM chỉ xác nhận ngữ nghĩa.**

Thứ tự quyền lực khi gán cấp (`HeaderExtractionPipeline.ResolveLevel`):

1. `w:lvl/w:pStyle` — danh sách đa cấp tự khai cấp này gắn với style Heading N
2. Style Heading built-in trên chính đoạn
3. Phiếu mô hình

Nguyên tắc xuyên suốt: **phán quyết của mô hình được quyền hạ độ tin cậy, không được quyền xoá
bằng chứng cấu trúc tường minh.** Mục bị bác mà có bằng chứng cấu trúc thì rơi vào trạng thái
*cần duyệt* (`Disputed`, confidence ≤ 0.5), không biến mất.

## 2. Kết quả đo

Bộ bench 8 tài liệu tổng hợp (`dhx bench`), mô hình Qwen2.5-7B-Instruct-Q4_K_M, GPU 20 lớp.

| Mốc | Precision | Recall | F1 | Đúng cấp | Tuyệt đối |
|---|---|---|---|---|---|
| Đầu phiên | 100% | 70,6% | 82,8% | 54,2% | 1/7 |
| + không cho critic xoá bằng chứng cấu trúc | 100% | 88,2% | 90,9% | 90,0% | 5/7 |
| + bỏ hardcode khỏi prompt, bench 8 tài liệu | 97,4% | **97,4%** | **97,4%** | **100%** | 6/8 |
| Cùng bản trên, chạy `--structural-only` | 97,4% | **97,4%** | **97,4%** | **100%** | 6/8 |
| **Bản bàn giao** (`--structural-only`) | **97,5%** | **100%** | **98,7%** | **100%** | **7/8** |
| + phản biện theo dấu hiệu (thay vì hỏi tất) | 97,5% | 100% | 98,7% | 100% | 7/8 |
| + lượt refactor harness (§6), hai lượt đo | 97,5% | 100% | 98,7% | 100% | 7/8 |
| + chuẩn hoá `outlineLevel` (§7.3), hai lượt đo | **100%** | **100%** | **100%** | **100%** | **8/8** |

Hai dòng cuối là **điều kiện nghiệm thu chứ không phải tiến bộ**: cả hai lượt đó nhằm bỏ code
sai/chết/trùng và đổi cách kích hoạt critic, nên số đo giữ nguyên mới là đạt. Riêng phản biện
theo dấu hiệu rút thời gian bench 1542 s → 1350 s.

Dòng `Bản bàn giao` quan trọng vì **giao diện web mặc định tick "Bỏ luật từ ngữ"** (`structuralOnly`), còn
`dhx eval` thì không — tức mọi con số trước đó đo ở một cấu hình khác cấu hình chạy thật. Sau khi
thay danh sách từ khoá bằng luật hình dạng, hai cấu hình cho kết quả trùng khít.

Và đây là số đo trên TÀI LIỆU THẬT với cùng bản code — **bảng trên KHÔNG dự báo được bảng dưới**,
§7 giải thích vì sao:

| Tài liệu thật, END-TO-END | P | R | F1 | Đúng cấp |
|---|---|---|---|---|
| Báo cáo thực tập 1183 đoạn, 61 heading | 83,1% | 96,7% | **89,4%** | **40,7%** |
| Công văn hành chính 344 đoạn, 18 heading | 88,9% | 88,9% | **88,9%** | **100%** |

Đáp án của báo cáo có HAI người gán nhãn độc lập đồng thuận 96,8% ở chọn đoạn và 100% ở cấp (§7.2).

Trần trên đo bằng Claude Sonnet đọc **đúng cùng document view và cùng prompt**: recall 97,1%,
đúng cấp 100%. Nghĩa là Qwen 7B trong pipeline này đã chạm mức của một mô hình mạnh hơn nhiều khi
cả hai nhận cùng lượng thông tin — dư địa còn lại không nằm ở việc đổi mô hình.

## 3. Những gì đã đổi, theo mức đóng góp đo được

### 3.1 Ngừng để mô hình xoá bằng chứng cấu trúc (+17,6 điểm recall)

Bốn nhánh trong `HeaderExtractionPipeline` cho phép một phán quyết — hoặc cả sự **im lặng** — của
mô hình xoá hẳn heading:

- `document_title` ở lượt critic
- `document_title` ở lượt phân loại (chí mạng nhất: `accepted` chỉ dựng từ phiếu, nên đoạn bị gán
  `d` ngay lượt đầu không bao giờ tới được critic)
- critic bác đoạn mang style Heading built-in
- critic **không trả lời** cho đoạn mang style built-in (xoá vì *thiếu* bằng chứng)

`document_title` không phải lời bác: nó khẳng định đoạn CÓ vai trò tiêu đề, chỉ khác là tiêu đề của
cả văn bản. Đo được: đoạn 0 bị mất ở 6/7 tài liệu vì heading mở đầu trông giống tiêu đề chính.

### 3.2 Bỏ hardcode khỏi prompt

Lượt đo đưa recall từ 88,2% lên 97,4% gộp ba thay đổi (bỏ hardcode, siết luật vớt dòng độc lập,
thêm tài liệu bench thứ tám) nên **không quy được toàn bộ 9,2 điểm cho riêng phần này**. Bằng chứng
riêng cho nó là ở mức tài liệu: `02-dinh-dang-thu-cong` từ thiếu 3 heading + sai 3 cấp thành tuyệt
đối, và đó là tài liệu gõ tay không có bằng chứng cấu trúc nào để hai thay đổi kia tác động.

Prompt critic từng liệt kê `"dòng điều phối hành chính, địa chỉ/người gửi-người nhận, lời
chuyển/kính gửi, tên cơ quan, mã biểu mẫu, dấu mật/khẩn…"` — một bảng phân loại cứng cho văn bản
hành chính Việt Nam, và **chỉ có vế phủ định**. Hệ quả đo được: critic loại 3 heading trong tài
liệu dùng toàn style Heading chuẩn.

Thay bằng phép thử quan hệ: *đoạn này có mở ra phạm vi nội dung bên dưới không, hay sau nó chỉ toàn
dòng cùng loại với nó?* — kèm vế khẳng định *"thì nó là heading, kể cả khi ngắn, không đậm, không
đánh số"*. `ChunkerAndJsonTests` khoá chiều ngược: prompt **không được** chứa lại các cụm đặc thù đó.

Cùng lý do, `KeywordPrefixRx` (`chương|phần|mục|điều|chapter|section|…`) được thay bằng
`LabelledNumberPrefixRx` — nhận theo **hình dạng** "từ viết hoa + số + phần tên", không quan tâm từ
đó là gì. Test có cả `Quyển 3.` và `Abschnitt 4.` (tiếng Đức).

### 3.3 Tắt chuẩn hoá cấp theo độ sâu ngăn xếp (đúng cấp 54% → 100%)

`NormalizeLevels` gán cấp theo độ sâu ngăn xếp nên heading đầu tiên **còn sống** luôn thành cấp 1 —
mất một heading cha là mọi con tụt theo. Dấu vân tay: 10/10 lỗi cấp đều một chiều "trả về 1, đáp án
2". Giờ mặc định tắt; bật lại bằng `--normalize-levels`.

### 3.4 Đọc thêm OOXML

- `w:lvl/w:pStyle` → `SlimParagraph.NumberingStyleLevel`
- `w:numStyleLink` → lần theo thư viện danh sách dùng chung (trước đây mọi tài liệu dùng list style
  đều ra numbering rỗng)
- Đoạn đứng ngay trước các dòng mục lục → `PrecedesTableOfContents`, thay cho danh sách từ khoá
  ("MỤC LỤC", "Danh mục hình ảnh", "Inhaltsverzeichnis" đều nhận được)

### 3.5 Tách cấu hình chia khối ra khỏi backend

`ChunkTokenBudget`, `MaxCandidatesPerChunk`, `ChunkOverlap` từng nằm trong `LlamaOptions` — lớp
mang tên backend GGUF cục bộ — chỉ vì backend đó ra đời trước. Chia khối là việc của pipeline:
cùng một tài liệu, cùng cách cắt, dù câu hỏi đi tới GGUF, LM Studio hay OpenRouter. Bốn hậu quả
đã đo được:

1. Nhánh LM Studio quên gọi profile chunk nên im lặng thừa hưởng mặc định 2200 của bản local bị
   giới hạn VRAM: 13 ứng viên thành 27 lượt RPC thay vì ~7.
2. Luật nâng ngân sách lên 5000 bám vào **tên file .gguf** chứa "qwen". Chạy đúng bộ trọng số đó
   qua LM Studio thì luật không bao giờ kích hoạt vì không có đường dẫn file nào.
3. Backend RPC phải ghi giá trị giả vào `Llama.ContextSize` chỉ để phép chia khối ra đúng.
4. Khoá cache model trong web gồm cả hai trường chunking, nên đổi ngân sách khối làm nạp lại
   4,4 GB weights một cách vô ích.

Nay là `PipelineOptions.Chunking`. `LlamaOptions` chỉ còn giữ phần thuộc về nó — "model này chịu
được context bao nhiêu" — và nhận ngân sách từ ngoài qua `ApplyRecommendedModelProfile(chunking)`.
`RemoteChunkProfileTests` khoá bằng phản chiếu rằng ba trường kia không quay lại `LlamaOptions`:
còn hai nguồn sự thật cho cùng một quyết định thì sớm muộn một trong hai đi lệch.

**Chưa làm, và là bước tiếp theo tự nhiên**: cho pipeline suy ngân sách từ
`IHeaderClassifier.ContextSize` (LM Studio đã trả đúng 16384) thay vì hằng 5000. Việc đó **đổi
thành phần khối** nên phải đo riêng, không gộp.

### 3.6 Tốc độ

Không hỏi lại những gì cấu trúc đã quyết:

- critic bỏ qua đoạn có `NumberingStyleLevel` hoặc style built-in (câu trả lời không dùng được vào
  việc gì: chúng đã được bảo vệ khỏi xoá và cấp lấy từ cấu trúc)
- lượt gán cấp toàn cục chỉ hỏi phần cấu trúc chưa quyết được; bỏ hẳn nếu không còn gì để hỏi

Đo được: `01-style-chuan` từ ~424 s xuống ~144 s. Hiệu quả phụ thuộc tài liệu — file soạn bằng
style/multilevel list chuẩn nhanh gấp ~3, file gõ tay gần như không đổi.

### 3.7 Sửa lỗi tái lập

- **LM Studio không được khai báo sampler** → kết quả phụ thuộc preset trong GUI. Cùng file, cùng
  model, khác preset là khác đáp án. Giờ gửi tường minh `top_k=1, top_p=0.9, repeat_penalty=1.0,
  seed` dùng chung hằng `LlamaOptions.SharedSamplerSeed` với backend local. Đo được: recall 40% →
  100% trên một tài liệu.
- **Số lớp offload lên GPU là một trục tái lập nữa, ngang hàng với sampler.** Cùng model, cùng seed,
  cùng `top_k=1`/`temperature=0`, cùng một thư mục một tài liệu: backend CPU cho đúng cấp 100% trên
  `02-dinh-dang-thu-cong`, chính bản build Vulkan chạy `-ngl 0` cũng 100%, `-ngl 20` vẫn 100% — chỉ
  `-ngl 99` mới rơi xuống 85,7% (i=10 trả về cấp 2, đáp án 1). Bản build không phải nguyên nhân; tám
  lớp cuối cộng lớp output chạy trên GPU đủ đổi logit để argmax lật ở một ca sát nút. Hai lượt
  `-ngl 99` cho kết quả giống hệt nhau, nên đây **không phải nhiễu mà là một cấu hình đo khác** —
  gộp số của hai mức offload vào cùng một bảng là lặp lại bẫy §4.1. Mọi con số phải ghi kèm số lớp
  offload; xem §8.1.
- **Backend local không nhận danh sách ID cần trả lời**, trong khi hai backend RPC đều gửi. Grammar
  ép được cú pháp nhưng không nằm trong ngữ cảnh mô hình đọc. Đo được: 4/5 → 5/5.
- `JsonSerializer` mặc định escape non-ASCII → dev log hiện `ạ`, trông như lỗi font.
- `dhx xml --compact` in `SlimXmlSerializer` trong khi pipeline gửi `NeutralDocumentViewSerializer` —
  dump lệch khỏi thứ mô hình thật sự đọc, dù trợ giúp CLI hứa ngược lại.
- CLI ghi đè file `-o` bằng nội dung rỗng khi mọi tài liệu đều lỗi (xoá mất kết quả lần trước).

## 4. Cách đo và những cái bẫy đã gặp

`dhx eval <thư-mục> -m <model.gguf> -ngl 20 --calibration-out <profile.json>` — mỗi `X.docx` cần một
`X.key` đi kèm; `dhx bench <thư-mục>` sinh cả hai.

Bốn cái bẫy đã thật sự làm sai kết luận trong phiên này:

1. **Đổi thành phần batch làm mô hình trả lời khác đi cho những mục KHÔNG liên quan.** Thêm 2 mục
   vào batch critic đưa một tài liệu từ "giữ 3, loại 0" sang "giữ 3, loại 3", kéo recall toàn bộ
   bench từ 73,5% xuống 70,6%. Không bao giờ gộp nhiều thay đổi vào một lượt đo.
2. **Chẩn đoán đúng triệu chứng nhưng vá sai tầng.** Bản vá `document_title` đặt ở critic trong khi
   đoạn chết từ lượt phân loại — chỉ lộ ra khi đo lại.
3. **Cấu hình đo lệch cấu hình chạy.** Giao diện mặc định tick "Bỏ luật từ ngữ" (`structuralOnly`),
   còn `dhx eval` không truyền cờ nên chạy ở cấu hình dễ hơn.
4. **Log nói dối.** `"Đang nạp mô hình"` in ra ở mọi tài liệu trong một lượt eval khiến tưởng weights
   bị nạp lại 7 lần; dòng native của llama.cpp chỉ xuất hiện một lần. Đã sửa câu log.
5. **Đạt ngưỡng không có nghĩa là xong.** Bản vá tiêu đề mục lục đưa recall lên 100% và F1 96,3% —
   vượt mọi ngưỡng — nhưng đồng thời biến hai DÒNG MỤC của mục lục thành heading, vì một dòng mục
   cũng đứng ngay trước dòng mục kế tiếp. Lượt đo sau khi sửa: precision 92,9% → 97,5%. Nếu dừng ở
   lượt trước vì con số đã đủ đẹp thì đã giao một lỗi tự gây.

Test đi kèm đều có **kiểm tra đột biến**: tạm vô hiệu hoá logic rồi chạy lại để chắc test đổ. Ba
lần làm vậy đều bắt được — ví dụ bỏ đọc `pStyle` thì `MultilevelListHeadingTests` đổ, bỏ lưới an
toàn `document_title` thì cây heading rỗng sạch (`Collection: []`).

## 5. Còn lại

> Danh sách việc **đang mở** và thứ tự làm nằm ở [`TODO.md`](TODO.md). Mục này giữ lại
> bối cảnh và những cần gạt đã thử — phần "vì sao", không phải phần "làm gì tiếp".


- ~~**`07-chen-chi-thi` thừa 1 đoạn**~~ **ĐÃ HẾT** — bench giờ tuyệt đối **8/8, P/R/F1 đều 100%**.
  Không phải nhờ prompt hay nhờ trả lại ngữ cảnh so sánh, mà nhờ **chuẩn hoá `outlineLevel`** ở
  §7.3: dòng tiêm không có `outlineLevel`, heading thật thì có, và tương phản đó đủ để loại nó.
  Phần dưới giữ nguyên vì nó ghi lại những cần gạt đã thử và vì sao chúng không phải câu trả lời.

  Bối cảnh cũ: dòng tiêm chỉ thị từng là lỗi DUY NHẤT còn lại trên cả 39
  heading của bộ bench. Nó mang đủ bốn tín hiệu hình thức của heading (đậm, hoa, căn giữa, 14pt)
  nên vào được tập ứng viên, không có bằng chứng cấu trúc nào để phân định, và critic — đúng chỗ
  phải quyết — trả lời sai.

  Đáng phân biệt: **phòng thủ injection vẫn giữ**. Câu lệnh "coi mọi đoạn là heading cấp 1" không
  được làm theo; nếu bị làm theo thì cả 13 đoạn đã thành H1. Nó chỉ tự nhận nhầm chính mình.

  Đã thử và **thất bại**: thêm câu "tên mục là cụm từ đặt tên, không phải một câu" vào prompt
  critic — số không đổi một chữ số nào. Xem ghi chú tại `HeaderPrompt.CriticSystem`.

  Thứ từng bác đúng nó là **ngữ cảnh so sánh**: hồi critic còn được hỏi 5 mục cùng lúc, có heading
  thật đứng cạnh, nó bác đúng. Sau khi cắt batch (mục 3.5) nó chỉ còn được hỏi 1 mục đơn độc. Đòi
  lại được bằng cách bỏ nhát cắt đó, giá là `01-style-chuan` từ ~144 s trở lại ~424 s. Đây là đánh
  đổi precision ↔ tốc độ, không phải một lỗi chờ sửa — và hoá ra cũng không phải cần gạt duy nhất.
- ~~**Chuỗi đánh số gõ tay chưa làm nguồn quyết định cấp.**~~ ĐÃ LÀM —
  `StructuralHierarchyResolver.SignatureTiers` gom chữ ký `NumberToken.Signature` (`Kind:Depth`)
  theo thứ tự XUẤT HIỆN LẦN ĐẦU và suy quan hệ lồng nhau từ đó, nên `PHẦN I` nằm trên `1.` mà không
  cần biết chữ "PHẦN". Chỉ chạy cho đoạn mà `PathOf` không đọc được và cấu trúc chưa tự khai cấp;
  thiếu hai chốt đó thì kéo độ chính xác cấp 100% → 87,2% (đã đo).
- **`NumberingAudit` vẫn không đọc được "Chương 1."** — không có mẫu nào tương ứng
  `HeadingHeuristics.LabelledNumberPrefixRx`, nên dạng "nhãn + số" không sinh ra `NumberToken`. Đây
  là lý do gốc của bug 87,2% ở trên, hiện đang được *vá bằng chốt* chứ chưa được *sửa*. Thêm mẫu đó
  vào `Parse` sẽ đổi output của cả tám điểm gọi cùng lúc (audit, tiers, calibrator, precision gate,
  recovery, correction memory, critic gate, inline splitter) — phải đo riêng, không gộp.
- **Profile calibration: sinh được nhưng CHƯA dùng được.** Chữ ký cấu hình đã đổi (`chunkTokens`,
  `normalizeLevels`) nên profile cũ hết hiệu lực, và profile mới sinh từ bench cũng không đủ mẫu:
  `MinimumCalibrationSamples = 52` cho mỗi bucket evidence, trong khi cả 8 tài liệu chỉ cho
  `model_critic_numbered` 22 mẫu, `model_critic_unnumbered` 16 mẫu, các bucket còn lại 1 mẫu.
  Vì vậy mọi lượt chạy đều báo *"evidence chưa calibration bằng holdout"* và cổng precision rơi về
  chấp nhận theo evidence thay vì theo cận dưới Wilson.

  Số học của ngưỡng, để biết còn thiếu bao xa: với `TargetPrecision = 0,93`, một bucket **không có
  lỗi nào** cần n=52 (Wilson = 0,931) — đúng giá trị `MinimumCalibrationSamples`. Nhưng chỉ cần
  **một** lỗi thì yêu cầu nhảy lên **n=80** (Wilson 0,933; ở n=52 chỉ còn 0,899). Bucket lớn nhất
  hiện có 28 mẫu, tức mới ~35% đường tới ngưỡng LẠC QUAN NHẤT.

  Muốn có profile thật thì cần vài trăm heading đã gán nhãn — tức vài chục tài liệu thật đi qua
  bảng Review, không phải thêm tài liệu tổng hợp. Tài liệu tổng hợp chỉ chứng minh được đường code,
  không sinh ra được phân phối đúng của tài liệu thật.
- **Bench là 8 tài liệu tổng hợp, 39 heading** — một heading sai là ~2,5 điểm phần trăm. §7 đã đo
  và xác nhận điều này bằng số: trên tài liệu thật F1 tầng OpenXML chỉ 76,3%, và trần recall của
  một công văn gõ tay là 66,7%. Con số 97,4%/98,7% KHÔNG đảm bảo cho tài liệu thật. Muốn có số thật thì cần `.key` cho vài tài liệu thật; bảng
  Review trong giao diện web sinh ra đúng thứ đó.

- **Ràng buộc "tối đa MỘT document_title" sai với tài liệu có bìa lặp.** Báo cáo thật có trang bìa
  lặp hai lần; "BÁO CÁO THỰC TẬP" xuất hiện y hệt ở i=56 và i=74. Prompt buộc mô hình phá thế đối
  xứng một cách tuỳ tiện — gán `d` cho lần đầu, hạ lần sau xuống `f`, không có tín hiệu khách quan
  nào để chọn. Không sửa được bằng đổi mô hình.
- **`InlineHeadingSplitter` không tách được block dính hai heading.** Ở báo cáo thật, i=452 chứa cả
  "Tình hình hoạt động kinh doanh trong 3 năm" lẫn "Tình hình huy động vốn" vì file gốc thiếu ngắt
  đoạn. Lược đồ chỉ cho một quyết định trên một `i` nên mô hình không có cách nào tách.
- **`CharsPerToken = 1.85` quá bi quan ~1,64 lần** (đo được: 3,03 với Qwen, 3,21 với Llama-3.2) —
  xem §7.5. LM Studio và OpenRouter chia 26 khối trong khi 15 là đủ, tức ~73% lượt gọi RPC thừa.
  Chưa sửa vì đổi ngân sách là đổi thành phần khối.
- **Đáp án tài liệu thật do agent gán nhãn, chưa có người xác nhận.** Cả hai file `.key` trong
  §7 đều là phán đoán của mô hình. Agent tự đánh dấu vài mục "không chắc" (`* Nhận xét:`,
  `- Biển Đông:`, nhãn phòng ban kết thúc bằng `:`). Muốn có thước đo thật thì cần người duyệt.

## 6. Lượt refactor theo kiến trúc harness

Lượt này **không nhằm đổi số đo** mà nhằm bỏ code sai, code chết và code trùng chức năng. Điều kiện
nghiệm thu vì vậy là *số đo không đổi*: hai lượt bench riêng (`eval-m6`, `eval-m7`, Qwen 7B,
`--structural-only`) đều cho lại đúng **P 97,5% · R 100% · F1 98,7% · đúng cấp 100% · 7/8**.

### 6.1 Một lỗi thật, cùng một họ, ở sáu chỗ

`NumberingAudit.Parse(text)` đọc ký hiệu đánh số từ **text hiển thị**. Nhưng khi Word đánh số qua
`w:numPr`, con số **không nằm trong text của run** — nó chỉ tồn tại ở `SlimParagraph.NumberLabel` do
`NumberingResolver` tính ra. Chỉ `StructuralHierarchyResolver` biết ghép nhãn vào trước khi đọc;
tám điểm gọi còn lại thì không.

Hệ quả: đúng nhóm tài liệu được đánh số bài bản nhất lại bị tính là "không có numbering" —
`numberingValid`, `siblingsValid`, `formattingConsistent` cùng trượt, mất 3/5 kiểm tra evidence, và
heading rơi xuống *cần duyệt*.

Đã gom về một điểm vào duy nhất `NumberingAudit.ParseParagraph(paragraph, fallbackText)` và dùng ở
`EvidenceConfidenceCalibrator`, `NumberingAudit.Run`, `HasStructuralEvidence`, `StructuralRecovery`,
`OutlineStructureResolver`, `HeadingAcceptanceSignature`.

Bằng chứng nó có tác dụng thật nằm ở **phân bố bucket calibration**, không ở chỉ số tổng:

| bucket | trước | sau |
|---|---|---|
| `model_single_numbered` | 23/23 | **28/28** |
| `model_single_unnumbered` | 16/16 | **11/11** |

Năm heading chuyển đúng rổ. Chỉ số tổng không đổi vì cả hai rổ đều đang 100%, nhưng nếu cứ để vậy
thì profile calibration sau này học sai phân phối — và đó mới là chỗ nó gây hại.

### 6.2 Thứ tự quyền lực bị vi phạm ở hai chỗ

`StructuralHierarchyResolver.SignatureTiers` có chốt "cấu trúc đã khai cấp thì không suy lại", kèm
số đo lý do (thiếu chốt: 100% → 87,2%). Nhưng **nhánh path Ả Rập ngay bên cạnh trong cùng file**
không có chốt đó, và `OutlineStructureResolver.Upsert` cũng không. Cả hai đều ghi đè `Level` của
đoạn mà tài liệu đã tự khai qua `w:lvl/w:pStyle` hoặc style built-in.

Đã thêm cùng một chốt ở cả hai. Ở `OutlineStructureResolver` chỉ cấm **ghi đè** cấp — vẫn cho phép
cứu đoạn chưa có mặt, vì cây La Mã nói được là đoạn CÓ vai trò đề mục, chỉ không nói được cấp; khi
cứu thì lấy cấp đã khai chứ không lấy cấp suy ra.

### 6.3 Code chết và trùng chức năng

- `SlimXmlSerializer.BuildLines` (2 overload) + `WrapChunk` là **bản song song từng dòng** của
  `NeutralDocumentViewSerializer`, không còn caller nào trong `src/` — chỉ test giữ nó sống. Xoá 74
  dòng; ba test chuyển sang serializer thật.
- `RequestOptions.Build` gán `SkipStyledCandidates = TrustStyles` ở cả ba nhánh backend, tức **mọi
  request web** chạy ở chế độ mà chính doc của `PipelineOptions.SkipStyledCandidates` đã bác bỏ
  bằng số đo (precision 100% → 94,1%), và giao diện không có ô nào cho nó. Bỏ. Ba cờ chung
  (`TrustStyles`, `ShowRawOutput`, `TwoPass`) gom về một chỗ thay vì lặp ở cuối mỗi nhánh.
- `LlamaHeaderExtractor.LoadAsync` áp `ApplyRecommendedModelProfile` lên chính `LlamaOptions` của
  caller — một hàm tên "Load" âm thầm sửa `ContextSize`/`ChunkTokenBudget` của đối tượng người gọi
  đang giữ và dùng lại cho lượt sau. Nay áp lên bản sao (`LlamaOptions.Clone()`).
- `OutlineStructureResolver.BulletRx` thiếu ký tự `o` so với `HeadingHeuristics.BulletPrefixRx`.
- Biến chết `explicitContext`; hai khối `<summary>` chồng nhau; một khối `<summary>` mồ côi cách
  method nó mô tả ~30 dòng. Build còn **0 warning**.

`HeadingHeuristics` và `NumberingAudit` vẫn giữ hai bộ mẫu riêng — **có chủ đích**, vì một bên sai
theo hướng rộng (bỏ sót ứng viên là mất hẳn) còn một bên sai theo hướng hẹp (nhận nhầm thì hậu kiểm
báo thiếu mục không tồn tại). Ba chỗ lệch cụ thể và lý do nay ghi rõ ở đầu `NumberingAudit`, thay
cho comment cũ nói chúng "giữ giống hệt nhau" — điều chỉ đúng với mẫu số Ả Rập.

### 6.4 Một lỗ trong hợp đồng harness

`WritebackTargetGuardrail` chốt "agent không được sửa file gốc". Nhưng pipeline ghi document view ra
`DumpXmlPath` **giữa lượt chạy**, không qua `IDocumentActionTool`, nên không guardrail nào thấy —
một cờ debug là đủ để ghi đè tài liệu nguồn trong khi harness vẫn báo *"run chỉ đọc"*. Descriptor
cũng khai cứng `MutatesExternalState: false`.

Đã thêm `AgentToolDescriptor.SideEffectPaths` (tool khai bằng code lúc dựng, không phải model tự
khai) và `ToolSideEffectPathGuardrail` áp đúng hai chốt như đích writeback. `SKILL.md` lên 1.2.0 với
`tool_side_effect_paths` trong `requires.guardrails`, để host không bỏ nó đi trong im lặng.

### 6.5 Một test đo nhầm thứ

`Slim_xml_is_much_smaller_than_raw_document_xml` đòi view nhỏ hơn **một nửa** `document.xml`. Chạy
lại đúng phép đo đó trên view **thật** gửi cho mô hình cho **2864 so với 3089 ký tự — tiết kiệm 7%**.
Con số 50% đúng với định dạng XML đã bị bỏ, không đúng với định dạng đang chạy: metadata JSON dài
hơn thuộc tính XML viết tắt, và tài liệu mẫu 14 đoạn thì gần như đoạn nào cũng là ứng viên nên phần
gom `OMITTED_NORMAL_BLOCKS` không có việc để làm. Đã **bỏ hẳn test thay vì hạ ngưỡng cho vừa**, ghi
lý do tại chỗ; phần tiết kiệm thật sự (gom đoạn thường) do test kế bên khoá.

Toàn bộ test mới đều qua **kiểm đột biến**: gỡ logic ra thì đổ, gắn lại thì xanh.

## 7. Đo trên tài liệu THẬT — và vì sao bench không dự báo được

Toàn bộ số đo trước mục này đến từ 8 tài liệu tổng hợp, 39 heading. Mục này là lần đầu chấm trên
tài liệu thật có đáp án. Kết luận ngắn: **F1 98,7% của bench không chuyển sang tài liệu thật**, và
điểm nghẽn khác nhau tuỳ loại tài liệu.

### 7.1 Hai tài liệu thật, hai chế độ hỏng ngược nhau

| | công văn hành chính gõ tay | báo cáo thực tập dùng style Word |
|---|---|---|
| quy mô | 344 đoạn, 18 heading | 1183 đoạn, 61 heading |
| heading lọt vào tập ứng viên | **66,7%** — rơi 6/18 | **100%** |
| precision tầng OpenXML | tốt | **55%** — 50/111 ứng viên là thừa |
| **end-to-end P / R / F1** | 88,9% / **88,9%** / 88,9% | 83,1% / 96,7% / **89,4%** |
| **end-to-end đúng cấp** | **100%** | **40,7%** |

Hai dòng cuối là kết quả đáng chú ý nhất của cả mục 7, và chúng NGƯỢC NHAU:

- **Công văn gõ tay đạt đúng cấp 100%** vì chuỗi đánh số do người soạn gõ ra là nguồn quyết định
  cấp, và nó nhất quán.
- **Báo cáo dùng style đạt 40,7%** vì tác giả gán `Heading2` cho gần như mọi thứ, kể cả mục cấp 3,
  4, 5. Pipeline đọc đúng tuyên bố đó rồi CẤM mô hình ghi đè — lượt hierarchy chỉ được hỏi 9/71
  heading. **Chính nguyên tắc đưa bench từ 54,2% lên 100% đúng cấp (§1) là thứ giữ tài liệu này ở
  40,7%.** Bench toàn tài liệu style đúng nên "tin cấu trúc" luôn thắng; tài liệu thật có style sai
  hệ thống thì "tin cấu trúc" là tin vào cái sai. Cần một tín hiệu đo được rằng *style của tài liệu
  NÀY có đáng tin không* trước khi quyết định trao quyền cho ai — chưa có.

**Và "trần recall" là cách gọi SAI, đã sửa nhãn trong bộ eval.** Công văn có 66,7% heading lọt vào
tập ứng viên nhưng recall cuối đạt **88,9%**: `StructuralRecovery` chạy SAU mô hình và cứu lại 4
trong 6 heading mà bộ lọc heuristic đánh rơi (log: `Tự đánh giá evidence: 4 heading Structure`).
Đúng là *mô hình* không thấy chúng; nhưng *pipeline* thì cứu được. Nhãn cũ nói "mô hình không cứu
được" rồi bị đọc thành "recall không thể vượt" — hai điều khác nhau.

Công văn gõ tay hỏng ở **recall**: mục `b.`/`c.` mất in đậm, mục `4.` bị tác giả quên bôi đậm
trong khi `1./2./3.` đều có, nhãn `* Kết quả bay…` không phải số/La Mã/chữ cái, ô bảng
`TRỰC CHỈ HUY` bold+hoa+căn giữa giống hệt một mục đã lọt. Tầng OpenXML đánh rơi thì **không mô
hình nào cứu được** — nó chưa từng nhìn thấy đoạn đó.

Báo cáo dùng style hỏng ở **precision**: tác giả gán style Heading cho chú thích bảng, dòng bìa,
khối chữ ký, mục liệt kê. Việc của mô hình ở đây là **cắt bỏ 45% số ứng viên** — một chế độ làm
việc mà bench chưa bao giờ luyện, vì trên bench mô hình gần như không có gì để loại.

Không có một chỉnh sửa nào chữa được cả hai. Bước tiếp theo đúng đắn là biết tài liệu thuộc nhóm
nào trước khi chọn ngưỡng, chứ không phải tinh chỉnh một bộ tham số cho cả hai.

### 7.2 Đo bằng agent Sonnet/Haiku — và một lỗi thiết kế phép đo

Cờ CLI mới `xml --dump-chunks <thư mục>` ghi các khối + system prompt pipeline sẽ gửi.

**CẢNH BÁO về chính bảng dưới**: bản ĐẦU của cờ này truyền `reviewIndexes: null` trong khi pipeline
luôn truyền khác null — nghĩa là view của nó chỉ chứa ứng viên, thiếu hẳn phần thân bài quanh mỗi
ứng viên mà mô hình thật được đọc (51.658 ký tự thay vì 217.733). Bảng dưới đo trên bộ khối THIẾU
ngữ cảnh đó, nên nó KHÔNG phải trần trên của mô hình trên đầu vào thật. Đã sửa cờ; bản sau cho đúng
15 khối như lượt chạy thật, nhưng phép đo Sonnet/Haiku thì chưa chạy lại.

Trên 14 khối (thiếu ngữ cảnh) của báo cáo thật, đáp án 61 heading:

| | n | P | R | F1 | đúng cấp |
|---|---|---|---|---|---|
| Sonnet | 61 | *100%* | *100%* | *100%* | *100%* |
| Haiku | 63 | 96,8% | 100% | 98,4% | 83,6% |

**Dòng Sonnet KHÔNG dùng được làm điểm chất lượng**: đáp án cũng do một agent Sonnet gán nhãn, nên
nó đo *tự nhất quán*, không đo *đúng*. Đây là lỗi thiết kế phép đo, ghi lại để không lặp: thước đo
và đối tượng đo phải khác mô hình.

**Đã vá bằng một người gán nhãn ĐỘC LẬP.** Một agent Opus đọc cùng toàn văn, không được phép nhìn
bản của Sonnet:

| | |
|---|---|
| Đáp án A (Sonnet) | 61 heading |
| Đáp án B (Opus) | 63 heading |
| Trùng chọn đoạn | **61 — Jaccard 96,8%** |
| Trùng cả cấp trên phần chung | **61/61 = 100%** |

Bất đồng duy nhất là `i=56` và `i=74` — dòng "BÁO CÁO THỰC TẬP" trên trang bìa BỊ LẶP HAI LẦN; Opus
tính cả hai là cấp 1, Sonnet loại cả hai. **Cả hai đều tự đánh dấu ca này là "không chắc"** trước khi
biết bên kia nghĩ gì. Nhờ vậy mọi con số P/R/F1 trên tài liệu này đứng trên đồng thuận của hai người
gán nhãn độc lập, không phải ý kiến của một mô hình.

**Sàn nhiễu của phép đo bằng agent** (đo riêng, 3 lượt mỗi nhóm, đầu vào y hệt nhau):

| | F1 | đúng cấp |
|---|---|---|
| Haiku, metadata cũ, n=3 | 97,8% ±0,9 | **62,4% ±21,1** |
| Haiku, metadata mới, n=3 | 93,8% ±5,7 | **83,7% ±20,2** |

Hai lượt CÙNG đầu vào chỉ đồng ý **33%** về cấp ở ca xấu nhất. Độ lệch giữa hai nhóm nhỏ hơn độ lệch
bên trong một nhóm, nên **agent-đóng-vai-LLM không đủ ổn định để NGHIỆM THU một thay đổi**. Nó dùng
được để TÌM lỗi cơ chế (trường `outlineLevel` mâu thuẫn ở §7.3 là phát hiện thật, giá trị thật),
nhưng thước đo nghiệm thu phải là backend tất định (`top_k=1`, `temperature=0`, seed cố định).

Điều nó CÓ chứng minh: Sonnet đọc 14 khối rời cho ra đúng cùng kết quả với Sonnet đọc toàn văn 621
đoạn — chia khối không làm hỏng câu trả lời của nó, ngược hẳn với Qwen (đổi thành phần khối là lật
câu trả lời cho cả mục không liên quan, xem §4).

Thêm một lý do đừng tin lời khai của agent yếu: Haiku tự báo "46 heading" trong khi file `.key` nó
ghi ra có 60 mục. Chấm theo file, không theo báo cáo.

### 7.3 `outlineLevel` mâu thuẫn — lỗi nằm ở thứ MÌNH gửi cho mô hình

Báo cáo thật khai `Heading1 → w:outlineLvl = 1` trong `styles.xml` (quy ước Word là 0). **Cả 73/73
đoạn mang style Heading đều lệch.** Pipeline xử lý đúng ở trong — `guessedLevel` lấy từ tên style
built-in — nhưng metadata gửi cho mô hình chở CẢ HAI:

```json
"styleId":"Heading1", "outlineLevel":1, "guessedLevel":1
```

…trong khi system prompt dạy "outlineLevel: 0 = cấp 1". Tức ta đưa cho mô hình hai trường mâu thuẫn
cộng một luật sai với tài liệu này. Sonnet chọn `guessedLevel`; **Haiku chọn `outlineLevel` và đẩy
MỌI mục cấp 1 xuống cấp 2** — 6 trong 10 lỗi cấp của nó đúng là ca này.

Đã sửa: không gửi `outlineLvl` thô cho đoạn đã mang style Heading built-in (style đã nói cấp rồi).
Đoạn KHÔNG có style built-in vẫn được gửi — ở đó nó là nguồn duy nhất nói về cấp. 97 trường mâu
thuẫn biến mất, prompt nhẹ 2,3 KB.

Chạy lại Haiku trên metadata đã sửa: **đúng cả 6 mục cấp 1 kia**, đúng cấp 83,6% → 91,4%. Nhưng
recall tụt 100% → 95,1% và F1 tụt 98,4% → 95,9%, **không quy được** cho bản sửa vì agent Haiku
không tất định và chỉ có một lượt mỗi bên. Thước đo đúng cho thay đổi này là Qwen (`top_k=1`,
`temperature=0`, seed cố định — tất định).

#### Đã đo bằng Qwen — và kết quả là ÂM

Tài liệu thật không còn trên máy (`.gitignore` chặn đúng như §7.6 muốn), nên ca lỗi được tái tạo:
lấy `01-style-chuan.docx`, vá `styles.xml` thành `Heading1→outlineLvl=1, Heading2→2, Heading3→3` —
lệch đúng 1 đơn vị như báo cáo thật — giữ nguyên `.key`. Kiểm chứng fixture TRƯỚC khi đo: bản tắt
sửa cho `"outlineLevel":1,"guessedLevel":1`, bản bật sửa thì trường đó biến mất. Qwen Vulkan
`-ngl 20`, `--structural-only`, hai lượt mỗi bên:

| | fixture lệch outlineLvl | bench 8 tài liệu |
|---|---|---|
| **bật** bản sửa (HEAD) | 100% mọi chỉ số | P 97,5% · **7/8** |
| **tắt** bản sửa | 100% mọi chỉ số | **P 100% · 8/8** |

Hai điều, cả hai đều tái lập ở cả hai lượt:

1. **Trên đúng ca nó nhắm tới, bản sửa không đổi một chữ số** — vì theo thứ tự quyền lực §1, đoạn
   mang style Heading built-in thì cấp lấy từ style, phiếu mô hình bị bỏ (log: *"Bỏ qua lượt gán cấp
   toàn cục: cấu trúc đã quyết cấp cho mọi heading"*). Bản sửa lại giấu `outlineLevel` cho **đúng
   nhóm đoạn đó**. Nó giấu một trường mà mô hình đọc sai cũng không ảnh hưởng đến kết quả.
2. **Trên bench nó có hại**: tắt đi thì `07-chen-chi-thi` hết thừa đoạn, bench lên tuyệt đối 8/8.
   Cơ chế hợp lý: heading thật đều mang `outlineLevel`, dòng tiêm chỉ thị thì không — đó là một
   tương phản để loại nó, và bỏ trường đi là san phẳng tương phản. Tức lỗi cuối cùng ở §5 còn một
   nguyên nhân thứ hai, rẻ hơn nhiều so với việc trả lại ngữ cảnh so sánh (144 s → 424 s).

Con số "6/10 lỗi cấp" ghi công cho bản sửa ở trên là đo trên **Haiku đọc dump**, một đường đi
**không tồn tại trong pipeline**: pipeline không để mô hình quyết cấp cho đoạn có style. Đây là lần
thứ hai trong cùng tài liệu này agent-đóng-vai-LLM dẫn tới kết luận sai (lần đầu: §7.2).

Bệnh gốc không nằm ở "gửi `outlineLevel` cho đoạn nào" mà ở chỗ **hai trường nói cùng một chuyện
bằng hai quy ước** (`outlineLevel` 0-based, `guessedLevel` 1-based). Bản vá đầu chỉ *giấu có điều
kiện* một trong hai, nên vẫn còn hai nguồn sự thật — chỉ khác là giờ một nguồn lúc có lúc không.

#### Cách sửa thứ ba: chuẩn hoá thay vì giấu — ĐÃ LÀM

Đoạn có style built-in thì phát ra `outlineLevel` **suy từ `GuessedLevel`** về đúng quy ước 0-based
mà prompt dạy, thay vì chép lại con số trong file. Hết mâu thuẫn mà trường vẫn còn, nên tương phản
"heading thật thì có outlineLevel" không mất.

Để phân biệt bản này với bản "gửi số thô" cần một phép đo mà **phiếu cấp của mô hình thật sự có
trọng lượng** — hai bản hoà nhau ở mọi phép đo khác, vì mặc định cấp do cấu trúc quyết (§1). Cờ
`--model-levels` làm đúng việc đó:

| bản | fixture lệch, cấp do cấu trúc | fixture lệch, `--model-levels` | bench 8 tài liệu |
|---|---|---|---|
| giấu trường (bản vá đầu) | 100% | 100% | P 97,5% · 7/8 |
| gửi số thô | 100% | **71,4%** | P 100% · 8/8 |
| **chuẩn hoá** | 100% | **100%** | **P 100% · 8/8** |

Dòng 71,4% là lần đầu **mối lo gốc của §7.3 được xác nhận bằng mô hình tất định** — trước đó chỉ có
bằng chứng Haiku một lượt. Nó có thật; chỉ là bản vá đầu chọn sai cần gạt. Chuẩn hoá thắng cả hai
trục, và bench lên tuyệt đối **8/8** — lỗi cuối cùng ở §5 biến mất mà không phải trả 144 s → 424 s.

Khoá bằng `ModelMetadataContractTests.OutlineLevel_gui_cho_mo_hinh_luon_khop_quy_uoc_voi_guessedLevel`,
đã kiểm đột biến **cả hai chiều**: đổi thành giấu trường thì đổ, đổi thành số thô cũng đổ. Test cũ
khoá *cách làm* ("không có trường") nên nó chặn luôn cách sửa tốt hơn; test mới khoá *ý định*
("trường gửi đi không được mâu thuẫn với `guessedLevel`").

### 7.4 Lọc chú thích bằng cấu trúc thay danh sách từ khoá

Nhóm dương tính giả lớn nhất là 13 chú thích bảng/hình mang style Heading3. `CaptionRx` đã có nhưng
nằm sau cờ `UseLexicalRules`, mà **giao diện web mặc định tắt cờ đó** — cùng họ với lỗi
`SkipStyledCandidates`: cấu hình đo khác cấu hình chạy.

Luật mới cần ba vế cùng đúng, không dùng một từ tiếng Việt nào: nhãn "TỪ + số NHIỀU PHẦN" (đòi
nhiều phần là chốt chống ăn nhầm "Chương 1.", "Điều 5.", "Phụ lục 1:"), `NumberingId` null (heading
đánh số thật luôn mang numbering của Word; số trong "Bảng 1.2:" là gõ tay — tách sạch 13/13), và có
bảng bắt đầu trong 4 đoạn kế tiếp.

Đo `--no-llm --structural-only`: bench **không đổi một chữ số**; báo cáo thật 111 → 99 ứng viên,
P 55% → 61,6%, F1 70,9% → 75,8%, recall giữ 100%. Bằng đúng mức `CaptionRx` từ vựng đạt được, nhưng
chạy cả ở chế độ giao diện dùng.

`PrecedesTable` phải tính ở một lượt TRƯỚC `Classify`. Đặt trong `PostProcess` — nơi đã có
`PrecedesTableOfContents` — thì cờ luôn false đúng lúc `Classify` cần đọc.

### 7.5 `CharsPerToken` quá BI QUAN ~1,64 lần — CHƯA SỬA

**Mục này đã bị viết sai HAI LẦN trước khi có số đo trực tiếp. Giữ lại cả hai bản sai vì cơ chế
gây sai mới là phần đáng học.**

- Bản 1: *"hằng 1.85 quá lạc quan, khối thật lớn hơn 2,5 lần, LM Studio sẽ tràn cửa sổ ngữ cảnh."*
- Bản 2: *"hai tokenizer lệch nhau ~5 lần; hằng số sai theo hai hướng ngược nhau."*

Cả hai suy tỉ lệ từ SỐ KHỐI của những lượt chạy dùng **view khác nhau**: lượt pipeline thật dựng
view toàn văn (217.733 ký tự) còn công cụ `--dump-chunks` bản đầu chỉ dựng view ứng viên (51.658 ký
tự). So táo với cam rồi quy chênh lệch cho tokenizer.

Số đo trực tiếp, cùng một view, cùng ngân sách 5000 token:

| | Số khối | Ký tự/token |
|---|---|---|
| Tokenizer Qwen2.5-7B | **15** (khớp lượt chạy thật) | **3,029** |
| Tokenizer Llama-3.2-3B | — | **3,206** |
| Ước lượng `CharsPerToken = 1.85` | **26** | 1,85 |

Hai tokenizer **gần như bằng nhau**. Hằng 1.85 **quá bi quan ~1,64 lần** cho cả hai, nên triệu
chứng KHÔNG phải tràn context mà là **chia nhỏ thừa: 26 khối thay vì 15 — khoảng 73% lượt gọi RPC
thừa** cho LM Studio và OpenRouter (hai backend duy nhất chạy bằng ước lượng, vì `countTokens` chỉ
khác null với `LlamaHeaderExtractor`).

Chưa sửa: nâng hằng số lên ~3 làm khối to gần gấp đôi, tức đổi thành phần khối — biến đã đo được là
làm lật câu trả lời cho cả những mục không liên quan (§4). Phải đo riêng. Hướng bền hơn là lấy số
token từ chính backend (LM Studio có endpoint tokenize; OpenRouter trả usage sau mỗi request).

**Bài học, và là loại lỗi lặp lại ba lần trong phiên này**: suy một đại lượng từ một quan sát gián
tiếp rồi phát biểu như tính chất của hệ thống. Lần một sai dấu, lần hai sai độ lớn, lần ba mới đúng
— và chỉ đúng khi đo trực tiếp trên đúng cùng một đầu vào. Khi một con số quan trọng, hãy dựng công
cụ đo nó, đừng suy từ triệu chứng.

### 7.6 Rò rỉ nội dung tài liệu qua file PHÁI SINH

`.gitignore` chặn `data/*.docx` là chưa đủ. Trong phiên này nội dung tài liệu thật rò ra qua ba
đường khác: file đáp án `.key` do agent gán nhãn ghi vào `data/`, `data/verified-corrections.jsonl`
(correction memory lưu nguyên văn tên đề mục), và hai file test chép thẳng tên đề mục thật.

Đã mở rộng `.gitignore` (`data/*.key`, `*.jsonl`, `*.review.json`) và thay nội dung test bằng văn
bản trung tính — test khoá HÌNH DẠNG (dãy La Mã → số, đậm, hoa), không khoá chữ nghĩa.
`verified-corrections.jsonl` đã nằm trên `main` từ trước, không gỡ được bằng git.

**Nguyên tắc rút ra**: mọi thứ phái sinh từ tài liệu — đáp án, correction memory, test fixture,
dump — đều mang nội dung tài liệu. Danh sách chặn phải theo *nguồn gốc dữ liệu*, không theo đuôi file.

## 8. Phần cứng

### 8.1 Máy đo — RTX 3060 12 GB (06/08/2026 – 07/08/2026)

> **HẾT HIỆU LỰC từ 07/08/2026.** Máy đã quay lại **WX 5100 4 GB** (§8.2): `Win32_VideoController`
> báo `Radeon (TM) Pro WX 5100`, và số đo §10 khớp hồ sơ §8.2 chứ không khớp bảng dưới. Đừng đọc
> "máy đo hiện tại" ở tiêu đề cũ — hãy kiểm GPU trước mỗi lượt đo, vì tốc độ lệch ~22× và **số lớp
> offload là một trục tái lập ngang hàng với sampler** (§3.7).

| | |
|---|---|
| GPU | NVIDIA GeForce RTX 3060 **12 GB** (10,6 GB trống), driver 591.86, compute 8.6 |
| CPU / RAM | Intel i5-14400F, 16 luồng / 32 GB |
| Backend dùng được | **Vulkan** — CUDA không kích hoạt được, xem bên dưới |

Tiền đề của §8.2 đã hết hiệu lực: Qwen2.5-7B Q4_K_M **nằm gọn trong VRAM**. Trọng số 4,36 GiB, KV
cache f16 **56 KiB/token** (28 lớp × 4 KV head × 128 dim × 2 × 2 byte) nên ctx 8192 chỉ tốn thêm
0,44 GiB; cộng compute buffer ≈ **5,3 GiB**. Còn dư ~5 GB — ctx 16384 hoặc mức lượng tử cao hơn đều
nằm trong tầm.

Bộ bench 8 tài liệu tổng hợp, `--structural-only`, sinh vào thư mục sạch bằng `dhx bench <thư-mục>`:

| Cấu hình | Thời gian cả bộ | P | R | F1 | Đúng cấp | Tuyệt đối |
|---|--:|--:|--:|--:|--:|--:|
| WX 5100, `-ngl 20` (§8.2) | 1350 s | 97,5% | 100% | 98,7% | 100% | 7/8 |
| RTX 3060 Vulkan, `-ngl 20` | **62 s** | 97,5% | 100% | 98,7% | **100%** | **7/8** |
| RTX 3060 Vulkan, `-ngl 99` | **27 s** | 97,5% | 100% | 98,7% | 97,4% | 6/8 |

`-ngl 20` tái lập nguyên vẹn bảng §2 kể cả chi tiết lỗi — vẫn chỉ `07-chen-chi-thi` thừa 1 đoạn —
và nhanh hơn **21,8×**. Dòng `-ngl 99` lệch không phải vì hồi quy: đó là cấu hình đo khác, lý do ở
trục offload §3.7.

Ba dòng trên đo TRƯỚC bản chuẩn hoá `outlineLevel` của §7.3; sau bản đó, cùng cấu hình
`Vulkan -ngl 20` cho **P/R/F1 100% · 8/8**. Giữ nguyên bảng vì mục này so **phần cứng**, và so phần
cứng thì phải giữ nguyên phần mềm.

**CUDA không kích hoạt được, và nó thất bại trong im lặng.** `-p:UseCuda=true` publish ra
`ggml-cuda.dll` 502 MB, nhưng DLL đó cần `cudart64_12.dll` + `cublas64_12.dll` mà máy không có vì
chưa cài CUDA Toolkit (`CUDA_PATH` rỗng; `nvcuda.dll` của driver là API *driver*, không thay được API
*runtime*). Đã thử và **thất bại**: tải redist NVIDIA (411 MB) rồi đặt ba DLL vào
`runtimes/win-x64/native/cuda12/`, đặt cạnh `dhx.exe`, và set cả `CUDA_PATH` lẫn `PATH` — vẫn
42,4 s/khối, tức vẫn CPU. Vulkan ngược lại chạy được ngay không cài thêm gì, vì driver NVIDIA đã kèm
`vulkan-1.dll`: **2,7 s/khối so với 43,3 s/khối của CPU, nhanh 15,7×**.

Kèm theo là một lỗi cùng họ với bẫy §4.4 *"log nói dối"*: CLI vẫn in `"Mô hình sẵn sàng … GPU 99
lớp"` **ngay cả khi backend đã âm thầm rơi về CPU** — câu log chỉ phản chiếu cờ người dùng gõ, không
phản ánh thư viện native thật sự nạp được. Thứ lộ ra sự thật là **thời gian mỗi khối**, không phải
log. Tin log thì sẽ ghi một lượt đo CPU vào bảng như là số GPU.

### 8.2 Máy đã sinh ra mọi số ở §2 — WX 5100 4 GB

Máy đo: Radeon Pro WX 5100 **4 GB**, runtime Vulkan. Qwen2.5-7B Q4_K_M nặng 4,36 GiB nên **không
nằm vừa VRAM** — chạy chủ yếu bằng CPU, ~45 token/s prefill. Đo được: `--gpu max` và `--gpu 0.4` ở
các mức context khác nhau đều cùng một dải tốc độ. Mọi tối ưu phần mềm chỉ giảm **số lượng** và
**kích thước** câu hỏi.

### 8.3 LM Studio

Hai lưu ý khi nạp model trong LM Studio:

- Đặt TTL rỗng (`lms load … ` không kèm `--ttl`). Model nạp kiểu JIT có TTL 1 giờ và bị đá ra giữa
  chừng — đó là nguyên nhân các lỗi `terminated` khi chạy dài.
- `--parallel 1`. llama.cpp **chia** context cho từng slot, nên `--parallel 4 -c 16384` chỉ còn 4096
  token/slot, nhỏ hơn chính khối 5000 token pipeline gửi. Cache prefix cũng theo slot nên chạy song
  song còn làm mất phần đã tiết kiệm được; đo được độ trễ mỗi request phình 354 s → 569 s.
  `LMSTUDIO_PARALLEL` mặc định 1 vì lý do đó.

## 9. Vòng lặp trên MỘT khoá luận thật — F1 67,1% → 95,1%

Mục này ghi lại một phiên khác hẳn §7: thay vì đo rồi kết luận "bench không dự báo được tài liệu
thật", lần này lấy đúng một tài liệu thật làm đích và sửa cho tới khi đạt. Điều đáng giữ lại không
nằm ở con số mà ở **cách chọn luật**: bảy luật mới, không luật nào chứa một từ tiếng Việt nào.

### 9.1 Thước đo dựng trước, không dựng sau

Khoá luận 1498 đoạn, đặt trong `bench/holdout/` (đã bị `.gitignore` chặn). Đáp án do **hai agent
độc lập** gán nhãn, mỗi bên đọc toàn văn 1232 đoạn không rỗng và không được nhìn bản của bên kia:

| | |
|---|---|
| Opus | 131 mục (tự đánh dấu 15 mục không chắc) |
| Sonnet | 147 mục (26 mục không chắc) |
| Trùng chọn đoạn | 130 — Jaccard 87,8% |
| **Trùng cả CẤP trên phần chung** | **130/130 = 100%** |
| Tập chắc chắn dùng làm `.key` | **110 mục** |

Trùng cấp tuyệt đối giữa hai mô hình khác nhau, đọc độc lập, là bằng chứng cây này có thật chứ
không phải ý kiến của một mô hình — lặp lại đúng điều §7.2 đã thấy.

**Quy tắc chấm cố định từ đầu, trước khi biết kết quả**: 13 mục mà hai người gán nhãn bất đồng hoặc
tự đánh dấu ngờ được **loại khỏi phép chấm**, không tính đúng cũng không tính sai. Ép chúng vào một
phía nào cũng là tự chấm điểm cho mình. Con số thô chưa loại trừ: F1 89,8%.

Thêm một agent Haiku làm người gán nhãn thứ ba và **loại bỏ**: nó trả đúng 68 mục, tự mô tả là lấy
từ `style=Heading*`, khai 0 mục không chắc — nhưng đối chiếu ra 7 mục nó gán không hề có style và 7
mục có style thì nó bỏ. Vừa không làm việc được giao, vừa mô tả sai việc mình đã làm, dù prompt đã
có hẳn một câu bắt nó đếm lại file trước khi báo cáo. Đây là lần thứ hai (sau §7.2) agent yếu dẫn
tới kết luận sai nếu tin lời khai của nó.

### 9.2 Đặc điểm quyết định hướng đi

| | |
|---|---|
| Đoạn mang `style=Heading*` | 68 |
| Trong đó là heading thật | **68/68 — không sai một mục** |
| Heading thật KHÔNG có style | **63** |

Tin style Word trên tài liệu này là **precision 100%, recall 52%**. Toàn bộ phần khó nằm ở 63 mục
không style — và nó giải thích vì sao hai bản sửa hiệu quả nhất đều là "đừng để mô hình xoá bằng
chứng cấu trúc" chứ không phải "dạy mô hình giỏi hơn".

### 9.3 Bảy luật, đo từng luật một

Mỗi vòng đổi ĐÚNG một biến, giữ nếu F1 tăng và bench 8 tài liệu không hồi quy, hoàn nguyên nếu giảm.

| Vòng | Luật | P | R | F1 | Ứng viên |
|---|---|--:|--:|--:|--:|
| — | mốc đầu | 59,4% | 77,1% | 67,1% | 295 |
| 1 | mục lục gõ tay (§9.4) | 66,9% | 74,0% | 70,3% | 274 |
| 2 | lời bác lượt phân loại không được xoá style (§9.5) | 68,6% | 80,2% | 73,9% | 274 |
| 3 | khối trang bìa | 73,4% | 80,2% | 76,6% | 256 |
| 4 | nhấn mạnh thân bài (đậm+nghiêng, không cấu trúc) | 77,0% | 79,4% | 78,2% | 220 |
| 5 | thân bài không đậm, không cấu trúc | 90,2% | 84,7% | 87,4% | 138 |
| 6 | đậm + numbering không được xoá | 90,6% | **96,4%** | 93,4% | 138 |
| 7 | dãy ứng viên không tự mở ra văn xuôi | **93,8%** | **96,4%** | **95,1%** | 138 |

*(vòng 1–5 đo trên đáp án Opus để so được liên tiếp; vòng 6–7 trên đáp án đồng thuận đã loại mục
tranh cãi — hai thang khác nhau nên chỉ so trong cùng nhóm)*

Bench 8 tài liệu giữ **8/8 ở từng vòng**, test xanh ở từng vòng.

### 9.4 Mục lục gõ tay — hàng phòng thủ cũ phụ thuộc hoàn toàn vào neo `_Toc`

`IsTableOfContentsEntry` chỉ nhận dòng mục lục qua style TOC1..TOC9 hoặc neo `_Toc`/`_heading`, và
comment tại chỗ khẳng định hai thứ đó "chính xác hơn nhiều so với đoán theo số trang cuối dòng".
Đúng — nhưng tài liệu gõ tay hoặc chuyển từ PDF thì **không có cả hai**, và khi đó không còn gì chặn.

Đo bằng cách lấy `04-bia-muc-luc-chu-thich` và **chỉ gỡ ba neo `_Toc`**, giữ nguyên mọi thứ khác:

| | Ứng viên | Thừa | P | R | F1 |
|---|--:|--:|--:|--:|--:|
| có neo | 7 | 3 | 100% | 100% | 100% |
| **gỡ neo** | 10 | 6 | **66,7%** | **50%** | **57,1%** |

Mất neo không chỉ thêm rác: **recall tụt một nửa**. Mô hình không phân biệt được bản sao với bản
gốc nên loại nhầm chính heading thật.

Luật thay thế nhận theo DÃY, ba vế: kết thúc bằng số trang và còn phần tên mục; ≥3 dòng LIỀN NHAU
cùng dạng; số trang không giảm — nhưng **cắt dãy tại mỗi chỗ tụt** thay vì loại cả cụm. Vế cuối do
chính tài liệu thật dạy: mục lục của nó có 21 dòng liên tiếp với dãy `5,6,6,7,1,16,16,37…` vì phần
đầu đánh số trang riêng rồi phần thân quay về 1. Bản đầu đòi cả dãy không giảm nên **loại sạch cả
21 dòng — luật chạy mà không bắt được gì**, và nếu chỉ nhìn bench thì không bao giờ phát hiện.

### 9.5 Cùng một nguyên tắc §1, hai điểm gọi bỏ sót

§3.1 đã chốt "mô hình được hạ độ tin cậy, không được xoá bằng chứng cấu trúc" cho nhánh critic.
Nhưng lưới an toàn `TrustStyles` vẫn từ chối cứu đoạn bị **lượt phân loại** bác tường minh — cùng
bằng chứng, khác điểm gọi, khác số phận. Đo được: 9 đoạn mang style Heading biến mất hẳn; giữ chúng
ở trạng thái *cần duyệt* cho recall 74,0% → 80,2% **và precision cũng tăng** 66,9% → 68,6%, tức cả
9 mục bị xoá đều đúng.

Vòng 6 mở thêm một tầng YẾU HƠN: đoạn **đậm + có numbering của Word**. Trong 50 ứng viên mang cả
hai dấu hiệu, cả 50 đều là đề mục theo HỢP của hai đáp án. Nhóm này được cứu khỏi bị xoá nhưng
**luôn ở trạng thái cần duyệt, không bao giờ tự nhận** — bằng chứng yếu hơn thì quyền cũng nhỏ hơn.

### 9.6 Bốn ý tưởng bị chính số liệu bác

Ghi lại vì cả bốn đều nghe rất hợp lý, và ba trong số đó chỉ lộ ra sau khi đo:

1. **Italic làm dấu hiệu loại.** Nhóm thừa lớn nhất đều `bold+italic` nên trông rất gọn. Đo: tài
   liệu có 113 đoạn nghiêng mà **13 trong số đó là đề mục thật** → luật một vế đổi 26 mục thừa lấy
   13 mục thiếu. Phải thêm hai vế "không numbering, không style" mới còn 3 mục thật dính vào.
2. **Luật nhấn mạnh không có chốt mức tài liệu.** Bản đầu cắt đúng 21 mục thừa trên tài liệu thật
   nhưng làm `02-dinh-dang-thu-cong` mất 2 heading (recall 100% → 94,9%): tài liệu đó KHÔNG dùng
   style hay numbering ở đâu cả, nên đậm/nghiêng là cách duy nhất tác giả đánh dấu đề mục. Nguyên
   tắc rút ra: **việc THIẾU dấu hiệu cấu trúc chỉ mang thông tin khi tài liệu có dùng dấu hiệu đó ở
   chỗ khác.** Đây là lần bench làm đúng việc nó sinh ra để làm — chặn một luật
   đúng-với-một-tài-liệu bị nâng thành luật chung.
3. **Chốt "cả dãy số trang không được giảm"** ở §9.4 — quá chặt, xem trên.
4. **Một test viết cho bản sửa §9.5 hoá ra không phân biệt được gì**: nó đỏ ở cả ba trạng thái
   (nguyên bản / đột biến / khôi phục). Nguyên nhân nằm trong harness — `ScriptedClassifier` được
   ghi chú là *"lượt một nhận mọi ứng viên là heading"*, nên lượt phân loại trong test không bao giờ
   sinh ra lời bác, đúng thứ cần dựng. Theo tiền lệ §6.5, gỡ hẳn test thay vì để một test xanh giả.
   **Bản sửa đó hiện chỉ được bảo chứng bằng phép đo trên tài liệu thật, không có unit test** — muốn
   có thì phải mở rộng harness cho lượt phân loại nhận kịch bản.

### 9.7 Còn lại

7 mục sai và 4 mục thiếu trên 110. Đáng chú ý là **không nhóm nào còn đủ lớn để đáng một luật**:

- 3 tiêu đề trong phụ lục mà chính hai người gán nhãn cũng bất đồng về ranh giới tiêu đề nhiều dòng
- 2 dòng ghi nguồn **in đậm** căn giữa — lọt lưới vì luật vòng 5 đòi "không đậm"
- 1 dòng liệt kê chương trong phần "bố cục", 1 ô bảng
- 4 mục thiếu: 2 dòng "tiểu kết" in đậm căn giữa không style, 2 nhãn phân nhóm trong danh mục tham khảo

Ba giới hạn cần biết trước khi tin con số 95,1%:

1. **Một tài liệu.** Bảy luật đều đo trên đúng một khoá luận. Chúng qua được bench nên không hồi
   quy, nhưng bench chỉ có 8 tài liệu tổng hợp — §7.1 đã chỉ ra hai loại tài liệu hỏng theo hai
   chiều ngược nhau, và ở đây mới chữa được một chiều.
2. **Đáp án do agent gán, chưa có người xác nhận** — vẫn đúng như §5 đã ghi, dù nay là đồng thuận
   hai bên độc lập thay vì một bên.
3. **Đúng cấp vẫn chỉ ~28%.** Tài liệu dùng `Heading1 → Heading3 → Heading4`, bỏ qua Heading2, nên
   pipeline trả 1,3,4 (trung thành với file) còn đáp án ghi 1,2,3 (lồng nhau thật). Cây cùng hình
   dạng, khác gốc đánh số; cờ `--normalize-levels` sửa được phần lớn nhưng chưa khớp hẳn.

   **Bản đầu của mục này xếp đó là "lựa chọn quy ước, không phải bug chờ sửa" — kết luận vội.** Lượt
   đo trên tài liệu thật thứ hai (§7, commit *"Số end-to-end cho tài liệu thật thứ hai"*) chỉ vào
   cùng một cơ chế từ hướng khác: ở đó tác giả gán `Heading2` cho gần như mọi thứ, pipeline CẤM mô
   hình ghi đè tuyên bố style nên lượt hierarchy chỉ được hỏi 9/71 heading, và đúng cấp dừng ở
   40,7%. Hai tài liệu khác nhau, hai kiểu lạm dụng style khác nhau, cùng một hậu quả — tức đây là
   **vấn đề thật**: nguyên tắc "cấu trúc quyết định cấp" đưa bench từ 54,2% lên 100% nhưng không có
   tín hiệu nào đo được rằng style của MỘT tài liệu cụ thể có đáng tin hay không.

Một đính chính về thuật ngữ, do lượt đo tài liệu thật thứ hai phát hiện: con số *"tiêu đề lọt vào
tập ứng viên"* từng được nhãn trong bộ eval gọi là **"trần trên của recall"**, và mục này lúc viết
cũng dùng theo. Sai: công văn có tỉ lệ đó 66,7% mà recall cuối vẫn đạt 88,9%, vì lượt cứu theo cấu
trúc vớt lại được đoạn chưa từng là ứng viên. Nhãn đã sửa trong code; đọc các con số 93,9% / 98,2%
ở phiên này là *tỉ lệ lọt vào tập ứng viên*, không phải trần.

## 10. Đo luật R1 "auto_assign theo style OOXML" — F1 tăng, nhưng vì lý do khác

Một spec đề xuất thay tầng lọc hiện tại bằng router hai nhánh `auto_assign | route_pass1`: đoạn mang
style Heading built-in, ngoài bảng/textbox, ngắn, không kết thúc bằng dấu chấm câu thì **gán thẳng
heading + cấp với confidence 1.0 và rút hẳn khỏi luồng LLM**.

Bằng chứng chống lại nó trước lượt này chỉ **gián tiếp**: `SkipStyledCandidates` (§6.3) bỏ HỎI nhưng
vẫn giữ đoạn trong khối làm ngữ cảnh, và riêng thế đã đủ làm precision tụt 100% → 94,1%. R1 mạnh hơn
hẳn nên **phải có số của chính nó**. Cài sau cờ `--style-auto-assign`, mặc định tắt
(`OoxmlStyleAutoAssign`).

**Cấu hình đo — đọc trước khi so với bất kỳ bảng nào ở trên**: máy là **WX 5100 4 GB** (hồ sơ §8.2),
KHÔNG phải RTX 3060 của §8.1; Qwen2.5-7B Q4_K_M, `-ngl 20`, `--structural-only`, sampler tất định.
Và `bench/` trên máy này **không phải bộ 8 tài liệu tổng hợp mà mọi bảng ở trên nói tới** — khác cả
về thành phần lẫn về tên:

| | `BenchDocumentFactory` sinh ra | git theo dõi `.key` | thực có trên máy |
|---|---|---|---|
| 01–06 | tổng hợp | ✔ | ✔ |
| 07 | `07-chen-chi-thi` | `07-mau-that` | `07-mau-that` (tài liệu THẬT) |
| 08 | `08-danh-sach-da-cap` | `08-plph2` | chỉ còn `.key`, không có `.docx` |
| 09 | — | — | `09-dien-mat-di` (tài liệu THẬT, `.gitignore` chặn) |

Tức **`07` là hai tài liệu khác nhau tuỳ bạn chạy `dhx bench` hay checkout repo**, và
`07-chen-chi-thi` — tài liệu mà §5 và §7.3 bàn suốt, lỗi cuối cùng của bench, thứ làm nên 8/8 —
KHÔNG có mặt. Bộ thực chấm là 01–06 + `07-mau-that` + `09-dien-mat-di`, trong đó **2 tài liệu thật**.

Vì vậy baseline ở đây là F1 90,9% · 6/8 chứ không phải 100% · 8/8 của §7.3, và toàn bộ dương tính
giả nằm ở `09-dien-mat-di`. Khác bộ đo, không phải hồi quy — nhưng phải mất một lượt truy nguyên mới
biết, nên xem §10.4 để biết hai hàng rào đã dựng.

Cài ở dạng **mạnh nhất có thể** để R1 không thua vì lý do phụ: R4 chạy trước R1; heading auto-assign
nhập vào TRƯỚC hậu kiểm nên vẫn làm anh em cho mục khác; cổng precision có ngoại lệ giữ chúng ở
trạng thái tự nhận, đúng như spec đòi. Ngưỡng "dấu chấm câu" lấy nghĩa hẹp (`.!?`) để phủ nhiều nhất.

### 10.1 Trên bench, R1 thắng

| | P | R | F1 | Đúng cấp | Tuyệt đối |
|---|--:|--:|--:|--:|--:|
| R1 tắt | 84,9% | 97,8% | 90,9% | 100% | 6/8 |
| **R1 bật** | 85,2% | **100%** | **92,0%** | 100% | **7/8** |

### 10.2 …nhưng phần việc riêng của nó đóng góp BẰNG 0

Toàn bộ chênh lệch là đúng một heading ở `07-mau-that`, tái lập 2/2 mỗi nhánh khi cô lập tài liệu đó.
Bốn đoạn R1 gán thẳng (i=0, 2, 5, 17 — Heading1/Heading2) thì nhánh tắt **cũng đã trả đúng cả bốn**.

Đoạn được cứu là **i=7 `PHỤ LỤC A – BẢNG ĐỐI CHIẾU`, `styleId: Normal`** — R1 không hề chạm tới. Nó
lật vì R1 rút 4 ứng viên ra khỏi khối, nên mô hình xét 2 ứng viên còn lại trong một thành phần khối
khác. Đó **đúng là cơ chế §4.1**, và cũng đúng là cơ chế đã lấy đi 6 điểm precision của
`SkipStyledCandidates`. Cùng một cần gạt, lần này ngửa thay vì sấp.

### 10.3 Phép đo quyết định: fixture style bị áp sai

Bench không có tài liệu nào style bị áp bừa, tức nó **không thể** bác mà cũng không thể xác nhận mối
lo gốc (§7.1: 13 chú thích bảng mang Heading3, precision tầng OpenXML 55%). Tái tạo bằng cách thêm
vào `07-mau-that` ba đoạn KHÔNG phải tiêu đề mang `Heading3` — dòng bìa, nhãn chú thích, nhãn khối
chữ ký — đặt ở CUỐI nên không chỉ số nào xê dịch và `.key` giữ nguyên. Kiểm chứng fixture trước khi
đo: cả ba thành ứng viên ở đúng cấu hình đo, và R1 gán thẳng 7 (4 thật + 3 rác).

Hai lượt mỗi nhánh, tái lập 2/2:

| | P | R | F1 | Đúng cấp | **Tự nhận / cần duyệt** |
|---|--:|--:|--:|--:|--:|
| R1 tắt | 66,7% | 100% | 80% | 66,7% | **0 / 9** |
| R1 bật | 66,7% | 100% | 80% | 100% | **7 / 2** |

**P/R/F1 GIỐNG HỆT NHAU, và cả hai nhánh đều thừa đúng 19, 20, 21** — mô hình không cắt được đoạn
nào kể cả khi còn được hỏi. Khác biệt nằm ở chỗ **không chỉ số nào trong bảng F1 nhìn thấy**:

- nhánh tắt đẩy cả 9 mục sang *cần duyệt*, nên ba mục rác đến tay người;
- nhánh bật **tự nhận 7 mục với confidence 1.0, trong đó 3 mục sai** — độ chính xác auto_assign
  **4/7 = 57,1%**, đối lại mục tiêu "gần 100%" mà chính §7 của spec đặt ra, và không có gì phía sau
  bắt lại.

### 10.3b Fixture đã vào bench, và nó cắn ngay ở `--no-llm`

Fixture nay là `09-style-ap-sai` trong `BenchDocumentFactory` — đáp án sinh từ định nghĩa đoạn nên
không có khâu gán nhãn tay để mà sai, và `dhx bench` dựng lại được. Chấm nó bằng
`--no-llm --structural-only`, tức KHÔNG tốn một giây suy luận nào:

| | P | R | F1 | Thừa |
|---|--:|--:|--:|---|
| R1 tắt | 57,1% | 100% | 72,7% | 4, 12, 13 |
| R1 bật | **50%** | 100% | **66,7%** | 4, **5**, 12, 13 |

R1 thêm dương tính giả **i=5 `Bảng 1.2 Đối chiếu thuật ngữ và viết tắt`**. Lý do là một lỗ thật
trong bảng luật của spec: `HeadingHeuristics.Classify` đã hạ đoạn đó xuống `Normal` bằng luật chú
thích cấu trúc của §7.4 (nhãn "từ + số nhiều phần" + `NumberingId` null + có bảng bắt đầu ngay sau
— luật cắt sạch 13/13 chú thích trên báo cáo thật), nhưng **R1 đọc thẳng `style_raw`, không đọc
`Role`**, nên nó đi vòng qua đúng hàng phòng thủ đó. Spec chỉ có R4 cho bảng/textbox và không có vế
nào cho chú thích.

Đáng chú ý về phương pháp: kết luận này **rẻ hơn hẳn** ba lượt đo bằng LLM ở trên và mạnh hơn — nó
tất định tuyệt đối. Khi một luật là deterministic thì hãy chấm nó ở chế độ deterministic trước;
lượt LLM chỉ cần cho phần mà LLM thật sự tham gia.

### 10.4 Kết luận và hai thứ nhặt được dọc đường

Giữ cờ, **mặc định tắt**. Con số đầu bảng nói R1 tốt hơn; truy nguyên thì lợi ích không đến từ nó,
còn thiệt hại thì đến từ đúng cơ chế nó tuyên bố là ưu điểm. Bật mặc định là mua một vé số vừa trúng
trên bộ đo mà R1 thậm chí không kích hoạt ở tài liệu thật duy nhất trong đó.

Bài học phương pháp: **F1 không phải thước đo đủ cho một thay đổi về QUYỀN.** R1 và bản gốc cho cùng
P/R/F1 trên fixture; thứ phân biệt chúng là ai được tự nhận. Nếu chỉ nhìn bảng F1 thì đây là một
thay đổi trung tính — trong khi nó biến ba dương tính giả từ "có người duyệt" thành "đã chốt".

Hai thứ nhặt được:

1. **`Model` của một lượt chạy từng nói dối.** Tài liệu không còn ứng viên nào để hỏi thì `_model`
   vẫn null nên `outline.Model` null, và `PrecisionCalibrationBuilder` thấy hai tài liệu trong CÙNG
   một lượt eval khai hai model khác nhau rồi ném *"Không được trộn nhiều model"*. Lỗi có sẵn, R1
   chỉ làm nó lộ ra. Đã sửa: `Model` báo theo CẤU HÌNH; lượt hỏi thực sự chạy đã có
   `OutlineRunProvenance` ghi riêng. Cùng họ với bẫy §4.4.
2. **`§8.1 đã hết hiệu lực`** — xem ghi chú ở đầu mục đó.

Ba hàng rào đã dựng, tất cả là **cơ chế** chứ không phải quy ước viết trong tài liệu:

- Báo cáo eval in **chữ ký cấu hình** đầy đủ. `ConfigurationFor` nay gồm cả `gpuLayers` và `seed` —
  trước đó nó có 21 trường mà thiếu đúng trục §3.7 đo được là lệch 14 điểm, nên profile dựng ở
  `-ngl 99` được coi là còn hiệu lực ở `-ngl 20`. `MeasurementConfigSignatureTests` khoá lại, đã
  kiểm đột biến.
- `dhx eval` **cảnh báo đáp án mồ côi** (có `.key`, không có tài liệu). Phép duyệt đi từ tài liệu
  nên nhóm này trước đây hoàn toàn vô hình — đúng cách `08` biến mất mà không ai hay.
- `bench/README.md` nói thẳng rằng bộ đo trên mỗi máy có thể khác nhau, kèm bảng tên thật.

## 11. StyleTrust — tín hiệu §7.1/§9.7 đòi, và vì sao nó chưa đủ

§7.1 và §9.7 cùng dừng ở một câu: *"cần một tín hiệu đo được rằng style của tài liệu NÀY có đáng tin
không — chưa có"*. Mục này dựng tín hiệu đó (`StyleTrustAudit`, cờ `--style-trust`, mặc định tắt),
và ghi lại kết quả **một phần âm**.

### 11.1 Hai quyền, đo tách nhau

Style Word mang hai quyền và hai tài liệu thật hỏng ở hai quyền khác nhau — khoá luận tin được
quyền CHỌN (68/68) nhưng không tin được quyền GÁN CẤP (H1→H3→H4, đúng cấp ~28%); báo cáo thì hỏng
cả hai (precision tầng OpenXML 55%, đúng cấp 40,7%). `StyleTrust` chấm riêng từng quyền:

- **SelectionTrusted** — tỉ lệ đoạn mang style mà lại mang hình dạng của thứ không phải đề mục
  (dùng LẠI đúng các luật đã có: chú thích đối tượng, dòng mục lục, gạch đầu dòng, dấu câu cuối,
  ô bảng), cộng mật độ style trên toàn tài liệu.
- **LevelTrusted** — số cấp riêng biệt, và có bỏ cấp giữa chừng không.

Dưới `MinimumStyledSample = 8` đoạn thì không kết luận gì. Nguyên tắc xuyên suốt: **chỉ hạ quyền,
không bao giờ xoá đoạn** — cấp vẫn giữ qua `prefixLevel`, thứ mất đi là quyền phủ quyết.

### 11.2 Kết quả: bộ dò đúng, nhưng CHƯA CÓ bộ chấp hành

Trên `09-style-ap-sai`, tín hiệu bắn đúng — *"style built-in 8 đoạn (50% tài liệu), 1 đoạn trông
không phải đề mục (12%), 3 cấp riêng biệt ⇒ quyền chọn HẠ, quyền gán cấp GIỮ"*. Nhưng kết quả
**không đổi một chữ số**: vẫn P 57,1%, vẫn thừa đúng 4, 12, 13.

Truy nguyên: ba dương tính giả là `Hà Nội, tháng 8 năm 2026`, `Người lập biểu`, `Nguyễn Văn A` —
dòng bìa và nhãn khối chữ ký. Hạ quyền style nghĩa là "để các luật hình dạng quyết", nhưng **không
luật hình dạng nào hiện có nói được gì về chúng**: không trong bảng, không gạch đầu dòng, không
kết thúc bằng dấu câu, không phải chú thích. Chuyển quyền cho một chỗ trống.

Và §10.3 đã đo rằng mô hình cũng không cắt được chúng. Tức với chế độ hỏng "dòng bìa / khối chữ ký
mang style Heading", hiện **không có tầng nào trong pipeline có khả năng bác** — không phải chuyện
trao quyền cho ai.

### 11.3 Trạng thái và bước tiếp

- `SelectionTrusted`: bộ dò xong, **hiệu lực bằng 0** cho tới khi có luật hình dạng nhận được dòng
  bìa/khối chữ ký. Đó mới là việc tiếp theo, không phải tinh chỉnh ngưỡng.
- `LevelTrusted`: đã cài ở cả `ResolveLevel` lẫn khâu chọn đoạn để hỏi cấp, nhưng **CHƯA ĐO** —
  `--no-llm` không đi qua `ResolveLevel` nên chỉ đo được bằng một lượt LLM, và cần thêm một fixture
  có cấp style thoái hoá (mọi mục cùng một cấp). Đây là nhánh nhắm thẳng vào 40,7% và ~28%.
- Bench giữ nguyên P 86,8% · R 100% · F1 92,9% · đúng cấp 76,1% · 5/8 ở cả hai trạng thái cờ — luật
  không kích hoạt vì mọi tài liệu bench đều dưới ngưỡng mẫu.

Một lỗi đáng ghi vì nó suýt lọt: test cho nhánh "bỏ cấp giữa chừng" đỏ, và nguyên nhân nằm trong
CHÍNH TEST — C# parse `i % 3 switch { … }` thành `i % (3 switch { … })`, nên cấp sinh ra là 0,1,2,3
chứ không phải 1,3,4. Nếu test đó xanh giả thì `SkipsLevels` sẽ không có gì bảo chứng.

## 12. Dòng bìa / khối chữ ký — StyleTrust có bộ chấp hành

TODO mục 1. §11.2 kết luận *"bộ dò đúng, nhưng CHƯA CÓ bộ chấp hành"*: `StyleTrust` nhận đúng rằng
style của `09-style-ap-sai` không đáng tin, mà kết quả không đổi một chữ số. Mục này tìm ra vì sao —
và hoá ra có **hai** khiếm khuyết chồng lên nhau, cái thứ hai mới là nút thắt.

### 12.1 Khiếm khuyết thứ nhất: hạ quyền style tự tắt luật thay thế

Các luật hình dạng (`DemoteInlineEmphasis`, `DemoteRunsWithoutOwnProse`) chỉ chạy trên tài liệu "có
đánh dấu cấu trúc bài bản", và chúng đo điều đó bằng `HasBuiltInHeadingStyle`. Nhưng nhánh hạ quyền
của StyleTrust **xoá sạch chính cờ đó** trước khi các luật chạy. Số đếm về 0, chốt không đạt, luật
trả về ngay.

Tức hạ quyền style không chỉ *"chuyển quyền cho một chỗ trống"* như §11.2 mô tả — nó **tắt luôn**
luật lẽ ra tiếp quản. Đã sửa: đếm trước khi hạ (`CountStructuralMarkers`).

**Sửa xong, kết quả không đổi.** Ghi lại vì đó là thông tin: nó loại một giả thuyết và ép phải tìm
tiếp, thay vì tưởng đã xong.

### 12.2 Khiếm khuyết thứ hai: "mở ra văn xuôi" là quan hệ BẮC CẦU

`DemoteRunsWithoutOwnProse` gặp văn xuôi thì `run.Clear()` — **tha cả dãy**. Nhưng một đề mục phải
mở ra văn xuôi *của chính nó*, tức văn xuôi xuất hiện trước ứng viên kế tiếp. Với `run.Clear()`, một
nhãn khối chữ ký đứng ngay trước đề mục của phần sau cũng được tính là đã mở ra văn xuôi của phần ấy.

Đổi một chữ thành `Flush()` — chỉ ứng viên CUỐI dãy được tha:

| `--no-llm --structural-only --style-trust` | P | R | F1 | Thừa |
|---|--:|--:|--:|---|
| trước | 57,1% | 100% | 72,7% | 4, 12, 13 |
| **sau** | **100%** | **100%** | **100%** | **—** |

Bench 9 tài liệu: F1 92,5% → **95,6%**, tuyệt đối 5/9 → **6/9**. Đường mặc định (không
`--style-trust`) **không đổi một chữ số** — ba dương tính giả ở đó vẫn còn, vì chúng mang style
Heading và luật miễn trừ đoạn có tuyên bố cấu trúc. Đó là chốt bắt buộc: bỏ nó thì chuỗi
`Chương 1 → 1.1 → 1.1.1` bị chính luật này giết (đã kiểm đột biến).

### 12.3 Trên tài liệu thật: HOÀ, và vì sao hai thước nói khác nhau

| Khoá luận 1498 đoạn | P | R | F1 |
|---|--:|--:|--:|
| đáp án Opus, trước | 92,9% | 89,3% | **91,1%** |
| đáp án Opus, sau | 94,2% | 86,3% | **90,0%** |
| đồng thuận (loại mục tranh cãi), trước | 93,8% | 96,4% | **95,1%** |
| đồng thuận, sau | 95,4% | 94,5% | **95,0%** |

Đáp án Opus đơn lẻ nói **giảm 1,1 điểm**; đáp án đồng thuận nói **hoà** (0,1 điểm, trong khi một mục
đã đáng 0,9 điểm trên 110 mục). Truy nguyên bốn mục recall mất đi: `PHỤ LỤC 1`, `PHỤ LỤC 2` và hai
nhãn nền tảng — **ba trong bốn nằm đúng vùng mà hai người gán nhãn bất đồng** (§9.7 đã ghi: "tiêu đề
phụ lục nhiều dòng"). Ở đó luật giữ dòng CUỐI của dãy, còn đáp án Opus giữ dòng ĐẦU; cả hai đều
biện hộ được.

Bài học phương pháp, cùng họ với §10.4: **khi hai bộ đo nói ngược nhau, đừng chọn bộ nào hợp ý —
hãy xem bất đồng nằm ở đâu.** Ở đây nó nằm gọn trong vùng đã biết là mơ hồ, nên con số "giảm 1,1"
là phạt oan chứ không phải hồi quy.

Giữ thay đổi: hoà trên tài liệu thật, thắng rõ trên đúng lớp lỗi nó nhắm tới.

### 12.4 Còn lại của mục này

- **Đường mặc định vẫn 57,1% trên `09-style-ap-sai`.** Muốn chữa thì phải để luật hình dạng lấn
  quyền style ngay cả khi `--style-trust` tắt — tức đổi mặc định. §10.4 đã cảnh báo đúng loại quyết
  định này ("mua một vé số vừa trúng trên bộ đo"), nên nó cần một phép đo riêng trên nhiều tài liệu
  thật hơn, không phải một dòng cờ.
- **Một dương tính giả mới xuất hiện** trên khoá luận thật (i=114, nhãn chữ ký) trong khi ba cái
  khác biến mất. Ranh giới dãy đổi thì thành viên sống sót cũng đổi; chưa truy tới cùng.

## 13. Đo nhánh `LevelTrusted` — ba kết quả ÂM liên tiếp, và cái thứ ba chỉ đúng chỗ tắc

TODO mục 2. §11.3 ghi nhánh này *"đã cài nhưng CHƯA ĐO"* và kỳ vọng nó chữa được đúng cấp 40,7% /
~28% trên hai tài liệu thật. Mục này đo, và câu trả lời là **không** — nhưng quan trọng hơn là **vì
sao không**, vì lý do thật khác hẳn lý do §11.2 dự đoán.

### 13.1 Fixture: `10-cap-style-thoai-hoa`

Thứ TODO đòi. 9 đề mục **cùng mang `Heading2`**, cây thật ba cấp nhìn ra được từ chuỗi đánh số người
soạn gõ (`Chương 1.` / `1.1.` / `1.1.1.`). Style nói ĐÚNG "đây là đề mục", nói SAI "cấp mấy".

Một chi tiết dựng fixture đáng ghi: bản đầu có 18 đoạn, 9 mang style ⇒ mật độ 50%, vượt
`MaxDensity` nên StyleTrust hạ **cả hai** quyền — đo như vậy là đo hai biến cùng lúc. Giãn thân bài
lên 36 đoạn để mật độ về đúng 25%; nay nó báo *"quyền chọn GIỮ, quyền gán cấp HẠ"*, đúng một biến.

Mốc `--no-llm`: chọn đoạn hoàn hảo (P 100% · R 100%) nhưng **đúng cấp 44,4%** — 5/9 sai. Chế độ hỏng
của hai tài liệu thật nay tái tạo được trong vài giây.

### 13.2 Ba lượt đo, ba kết quả âm

| Bước | Đúng cấp trên fixture | Bench 10 tài liệu |
|---|--:|---|
| mốc `--no-llm` | 44,4% | — |
| bật `--style-trust` | **44,4%** | P 100% · F1 100% · 8/10 |
| + giấu `outlineLevel`/`guessedLevel` | **44,4%** | không đổi |
| + bỏ chữ số khỏi tên style | **33,3%** | đúng cấp bench 88,5% → 86,5% |

Hai bản sửa metadata **đã hoàn nguyên**: một cái đo được 0, cái kia đo được âm.

### 13.3 Chẩn đoán: cơ chế CHẠY ĐÚNG, người nhận quyền chỉ nói lại

Khác hẳn §11.2 (nơi quyền được chuyển cho một chỗ trống), ở đây log chứng minh việc chuyển quyền
thành công:

- không cờ → *"Bỏ qua lượt gán cấp toàn cục: cấu trúc đã quyết cấp cho mọi heading"* — mô hình
  **không bao giờ được hỏi**;
- có cờ → *"hierarchy 1: 9 heading → gán cấp toàn cục"*, kết quả ghi `src=Model` — mô hình **được
  hỏi cả 9 mục** và trả về `l=2` cho tất cả.

Giả thuyết đầu: tại metadata chở sẵn câu trả lời (`"outlineLevel":1,"guessedLevel":2` ở cả 9 block),
cùng họ §7.3. Giấu đi ⇒ **vẫn 44,4%**, mô hình vẫn trả `l:2` cho cả chín.

Giả thuyết hai: tại tên style tự nói cấp (`"styleName":"heading 2"`) ngay cạnh chỗ vừa bịt. Bỏ chữ
số ⇒ **33,3%, tệ hơn**.

Kết luận: **không phải lỗi ở thứ mình gửi.** Qwen 7B ở lượt gán cấp toàn cục không suy được cấp từ
chuỗi đánh số trong nội dung; bỏ bớt tín hiệu chỉ làm nó đoán tệ hơn. Đây là lần đầu trong dự án một
chẩn đoán "lỗi nằm ở thứ mình gửi" bị bác — hai lần trước (§7.3, §12.2) thì đúng.

### 13.4 Chỗ tắc thật, và nó là TODO mục 3

Có sẵn một bộ suy cấp TẤT ĐỊNH đọc đúng thứ mô hình không đọc được:
`StructuralHierarchyResolver.SignatureTiers` gom chữ ký `NumberToken` và suy quan hệ lồng nhau. Nó
không chạy trên fixture này vì dòng đầu vòng lặp là `if (Declared(current, document)) continue;` —
style đã "khai" cấp 2 nên nó đứng ngoài. Và `--style-trust` **không với tới chốt đó**: cờ chỉ tác
động `ResolveLevel` cùng khâu chọn đoạn để hỏi, không tác động `Declared`.

Nhưng cho nó chạy cũng chưa đủ: `NumberingAudit.Parse` **không đọc được `Chương 1.`** (TODO mục 3,
§5) nên `Chương 1./2./3.` không sinh ra token, chỉ `1.1.` và `1.1.1.` tạo tầng. Tức mục 2 **bị chặn
bởi mục 3**, không phải bởi thiếu tín hiệu hay thiếu quyền.

Thứ tự đúng từ đây: mục 3 trước (thêm mẫu "nhãn + số" vào `Parse`, đo riêng vì nó đổi output của 13
điểm gọi trên 9 file), rồi mới nới `Declared` để tôn trọng `LevelTrusted`. Làm ngược lại thì nới
xong vẫn không có token để suy.

### 13.5 Thứ giữ lại

- **Fixture `10-cap-style-thoai-hoa`** — bench lên 10 tài liệu. Nó là dụng cụ đo cho cả mục 2 lẫn
  mục 3, và nó tái tạo được trong vài giây một chế độ hỏng trước đây chỉ quan sát được trên tài liệu
  thật không chia sẻ được.
- **Con số bench mới**: `--style-trust` cho P 100% · R 100% · F1 100% · đúng cấp 88,5% · **8/10**;
  không cờ cho P 94,5% · F1 97,2% · 7/10. Chênh lệch đến từ §12, không phải từ mục này.

## 14. "Chương 1." đọc được — bench đạt tuyệt đối toàn phần

TODO mục 3, và nó gỡ nốt chỗ tắc mà §13.4 chỉ ra. Hai thay đổi TÁCH BẠCH, đo riêng từng cái.

### 14.1 Thêm `NumberKind.Labelled`

`Parse` trước đây chỉ có mẫu Ả Rập / La Mã / chữ cái, nên `Chương 1. Tổng quan` không sinh ra
`NumberToken` — lý do gốc của bug 87,2% ở §5, lâu nay *vá bằng chốt* chứ chưa *sửa*.

Quyết định thiết kế đáng nhất là **chữ ký**. `PHẦN I` nằm trên `1.` đúng, nhưng đúng chỉ vì TÌNH CỜ
hai loại số khác nhau (La Mã vs Ả Rập). Với `Chương 1.` và `1.1.` thì sự tình cờ đó không có: nếu
`Chương 1.` ra `Arabic:1` thì nó **trùng chữ ký với `1.` trần** và `SignatureTiers` gộp hai tầng
khác nhau làm một. Nên nhãn phải nằm trong chữ ký — `Labelled(chương):1`. Nhãn đọc từ chính tài
liệu, không phải danh sách cài sẵn, đúng nguyên tắc §3.2 đã dùng khi bỏ `KeywordPrefixRx`.

Mẫu ở đây **hẹp hơn** `HeadingHeuristics.LabelledNumberPrefixRx`, theo đúng hợp đồng ghi ở đầu file:
đòi dấu ngắt tường minh và đòi phần còn lại bắt đầu bằng CHỮ. Thiếu vế sau thì
`Bảng 1.2 Đối chiếu…` tách thành nhãn "Bảng" + số 1 và hậu kiểm đi báo thiếu những mục không tồn tại.

**Đo riêng vế này** (không cờ): bench đúng cấp 88,5% → **90,4%**, tuyệt đối 7/10 → **8/10**. Nhỏ,
dương, không hồi quy — đúng kỳ vọng, vì phần lớn tài liệu bench có style khai cấp nên `Declared`
vẫn chặn.

### 14.2 Nới `Declared` để tôn trọng `LevelTrusted`

Style chỉ được tính là "đã khai cấp" khi nó THẬT SỰ mang thông tin cấp. Danh sách đa cấp
(`NumberingStyleLevel`) thì **không nới**: nó khai cấp bằng cấu hình một lần cho cả tài liệu, không
nhiễm lỗi copy định dạng như style.

| Bench 10 tài liệu, có `--style-trust` | Đúng cấp | Tuyệt đối |
|---|--:|--:|
| trước | 88,5% | 8/10 |
| **sau** | **100%** | **10/10** |

`10-cap-style-thoai-hoa` đi từ **44,4% → 100%**. Lần đầu bench đạt tuyệt đối toàn phần:
**P 100% · R 100% · F1 100% · đúng cấp 100% · 10/10**.

### 14.3 Một lỗi của chính tôi, và vì sao nó khó thấy

Bản đầu của §14.2 móc `Declared` thẳng vào `document.StyleTrust` mà **quên kiểm cờ**
`--style-trust`. Nhánh mặc định vì thế cũng lên 100% — trông như một chiến thắng lớn hơn.

Cái bẫy tinh vi hơn một lỗi cờ thường: `StyleTrust` **luôn được đo** và ghi vào `SlimDocument` để
báo cáo, kể cả khi cờ tắt. Nên đọc nó mà không kiểm cờ thì **không có gì báo lỗi** — chỉ có bảng số
đẹp lên, và đẹp lên vì một cơ chế người dùng không hề bật. Đúng loại quyết định §10.4 gọi là "mua
một vé số vừa trúng trên bộ đo".

Đã gắn cờ: `Apply(headings, document, respectStyleTrust)` mặc định `false`, pipeline truyền
`UseStyleTrust` vào. Sau khi gắn, nhánh mặc định về đúng 90,4% / 8/10 và toàn bộ cải thiện nằm sau
cờ.

### 14.4 Ba lần test suýt xanh giả

Đáng ghi vì cả ba đều lộ ra nhờ **kiểm đột biến**, không phải nhờ đọc lại code:

1. `SignatureTierTests.La_Ma_bao_ngoai_A_Rap` đỏ ngay khi thêm mẫu mới. Nguyên nhân:
   `HasTitleRemainder` đọc cứng `Groups[2]`, mà ở regex mới `Groups[2]` là CHỮ SỐ. `TitleWordRx`
   đòi ≥2 chữ cái nên nó khớp `II` mà không khớp `I` — `PHẦN I.` trượt còn `PHẦN II.` lọt, hai mục
   cùng dạng ra hai chữ ký khác nhau. Nếu bộ test không sẵn có ca La Mã bao ngoài Ả Rập thì lỗi này
   đã lọt và chỉ lộ ra rất lâu sau trên một tài liệu thật.
2. Test đầu tôi viết cho năng lực mới đặt ở tầng `StructuralHierarchyResolver` và **không bắt được
   đột biến nào**: trên fixture đó cấp do nhánh đường dẫn Ả Rập quyết, token `Labelled` không tham
   gia. Siết từ quan hệ `>` sang cấp tuyệt đối — **vẫn không bắt được**.
3. Chuyển xuống tầng `Parse` thì bắt được cả ba đột biến: bỏ nhãn khỏi chữ ký, tắt mẫu mới, và đúng
   lỗi nhóm regex ở mục 1.

Bài học: **test phải đặt ở tầng mà thay đổi thật sự xảy ra.** Hai lần đầu test xanh vì nó đo một
tầng khác, không phải vì code đúng.

## 15. Writeback tách được đoạn dính — hẹp, fail-closed

TODO mục 5. `OutlineWriteback` từ chối bằng `inline_body_not_splittable` mỗi khi heading chỉ chiếm
một phần paragraph; `SlimSourceSegment` (ánh xạ offset chuẩn hoá → run + offset thô) mở khoá được ca
đó. Nay đã nối, nhưng chỉ ở phạm vi hẹp.

### 15.1 Phạm vi: hai vế, cả hai đều fail-closed

Chỉ tách khi ranh giới rơi ĐÚNG đầu một run (`Start == offset` và `RawStart == 0`), VÀ mọi run của
đoạn là con trực tiếp của `w:p`. Vế đầu tránh phải cắt đôi text trong run — việc đó đổi cách chia
run của tài liệu. Vế sau vì `SourceSegments.RunIndex` đếm theo `Descendants<Run>()` nên nó tính cả
run lồng trong `w:hyperlink`; tách ở một run như vậy đòi tách cả hyperlink bao ngoài, và chỉ số cũng
không còn khớp `Elements<Run>()`. Mọi ca khác giữ nguyên `inline_body_not_splittable`.

Các `w:r` được **di chuyển nguyên vẹn**, không dựng lại — nên bất biến 2 ("không chạm vào một ký tự
nội dung nào") vẫn đúng theo nghĩa ký tự. Cái ĐỔI là cấu trúc: một `w:p` thành hai. `w:pPr` của phần
sau là bản sao của phần đầu nhưng bỏ `outlineLvl` và `pStyle`, vì thân bài không được vào cây điều
hướng và cũng không được mang hình thức tiêu đề.

### 15.2 Ràng buộc mà mô tả TODO bỏ sót: chỉ số dịch

Chèn một `w:p` làm mọi đoạn phía sau lệch +1. Bất biến 3 của writeback là đọc lại bản đích rồi đối
chiếu `heading.Index` → đoạn, nên **không có bản đồ chỉ số thì khâu xác minh đi soi nhầm đoạn ngay ở
mục kế tiếp**. `Verify` nay nhận danh sách chỉ số đã tách và dịch theo; riêng `stableId` (địa chỉ
theo vị trí `body[1]/p[N]`) chỉ so khi phía trước chưa có lần tách nào.

### 15.3 Một lỗi lúc cài, đáng ghi

Bản đầu đọc `SlimDocument` từ chính **bản đích đang mở để ghi** → 8 test đổ với
`IOException: file đang được tiến trình khác dùng`. Đọc từ NGUỒN là đủ và không tranh khoá: hai file
lúc đó giống hệt nhau từng byte. Lỗi này lộ ra ngay vì bộ test writeback vốn đã dày.

### 15.4 Test và kiểm đột biến

`InlineBodyWritebackTests`: round-trip mở/ghi/đọc lại (đúng tiêu chí nghiệm thu TODO đặt), cộng ca
fail-closed. Ba đột biến đều bị bắt — nhưng **hai trong ba chỉ bị bắt sau khi siết fixture**:

- tắt hẳn việc tách → đổ ngay từ đầu;
- bỏ chốt "đầu run" → ban đầu KHÔNG đổ, vì fixture một-run không có segment nào bắt đầu đúng ở ranh
  giới. Phải thêm ca ba dấu cách liên tiếp (chuẩn hoá gộp lại) mới sinh ra segment `RawStart != 0`
  tại đúng chỗ đó;
- bỏ bản đồ chỉ số → ban đầu KHÔNG đổ, vì test chỉ có một heading nên không có mục nào phía sau để
  lệch. Phải thêm một đề mục thứ hai đứng sau chỗ tách.

Cùng bài học §14.4 ở dạng khác: **fixture phải chứa đúng tình huống mà chốt sinh ra để chặn**, nếu
không test xanh vì không chạm tới chốt, chứ không phải vì chốt đúng.

## 16. Đúng cấp 26,5% trên khoá luận thật — và hai lần tôi mô tả sai nó

Mục 3 gỡ chốt cho mục 2, nên §13.4 dự đoán chuỗi này sẽ chữa được đúng cấp ~28% trên khoá luận thật.
**Đo lại: không đổi một chữ số** — P 94,2% · R 86,3% · F1 90% · đúng cấp 26,5% ở cả hai nhánh cờ.

### 16.1 Một lỗi thực tế của tôi, đã lan qua nhiều chỗ

§9.7 viết, và §13/§14 cùng nhiều thông điệp commit lặp lại, rằng khoá luận này *"dùng
Heading1→Heading3→Heading4, bỏ qua Heading2"*. **Sai.** Đếm ra:

| Heading1 | Heading2 | Heading3 | Heading4 | Heading5 |
|--:|--:|--:|--:|--:|
| 12 | **8** | 17 | 15 | 16 |

Đủ cả năm cấp, liên tục, không bỏ cấp nào. Tôi suy "bỏ qua Heading2" từ bốn mục đã lấy mẫu chứ chưa
đếm, rồi dùng lại kết luận đó nhiều lần mà không kiểm.

Hệ quả: `StyleTrust` chấm tài liệu này là *"5 cấp riêng biệt ⇒ quyền gán cấp GIỮ"* — và nó **chấm
đúng** theo định nghĩa hiện có. `SkipsLevels` sai, `DistinctLevels = 5 > 1`, nên `LevelTrusted` đúng
và không có gì được nới. Chuỗi mục 2 + mục 3 không bao giờ kích hoạt ở đây.

### 16.2 Vấn đề thật: cùng một style mang hai độ sâu

Đối chiếu 68 đoạn có style với đáp án đồng thuận — 28 khớp, **40 lệch**:

| style | → cấp đáp án | số mục |
|---|--:|--:|
| Heading1 | 1 | 12 ✓ |
| Heading2 | 2 | 8 ✓ |
| **Heading3** | **2** | **9** ✗ |
| **Heading3** | **3** | **8** ✓ |
| Heading4 | 3 | 15 ✗ |
| Heading5 | 4 | 16 ✗ |

`Heading3` mang **hai độ sâu khác nhau ở hai phần khác nhau** của cùng một tài liệu: ở những chương
tác giả có dùng Heading2 thì Heading3 là cấp 3, ở những chương tác giả nhảy thẳng từ Heading1 thì
Heading3 là cấp 2. Heading4/Heading5 lệch đều một cấp vì chúng nằm dưới nhóm chương thứ hai.

### 16.3 Vì sao không bộ dò mức tài liệu nào thấy được

`StyleTrust` đo hai thứ: số cấp riêng biệt, và có bỏ cấp giữa chừng không. Tài liệu này **khoẻ mạnh
theo cả hai** — dùng đủ 5 cấp, liên tục. Bất nhất nằm ở mức PHẦN, không ở mức tài liệu, nên mọi
thống kê gộp toàn tài liệu đều mù với nó. Thêm ngưỡng cũng không cứu được: không có ngưỡng nào phân
biệt "5 cấp dùng nhất quán" với "5 cấp dùng bất nhất theo phần".

### 16.4 Hai lần mô tả sai, và cái đúng

- §9.7 bản đầu: *"lựa chọn quy ước, không phải bug"* — sai, vì cây không cùng hình dạng: `Heading3`
  ở hai chỗ ra hai độ sâu, đổi gốc đánh số không sửa được.
- §9.7 bản sửa (và §13, §14): *"lệch đều một cấp, bỏ qua Heading2"* — cũng sai, như 16.1.
- Đúng: **bất nhất theo phần**, không phải lệch đều, không phải quy ước.

### 16.5 Hướng đo tiếp, và nó KHÔNG phải nới thêm ngưỡng

Tài liệu này có sẵn một nguồn cấp đáng tin hơn style: **chuỗi đánh số người soạn gõ** (`1.1.`,
`1.1.1.`, `2.2.3.2.`) — nó nhất quán suốt tài liệu trong khi style thì không. `NumberingAudit`
đọc được nó, `SignatureTiers` suy được tầng từ nó, nhưng cả hai đứng ngoài vì `Declared()` thấy
style đã khai cấp và `LevelTrusted` thì đúng.

Phép đo cần làm: khi ĐỘ SÂU của chuỗi đánh số gõ tay mâu thuẫn có hệ thống với cấp style, ưu tiên
chuỗi đánh số. Đây là tín hiệu **mức đoạn** nên nó nhìn được thứ mà thống kê mức tài liệu mù. Chưa
làm, và phải đo riêng — nó đổi nguồn quyết định cấp cho mọi tài liệu có đánh số gõ tay.

## 17. Cấp theo độ sâu đánh số — vế đối chiếu độc lập đầu tiên của StyleTrust

TODO 3b, và nó là bản sửa đầu tiên nhích được đúng cấp trên tài liệu thật.

### 17.1 Vì sao hai vế cũ mù

`StyleTrust` đo *số cấp riêng biệt* và *có bỏ cấp giữa chừng không*. Cả hai đều **chỉ soi chính
style**. Khoá luận thật khoẻ mạnh theo cả hai — dùng đủ 5 cấp, liên tục — nên `LevelTrusted` đúng và
toàn bộ chuỗi mục 2 + mục 3 không bao giờ kích hoạt (§16.1).

Vế mới đối chiếu style với một nguồn **ĐỘC LẬP**: độ sâu của chuỗi đánh số người soạn gõ
(`1.1.2.` ⇒ sâu 3). Đây là thứ duy nhất trong ba vế không tự soi mình.

| | |
|---|---|
| Mẫu tối thiểu | 8 đoạn vừa có style vừa có đường dẫn số |
| Ngưỡng bất đồng | > 1/3 |

Ngưỡng 1/3 vì lệch lác đác là chuyện thường (một mục đánh số sai, một mục cố ý nâng cấp); một phần
ba trở lên thì đó là *cách dùng style*, không phải lỗi lẻ.

### 17.2 Đo được

| | Trước | Sau |
|---|--:|--:|
| Khoá luận thật — **đúng cấp** | 26,5% | **37,2%** |
| Khoá luận thật — P / R / F1 | 94,2 / 86,3 / 90 | **không đổi** |
| Bench 10 tài liệu | 10/10 · cấp 100% | **10/10 · cấp 100%** |

Phán quyết trên khoá luận đổi từ *"quyền gán cấp GIỮ"* sang *"HẠ"*, và dòng mô tả nay in kèm tỉ lệ
bất đồng để lần sau không phải đoán vì sao.

**Chưa đủ.** 37,2% vẫn thấp: luật chỉ chạm được đoạn CÓ chuỗi đánh số. `MỞ ĐẦU`, `KẾT LUẬN`,
`Tiểu kết chương 1` không có số nào để bám, và cấp của chúng vẫn theo style.

### 17.3 Fixture phải cô lập đúng vế đang kiểm

Bản đầu của test dùng `Heading1` + `Heading3` cho tài liệu bất nhất — nhưng như thế là **bỏ cấp 2**,
tức kích hoạt vế CŨ (`SkipsLevels`), và test xanh mà không chạm tới vế mới. Phải dựng lại cho dùng
liên tục cả ba cấp, bất nhất nằm ở chỗ cùng độ sâu 2 mà ba mục mang `Heading2` còn sáu mục mang
`Heading3` — đúng hình dạng khoá luận thật.

Đây là lần thứ ba trong hai phiên một fixture xanh vì **không chạm tới chốt** chứ không phải vì chốt
đúng (xem §14.4, §15.4). Ba lần đều chỉ lộ ra nhờ kiểm đột biến.

## 18. Đo trực tiếp document view: 129 546 token — và §7.5 sai chiều

Thí nghiệm one-pass (multi-pass so với một lượt trên toàn bộ ứng viên) đòi biết view thật lớn bao
nhiêu. Đo bằng `xml --dump-chunks --model`, tức tokenizer thật:

| | |
|---|---|
| Document view khoá luận (1498 đoạn, 129 ứng viên) | **416 289 ký tự / 129 546 token** |
| Trần context Qwen2.5-7B | 32 768 |

**One-pass trên Qwen2.5-7B là bất khả** — thiếu gần 4 lần. Ước lượng "129 ứng viên ≈ 16 000 token"
mà tôi nêu trước đó **sai ~8 lần**: tôi suy từ SỐ ỨNG VIÊN chứ chưa đo, đúng lỗi §7.5 đã cảnh báo và
tôi vẫn lặp lại.

### 18.1 §7.5 sai, và sai theo chiều ngược

§7.5 ghi bảng này:

| Cách đếm | Ký tự/token |
|---|---|
| Tokenizer Qwen2.5-7B | ~0,64 — **"suy từ số khối"** |
| Tokenizer Llama-3.2-3B | 3,206 — **đo trực tiếp** |

và kết luận *"hai tokenizer lệch nhau ~5 lần trên cùng đoạn văn tiếng Việt"*, giải thích bằng vocab
Qwen nghiêng Trung/Anh nên chữ có dấu rơi xuống token mức byte.

**Đo trực tiếp Qwen2.5 trên khoá luận thật: 3,213 ký tự/token** — gần như trùng khít 3,206 của
Llama-3.2. Hai tokenizer **không lệch 5 lần; chúng gần bằng nhau**.

Con số 0,64 là suy từ số khối, và §7.5 tự ghi rõ như vậy — nhưng vẫn dùng nó để phát biểu một tính
chất về tokenizer. Đây đúng là lỗi mà chính §7.5 đã tự phê bình ở đoạn cuối (*"con số 2,5× ban đầu
suy từ một quan sát gián tiếp trên MỘT mô hình, rồi được phát biểu như một tính chất của tiếng
Việt"*) — lặp lại lần hai, trong cùng một mục, với cùng một cơ chế.

### 18.2 Hệ quả thực tế cũng đảo chiều

§7.5 kết luận: *"với họ Qwen thì khối thật LỚN HƠN ngân sách (rủi ro tràn cửa sổ ngữ cảnh)"*.

Với 3,213 ký tự/token thì ngược lại: hằng ước lượng `CharsPerToken = 1.85` đóng gói 5000 × 1,85 =
9250 ký tự mỗi khối, mà 9250 ký tự thật ra chỉ là **~2879 token** — tức khối thật **NHỎ HƠN** ngân
sách ~1,7 lần. Không phải rủi ro tràn, mà là **lãng phí cửa sổ và tốn thêm lượt gọi**.

Phạm vi ảnh hưởng vẫn như §7.5 nói: chỉ backend dùng ước lượng (LM Studio, OpenRouter). Backend local
đã đếm bằng tokenizer thật — dòng log in "ngân sách 28000 token THẬT" là bằng chứng.

### 18.3 Thí nghiệm one-pass chỉ đo được trên model context dài

Với 129 546 token, nhánh "một lượt" cần model ≥ 130K context. Qwen3.5-9B khai 262 144 nên nó là
model đầu tiên trong tay chạy được nhánh đó. Trên Qwen2.5-7B chỉ so được "nhiều khối nhỏ" với "ít
khối lớn" (29 khối ở ngân sách 5000 so với 5 khối ở ngân sách 28000), không phải one-pass thật.

## 19. One-pass so multi-pass, đo thật — và Qwen3.5-9B

Câu hỏi "7B/9B có suy được bố cục toàn văn bản trong một lượt không" lâu nay chỉ trả lời được bằng
lập luận. §18 cho biết document view khoá luận là **129 546 token**, nên nhánh one-pass thật đòi model
≥130K context. Qwen3.5-9B khai 262 144 — đủ. Mục này đo cả ba nhánh trên cùng tài liệu, cùng đáp án.

### 19.1 Ba nhánh, cùng tài liệu, cùng đáp án

| | Qwen2.5 · 29 khối (5K) | Qwen2.5 · 5 khối (28K) | Qwen3.5 · **1 khối** (150K) |
|---|--:|--:|--:|
| Precision | 94,2% | 91,5% | **99,1%** |
| Recall | 86,3% | **90,1%** | 83,2% |
| F1 | 90,0% | **90,8%** | 90,5% |
| Đúng cấp | **37,2%** | 35,6% | 36,7% |
| Mục thừa | 7 | 11 | **1** |
| Thời gian | ~330 s | 267 s | **195 s** |

**F1 gần như không đổi qua cả ba** (90,0 / 90,8 / 90,5). Cách đóng gói context không đổi *chất lượng
tổng*, nó đổi **cán cân precision ↔ recall**.

### 19.2 Recall theo vị trí — phép kiểm quyết định

| | đầu | giữa | cuối |
|---|--:|--:|--:|
| A · 29 khối | 97,7% | 84,1% | 77,3% |
| **B · 5 khối** | 97,7% | **93,2%** | 79,5% |
| one-pass | 93,0% | 84,1% | **72,7%** |

Ba điều, cả ba đều đo được:

1. **Recall tụt dần về CUỐI tài liệu ở cả ba nhánh** — 97,7% → 84,1% → 77,3% ở nhánh A. Đây là hiệu
   ứng vị trí thật, không phải nhiễu.
2. **Khối vừa (28K) chữa được phần GIỮA**: 84,1% → 93,2%. Giả thuyết "chia khối làm hỏng ngữ cảnh"
   có cơ sở — nhưng chỉ ở mức khối nhỏ.
3. **One-pass TỆ NHẤT ở cả hai đầu** (93,0% và 72,7%). Nhét cả tài liệu vào một lượt **không** chữa
   được "lost in the middle"; nó làm recall xấu đi ở mọi vị trí.

Đổi lại, one-pass cho precision **99,1%** — đúng 1 mục thừa trên ~110 mục. Nhìn tất cả cùng lúc làm
mô hình **thận trọng hơn**, không phải bao quát hơn.

### 19.3 Kết luận thực dụng

**Cấu hình tốt nhất đo được là Qwen2.5-7B với khối ~28K** (nhánh B): F1 cao nhất, recall cân nhất,
nhanh hơn mặc định 20%. Đây là một dòng cờ, không phải đổi model.

**Đổi sang Qwen3.5-9B không cải thiện gì**: bench 10 tài liệu cho 9/10 · đúng cấp 88,5%, thua
Qwen2.5-7B (10/10 · 100%). Context 262K mở ra nhánh one-pass, mà one-pass lại là nhánh kém nhất.

### 19.4 Một chi phí kiến trúc không lập luận lý thuyết nào nêu ra

Qwen3.5 **không chạy được** với tối ưu tái dùng prefill của pipeline:

```
find_slot: seq_id=1 >= n_seq_max=1
init_batch: failed to prepare recurrent ubatches
decode: failed to find a memory slot for batch of size 512
```

Các lớp linear-attention mang **trạng thái hồi quy** — đúng thứ khiến nó tiết kiệm KV cache — nên
llama.cpp đòi slot chuỗi riêng cho chuỗi thứ hai. Phải chạy `--no-reuse-prefix`, thứ mà trợ giúp CLI
ghi là "chậm hơn ~2 lần".

Tức lợi ích context dài đi kèm một khoản trả bằng tối ưu khác, và nó chỉ lộ ra khi chạy thật. Đây là
lần thứ hai trong dự án một suy luận từ kiến trúc bị phép đo trực tiếp bổ sung ngược (lần đầu: §7.5).

## 20. Lưới model × cách đóng gói — và chỗ tôi kết luận vội hai lần

§19 so Qwen2.5 ở hai cách chia khối với Qwen3.5 ở một-khối-khổng-lồ, rồi kết luận *"đổi sang
Qwen3.5-9B không cải thiện gì"*. **Phép so đó lẫn HAI biến** — vừa đổi model vừa đổi cách đóng gói —
đúng bẫy §4.1. Mục này chạy nốt các ô còn thiếu.

### 20.1 Lưới đầy đủ, khoá luận thật, đáp án Opus

| | khối 5K | khối 28K | 1 khối 150K |
|---|--:|--:|--:|
| **Qwen2.5-7B** | P 94,2 · R 86,3 · **F1 90,0** · cấp 37,2 | P 91,5 · R 90,1 · **F1 90,8** · cấp 35,6 | *bất khả* (trần 32K) |
| **Qwen3.5-9B** | P 92,2 · R 90,1 · **F1 91,1** · cấp **39,0** | P 92,9 · R 90,1 · **F1 91,5** · cấp 37,3 | P 99,1 · R 83,2 · **F1 90,5** · cấp 36,7 |

Thời gian: 7B 330s / 267s; 9B 394s / 295s / 195s. 9B **buộc** chạy `--no-reuse-prefix` (§19.4).

**Kết luận §19 sai.** Ở phép so hợp lệ, 9B thắng ở mọi ô so được: F1 90,0 → 91,1 và 90,8 → 91,5.
Cấu hình tốt nhất đo được là **9B ở khối 28K, F1 91,5%**; nếu ưu tiên đúng cấp thì **9B ở khối 5K,
cấp 39,0%** — cao nhất trong mọi cấu hình.

### 20.2 Trần recall là của PIPELINE, không phải của model

Cả 13 mục thiếu ở nhánh 28K **chưa bao giờ là ứng viên** — 0/13 tới được model. Kiểm trực tiếp bằng
cách đối chiếu danh sách thiếu với tập ứng viên tầng OpenXML.

Vì vậy **R 90,1% là TRẦN của pipeline** trên tài liệu này, và cả hai model đều chạm đúng trần đó khi
dùng khối 28K. Hệ quả:

- Muốn recall cao hơn thì **đổi model là vô ích** — phải sửa tầng lọc ứng viên. §7.1 đã ghi điều này
  (*"tầng OpenXML đánh rơi thì không mô hình nào cứu được"*), nay có bằng chứng mạnh hơn: hai model
  khác thế hệ, khác kiến trúc, context lệch 8 lần, bỏ sót **đúng cùng một tập 13 mục**.
- Trục duy nhất model có tiếng nói là **precision**, và ở đó 9B tốt hơn thật.

### 20.3 Nhưng "recall do đóng gói quyết định" cũng là kết luận vội

Sau khi thấy hai model trùng khít ở khối 28K, tôi viết *"recall do pipeline quyết định, không phải
model"*. Ô cuối bác lại: **7B ở khối 5K chỉ đạt R 86,3%, còn 9B ở cùng khối 5K đạt 90,1%** — tức
chạm trần.

Phát biểu đúng: **trần recall do pipeline đặt ra; khả năng CHẠM trần thì tuỳ model và cách đóng
gói.** 7B cần khối lớn mới chạm được; 9B chạm ngay ở khối 5K. Nói cách khác 9B **bền hơn với việc
chia nhỏ context** — đúng chiều mà §4.1 mô tả điểm yếu của model nhỏ (*"đổi thành phần khối là lật
câu trả lời cho cả mục không liên quan"*).

### 20.4 One-pass vẫn là nhánh kém nhất — và nay biết vì sao

One-pass thua không phải vì 9B yếu (9B thắng ở hai ô kia) mà vì **chính cách đóng gói**: R 83,2%,
dưới trần pipeline 7 điểm, tệ nhất ở cả đầu lẫn cuối tài liệu (93,0% / 72,7%). Đổi lại precision
99,1% — đúng 1 mục thừa.

Nhìn tất cả cùng lúc làm mô hình **thận trọng hơn**, không phải bao quát hơn. Đó là kết quả đo được,
ngược với giả thuyết thường gặp rằng context dài giúp "thấy hết".

## 21. "Tầng lọc phải sai theo hướng rộng" — đúng nguyên tắc, sai khi áp cho luật cấu trúc

Hợp đồng ghi ở đầu `NumberingAudit` nói tầng chấm điểm phải **sai theo hướng rộng**, vì *"bỏ sót một
ứng viên là mất hẳn"*. Bốn luật hạ cấp thêm ở §12–§15 (`DemoteCoverPageBlock`,
`DemoteInlineEmphasis`, `DemoteRunsWithoutOwnProse`, luật mục lục gõ tay) đều SIẾT đúng tầng ấy, và
mục này không nói ra điều đó — chỉ báo cáo F1 tăng.

Nặng hơn: chúng đặt `Role = Normal`, tức **xoá hẳn**, đúng thứ §3.1 cấm (*"được quyền hạ độ tin cậy,
không được quyền xoá bằng chứng"*). Nguyên tắc đó được áp cho phán quyết của mô hình nhưng miễn trừ
cho heuristic của chính mình.

### 21.1 Giá của việc siết, đo bằng cách tắt từng luật

13 mục thiếu trên khoá luận thật, truy nguyên bằng cách vô hiệu hoá từng luật một:

| Nguyên nhân | Mục |
|---|---|
| `DemoteInlineEmphasis` | 1202, 1205, 1209 |
| `DemoteRunsWithoutOwnProse` | 1294, 1335 |
| Nhãn gạch đầu dòng `●` ⇒ list item | 1239, 1256 |
| Điểm 0,35 < ngưỡng 0,45 | 1116 |
| Điểm 0 — định dạng không nổi bật | 215, 964, 979, 1000, 1113 |

Tức **5/13 là do luật mới**, 8/13 là tầng chấm điểm cũ.

### 21.2 Nới ra thì sao — đo, không đoán

Đổi hai luật đó từ "xoá tư cách ứng viên" sang "hạ điểm, giữ ứng viên", tức trả lại đúng tinh thần
§3.1 và hợp đồng của tầng. Tập ứng viên **129 → 256**; cả 5 mục quay lại.

| Khoá luận thật, 7B khối 28K | Siết | **Nới** |
|---|--:|--:|
| Precision | 91,5% | **76,6%** |
| Recall | 90,1% | **80,2%** |
| F1 | **90,8%** | **78,4%** |
| Mục thừa | 11 | **32** |

**Xuống cả hai trục.** Precision tụt là điều dự đoán được. Recall tụt mới đáng chú ý: thêm 127 ứng
viên **đổi thành phần khối**, và mô hình lật câu trả lời cho cả những mục không liên quan — đúng bẫy
§4.1, nay đo được ở quy mô lớn.

Bench 10 tài liệu **giữ nguyên 10/10** ở cả hai nhánh: bộ bench hoàn toàn mù với đánh đổi này.

### 21.3 Kết luận, và đính chính hợp đồng thay vì lờ đi

Vế *"bỏ sót một ứng viên là mất hẳn"* vẫn ĐÚNG — 5 mục kia mất thật. Nhưng kết luận rút ra từ nó
(*"nên luôn sai theo hướng rộng"*) **bị số liệu bác** cho nhóm luật cấu trúc: mô hình không cắt được
chúng (§10.3, §11.2 đã đo hai lần), nên giữ chúng lại chỉ đổi 5 mục thiếu lấy 21 mục thừa, cộng thêm
thiệt hại lan sang các mục khác.

Phân biệt cần giữ:

- **Tầng chấm điểm hình thức** (đậm/hoa/cỡ chữ) — vẫn phải sai theo hướng rộng. Nó ĐOÁN, và đoán sai
  theo hướng hẹp thì mất vĩnh viễn.
- **Luật hạ cấp theo cấu trúc** (dòng bìa, dãy không mở ra văn xuôi, mục lục theo dãy số trang) — đây
  không phải phỏng đoán mà là dữ kiện cấu trúc đọc được từ tài liệu, và mô hình đã được đo là không
  bác nổi. Siết ở đây là đúng chỗ.

Comment ở đầu `NumberingAudit` đã được đính chính để không còn phát biểu một luật mà code cố ý làm
ngược. Nếu chỉ sửa code mà để nguyên câu chữ thì lần sau người đọc sẽ tin vào câu chữ.

## 22. Nới tầng lọc cho LLM tự luận — ba cách, ba lần bị số liệu bác

§21 đo "nới tầng lọc, giữ nguyên harness" và thấy F1 sụp. Phản biện đúng: model được thêm 127 ứng
viên mà **không thêm một tín hiệu nào** để phân biệt chúng — đúng điều literature về LMDX nói, rằng
model nhỏ cần được "mã hoá layout hộ" chứ không phải nhận thêm dữ liệu thô. Mục này đo nốt.

### 22.1 Ba nhánh, khoá luận thật, 7B khối 28K

| | Ứng viên | P | R | F1 |
|---|--:|--:|--:|--:|
| **Siết** (hiện tại) | 129 | 91,5% | **90,1%** | **90,8%** |
| Nới, không tín hiệu | 256 | 76,6% | 80,2% | 78,4% |
| Nới + **tín hiệu cấu trúc trong metadata** | 256 | 88,4% | 75,6% | 81,5% |

Tín hiệu gửi kèm là phán quyết của chính ba luật hạ cấp — `opens_no_prose`, `inline_emphasis`,
`unmarked_body` — cộng một câu trong prompt nói rõ đây là *bằng chứng mạnh nghiêng về l=0, KHÔNG
phải phán quyết*.

**Mô hình CÓ dùng tín hiệu**: precision 76,6% → 88,4%, mục thừa 32 → 13. Nhưng recall tụt xuống
75,6%, thấp nhất cả ba nhánh, và F1 vẫn thua luật tất định 9,3 điểm.

### 22.2 Vì sao thua — tính được trước khi chạy

Đo độ chuẩn của chính ba cờ đó trên khoá luận thật, đối chiếu HỢP hai đáp án:

| Cờ | Số mục | Thực ra là đề mục | Tỉ lệ sai |
|---|--:|--:|--:|
| `unmarked_body` | 35 | 0 | **0%** |
| `opens_no_prose` | 61 | 2 | **3%** |
| `inline_emphasis` | 33 | 3 | **9%** |
| **Tổng** | **129** | **5** | **4%** |

Ba luật đó **đúng 96%** khi dùng làm tín hiệu phủ định. Nên phép đo thực chất hỏi: *giao quyết định
cho một model, trên đúng lớp mà luật tất định đã đúng 96%, có tốt hơn không?* Để hoà vốn model phải
loại đúng ≥96% của 129 mục, vì phần được nhiều nhất chỉ là 5 đề mục thật.

**Nguyên tắc rút ra**: khi một luật tất định đã đúng ~96% trên một lớp, chuyển quyền cho mô hình chỉ
có lãi nếu mô hình đúng hơn 96% **trên đúng lớp đó** — không phải "trên trung bình". Đây là phép thử
mà mọi đề xuất "nới lỏng cho LLM tự luận" phải vượt qua.

### 22.3 Nhưng cách hiểu "mục lục chuẩn, đầy đủ" mở ra thứ khác — và nó ăn

Đặt lại bài toán thành *dựng lại bố cục đáng lẽ phải có* thay vì *phân loại từng đoạn*, thì tài liệu
**tự khai bố cục đó**: 21 dòng mục lục. Pipeline loại chúng (đúng — chúng không phải đề mục) rồi
**vứt luôn thông tin chúng mang**.

Đối chiếu với đáp án đồng thuận: **21/21 dòng khớp đúng một đề mục thật**, phủ 23/110 mục, kèm cả
cấp. Đây là tín hiệu chính xác 100% mà pipeline chưa từng đọc.

`TableOfContentsAnchor` chỉ **pin cấp**, không thêm và không xoá mục nào — dòng mục lục nói "mục này
tồn tại và sâu chừng này", nó không nói gì về mục nó không nhắc tới. Ghép theo text đã chuẩn hoá (bỏ
số trang, tiền tố đánh số, dấu câu) nên không phụ thuộc ngôn ngữ.

| Khoá luận thật, 7B khối 28K | Trước | Sau |
|---|--:|--:|
| **Đúng cấp** | 35,6% | **45,8%** |
| P / R / F1 | 91,5 / 90,1 / 90,8 | **không đổi** |
| Bench 10 tài liệu | 10/10 · cấp 100% | **10/10 · cấp 100%** |

### 22.4 Comment nói một đằng, code làm một nẻo — lần thứ ba

Bản đầu đặt lượt neo **TRƯỚC** `StructuralHierarchyResolver`, kèm comment nói mục lục *"đứng trên
mọi suy luận trong thứ tự quyền lực §1"* — tức để bộ suy luận nói lời cuối, đúng ngược điều comment
tuyên bố. Đo được: pin 8 cấp mà **đúng cấp không đổi một chữ số**. Đổi sang chạy sau: +10,2 điểm.

Cùng họ với §7.4 (`PrecedesTable` tính sau `Classify` nên cờ luôn false đúng lúc cần) và §12.1 (chốt
mức tài liệu đếm sau khi StyleTrust xoá cờ). Ba lần, cùng một cơ chế: **thứ tự các lượt là một phần
của hợp đồng, và comment không thi hành được nó.**

Thứ phát hiện ra là số đo, không phải đọc lại code — log in "pin lại 8 cấp" trông như đã chạy đúng.

## 23. Chốt: F1 95,1% — và đúng cấp gần gấp đôi

Cấu hình tốt nhất đo được, trên đáp án đồng thuận 110 mục, quy tắc chấm cố định từ §9.1 (loại mục
hai người gán nhãn bất đồng):

**Qwen3.5-9B · khối 28K · `--style-trust` · `--no-reuse-prefix` · lượt neo mục lục**

| | |
|---|--:|
| Precision | **93,8%** |
| Recall | **96,4%** |
| **F1** | **95,1%** |
| Đúng cấp | **51,9%** |
| Thời gian | 293 s |

Còn 7 mục sai thật và 4 mục thiếu trên 110.

### 23.1 Đọc con số cho đúng

**F1 95,1% KHÔNG phải tiến bộ so với §9** — phiên trước đã đạt đúng 95,1% với 7B và cấu hình cũ.
Trên 110 mục thì một mục đáng 0,9 điểm, nên **mốc "hơn 95%" đang được vượt qua bằng đúng một mục**.
Đó là ngưỡng chạm tới, không phải ngưỡng bỏ xa.

Thứ THẬT SỰ tiến bộ trong hai phiên này là **đúng cấp: 26,5% → 51,9%**, gần gấp đôi, đến từ ba
nguồn tách bạch và đo riêng từng cái:

| Nguồn | Đóng góp |
|---|---|
| Vế đối chiếu độ sâu đánh số cho `StyleTrust` (§17) | 26,5% → 37,2% |
| Đổi 7B sang 9B, cùng cách đóng gói (§20) | 37,2% → 37,3% |
| Lượt neo theo mục lục tài liệu (§22) | 35,6% → 45,8% (trên thước Opus) |
| Cộng dồn trên thước đồng thuận | **51,9%** |

### 23.2 Bốn mục thiếu — và vì sao mục lục không cứu được chúng

`1239`, `1256` (nhãn phân nhóm trong danh mục tham khảo) mang nhãn gạch đầu dòng `●` nên bị coi là
list item ngay ở tầng chấm điểm. `1294`, `1335` (`PHỤ LỤC 1`, `PHỤ LỤC 2`) bị `DemoteRunsWithoutOwnProse`
hạ.

Mục lục có dòng `PHỤ LỤC 157` nhưng chuẩn hoá xong nó thành `phụ lục`, còn đề mục thật là
`phụ lục 1` — **không khớp**. Tức mục lục chỉ nhắc tới mục CHA, không nhắc hai mục con. Đây là giới
hạn thật của tín hiệu này: nó pin được những gì nó liệt kê, và mục lục của tài liệu này chỉ liệt kê
tới cấp 2.

### 23.3 Bảy mục sai thật

`114` (nhãn chữ ký), `262` (liệt kê chương trong phần "bố cục"), `1031`/`1032`/`1033`/`1063` (chỉ
xuất hiện ở khối 28K — hệ quả của việc đổi thành phần khối, §4.1), `1336`.

Nhóm `1031`–`1063` đáng chú ý: chúng KHÔNG xuất hiện ở khối 5K. Tức một phần "cái giá" của khối lớn
là mục thừa mới ở vùng giữa tài liệu — đánh đổi mà bảng F1 tổng che mất.

### 23.4 Trần còn lại nằm ở đâu

Recall 96,4% với 4 mục thiếu, cả 4 đều bị loại **trước khi mô hình nhìn thấy**. §20.2 đã đo: hai
model khác thế hệ bỏ sót đúng cùng một tập. Nên mọi cải thiện recall tiếp theo phải đến từ tầng lọc
ứng viên, không từ mô hình — và §21/§22 đã đo rằng nới tầng lọc ra (dù có kèm tín hiệu) làm F1 tụt.

Đường còn lại chưa thử: dùng mục lục để **pin việc CHỌN**, không chỉ pin cấp. Nhưng §23.2 vừa cho
thấy mục lục tài liệu này không phủ được bốn mục đang thiếu, nên đường đó không cứu được ca cụ thể
này — nó chỉ có giá trị với tài liệu mà mục lục liệt kê sâu hơn.

## 24. Reasoning/thinking và độ sâu đánh số — hai phép đo, hai chiều ngược nhau

### 24.1 Độ sâu đánh số quyết cấp khi style bất nhất: +14,1 điểm

§17 cài BỘ DÒ (`StyleTrust` hạ quyền gán cấp khi style không bám độ sâu đánh số) nhưng bộ chấp hành
phía sau vẫn đi qua `FindSiblingLevel`/`FindParentLevel` — **suy cấp từ hàng xóm, mà hàng xóm cũng
sai đúng cùng kiểu.** Hạ quyền style xong rồi giao cho một bộ suy luận kế thừa đúng lỗi vừa hạ.

Chẩn đoán từ bảng lỗi: **39/51 lỗi cấp là "sâu hơn đúng một bậc"** (5→4: 24 mục, 4→3: 15 mục), theo
style thì `Heading5` (16) + `Heading4` (15) chiếm 31 — đúng nhóm §16.2 truy ra.

Luật: khi `--style-trust` bật VÀ `LevelTrusted` sai VÀ đoạn có đường dẫn số, lấy **thẳng** độ sâu làm
cấp. `1.1.1.` sâu 3 thì cấp 3, không hỏi hàng xóm.

| Đáp án đồng thuận, 9B khối 28K | Trước | Sau |
|---|--:|--:|
| **Đúng cấp** | 51,9% | **66,0%** |
| P / R / F1 | 83,5 / 96,4 / 89,5 | **không đổi** |

### 24.2 Thinking: mất recall để đổi lấy cấp

Qwen3.5 GGUF có `/think`, `<think>`, `enable_thinking` trong chat template nên bật được thật. Nhưng
**thinking và GBNF loại trừ nhau**: `<think>…</think>` đứng trước JSON, còn grammar ép output khớp
lược đồ ngay từ token đầu. Cờ `--think` vì vậy tự tắt grammar.

| Đáp án đồng thuận | Không thinking | **Thinking** |
|---|--:|--:|
| Precision | 83,5% | **86,4%** |
| Recall | **96,4%** | 86,4% |
| F1 | **89,5%** | 86,4% |
| Đúng cấp | 66,0% | **70,5%** |
| Thời gian | 293 s | 352 s |

**Nguyên nhân recall tụt, đo được: 5 trong 10 khối trả về 0 tiêu đề.** Mất trắng nửa số khối, không
phải suy giảm dần. Không có "chỉ số bịa" nào nên model không bịa ID — output chỉ không parse được
thành kết quả dùng được. Đó là cái giá của việc tắt grammar, và nó đắt hơn phần thinking mang lại.

### 24.3 Một mẫu lặp lại qua ba phép đo

| Chế độ "suy nghĩ nhiều hơn" | Precision | Recall |
|---|--:|--:|
| One-pass 150K, 1 khối (§19) | 99,1% | 83,2% |
| Thinking, grammar tắt (§24.2) | 86,4% | 86,4% |
| Nhiều khối vừa, grammar bật | 83,5% | **96,4%** |

Ba lần, cùng một chiều: **mọi cách làm mô hình cân nhắc nhiều hơn đều nâng precision và hạ recall.**
Nó trở nên thận trọng hơn, không bao quát hơn. Đây là mẫu đủ nhất quán để dùng làm dự đoán, không
còn là quan sát lẻ.

### 24.4 Chỗ thinking đáng dùng — và chưa đo

Thinking nâng đúng cấp **66,0% → 70,5%**, và đúng cấp là chỉ số yếu nhất còn lại. Nhưng nó phá recall
vì phải tắt grammar cho **lượt phân loại** — nơi recall được quyết định.

Lượt gán cấp toàn cục thì khác: ở đó tập heading đã chốt, recall không còn gì để mất. Bật thinking
**chỉ cho lượt đó** có thể lấy được +4,5 điểm cấp mà không trả giá recall. Chưa cài, chưa đo.

### 24.5 Khung outline tăng dần — đã tồn tại một nửa

Ý tưởng "luôn mang theo khung đã dựng" đã có trong repo: tham số `anchorsFor` truyền khung heading đã
chốt vào **lượt phản biện** qua `BuildCriticAnchorContext`. Thiếu là áp cho **lượt phân loại**.

Rào cản không phải thiếu luật mà là kiến trúc: `RunPassAsync` dựng view của MỌI khối trước rồi mới
gửi — có chủ đích, để gửi song song và giữ dev-log đúng thứ tự. Khung tăng dần đòi view khối 2 chỉ
dựng sau khi có kết quả khối 1, tức **tuần tự hoá lượt phân loại**. Backend local vốn đã song song 1
nên không mất gì; LM Studio và OpenRouter thì mất. Phải làm thành cờ riêng và đo tách bạch.

## 25. Thinking chỉ ở lượt gán cấp — giả thuyết §24.4 bị bác

§24.4 lập luận: thinking phá recall vì tắt grammar ở lượt PHÂN LOẠI, còn lượt gán cấp thì tập heading
đã chốt nên recall không còn gì để mất — bật riêng ở đó sẽ lấy được +4,5 điểm cấp miễn phí.

Đã cài đúng như vậy: `--think` không còn tắt grammar toàn cục; grammar tắt **cục bộ** trong
`ClassifyHierarchyAsync`; `MaxTokens` nới cho lượt đó vì trần cũ chỉ đủ cho JSON.

| Đáp án đồng thuận, 9B khối 28K | Mốc | Thinking ở lượt gán cấp |
|---|--:|--:|
| Recall | 96,4% | **96,4%** |
| Khối trả về rỗng | 0 | **0** |
| P / F1 | 83,5 / 89,5 | 83,5 / 89,5 |
| **Đúng cấp** | 66,0% | **66,0%** |

Nửa đầu giả thuyết ĐÚNG: recall được bảo vệ trọn vẹn, không khối nào trả về rỗng. Nửa sau **SAI**:
cấp không nhích một chữ số. Và lượt đó có chạy thật — log ghi `hierarchy 1: 127 heading → gán cấp
toàn cục (22 856 ms)` — nên đây không phải ca "cờ không có gì để tác động".

### 25.1 +4,5 điểm cấp ở §24.2 là ảo giác thống kê

Nếu thinking không giúp gì ở lượt gán cấp, thì +4,5 điểm ở §24.2 đến từ đâu? Từ chính **10 điểm
recall bị mất**: bỏ đi 10 mục khó thì phần còn lại có tỉ lệ đúng cấp cao hơn. Mẫu số nhỏ đi và
"dễ" đi, không phải tử số tốt lên.

Đây là cùng một lỗi đọc số mà §10.4 đã ghi (*"F1 không phải thước đo đủ cho một thay đổi về QUYỀN"*),
lần này ở dạng khác: **một chỉ số tính trên tập được giữ lại sẽ tự đẹp lên khi tập đó bị lọc bớt.**
Muốn so đúng cấp giữa hai nhánh thì recall phải bằng nhau, nếu không so nhầm hai mẫu số.

Cách tránh, ghi lại để dùng: khi một thay đổi làm đổi recall, đừng đọc "đúng cấp" như một cải thiện
độc lập — hoặc so trên cùng tập giao nhau, hoặc chốt recall trước rồi mới so cấp.

### 25.2 Trạng thái cờ

`--think` giữ lại, **mặc định tắt**, cùng lý do §10.4 giữ `--style-auto-assign`: nó là đối chứng có
số, và xoá đi thì người sau sẽ thử lại đúng cái đã đo là vô ích. Hàm `Think()` ở lượt phân loại cũng
giữ nguyên dưới dạng no-op kèm ghi chú, thay vì xoá.

Kết luận về reasoning cho bài toán này: **ba lần đo, không lần nào thinking hay nhìn-toàn-cục mang
lại gì thật.** Mọi tiến bộ đo được đều đến từ đọc dữ kiện cấu trúc có sẵn trong tài liệu (§17, §22,
§24.1), không từ việc bắt mô hình suy nghĩ nhiều hơn.

## 26. Neo vào literature, và baseline kiểu pandoc lật một kết luận của tôi

Đến giờ dự án chưa từng neo vào công trình nào. Đã xác thực: nhánh nghiên cứu đúng tên là
**hierarchical document structure reconstruction / ToC extraction**, có từ trước LLM. Mốc chính là
**HRDoc** (AAAI 2023, arXiv 2303.13839): 2.500 tài liệu nhiều trang, ~2 triệu đơn vị ngữ nghĩa,
baseline **DSPS** encoder–decoder (không phải prompt một LLM lớn), metric **Semantic-TEDS**.

Chi tiết đắt nhất không phải dataset mà là **cách chia bài toán**. HRDoc chia làm BA bài toán con:
*semantic unit classification*, **parent finding**, *relation classification*. Không phải
"phân loại + gán cấp" như tôi vẫn đo.

### 26.1 Baseline kiểu pandoc: style là tín hiệu CHỌN hoàn hảo, tín hiệu CẤP tệ

Cách Claude (skill `docx`) đọc file .docx là `pandoc -t markdown`, tức style `HeadingN` → `#`×N.
Đúng bằng R1 và chỉ R1. Mô phỏng đúng luật đó trên khoá luận rồi chấm với đáp án đồng thuận:

| KLTN, 1.498 đoạn, 110 mục trong đáp án | pandoc (chỉ style) | Pipeline |
|---|--:|--:|
| Precision | **100,0%** | 83,5% |
| Recall | 61,8% | **96,4%** |
| F1 | 76,4% | **89,5%** |
| Đúng cấp | 41,2% | **66,0%** |

Bỏ sót 42/110, bắt nhầm **0**. Pipeline mua được +34,6 điểm recall và +24,8 điểm cấp, trả bằng
16,5 điểm precision. Đây là lần đầu có con số cho câu "cả tầng LLM này đáng giá bao nhiêu".

### 26.2 Đo bằng metric của HRDoc thì kết luận lật ngược

Chấm đúng 68 mục style-only đó bằng **parent finding** thay vì cấp tuyệt đối:

```
dung CAP TUYET DOI : 41,2%  (28/68)
dung CHA (parent)  : 100,0% (68/68)   <- sai cha o 0 muc
```

Cây **không sai một cạnh nào**. 40 lỗi cấp đều lệch ĐỀU một bậc: `H5→4: 16, H4→3: 15, H3→2: 9`.
Tác giả dùng Heading3 ở chỗ ngữ nghĩa là cấp 2 — **con số sai, quan hệ đúng**.

Nên thử luật hiển nhiên theo sau: bỏ con số trong tên style, chỉ giữ thứ tự lồng nhau, gán
cấp = độ sâu trong cây đó:

```
cap = so trong ten style (kieu pandoc) :  41,2%  (28/68)
cap = DO SAU trong cay do style dung nen: 100,0% (68/68)
```

**41,2% → 100%**, thuần xác định, không một giây suy luận.

### 26.3 Lỗi thiết kế của chính tôi: `LevelTrusted` nhị phân nên vứt cả phần đúng

§17 dựng `LevelTrusted` như một công tắc: style không bám độ sâu đánh số ⇒ **bỏ hẳn** tín hiệu
style, thay bằng độ sâu đánh số (§24.1, +14,1 điểm). Nhưng chỉ **giá trị tuyệt đối** của style sai;
**thứ tự lồng nhau** thì đúng tuyệt đối. Tôi đã ném phần tốt đi cùng phần xấu, và mất 34 điểm cấp
trên chính tập mục mà tài liệu khai rõ nhất.

Thêm `StyleTrust.NestingTrusted` làm trục thứ ba, và `StructuralHierarchyResolver.StyleNestingDepths`
làm bộ chấp hành, đứng TRƯỚC nhánh độ sâu đánh số.

### 26.4 Tiền đề của luật không phải trang trí

Quét luật này trên cả 10 tài liệu bench:

| | raw (số trong tên style) | độ sâu lồng nhau |
|---|--:|--:|
| 9 tài liệu còn lại | 100% | 100% |
| `10-cap-style-thoai-hoa` | 44,4% | **33,3%** ← tệ hơn |

Tài liệu đó cho **mọi** đề mục mang `Heading2`: cây lồng nhau sập hết về một cấp. Nên
`DistinctLevels > 1` là **điều kiện tồn tại** của luật, không phải điều kiện cho chắc — và nó đã có
sẵn trong `StyleTrust` từ §17, chỉ chưa ai nối vào đúng chỗ.

### 26.5 Ba điều còn nợ

1. **Metric.** Đang chấm "đúng cấp tuyệt đối", trong khi chuẩn của nhánh này là so khớp CÂY. Chính
   §25.1 đã vấp: một chỉ số tính trên tập được giữ lại tự đẹp lên khi tập bị lọc bớt. Metric cây
   không có lỗ đó. Chưa cài.
2. **Đáp án vẫn là đồng thuận của model** (TODO 4). Mọi con số ở trên đứng trên nền đó.
3. **Đối chứng model chuyên dụng** (kiểu DSPS) chưa làm được: không đủ dữ liệu gán nhãn. Cùng cổ
   chai với điểm 2.

## 27. Một phép đo vô hiệu vì build sai backend, và cách chặn nó tái diễn

Phép đo `--rolling-outline` đầu tiên chạy **36 phút rồi bị dừng**, mới xong 1/5 khối. Nhìn số thì
tưởng "khung tăng dần đắt kinh khủng". Log nói khác:

```
Mô hình sẵn sàng. Ngữ cảnh 32768 token, CPU 8 luồng      <- mốc trước: "GPU 99 lớp"
khối 1/5: 43 ứng viên → 43 tiêu đề (1 156 524 ms)        <- 19 phút cho MỘT khối
```

Nguyên nhân không nằm ở tính năng đang đo. Trước đó tôi chạy `dotnet build -c Release` **trần**,
quên `-p:UseVulkan=true`; NuGet thay backend Vulkan trong thư mục output bằng bản CPU. Toàn bộ phép
đo chạy trên CPU.

Cùng họ với hai lỗi đã ghi: fixture patch âm thầm no-op (§ trước) và CUDA lặng lẽ rơi về CPU (§7).
Cả ba đều là **đổi môi trường rồi tin kết quả mà không đọc dòng log xác nhận môi trường**.

### 27.1 Chẩn đoán đầu tiên của tôi cũng sai — và chính dòng log đã nói ra

Viết xong mục trên, tôi build sạch lại chỉ với Vulkan rồi chạy thử: **vẫn** `CPU 8 luồng`. Nên
nguyên nhân không phải (chỉ) cờ build.

Câu trả lời nằm ngay trong `LlamaHeaderExtractor.Describe()`, hàm mà §7 đã viết riêng cho đúng
tình huống này. Nó có HAI nhánh CPU khác nhau:

```csharp
if (gpuLayers <= 0) return $"CPU {threads} luồng" + template;          // <- không ai yêu cầu GPU
return supportsOffload ? $"GPU {gpuLayers} lớp"
                       : $"CPU {threads} luồng — ĐÃ YÊU CẦU GPU {gpuLayers} lớp nhưng thư viện
                          native không hỗ trợ offload, đang chạy CPU";
```

Log in ra nhánh THỨ NHẤT, không kèm cảnh báo. Tức `GpuLayerCount == 0`: **tôi chưa bao giờ truyền
`-ngl 99`** trong các lệnh hôm nay. Thêm vào thì lượt chạy thử đi từ *"CPU 8 luồng"* sang
*"GPU 99 lớp"*, 7 giây.

Bài học kép, và vế thứ hai đắt hơn:

* **Vế một:** cờ build (`-p:UseVulkan=true`) và cờ chạy (`-ngl 99`) là HAI thứ. Thiếu cờ build thì
  được nhánh cảnh báo; thiếu cờ chạy thì được nhánh im lặng. Tôi thiếu cả hai nên thấy nhánh im lặng
  và quy sai cho cờ build.
* **Vế hai:** §7 đã dựng sẵn công cụ phân biệt đúng hai ca này, viết hẳn tài liệu cho nó, mà tôi
  vẫn đọc lướt dòng log rồi đoán. Công cụ chẩn đoán chỉ có giá trị khi người ta ĐỌC nó.

### 27.2 Luật kiểm trước khi tin bất kỳ con số nào

1. Build kèm `-p:UseVulkan=true`, và chạy kèm `-ngl 99`. Thiếu một trong hai là chạy CPU.
2. Đọc dòng `Mô hình sẵn sàng…`: phải là **GPU N lớp**. `CPU N luồng` trơn nghĩa là thiếu `-ngl`;
   `CPU … — ĐÃ YÊU CẦU GPU …` nghĩa là thiếu backend. Hai lỗi khác nhau, log đã phân biệt sẵn.
3. Đối chiếu thời gian mỗi khối với mốc đã biết (~30–40 s/khối cho 9B khối 28K). Lệch một bậc độ
   lớn nghĩa là môi trường khác, không phải tính năng khác.

### 27.3 Và một lỗi quan sát nữa của tôi

Tôi bọc lệnh đo trong `… | grep -vE … | tee log | grep -E …`. `grep` **đệm theo khối khi đầu ra
không phải terminal**, nên `log` đứng ở 0 byte suốt cả phép chạy và tôi mù hoàn toàn với tiến độ —
đúng lúc cần thấy nhất. Phải dùng `grep --line-buffered`, hoặc ghi thẳng log rồi lọc sau.

## 28. Luật độ sâu lồng nhau: đúng cấp 66,0% → 81,1%

Đo lại đúng môi trường (§27.2), một biến, cùng mọi cờ khác:

| Đáp án đồng thuận, 9B khối 28K, `--style-trust` | Mốc | + `StyleNestingDepths` |
|---|--:|--:|
| Precision | 83,5% | **83,5%** |
| Recall | 96,4% | **96,4%** |
| F1 | 89,5% | **89,5%** |
| **Đúng cấp** | 66,0% | **81,1%** |
| Bench 10 tài liệu | 100% · 10/10 | **100% · 10/10** |
| Thời gian | 295 s | 314 s |

P/R/F1 **không đổi một chữ số**. Đó chính là dấu hiệu một luật chỉ động vào cấp mà không lấn sang
việc chọn — nếu ba con số kia nhúc nhích thì luật đang làm gì đó ngoài phạm vi của nó và phải đi
tìm nguyên nhân trước khi nhận.

Chi phí: +19 s (6%), toàn bộ là suy luận tất định, không thêm một lượt gọi model nào.

### 28.1 Đường đi của "đúng cấp" trên tài liệu thật

| | đúng cấp |
|---|--:|
| Trước §16 | 26,5% |
| §17 hạ quyền gán cấp của style | 37,2% |
| §22 neo theo mục lục của tác giả | 51,9% |
| §24.1 dùng thẳng độ sâu đánh số | 66,0% |
| §28 độ sâu lồng nhau của style | **81,1%** |

Cả năm bước đều là **đọc dữ kiện cấu trúc có sẵn trong tài liệu**. Không bước nào đến từ mô hình lớn
hơn, prompt khéo hơn, hay nhiều suy luận hơn — ba thứ đó đã đo và đều cho số không (§19, §24.2, §25).

### 28.2 Còn lại gì

Precision 83,5% giờ là điểm yếu nhất, và §26.1 chỉ đúng chỗ: lớp style-only có precision **100%**,
mọi dương tính giả đều đến từ 61 ứng viên heuristic. Đó là chỗ tiếp theo, không phải cấp.

## 29. Thị giác: Qwen3.5-9B nhìn ảnh trang in

Câu hỏi: model 9B có "nhìn" tài liệu như Claude vẫn làm không, và nếu có thì tốt tới đâu.

### 29.1 Dựng nhánh

Qwen3.5 **có** thị giác, và đó là bộ trọng số ĐANG dùng — không phải model khác. Kiểm bằng metadata:

| | arch | tensor | tensor thị giác |
|---|---|--:|--:|
| `Qwen3.5-9B-Q4_K_M.gguf` | `qwen35` | 427 | 0 |
| `mmproj-Qwen3.5-9B-F16.gguf` | `clip` | 352 | **352** |

Thị giác nằm ở file projector RỜI (`general.name = Qwen3.5-9B`, `projector_type = qwen3vl_merger`),
0,86 GB. llama.cpp luôn cần nó tách rời. Nhờ vậy phép đo này **một biến**: cùng trọng số, đổi đầu
vào từ text OOXML sang ảnh trang in.

Chuỗi dựng: Word COM → PDF (máy này không có LibreOffice) → PyMuPDF 150 DPI → 171 ảnh. Suy luận qua
`llama-server` của llama.cpp b10327 bản Vulkan, KHÔNG qua LLamaSharp. VRAM 7 868 / 12 288 MiB.

### 29.2 Kết quả

Chấm trên cùng đáp án đồng thuận, hai cửa sổ trang, cùng một bộ chấm, mỗi trang một lượt hỏi:

| | tr 17–24 (dùng để tinh chỉnh thước đo) | tr 142–149 (**giữ lại**) |
|---|--:|--:|
| Precision | 100% | 100% |
| Recall | 100% | 100% |
| F1 | **100%** | **100%** |
| Đúng cấp | 83,3% (10/12) | 66,7% (6/9) |
| Thời gian | ~5 s/trang | ~5 s/trang |

Gộp: 21 đề mục, 11 trang — **chọn đúng tuyệt đối, đúng cấp 76,2%**.

Đối chiếu pipeline đọc OOXML trên CẢ tài liệu: P 83,5 · R 96,4 · F1 89,5 · cấp 81,1.

### 29.3 Đọc số cho đúng — bốn điều làm nhẹ kết quả này

1. **Quy mô lệch hẳn.** 21 đề mục / 11 trang so với 110 đề mục / 1.498 đoạn.
2. **Chọn mẫu có lợi.** Hai cửa sổ đều là trang thân bài. Con số của pipeline tính trên cả bìa, mục
   lục, danh mục tài liệu tham khảo — chính những phần sinh dương tính giả.
3. **Chi phí.** ~5 s/trang × 171 trang ≈ 14 phút, chưa kể render, so với 314 s cho cả pipeline.
4. **Không có đường về XML.** Ảnh trả lời "dòng này trông như đề mục", không trả lời "sửa ở đâu
   trong `document.xml`". Muốn ghi ngược vẫn phải khớp text về đoạn.

### 29.4 Nhưng lỗi cấp thì có hình dạng rất rõ

Cả ba lỗi cấp ở cửa sổ giữ lại đều cùng một kiểu: `a. Hạn chế về…`, `b. Hạn chế về…`,
`c. Chưa chú trọng…` — model gán cấp 3, đáp án cấp 4. Nhìn MỘT trang thì không thể biết quy ước độ
sâu của cả tài liệu. Đó đúng là thông tin mà OOXML có và ảnh không có.

Ngược lại, ở phần CHỌN thì ảnh không bỏ sót gì, kể cả đề mục **không đánh số, không style**
(`Hạn chế về phát triển nội dung số`) — đúng lớp mà §26.1 đo được là style bỏ sót 42/110.

### 29.5 Kết luận kiến trúc

Hai tín hiệu bù nhau chứ không thay nhau:

| | chọn | cấp |
|---|--:|--:|
| Style built-in (§26.1) | P 100%, R 61,8% | 41,2% (100% nếu đọc thứ tự lồng nhau — §28) |
| Ảnh trang in (§29.2) | **P 100%, R 100%** | 76,2% |
| Pipeline hiện tại | P 83,5%, R 96,4% | 81,1% |

Hướng đáng thử: **thị giác làm tầng ỨNG VIÊN cho riêng những đoạn không có bằng chứng cấu trúc**,
còn CẤP vẫn lấy từ cấu trúc tài liệu. Vì toàn bộ dương tính giả hiện nay đến từ 61 ứng viên heuristic
(§28.2), mà đúng chỗ đó thì ảnh đang đo được P 100%.

Chưa cài. Đây là giả thuyết có số đỡ, không phải kết luận.

### 29.6 Bộ chấm phải sửa năm lần — và đó là phần đáng cảnh giác nhất

Con số đi 66,7 → 81,5 → 88,0 → 94,7 → 100 qua năm lần tôi sửa **thước đo**, không phải sửa model:

1. `max_tokens` 700 bị thinking ăn sạch (698/700 token vào `reasoning_content`, `content` rỗng cả 5
   trang) — cùng cơ chế §24 đã đo ở phía văn bản, chỉ khác là ở đây nó giết cả câu trả lời.
2. Prompt tôi gõ KHÔNG DẤU nên model bắt chước, trả về "CHUONG 1: CO SO LY LUAN"; hàm khớp giữ dấu
   nên cả trang 24 bị chấm sai.
3. Định vị trang lấy khớp text đầu tiên, nên ba tên chương bị gán vào mục "Bố cục khoá luận" — nơi
   chúng là VĂN BẢN TRONG ĐOẠN (14pt, không đậm), không phải đề mục (14pt, đậm, in hoa, trang sau).
4. Khoá khớp 60 ký tự dài hơn một dòng in nên dòng heading thật không khớp.
5. Điều kiện `len(k) > 10` loại hẳn mọi đề mục ngắn: `2.3.2. Hạn chế` → `232hanche`, 9 ký tự, không
   bao giờ khớp được nên bị tính dương tính giả — dù nó nằm ngay trong đáp án (đoạn 1118).

Mỗi lần sửa đều được kiểm bằng bằng chứng ĐỘC LẬP (cỡ chữ/đậm đọc từ PDF, tư cách thành viên trong
đáp án), không phải bằng việc đối chiếu với câu trả lời của model. Nhưng **động cơ đi tìm** thì đến
từ chỗ model "sai", và đó là con đường quen thuộc dẫn tới việc gọt thước đo cho vừa kết quả.

Hai điều đã làm để chặn: (a) một cửa sổ **giữ lại** (tr 142–149) chưa từng dùng để tinh chỉnh, và
(b) sau lần sửa thứ năm thì chốt bộ chấm, chạy MỘT lần cho cả hai cửa sổ, lấy số bất kể ra sao.
Kịch bản đã đưa vào repo (`scripts/vl-probe.py`) để kiểm lại được.

## 30. Khung outline tăng dần: không giúp. Và metric cây đổi việc cần làm tiếp

### 30.1 Trước hết, một lỗi thật mà chỉ phép đo mới lộ

Lượt đo đầu trên GPU **hỏng hẳn** ở khối 4:

```
init_batch: failed to prepare attention ubatches
decode: failed to find a memory slot for batch of size 205
Thất bại: llama_decode failed: 'NoKvSlot'
```

Tràn context. Khối khung được cộng vào view mà ngân sách 28000 token vốn đã tính để lấp gần đầy cửa
sổ 32768; tới khối 4, khi khung tích đủ mục, prompt vượt trần và cả lượt chạy trả về 0%.

**Luật:** thứ cộng thêm vào prompt phải được trừ khỏi ngân sách của prompt. Không có ngoại lệ cho
"chỉ một khối nhỏ thôi". Đã vá: trả lại `min(2000, ngân sách/4)` token, trần ký tự của khung bám
theo đúng phần dự trữ đó.

Bản vá ĐẦU dùng hằng 2000 cứng — nó nuốt gần trọn ngân sách mặc định 2200 và làm vỡ hai test khung
ngay lần build đầu. Trần phải TỈ LỆ, không phải hằng số. Test bắt được trước khi nó thành phép đo sai.

### 30.2 Kết quả

Một biến, cùng mọi cờ khác, cùng phiên, cùng GPU:

| Đáp án đồng thuận | Mốc §28 | + khung tăng dần |
|---|--:|--:|
| Precision | 83,5% | **82,2%** |
| Recall | 96,4% | 96,4% |
| F1 | 89,5% | **88,7%** |
| Đúng cấp | 81,1% | 81,1% |
| Đúng cha | 97,2% | 97,2% |

**Không giúp gì, còn mất 1,3 điểm precision.** Ý tưởng nhắm đúng cơ chế hỏng đã đo hai lần (§4.1,
§21: đổi thành phần khối là lật câu trả lời cho cả mục không liên quan), được cài đúng như mô tả —
khối 2 nhận lại mục lục khối 1 chốt, khối 3 nhận cả hai — và vẫn không đổi được gì.

Đây là lần thứ **tư** một hướng "cho mô hình nhiều ngữ cảnh / nhiều suy nghĩ hơn" cho số không:

| Hướng | Kết quả |
|---|---|
| §19 one-pass 129.546 token, một khối | R 83,2% — kém nhất |
| §24.2 thinking toàn bộ | mất 10 điểm recall, +4,5 cấp là ảo giác (§25.1) |
| §25 thinking riêng lượt gán cấp | không đổi một chữ số |
| §30 khung outline tăng dần | −1,3 precision, còn lại không đổi |

Giữ cờ `--rolling-outline`, mặc định tắt, cùng lý do §10.4/§25.2: nó là đối chứng có số.

### 30.3 Metric cây nói việc còn lại là gì

Cài `ParentAccuracy` (bài toán con *parent finding* của HRDoc) bên cạnh cấp tuyệt đối:

| | đúng cấp | đúng cha |
|---|--:|--:|
| Lớp style-only (§26.2) | 41,2% | **100%** |
| Pipeline hiện tại | 81,1% | **97,2%** |

Cây gần như hoàn hảo: chỉ **2,8% quan hệ cha–con sai**, trong khi 18,9% cấp tuyệt đối sai. Nghĩa là
gần như toàn bộ lỗi cấp còn lại là **lệch gốc của một nhánh ĐÚNG HÌNH**, không phải hiểu sai cấu
trúc.

Việc tiếp theo vì thế KHÔNG phải "làm mô hình hiểu cấp giỏi hơn" — nó đã hiểu đúng quan hệ tới
97,2%. Nó là **neo độ sâu tuyệt đối cho một nhúm nhánh**, và đó là luật tất định.

Mutation test bắt được một lỗ của chính metric này: đổi `>=` thành `>` khi đẩy ngăn xếp biến anh em
cùng cấp thành cha–con, mà ba test đầu vẫn xanh — vì chúng dựng CẢ HAI cây bằng cùng một hàm nên lỗi
triệt tiêu. Chỉ ca mà đáp án có anh em còn kết quả trả về thì không mới lộ ra.

## 31. Neo cục bộ cho item danh sách: đúng cấp 81,1% → 91,5%

§30.3 nói việc còn lại không phải "làm mô hình hiểu cấp giỏi hơn" mà là neo độ sâu tuyệt đối. Chẩn
đoán trước khi sửa, và nó chỉ ra một thủ phạm cụ thể chứ không phải một khiếm khuyết chung.

### 31.1 20 lỗi cấp nằm trong ba vùng, mỗi vùng lệch một hằng số

| vùng | đoạn | lệch |
|---|---|--:|
| A | 266, 282, 287, 297, 302 | +1 |
| B | 1120, 1150, 1152 | +1 |
| C | 1170, 1175, 1183, 1192, 1201, 1222 | +2 |
| D | 1446, 1447, 1453, 1460, 1467, 1473 | −1 |

Không một lỗi nào rải rác. Đúng hình dạng mà metric cây đã báo (cha 97,2% / cấp 81,1%): nhánh đúng
hình, sai gốc.

Cả 20 đều `style="Normal"`, và phần lớn mang `num="20.0" nlab="a."` — item của danh sách đa cấp.
Nhãn "a." không phải đường dẫn số Ả Rập nên `PathOf` trả null và chúng rơi xuống `SignatureTiers`.

### 31.2 Tầng chữ ký gán một con số TOÀN CỤC cho một quan hệ CỤC BỘ

`SignatureTiers` xếp hạng chữ ký theo **thứ tự xuất hiện lần đầu trong cả tài liệu**. Nhưng
"a., b., c." là quan hệ với mục cha ngay trên nó:

| đoạn | cha gần nhất | tier gán | đáp án |
|---|---|--:|--:|
| 266 | `1.1.1.` — cấp 3 | 5 | **4** |
| 1120 | `2.3.2.` — cấp 3 | 5 | **4** |
| 1175 | `3.1.` — cấp 2 | 5 | **3** |

Cùng một chữ ký, ba độ sâu khác nhau. Một con số toàn cục chỉ đúng ở chỗ nó xuất hiện lần đầu.
Cả ba đều đúng bằng **cha + 1**.

### 31.3 `LocalListDepth` và kết quả

Luật: đoạn CÓ `NumberingId` mà không đọc được đường dẫn số Ả Rập thì lấy cấp = cấp của mục gần nhất
đứng trước **không cùng `NumberingId`**, cộng một. Bỏ qua anh em cùng danh sách — nếu không thì mục
thứ hai lấy mục thứ nhất làm cha và cả dãy sâu dần.

| Đáp án đồng thuận | Mốc §28 | + `LocalListDepth` |
|---|--:|--:|
| P / R / F1 | 83,5 / 96,4 / 89,5 | **83,5 / 96,4 / 89,5** |
| **Đúng cấp** | 81,1% | **91,5%** |
| Đúng cha | 97,2% | 96,2% |
| Bench 10 tài liệu | 100% · 10/10 | **100% · 10/10** (cha 100%) |

+10,4 điểm cấp, P/R/F1 không đổi một chữ số. Nhưng **đúng cha giảm 1,0 điểm** — luật sửa được 10
điểm độ sâu tuyệt đối và làm hỏng một quan hệ cha–con. Đổi chác rõ ràng có lợi, nhưng phải ghi cả
hai vế: nếu chỉ nhìn "đúng cấp" thì vế mất đi vô hình, và đó chính là lý do §30.3 cài metric cây.

### 31.4 Đường đi của "đúng cấp"

| | đúng cấp |
|---|--:|
| Trước §16 | 26,5% |
| §17 hạ quyền gán cấp của style | 37,2% |
| §22 neo theo mục lục của tác giả | 51,9% |
| §24.1 dùng thẳng độ sâu đánh số | 66,0% |
| §28 độ sâu lồng nhau của style | 81,1% |
| §31 neo cục bộ cho item danh sách | **91,5%** |

Sáu bước, sáu lần đọc dữ kiện cấu trúc có sẵn trong tài liệu. Không bước nào đến từ mô hình lớn hơn
hay nhiều suy luận hơn — bốn lần thử hướng đó đều cho số không (§19, §24.2, §25, §30.2).

## 32. Thị giác làm tầng lọc ứng viên: giả thuyết §29.5 KHÔNG đủ đỡ

§29.5 đề xuất dùng thị giác làm tầng ứng viên cho riêng đoạn không có bằng chứng cấu trúc, vì
style-only đo được P 100% và ảnh đo được P 100%. Đo thẳng vào chế độ hỏng thì nó không đứng.

### 32.1 Trước hết, xác thực khẳng định đã lặp nhiều lần

"Toàn bộ dương tính giả đến từ ứng viên heuristic" — kiểm trực tiếp: **0/21** dương tính giả mang
style Heading built-in. Khẳng định đúng, và giờ có bằng chứng thay vì suy ra.

Nhưng soi từng mục thì "thuộc danh sách" KHÔNG tách được chúng: 14/21 có `num=`. Chúng là
`1920 x 1080 pixels tỉ lệ 16:9`, `Nguồn: Tik Tok`, `Nguyễn Hà Phương`, `Thử nghiệm nhiều khung giờ`
— phán đoán ngữ nghĩa, không phải hình thức.

Một ca sạch: `Mạng xã hội Facebook` xuất hiện hai lần — đoạn 348 có `num="39.0" nlab="b."` (đề mục
thật, điểm 0,8) và đoạn 634 chỉ in đậm không đánh số (điểm 0,55, đáp án nói không). Đáp án phân
biệt đúng, pipeline thì không.

### 32.2 Kết quả

Tập kiểm là CHÍNH 19 dương tính giả định vị được, cộng 8 đề mục thật trên cùng những trang đó — để
đo cả cái được lẫn cái mất, không chỉ cái được:

```
loai dung  8/19 duong tinh gia cua pipeline
giu lai    7/8  de muc THAT tren cung nhung trang do
```

Ngoại suy lên cả tài liệu: P 83,5% → ~89,1%, R 96,4% → ~95,5%, F1 89,5% → ~92,1%.

**Không đáng cài.** +2,6 điểm F1 đổi lấy: render 171 trang qua Word/LibreOffice, ~5 s/trang suy
luận, tức ~15 phút cho một tài liệu mà cả pipeline hiện chạy 5 phút — **gấp ba thời gian**. Và mẫu
chỉ 27 mục.

### 32.3 Vì sao thị giác không loại được 11 mục còn lại

Nhìn danh sách nó GIỮ: `Mạng xã hội Facebook`, `Nghiên cứu "đối thủ"`, `Thử nghiệm nhiều khung giờ`,
`KẾT QUẢ KHẢO SÁT`. Đó đúng là những dòng **trông y hệt đề mục** — in đậm, đứng riêng một dòng.

Thị giác không thể phân biệt vì trên trang in chúng KHÔNG khác đề mục. Thứ phân biệt chúng là
`num="39.0"` — dữ kiện chỉ tồn tại trong OOXML và biến mất khi rasterize. Đây là mặt trái của cùng
một quan sát ở §29.4: ảnh không biết quy ước độ sâu của tài liệu, và cũng không biết đoạn nào thuộc
danh sách nào.

Kết luận: thị giác mạnh ở chỗ OOXML yếu (đề mục không style, không đánh số) và **yếu ở đúng chỗ
OOXML mạnh**. Nó không phải tầng lọc thay thế được, ít nhất không với giá này.

### 32.4 Lần thứ ba cùng một lỗi phép đo

Lượt chạy đầu của phép thử này cho "loại 10/19, mất 15/29" — nhìn thì kết luận ngay "thị giác phá
recall". Sai. `Truyền hình` chuẩn hoá thành `truyenhinh`, 10 ký tự, khớp MỌI trang chứa cụm từ đó,
nên một đề mục bị tính là "bị làm mất" ở bốn trang khác nhau.

Đây là lần thứ BA cùng một lớp lỗi (xem §29.6 mục 3 và 4), và tôi đã tự viết cảnh báo về nó rồi vẫn
đi vào. Bản vá tạm (đòi văn bản ≥ 20 ký tự và duy nhất một trang) loại mất 13/21 dương tính giả và
để lại **0** đề mục đối chứng — tức nó sẽ cho một con số một chiều, đẹp và vô nghĩa.

Sửa đúng gốc: **căn theo THỨ TỰ**, không tìm text tự do. Đoạn trong docx và text trong PDF cùng một
trật tự đọc nên con trỏ trang chỉ được TIẾN. Nhờ vậy đoạn 348 và đoạn 634 — cùng văn bản
`Mạng xã hội Facebook` — được gán trang 36 và trang 80, phân biệt đúng. Bảng ánh xạ đoạn→trang này
là công cụ dùng lại được cho mọi phép đo thị giác sau.

## 33. `PromoteStandaloneLine`: đắt, nhưng không vô dụng như tôi vừa nói

### 33.1 Hai phát hiện phụ, cả hai đáng giữ

**Pipeline TẤT ĐỊNH.** Hai lượt chạy y hệt cho trùng khít từng chữ số
(`P 83.5 · R 96.4 · F1 89.5 · cấp 91.5 · cha 96.2`). Phép kiểm này đáng lẽ phải làm từ đầu: nó xác
nhận mọi chênh lệch ±1 điểm trong handoff là hiệu ứng thật, không phải nhiễu chạy lại.

**`--threshold` không làm cái tên nó hứa.** Số ứng viên Y HỆT ở 0,45 và 0,55 (129 = 68 + 61) nhưng
kết quả vẫn đổi (P 83,5→84,0, R 96,4→95,5). Cơ chế nằm ở `PromoteStandaloneLine`:

```csharp
p.Role = ParagraphRole.HeadingCandidate;
p.Score = options.CandidateThreshold;   // gán ĐIỂM = NGƯỠNG
```

Đường này nhận đoạn làm ứng viên KHÔNG qua cổng điểm, rồi gán điểm bằng đúng ngưỡng. Nâng ngưỡng
chỉ đổi **con số tự tin hiển thị cho mô hình** trong document view (`sc="0.55"`) — cùng tập ứng viên,
mô hình thấy số khác nên trả lời khác. Cái tên `--threshold` nói dối về việc nó làm.

### 33.2 Cái giá của đường "dòng đứng riêng"

| Khoá luận, một biến | Mốc | `--no-standalone-lines` |
|---|--:|--:|
| Ứng viên | 129 (68 style + 61 heuristic) | **111** (68 + 43) |
| Precision | 83,5% | **92,8%** |
| Recall | 96,4% | **93,6%** |
| F1 | 89,5% | **93,2%** |
| Đúng cấp | 91,5% | 94,2% |
| Đúng cha | 96,2% | 99,0% |
| Bench 10 tài liệu | 100% · 10/10 | **100% · 10/10** |

+9,3 precision đổi lấy −2,8 recall; F1 +3,7.

### 33.3 Và một con số tôi vừa báo SAI

Trước khi đo, tôi đếm trên dump và tuyên bố: *"75 ứng viên, 0 đề mục thật, độ chính xác 0%"*. Sai.

Dump đó sinh bằng `dhx xml` mặc định — **khác cấu hình** với lượt eval đang bàn (không `--style-trust`,
nên các tầng hạ cấp chạy khác). Tôi phân tích một tập và kết luận cho một tập khác. Cùng lớp lỗi với
§27 (đo môi trường này, kết luận cho môi trường kia).

Con số đúng, đọc từ chính hai lượt eval: đường này đóng góp **18 ứng viên**, trong đó **3 là đề mục
thật** — độ chính xác **16,7%**, không phải 0%.

Ba mục đó là `590`, `1170` (`Tiểu kết chương 2`) và `1222` (`Tiểu kết chương 3`): in đậm, căn giữa,
**không đánh số, không style**. Không tầng nào khác trong pipeline bắt được chúng — đó chính là lý do
đường này tồn tại.

### 33.4 Quyết định

**Giữ mặc định BẬT.** Hợp đồng của tầng ứng viên là chọn RỘNG, không đánh rơi; tắt đường này làm rơi
3 đề mục thật và recall xuống 93,6%. Đổi 2,8 điểm recall lấy 9,3 điểm precision là một lựa chọn về
SẢN PHẨM, không phải về kỹ thuật — và nó thuộc về người dùng, không thuộc về tôi.

Cờ `--no-standalone-lines` tồn tại để cái giá đó đo được, cùng lý do §10.4/§25.2/§30.2 giữ các cờ
đối chứng khác.

Hướng đúng nếu muốn cả hai: giữ đường này nhưng **hạ cấp sau khi mô hình quyết**, bằng một luật tất
định nhắm đúng lớp nhiễu nó sinh ra (`Nguồn: …` lặp lại, phương án trắc nghiệm, dòng mục lục gõ tay
kèm số trang). Chưa cài.

## 34. `Nguồn: …` — model có đủ dữ kiện và vẫn đọc sai; ba luật thay thế đều hỏng

Câu hỏi: vì sao LLM không thấy `Nguồn: Facebook` là thứ không thuộc khung chính.

### 34.1 Nó THẤY — 11/12 lần

Trong 12 đoạn `Nguồn: …` lọt vào tập ứng viên, mô hình **bác 11**. Chỉ `Nguồn: Tik Tok` (đoạn 1063)
lọt ra kết quả. Đây là tỉ lệ lỗi, không phải bất lực hệ thống.

### 34.2 Và nó có đủ dữ kiện — dump chứng minh

`dhx xml --dump-chunks` cho đúng thứ mô hình đọc:

```
BLOCK  metadata: {"i":1062,"requested":false,...}
content:   Hình… Title bài đăng “khai thác” nội dung từ chương trình Đường lên đỉnh Olympia…
END_BLOCK
BLOCK  metadata: {"i":1063,"requested":true,"bold":true,...}
content:   Nguồn: Tik Tok
END_BLOCK
```

Khối ngay trên là chú thích hình, và tầng lọc đã nhận ra (`requested: false`). Hai khối nằm liền
nhau trong cùng một prompt. Không thiếu dữ kiện nào — **mô hình đọc sai**.

Ghi lại vì nó bác một giả thuyết dễ tin: *"mô hình sai vì không thấy khung tổng thể"*. Ở ca này nó
thấy đủ. Và §19 (one-pass toàn văn) với §30.2 (khung tăng dần) đã đo hai lần rằng đưa thêm khung
toàn cục không cải thiện gì.

Lý do sâu hơn: khung chính phân biệt được *đề mục đánh số* với *phần còn lại*, nhưng ranh giới thật
nằm BÊN TRONG phần còn lại — giữa `Về ngôn ngữ` (đề mục thật, không đánh số) và `Nguồn: Tik Tok`
(chú thích). Khung không cắt qua ranh giới đó.

### 34.3 Ba luật thay thế, đo hết, hỏng hết

Mục tiêu: loại lớp này bằng luật tất định, KHÔNG thêm danh sách từ khoá tiếng Việt.

| Luật | Loại được | Làm mất |
|---|--:|--:|
| Văn bản lặp ≥ 3 lần trong tài liệu | 2/21 | **4 đề mục thật** |
| Đứng ngay sau đoạn chứa `w:drawing` | **0/21** | 0 |
| Đứng ngay dưới dòng đã bị nhận là chú thích | 1/21 (nới lỏng) | **6 đề mục thật** |

* **Lặp** hỏng vì khoá luận dùng cấu trúc song song: `Về ngôn ngữ` là đề mục thật, lặp ở ba chương.
* **Kề ảnh** không kích hoạt: ảnh nằm trong ô bảng hoặc cấu trúc khác, không phải đoạn liền trước.
* **Kề chú thích** với `CaptionRx` bản chặt loại được 0 vì regex đòi chữ số sau từ khoá, mà tài liệu
  viết `Hình…` (dấu ba chấm). Nới ra cho khớp thì nó bắt luôn dòng mục lục bắt đầu bằng `Bảng…` và
  giết 6 đề mục thật đứng sau.

### 34.4 Đặt đúng tỉ lệ trước khi đầu tư thêm

Lớp này đáng **1 trong 21** dương tính giả — khoảng **0,8 điểm precision**. Nó gây khó chịu khi nhìn
danh sách ứng viên (12 lần xuất hiện), nhưng 11/12 đã bị mô hình chặn.

Cách duy nhất còn lại là một mẫu ĐẶC NGỮ (`Từ: DanhTừRiêng` hoặc thêm `nguồn`/`source` vào
`CaptionRx`). Đáng nói: `CaptionRx` **đã chứa** từ khoá tiếng Việt (`hình`, `ảnh`, `bảng`,
`biểu đồ`…), nên đó là nới một luật chú thích đã có chứ không phải lập danh sách từ khoá mới để nhận
heading. Nhưng ranh giới ấy là quyết định của người dùng, không phải của tôi — chưa làm.

## 35. Lỗi UI `NoKvSlot`: tái dùng prefill sai bản chất với mô hình có trạng thái hồi quy

Người dùng chạy qua giao diện Web và nhận `llama.cpp decode thất bại: NoKvSlot`. Tái hiện được từ
CLI bằng đúng cấu hình UI (ctx 8192, 5000 token/khối, 30 khối, `-ngl 20`), rồi cô lập một biến:

| Cấu hình UI | Kết quả |
|---|---|
| **Có** tái dùng prefill | **0/30 khối** — `NoKvSlot` ngay khối đầu |
| **Không** tái dùng prefill | **30/30 khối chạy hết** |

### 35.1 Vì sao

Tái dùng prefill giữ KV của phần prompt chung rồi nối phần riêng từng khối. Với attention thuần thì
đúng — KV của một token chỉ phụ thuộc các token trước nó. Với lớp **state-space (SSM / linear
attention)**, trạng thái được cuộn theo toàn bộ chuỗi và không tách ra theo token, nên "phần chung"
không tái dùng được. Đây là sai về BẢN CHẤT, không phải kém tối ưu.

Handoff đã ghi Qwen3.5 dùng Gated DeltaNet, nhưng **đường CLI không bao giờ chạm phải** vì mọi phép
đo đều truyền `--no-reuse-prefix`. Đường Web bật mặc định, nên người dùng lãnh trọn. Một cờ tôi luôn
bật "cho sạch phép đo" đã che mất một lỗi sản phẩm suốt nhiều phiên.

### 35.2 Nhận biết bằng metadata, không bằng tên file

| | arch | khoá SSM |
|---|---|---|
| Qwen3.5-9B (hỏng) | `qwen35` | `qwen35.ssm.state_size`, `.conv_kernel`, `.group_count`… |
| Qwen2.5-7B (chạy được) | `qwen2` | **không có** |

Luật: có bất kỳ khoá `{arch}.ssm.*` nào thì từ chối tái dùng prefill và **nói ra lý do trong log**.
Bám vào kiến trúc đọc từ GGUF chứ không vào tên file — tên file là anti-pattern mà `ChunkingOptions`
đã phê. Nhờ vậy luật phủ luôn Mamba, Jamba, Falcon-H1, RWKV và mọi kiến trúc lai sau này.

Sau khi vá, đúng cấu hình UI đó:
```
Tắt tái dùng prefill: mô hình qwen35 có lớp trạng thái hồi quy (qwen35.ssm.inner_size)
  — phần prompt chung không tách ra tái dùng được.
khối 30/30 ✓
```

### 35.3 Bài học phương pháp

Cờ dùng để LÀM SẠCH phép đo (`--no-reuse-prefix`) đã vô tình trở thành lớp che một lỗi chỉ xuất hiện
ở đường mặc định. Phép đo sạch và đường người dùng đi là hai thứ khác nhau; ít nhất một lượt chạy
phải đi đúng đường mặc định.

## 36. Hai cờ mới: cả hai đều SỐ KHÔNG — và phân tích dẫn tới chúng dựa trên artifact cũ

Ý tưởng: dùng `w:sdt` (content control) làm dấu hiệu cấu trúc. Đo được trước khi cài (tưởng vậy):
21/129 ứng viên nằm trong `w:sdt`, 0 mục là đề mục thật; và mẫu `NHÃN + SỐ + HẾT` tách sạch 8/8
trong sdt (dòng mục lục kèm số trang) với 5/5 ngoài sdt (`PHỤ LỤC 1`, `Tiểu kết chương 2`…).

Cài hai cờ độc lập, 339 test xanh, 3 mutation bị giết. Kết quả đo, mỗi cờ một lượt:

| Khoá luận | Mốc §31 | `--skip-content-controls` | `--bare-labels` |
|---|--:|--:|--:|
| Ứng viên | 129 | **129** | **129** |
| P / R / F1 | 83,5 / 96,4 / 89,5 | không đổi | không đổi |
| Đúng cấp / cha | 91,5 / 96,2 | không đổi | không đổi |

**Không một chữ số nào thay đổi.**

### 36.1 Vì sao — và lỗi ở đâu

Sinh lại dump bằng ĐÚNG cờ của lượt đo (`--style-trust`):

| | đoạn có `sdt=1` | trong đó là ứng viên |
|---|--:|--:|
| Dump đúng cờ, build hiện tại | 21 | **0** |
| `kltn-full.xml` — build phiên trước, không `--style-trust` | 21 | **21** |

Con số "21/129 ứng viên nằm trong sdt" đọc từ một artifact **cũ, sinh bằng build khác và cờ khác**.
Pipeline hiện tại đã loại 21 đoạn đó bằng đường khác từ trước. Cờ A không có gì để làm.

Đây là **lỗi thứ hai cùng loại trong hai mục liên tiếp**: §33.3 đã ghi đúng bài học này ("phân tích
một tập, kết luận cho một tập khác") và tôi vẫn lặp lại ngay sau đó. Nguyên nhân hệ thống: dump nằm
trong scratchpad không mang dấu vết cấu hình sinh ra nó, nên nhìn tên file không biết nó thuộc lượt
nào. Luật từ nay: **mọi dump dùng để suy luận phải sinh lại bằng đúng cờ của lượt đang bàn, ngay
trước khi đọc.**

### 36.2 Cờ B không sai, nhưng nối vào chỗ không tác động được

Bộ đọc `NHÃN + SỐ + HẾT` chạy đúng (có test và mutation test). Nhưng nó chỉ nuôi
`HasStructuralEvidence`, mà chỗ đó dùng đúng MỘT lần: cứu đoạn đã bị mô hình gắn nhãn
`DocumentTitle` rồi loại — tức đoạn **phải từng là ứng viên**. `PHỤ LỤC 1` (đoạn 1294) có
`role="Normal"`, chưa bao giờ là ứng viên, nên không có đường nào tới.

Muốn cờ B có tác dụng thật thì phải mở rộng `StructuralRecovery` sang token `Labelled` — hiện nó
chỉ xử lý đường dẫn số Ả Rập nhiều cấp ("3.2"). Chưa làm.

### 36.3 Đính chính một khẳng định trước đó

Tôi đã nói 4 mục bỏ sót chia làm hai kiểu: hai mục "model thấy và bác" (`PHỤ LỤC 1`, `PHỤ LỤC 2`).
**Sai** — đọc từ chính dump cũ đó. Với cấu hình thật, cả bốn (`1239`, `1256`, `1294`, `1335`) đều
`role="Normal"`: **không mục nào tới được mô hình**. Cả bốn là mất mát ở TẦNG ỨNG VIÊN, không phải
lỗi phán đoán của mô hình.

Điều này đổi hướng việc cần làm: chỗ phải sửa là tầng ứng viên, không phải prompt hay mô hình.

### 36.4 Giữ lại gì

* `SlimParagraph.InContentControl` + thuộc tính `sdt="1"` trong dump — **giữ**. Chính nó phơi ra sai
  lệch giữa hai dump và giúp bắt lỗi ở 36.1.
* Cả hai cờ — **giữ, mặc định tắt**, cùng lý do §10.4/§25.2/§30.2/§33: số không cũng là số đo, và
  không ghi lại thì người sau sẽ thử lại đúng cái đã đo là vô ích.

## 37. Người dùng gán nhãn thật, và nó phơi ra một phép đảo trong thang confidence

### 37.1 Nhãn của người — TODO 4 bắt đầu đóng

Người dùng duyệt kết quả trên UI và chỉ ra 5 mục mà **đáp án đồng thuận (Sonnet+Opus+Haiku) xếp là
đề mục cấp 3 nhưng thực tế không phải**: đoạn 1447, 1453, 1460, 1467, 1473 — tên người được phỏng
vấn bên trong `PHỤ LỤC 3: CÁC BIÊN BẢN PHỎNG VẤN SÂU`.

| Khoá luận | Đáp án model (110 mục) | Sau chỉnh của người (105) |
|---|--:|--:|
| Precision | 83,5% | **79,5%** |
| Recall | 96,4% | 96,2% |
| F1 | 89,5% | **87,1%** |
| Đúng cấp | 91,5% | **96,0%** |
| Đúng cha | 96,2% | 96,0% |

Nhãn người làm precision xấu đi 4 điểm và đúng cấp tốt lên 4,5 — vì 5 mục đó vừa được tính "bắt
đúng" vừa bị tính "sai cấp". Đúng loại sai lệch mà đáp án do model dựng không tự thấy được.

Người dùng cũng XÁC NHẬN đáp án đúng ở các mục khác họ nêu (1031–1033 pixel, 634/657/665
`Mạng xã hội …`, 1296, 1315) — chúng vốn đã nằm ngoài đáp án.

### 37.2 Và phơi ra một lỗi lớn hơn: thang confidence gán NGƯỢC

Log của mọi lượt chạy: `Cổng precision 93%: 16 tự nhận, 111 cần duyệt (evidence chưa calibration)`.
Nghĩa là 111/127 mục bị bắt duyệt tay — người dùng thấy gần như mọi thứ đều "chưa đạt cổng", kể cả
`Lý do chọn đề tài`. Nhưng chấm lại theo đúng nhãn:

| `evidence.status` | số mục | precision THẬT |
|---|--:|--:|
| `verified_by_multiple_checks` (5/5) | 86 | **95,3%** |
| `supporting_checks` (2/5) | 36 | 52,8% |
| *(không có evidence)* | 5 | **0,0%** |

| **confidence hiển thị** | số mục | precision THẬT |
|---|--:|--:|
| **0,93 — TỰ NHẬN, qua cổng** | 21 | **47,6%** |
| 0,85 — "chưa đạt cổng" | 64 | **100,0%** |
| 0,80 — "chưa đạt cổng" | 42 | 64,3% |

**Cổng đang tự nhận đúng nhóm tệ nhất và bắt duyệt đúng nhóm hoàn hảo.**

Nguyên nhân, trong `PrecisionAcceptanceGate.EvidenceScore`:

```csharp
if (Source == Model && CriticConfirmed) return independentStructure ? 0.95 : 0.93;
if (Source == Style && CriticConfirmed) return 0.93;          // không xét evidence
if (Source is Model or Style)           return Math.Min(Confidence, 0.85);
```

`CriticConfirmed` được đặt trên MỌI bằng chứng cấu trúc. Nhưng lượt phản biện chỉ chạy trên những
khối mà **chính pipeline đã đánh dấu là không đáng tin** (bịa chỉ số, hoặc mọi mục cùng một cấp).
Nên "đã qua phản biện" thực chất là dấu hiệu *đến từ vùng đáng ngờ* — và nó đang được thưởng điểm
cao nhất, trong khi mục qua đủ 5/5 kiểm tra bị chặn trần 0,85, dưới cổng 93%.

Thành phần nhóm 0,93: 16 mục `supporting_checks` (2/5) + **5 mục `Style` KHÔNG có evidence nào** —
đúng 5 biên bản mà người dùng vừa loại. Nhóm không-evidence ấy đúng **0/5**.

### 37.3 Việc phải làm

1. Thang confidence phải do **bằng chứng** dẫn dắt, không do `CriticConfirmed`. `ConfidenceForChecks`
   (5/5→0,95, 4/5→0,85, 3/5→0,80) đã đúng chiều và đã tồn tại — nhưng chỉ áp cho nguồn `Structure`.
2. Mục **không có evidence** không được nhận điểm cao nhất; 0/5 đúng là bằng chứng đủ mạnh.
3. Khi chưa có calibration profile, UI không nên trình bày một CỔNG như một phán quyết. Nói
   "chưa calibration" và xếp theo `evidence.status` — thứ đã đo được là tách 95,3% với 52,8%.

Chưa cài. Cả ba đều đổi hành vi tự-nhận nên phải đo từng cái, và số nền phải là đáp án đã có nhãn
người (105 mục), không phải đáp án đồng thuận model.

### 37.4 Sửa xong: cổng tự nhận 62,5% → 95,3%

Bỏ hẳn `CriticConfirmed` khỏi thang điểm; chấm theo số kiểm tra bằng chứng đã qua
(`ConfidenceForChecks`: 5/5→0,95, 3/5→0,80); mục **không có evidence** rơi về trần 0,60; ứng viên
heuristic giữ trần 0,75.

Đo trên khoá luận, chấm bằng đáp án **có nhãn người** (105 mục):

| | TỰ NHẬN | BẮT NGƯỜI DUYỆT |
|---|---|---|
| Trước | 16 mục, đúng 10 — **62,5%** | 111 mục, đúng 91 — 82,0% |
| Sau | 86 mục, đúng 82 — **95,3%** | 41 mục, đúng 19 — **46,3%** |

Người duyệt **41 thay vì 111** mục (giảm 63% công) mà nhóm tự nhận đáng tin hơn hẳn; nhóm bắt duyệt
giờ đúng nghĩa "chỗ có lỗi".

**Tập heading trả về KHÔNG đổi** (127 mục, cùng index và cấp) — cổng chỉ đổi quyết định
tự-nhận/bắt-duyệt, không đụng P/R/F1/cấp. Đúng phạm vi của thay đổi.

Hai test cũ đổ vì mã hoá đúng hành vi sai. Không sửa test cho khớp code:
* `Independent_critic_reaches_93_...` khẳng định "phản biện một mình đạt 0,93 và được tự nhận" —
  **đảo ngược** khẳng định đó, ghi số đo vào chỗ, đổi tên thành
  `Phan_bien_mot_minh_khong_con_du_de_tu_nhan`.
* `Style_or_heuristic_alone_cannot_claim_93_percent` giữ nguyên Ý ĐỊNH, chỉ đổi bậc 0,85/0,75 →
  ≤ 0,60 vì hai mục đó không có evidence. Chặt hơn bản cũ, cùng chiều với ý định.

### 37.5 Và ngừng phát biểu như một phán quyết khi chưa có holdout

Log cũ: `Cổng precision 93%: 16 tự nhận, 111 cần duyệt (evidence chưa calibration)`. UI dịch
`RequiresReview` thành **"chưa đạt cổng precision"** — một phát biểu về precision, trong khi không
hề có ước lượng precision nào.

Khi `confidenceBasis == evidence_not_calibrated`:
* log: `Xếp theo bằng chứng (chưa calibration bằng holdout): N bằng chứng đủ, M bằng chứng yếu — nên xem.`
* UI: nhãn `bằng chứng đủ` / `bằng chứng yếu — nên xem` thay cho `evidence ≥ mục tiêu` /
  `chưa đạt cổng precision`; con số tin cậy kèm dấu `~` và tooltip nói rõ đây là bậc theo số kiểm
  tra, không phải precision đo được.

Cái giá của việc nói nhầm đo được ở chính phiên này: người dùng thấy 111/127 mục "chưa đạt cổng" và
không còn phân biệt được đâu là chỗ thật sự cần xem.

## 39. Refactor bước 2 — nhãn lặp (spec §6.3c): số không, có lý do đo được

§34.3 đã đo luật "lặp ≥ 3 lần thì không phải heading" và nó hỏng: loại 2 dương tính giả, mất **4 đề
mục thật**. Spec §6.3c thêm hai điều kiện — không mang đánh số, và không có anh em liền kề cùng cấp
— nhắm đúng chỗ luật cũ chết. Cài thành `RepeatedLabelAudit`, cờ `--flag-repeated-labels`, mặc định
tắt (spec: đây là quyết định cấu hình một lần cho cả tập, tuỳ outline dùng để điều hướng hay tái
dựng cấu trúc).

### 39.1 Lượt đo đầu ra số không — và đó là lỗi ĐO, không phải lỗi luật

Tôi đếm số lần lặp trên **tập heading ĐÃ NHẬN**. Nhưng `Nguồn: Facebook` lặp 13 lần trong tài liệu
mà **không lần nào lọt vào kết quả** — mô hình đã bác sạch. Nhóm không bao giờ đủ ngưỡng.

Nhãn cấu trúc là thuộc tính của **TÀI LIỆU**; việc mô hình đã chặn phần lớn không làm nó bớt là nhãn.
Đếm sai tầng thì một luật đúng vẫn cho số không. Đã sửa sang đếm trên toàn tài liệu.

Lần thứ ba trong dự án một phép đo "không đổi gì" hoá ra là lỗi đo chứ không phải lỗi ý tưởng —
cùng họ §27 (thiếu `-ngl`) và §36 (dump cũ). Ba lần đều lộ ra khi hỏi *"vì sao KHÔNG đổi"* thay vì
ghi nhận số không rồi đi tiếp.

### 39.2 Sau khi sửa vẫn số không — lần này là kết quả thật

| Khoá luận, đáp án nhãn người (105 mục) | Mốc | `--flag-repeated-labels` |
|---|--:|--:|
| P / R / F1 | 79,5 / 96,2 / 87,1 | **không đổi** |
| Đúng cấp / cha | 96,0 / 96,0 | **không đổi** |

Truy từng mục:

| đoạn | lặp trong tài liệu | vì sao không bị đánh dấu |
|---|--:|---|
| `Nguồn: Tik Tok` (1063) | **1** | 13 lần lặp là `Nguồn: Facebook`, khác chuỗi |
| `Nguyễn Hà Phương` (114) | 3 | có hàng xóm cùng cấp 1 ⇒ điều kiện 3 bảo vệ |
| `Về ngôn ngữ` | 3 | đúng ý đồ — đề mục thật, được bảo vệ |

Hai giới hạn đo được của luật spec:

1. **So khớp theo chuỗi CHÍNH XÁC quá hẹp.** `Nguồn: Facebook` / `Youtube` / `Tik Tok` là cùng một
   lớp nhãn nhưng ba chuỗi khác nhau. Muốn bắt cần so theo MẪU (`Nguồn: <tên riêng>`), mà đó lại là
   luật đặc ngữ — thứ §34.4 đã ghi là quyết định của người dùng, không phải của tôi.
2. **Điều kiện "không có anh em liền kề cùng cấp" quá lỏng ở cấp 1**, nơi mọi mục front/back matter
   đều cùng cấp nên ai cũng có hàng xóm. Nó bảo vệ luôn cả `Nguyễn Hà Phương`.

Giữ luật, mặc định tắt, cùng lý do §10.4/§25.2/§30.2/§33/§36: số không cũng là số đo.

### 39.3 Bốn luật lớn của phiên Claude gần đây đều ĐÃ CÓ từ trước

| Đề nghị của phiên đó | Trạng thái |
|---|---|
| Bỏ R1 (style → auto_assign 1.0), style sai 51% | đã có — `StyleTrust` §17, `--style-auto-assign` mặc định tắt §10.4 |
| R0 loại caption `Bảng/Hình` bất kể style | đã có — `CaptionRx` |
| R7 TOC làm ground truth ưu tiên cao nhất | đã có — `TableOfContentsAnchor` §22 |
| Bỏ suy level từ style | đã có — `StyleNestingDepths` đọc thứ tự lồng nhau, không đọc con số §28 |

Còn thiếu thật: `is_doubled` (§3.6), kiểm chéo hình dạng anh em, `vn-legal` thành chế độ riêng,
`toc-anchored`, `custom-style`, `w:instrText` (§3.2.3), phân loại bảng ba nhóm (§5.5).

## 40. Refactor bước 3 — cài nốt phần còn lại của spec

Người dùng yêu cầu làm hết rồi đo sau. Mọi thứ cài trong mục này đều **sau cờ, mặc định tắt**, nên
đường mặc định không đổi — đã xác nhận: khoá luận `P 79,5 · R 96,2 · F1 87,1 · cấp 96,0 · cha 96,0`
và bench `100% · 10/10`, trùng khít mốc.

### 40.1 Một mục hoá ra KHÔNG phải khoảng trống

§3.2.3 field code `w:instrText`: đọc code thì thấy SDK map nó thành `FieldCode`, còn vòng lặp gom
text chỉ nhận `Text` (`<w:t>`) — **đã loại sẵn theo cấu trúc**. Không cài gì. Đây là lần thứ ba
trong hai phiên việc kiểm code trước khi viết cứu được một thay đổi thừa.

### 40.2 Đã cài

| Spec | Lớp | Cờ |
|---|---|---|
| §3.6 paragraph hỏng (`is_doubled`) | `CorruptParagraphDetector` | `--skip-corrupt` |
| §5.5 phân loại bảng ba nhóm | `TableRoleClassifier` | `--skip-data-tables` |
| §4.3 chế độ `vn-legal` | `DocumentModeClassifier` | (chẩn đoán) |
| §4.2 chế độ `toc-anchored`, `custom-style` | `DocumentModeClassifier` | (chẩn đoán) |
| kiểm chéo hình dạng anh em | `SiblingShapeAudit` | `--audit-sibling-shape` |

`vn-legal` phải kiểm **TRƯỚC** ký hiệu hành chính: `Điều 5.` cũng khớp mẫu `\d+\.` của lớp hành
chính nên bị bắt nhầm nếu để sau. Trên corpus 95 tài liệu, `vn-legal` khớp **3/3** với bản Python.

Phân loại bảng gom theo *dãy đoạn liên tiếp có `TableDepth > 0`* — xấp xỉ, vì hai bảng kề nhau
không có đoạn ngoài bảng xen giữa sẽ bị gộp làm một. Ghi ra để người sau biết chỗ cần dựng lưới ô
nếu muốn chặt hơn.

### 40.3 Mutation test bắt được một lỗ về THAM SỐ, không phải về hành vi

Hạ ngưỡng `is_doubled` từ 0,55 xuống 0,30 mà **không test nào đổ**. Bốn test đầu chỉ ghim hai đầu —
rõ hỏng và rõ bình thường — nên khoảng giữa bỏ trống. Phải thêm một chuỗi có đúng 33% cặp trùng
(`aabbccddefghijklmnopqrst`) mới ghim được ngưỡng.

Lỗ này đáng nhớ: test đúng HÀNH VI nhưng không đúng THAM SỐ. Với mọi hằng số có ngưỡng, cần một ca
nằm giữa hai bên ngưỡng, không chỉ hai ca ở hai đầu.

### 40.4 Còn nợ

Toàn bộ phần cài trong mục này **chưa có số đo đầu-cuối** — đúng như yêu cầu "làm hết rồi test sau".
Mỗi cờ phải đo riêng trên `key-human.key` + bench trước khi bàn chuyện đổi mặc định.

Và điều lớn nhất vẫn đứng nguyên: **chế độ tài liệu mới là chẩn đoán, chưa luật nào đổi hành vi theo
nó.** Đó là chỗ spec kỳ vọng tạo khác biệt, và cũng là chỗ rủi ro nhất.

## 41. Người dùng chốt định nghĩa outline — và nó cho 100% tuyệt đối

### 41.1 Định nghĩa

Người dùng xác nhận danh sách 68 mục là chuẩn, kèm cột `evidence`. Luật rút ra, **tất định hoàn toàn**:

```
outline = ĐÚNG các đoạn Word đã gán style Heading built-in
cấp     = số gõ tay độ sâu d  →  d + 1      (1.1 sâu 2 ⇒ cấp 3)
        | numPr, không số trong text →  2
        | còn lại                    →  1
```

Kiểm trên chính 68 mục: **tái tạo đúng 68/68 cấp**.

Và tập mục tách sạch tuyệt đối:

| bằng chứng | trong đáp án | mục pipeline trả THÊM |
|---|--:|--:|
| **style Heading** | **68** | **0** |
| `numPr` | 0 | 46 |
| không có gì | 0 | 13 |

Không một mục thừa nào mang style; không một mục đáp án nào thiếu style. Ranh giới trùng khít.

### 41.2 Kết quả

| Khoá luận, đáp án người dùng xác nhận (68 mục) | Pipeline đầy đủ | `--style-outline` |
|---|--:|--:|
| Precision | 53,5% | **100%** |
| Recall | **100%** | **100%** |
| F1 | 69,7% | **100%** |
| Đúng cấp | 41,2% | **100%** |
| Đúng cha | 100% | **100%** |
| Thời gian | 314 s | **1,1 s** |

Pipeline đầy đủ đã có **recall 100% và đúng cha 100%** — nó tìm đủ và dựng cây đúng; chỉ thừa 59 mục
và lệch gốc cấp. Chế độ mới bỏ hẳn phần thừa bằng một điều kiện duy nhất, và không cần mô hình.

### 41.3 Nhưng KHÔNG phổ quát — bench nói ngược lại

| Bench 10 tài liệu | Pipeline đầy đủ | `--style-outline` |
|---|--:|--:|
| P / R / F1 | 100 / 100 / 100 | 92,3 / **69,2** / 79,1 |
| Đúng cấp | 100% | **41,7%** |
| Đạt tuyệt đối | **10/10** | **0/10** |

Bench có 5 tài liệu dựng riêng cho ca style vắng hoặc sai (`02-dinh-dang-thu-cong`,
`06-style-ban-dia`, `08-danh-sach-da-cap`, `09-style-ap-sai`, `10-cap-style-thoai-hoa`), và đáp án
của chúng TÍNH CẢ đề mục không có style.

Nên hai đáp án mã hoá **hai định nghĩa outline khác nhau**, không phải một cái đúng một cái sai:

* **"Tác giả khai gì"** — outline là tuyên bố tường minh qua style. Đúng cho tài liệu soạn chuẩn;
  trên khoá luận cho 100% tuyệt đối trong 1,1 giây.
* **"Cấu trúc thật là gì"** — outline gồm cả đề mục tác giả quên gán style. Đúng cho tài liệu gõ ẩu;
  đó là định nghĩa mà bench và `key-human.key` (105 mục) dùng.

`--style-outline` **mặc định TẮT**. Chọn định nghĩa nào là quyết định sản phẩm, và nó phụ thuộc tập
tài liệu thật của người dùng — nếu phần lớn soạn chuẩn thì chế độ này vừa chính xác hơn vừa nhanh
gấp 280 lần.

### 41.4 Đính chính của tôi

Lượt trước tôi đọc luật cấp là *"con số trong tên style"* — SAI. Trùng khớp là ngẫu nhiên vì tài liệu
này đặt `Heading3` cho mục `x.y`. Luật thật là **độ sâu đánh số + 1**, và chính nó giải thích cái tôi
tưởng là "nhảy cấp": `CHƯƠNG 1` không đánh số nên cấp 1, `1.1` sâu 2 nên cấp 3 — dưới chương không có
mục cấp 2 nào, đúng hình dạng tài liệu chứ không phải lỗi.

## 42. Tài liệu thứ hai, luật khác hẳn — và một hồi quy tôi tự gây ra

Người dùng xác nhận đáp án cho **báo cáo thực tập** (33 mục), và luật khác hẳn khoá luận:

| | Khoá luận (§41) | Báo cáo thực tập |
|---|---|---|
| Chọn mục | style Heading | **`numPr`** + từ khoá chương/front-matter |
| Cấp | số gõ tay `d + 1` | **`ilvl + 1`** |
| Style | tin tuyệt đối | **bỏ qua** — sai 51%, gán cho dòng bìa và khối chữ ký |

Đúng nguyên tắc N1 của spec: *"Không tồn tại một luật deterministic dùng chung."*

### 42.1 Ba luật loại trừ, mỗi luật gỡ một nhóm

| nhóm thừa | dấu hiệu | nguồn |
|---|---|---|
| dòng mục lục (217–245) | style `TOC1`–`TOC9` | khối mục lục ≠ outline |
| `Chương 1:` trong thân bài (296, 303) | style `BodyText` | từ khoá không kích hoạt trên thân bài |
| danh sách nội dung (715–719, 797–803) | `numId=4` | spec §4.3 lọc theo `numId` |

Precision đi **37,7% → 72,5%** sau hai luật đầu.

### 42.2 Lọc `numId` theo ĐỘ DÀI thất bại, theo STYLE thì đúng chiều nhưng quá chặt

Bản đầu lọc `numId` theo độ dài trung bình — KHÔNG tách được, vì `numId=4` có nhiều mục ngắn kéo
trung bình xuống dưới ngưỡng. Đổi sang luật của spec (*"numId nào xuất hiện cùng block có style
Heading với tỉ lệ cao"*): precision lên **100%** nhưng recall tụt xuống **65,5%** — chương 2 dùng một
`numId` khác không đạt ngưỡng 50%.

Ngưỡng cần hiệu chỉnh, chưa xong. Trạng thái hiện tại: `P 100 · R 65,5 · cấp 100 · cha 100`.
**Cấp và cha đúng tuyệt đối** — luật `ilvl + 1` không sai một mục nào; chỉ khâu CHỌN còn dở.

### 42.3 Hồi quy tôi tự gây ra, và cách nó lộ ra

Tôi thêm `RepairInvertedTree` vào đường style để sửa "cây lộn ngược" quan sát được ở báo cáo thực
tập. Nó kéo **khoá luận** từ `cấp 100% / cha 100%` xuống `89,7% / 82,4%`.

Bài học: cây "lộn ngược" ở báo cáo thực tập là **hình dạng thật** của tài liệu đó, không phải lỗi
cần sửa — và tôi đã đem một luật quan sát từ tài liệu này áp lên tài liệu kia. Đúng cái mà N1 cảnh
báo, mắc ngay trong lúc đang cài N1.

Nó chỉ lộ ra vì tôi chạy lại KHOÁ LUẬN sau khi sửa cho BÁO CÁO. Luật rút ra: **mọi thay đổi phải đo
lại trên MỌI tài liệu đã có đáp án, không chỉ tài liệu đang sửa.**

## 43. Khoá theo CẶP (numId, ilvl) — cả hai tài liệu về 100% tuyệt đối

| | `--style-outline` | `--numbering-outline` |
|---|--:|--:|
| Khoá luận (68 mục) | **100 · 100 · 100 · 100** | — |
| Báo cáo thực tập (29 mục) | — | **100 · 100 · 100 · 100** |

*(P · R · đúng cấp · đúng cha)*

### 43.1 Khoá theo `numId` đơn lẻ là SAI — đo được

`numId=4` trên báo cáo thực tập có **21 mục: 10 đề mục thật (ilvl 1–2) + 11 mục nội dung (ilvl 3)**,
dùng chung một danh sách. Khoá ở mức `numId` thì chỉ có hai kết cục, cả hai đều sai:

* ngưỡng chặt (≥80% mang style Heading) → loại cả `numId=4` → **mất trắng 10 đề mục chương 2**,
  recall tụt còn 65,5%;
* ngưỡng lỏng (≥50%) → nhận cả 11 mục nội dung → precision 72,5%.

Khoá theo **cặp `(numId, ilvl)`** tách sạch: giữ `{(3,1),(3,2),(4,1),(4,2)}`, bỏ `(4,3)`.

Tôi đã thử hai ngưỡng ở mức numId và cả hai đều hỏng trước khi nhận ra rằng **đơn vị khoá mới là
chỗ sai**, không phải giá trị ngưỡng. Chỉnh ngưỡng của một luật sai đơn vị thì chỉ đổi được kiểu
hỏng, không sửa được.

### 43.2 Ba luật loại trừ đưa precision 37,7% → 100%

| nhóm | dấu hiệu |
|---|---|
| dòng mục lục (217–245) | style `TOC1`–`TOC9` |
| `Chương 1:` trong thân bài (296, 303) | style `BodyText`, có dấu `:` |
| danh sách nội dung (715–803) | cặp `(4,3)` |

### 43.3 Hai chế độ, hai luật, cùng một khung

```
khoá luận  → style Heading chọn mục, cấp = độ sâu số gõ tay + 1
báo cáo    → (numId, ilvl) chọn mục,  cấp = ilvl + 1, style CHỈ dùng để nhận diện danh sách nào
             là danh sách đề mục — không dùng để chọn từng đoạn
```

Điểm tinh tế đáng ghi: ở báo cáo thực tập style **sai 51% khi chọn từng đoạn**, nhưng vẫn **tin được
khi nhận diện cả một danh sách** — sai lẻ tẻ không kéo nổi tỉ lệ của một cặp `(numId, ilvl)` xuống
dưới 80%. Cùng một tín hiệu, hai mức tin cậy khác nhau tuỳ đơn vị áp dụng.

Cả hai đều **không gọi mô hình** và chạy trong ~1 giây.

## 44. Đối chiếu 13 ca còn thiếu của spec với code — 9/13 đã có

| # | ca | C# |
|---|---|---|
| 1 | bảng layout/content/data (mâu thuẫn X3 ↔ 5.5) | **có** — `TableRoleClassifier` §40 |
| 2 | trang bìa lặp | **có** — `DemoteCoverPageBlock` + X6 |
| 3 | tracked changes `w:del` | **có** — bỏ `DeletedRun` khi gom text |
| 4 | content control `w:sdt` | **có** — `InContentControl` §36 |
| 5 | field code `w:instrText` | **không phải khoảng trống** — SDK map thành `FieldCode`, vòng gom text chỉ nhận `Text` |
| 6 | section break | **có** — `ParagraphWalker` |
| 7 | textbox | **có** — `TextBoxContent` |
| 8 | văn bản quy phạm `Chương/Điều` | **có** — `DocumentMode.VietnameseLegal` §40 |
| 12 | file `.doc` cũ | **có** — `LegacyDocConverter` |
| 9 | numbering reset theo chương | **chưa** |
| 10 | heading bị Enter thật cắt đôi | **chưa** — TODO 6, đã ghi điều kiện mở lại |
| 11 | phụ lục có hệ đánh số riêng | **chưa** |
| 13 | số La Mã thường `i. ii. iii.` | **chưa — và có va chạm, xem dưới** |

### 44.1 Ca 13 không phải "thêm regex" — nó va chạm với lớp chữ cái

`i.` vừa là **La Mã thường số 1**, vừa là **chữ cái thứ 9** trong dãy `a) b) c)`. Hai lớp ký hiệu
giẫm lên nhau ở đúng ký tự đó, và cả `v.` `x.` `l.` `c.` `d.` `m.` cũng vậy.

Nên bật `IgnoreCase` cho mẫu La Mã là tạo ra lỗi mới, không phải sửa lỗi cũ: mọi mục `c.` `d.` `i.`
trong một dãy chữ cái sẽ bị đọc nhầm thành La Mã và nhảy lên cấp 1.

Luật đúng phải nhìn **cả dãy**, không nhìn từng mục: chỉ coi là La Mã thường khi dãy có chứa một
ký hiệu không thể là chữ cái đơn (`ii`, `iii`, `iv`, `vi`…). Đây là quyết định thiết kế, không phải
một dòng regex — và nó chưa được đo trên tài liệu nào.

### 44.2 Ba ca còn lại đều cần tài liệu để đo

Ca 9, 10, 11 chưa gặp trong bất kỳ tài liệu nào đang có đáp án. Theo đúng kỷ luật §10.4, cài luật
cho ca chưa có dữ liệu là thêm mã không kiểm chứng được — điều kiện mở lại đã ghi ở TODO mục 6.

## §45. Chạy 95 file corpus todo10_8 — hai luật mới, một lỗ hổng kiến trúc

### 45.1 Bảng chữ cái tiếng Việt (Nghị định 30/2020)

`NumberingAudit` xếp thứ tự chữ cái bằng `c - 'A' + 1` và regex `[A-Za-z]`. Hai lỗi:
`đ)` **không khớp regex nên vô hình hoàn toàn**; và kể cả khớp thì `d) → đ)` bị tính là nhảy.

Không có một bảng chữ cái cố định nào đúng cho cả hai phía: chọn Latin thì mọi văn bản hành
chính có `đ)` báo đứt quãng sai; chọn tiếng Việt thì mọi tài liệu Latin có `d) e)` báo "thiếu đ)".
Quyết định phải nhìn **cả dãy** — chấm theo ba bảng ứng viên (Latin 26, tiếng Việt 23 quan sát
được, tiếng Việt 29 đầy đủ), lấy bảng có tổng độ hụt nhỏ nhất, hoà thì ưu tiên Latin.

Giá trị token lưu theo **thứ tự hợp nhất** `aăâbcdđeêfghijklmnoôơpqrstuưvwxyz`, đơn điệu tương
thích với cả ba bảng, nên việc cắt dãy ở `CheckSequenceGaps` đúng cho mọi quy ước.

Mutation test: bỏ hai bảng tiếng Việt → 1 test đỏ; bỏ bảng Latin → 1 test đỏ. Cả hai đột biến
đều bị giết.

### 45.2 Lỗ hổng kiến trúc: heading nằm LỌT GIỮA paragraph

Đây là phát hiện lớn nhất, và nó **không phải thiếu regex**.

| | |
|---|--:|
| file là bản chuyển PDF→DOCX | **83/95** |
| mục mà cả đoạn là heading | 2.060 |
| mục phải cắt bên trong đoạn | **4.590 (67%)** |

Toàn bộ pipeline coi paragraph là đơn vị nguyên tử. Với 67% mục của corpus này, giả định đó sai.
`001_Bo_luat_Dan_su` ra **đúng 1 mục trên 151 đoạn**, và mục đó là *tên file PDF*.

`ParagraphHeadingSplitter` (cờ `--split-merged`, **mặc định tắt**) cắt theo dạng "nhãn + số"
tổng quát — cùng hình dạng `LabelledRx`, **không dùng danh sách từ khoá**, nên chạy được cả
`Article 4.` tiếng Anh. Chỉ số paragraph **không đổi**: lát cắt cùng trỏ về một `Index`, vì tách
đoạn thật sẽ làm dịch chỉ số và hỏng mọi đáp án trong `keys/`.

**Chi tiết quyết định nằm ở dữ liệu, không ở luật.** Bản chuyển PDF xoá xuống dòng mà không chèn
dấu cách: `…Bộ luật dân sự1. Bộ luật này…`. Lookbehind `(?<![\p{L}\d])` làm mọi mốc kiểu đó
trượt hết. Nới thành `(?<![\p{Lu}\d])` — cho phép chữ thường đứng trước (dấu hiệu chỗ dán),
vẫn chặn chữ hoa và chữ số. Thứ chặn tham chiếu chéo là **dấu ngắt bắt buộc sau số**:
`Điều 3 của Bộ luật này` không có dấu ngắt nên không bao giờ khớp.

### 45.3 Đo được — ⚠️ **BẢNG NÀY ĐÃ LỖI THỜI, giữ lại để đối chiếu lịch sử**

> Đo TRƯỚC §51, tức thiếu bộ suy cấp tất định. **Số MỤC vẫn đúng** (cờ chỉ đổi cấp, không đổi
> tuyển chọn — đã kiểm ở §51.3), nhưng **phân bố CẤP thì sai**: cấp 9 ghi 221 mục trong khi số
> đúng là 20. Con số còn hiệu lực ở §51.3 và §55.9.

```
95 file, --no-llm            TẮT      BẬT     tăng   file tăng
UNCLASSIFIED      (55)        186     1435   +1249         52
vn-administrative (18)        320      820    +500         18
format-driven      (6)       2454     3169    +715          6
vn-legal           (3)          3       59     +56          3
numpr-driven       (2)        495      558     +63          2
typed-numbering    (1)        244      306     +62          1
insufficient_text (10)         10       10      +0          0
TỔNG                         3712     6357   +2645         82
```

Bench 10 (có đáp án): TẮT và BẬT **giống hệt** — P 92,3 · R 100 · F1 96 · cấp 86,1 · cha 91,7.
Không hồi quy. (Số test ghi lúc đó là 382 — **sai**, xem §50.)

### 45.4 Ba điều KHÔNG được suy ra từ bảng trên

1. **6.357 là số lượng, không phải độ đúng.** 95 file này *không có đáp án*. Bản Python ra 6.858
   nhưng nó cũng chưa được người kiểm. Hai bản cài không có đáp án thì trùng nhau không chứng
   minh cái nào đúng.
2. **`001_Bo_luat_Dan_su` vẫn chỉ 3 mục** trong khi 013 và 015 lên 26 và 30. Nguyên nhân: thân các
   Điều trong 001 là văn xuôi dài **không có khoản đánh số**, nên mốc kết thúc tiêu đề nằm xa hơn
   `MaxHeadingLength = 200` và lát cắt bị loại. Đây là lựa chọn cố ý — thà bỏ còn hơn nhận cả thân
   bài làm nhan đề — nhưng nới nó cần đáp án để đo, chưa có thì không đụng.
3. **Mặc định vẫn tắt.** Cờ này đổi giả định "mỗi đoạn nhiều nhất một mục" mà phần còn lại của
   pipeline và mọi đáp án đang dựa vào. Bật mặc định là đổi hành vi cả tập vì một thể loại.

### 45.5 Trả lời "đã đủ cho mọi văn bản Việt Nam chưa"

**Chưa.** Bằng chứng đo được, không phải phỏng đoán:
- 55/95 file bản Python xếp `UNCLASSIFIED` — hơn nửa corpus không rơi vào chế độ nào.
- 4/9 chế độ (`outlinelvl`, `custom-style`, `semantic-only`, `vn-legal`) **chưa từng chạy có đáp án**.
- Tài liệu ghép nhiều chế độ trong một file vẫn chưa có hướng giải: tầng 1 gán một chế độ cho cả file.
- Ca 13 (La Mã thường `i. ii. iii.`) vẫn treo: `i.` vừa là La Mã 1 vừa là chữ cái thứ 9.

Ba tài liệu **có** đáp án người xác nhận vẫn đạt 100%, nhưng ba tài liệu không đại diện cho
mọi văn bản Việt Nam, và bảng ở 45.3 nói rõ tại sao.

## §46. Bác chẩn đoán "53% tài liệu không có lớp text"

Một phiên khác đọc `outline_all.csv` và kết luận: 50/95 file (53%) là vỏ `.docx` bọc PDF, không
có lớp text, "việc đầu tiên là lấy lại nguồn có text, không phải sửa pipeline". Kèm theo là một
nhánh `no_text_layer` mới trong `tier1_batch.py`.

**Chẩn đoán đó sai.** Đo trực tiếp trên `word/document.xml` của cả 95 file:

| file bị gọi là "không có text" | ký tự text | ảnh |
|---|--:|--:|
| `006_Luat_Dat_dai` | 188.690 | 0 |
| `007_Luat_Nha_o` | 209.042 | 0 |
| `028_WB_RFB_Works` | 793.161 | 0 |
| `041_IBRD_Financial` | 547.714 | 0 |

- **0/95 file thiếu lớp text.** 89 file > 2.000 ký tự, 6 file ít hơn, không file nào < 200.
- **86/95 file không có `w:drawing` nào.**
- Nhánh `no_text_layer` (`len(blocks) <= 3 and n_drawing >= 1`) **thoả 0/95 file** — mã chết.

### 46.1 Lỗi suy luận, không phải lỗi số học

Phiên kia đọc **đầu ra của pipeline** rồi suy ngược về **đầu vào**: pipeline trả về đúng một mục
và mục đó trùng tên file PDF, nên kết luận file rỗng. Sự thật ngược lại — chữ có đủ, pipeline
không tìm ra. Đây là cùng một dữ liệu dẫn tới hai kết luận trái ngược, và chỉ một cách phân biệt
được: mở file gốc ra đếm.

Hệ quả thực tế: lời khuyên "lấy lại nguồn có text" sẽ là công toi trên toàn bộ 50 file.

### 46.2 Bằng chứng ngược từ chính pipeline C#

```
                                       python   C#    C# +split
006_Luat_Dat_dai_31-2024-QH15               1    1        37
007_Luat_Nha_o_27-2023-QH15                 1    1        42
028_WB_RFB_Works_Without_Prequal_2017       1   19       100
```

Mẫu trích từ 006 — tiêu đề sạch, không lẫn thân bài: *Điều 9. Phân loại đất* · *Điều 24. Quyền
tiếp cận thông tin đất đai* · *Điều 28. Nhận quyền sử dụng đất*. Trung vị 60 ký tự, dài nhất 182.

### 46.3 So sánh chất lượng hai bản cài

Phiên kia tự xác định lỗi nặng nhất của họ là "tách heading/body sai 16%".

| bản cài | mục | trung vị | dài nhất | > 300 ký tự | > 1000 |
|---|--:|--:|--:|--:|--:|
| Python | 6.858 | 49 | **4.444** | **16,1%** | 6,9% |
| C# `--split-merged` | 6.357 | 43 | 1.007 | **0,4%** | 0,0% |

Bản C# gần như không có lỗi đó, **do thiết kế chứ không do may**: `MaxHeadingLength = 200` thì
BỎ lát cắt thay vì phát ra. Đây là đánh đổi có chủ ý — mất recall để giữ precision. Bản Python
chọn ngược lại nên sinh heading 4.444 ký tự chứa Điều 4→8 dính liền.

Cùng lý do đó khiến `001_Bo_luat_Dan_su` của tôi dừng ở 3 mục (§45.4 mục 2): thân các Điều là
văn xuôi dài không có khoản đánh số. Đây là **giới hạn đã biết và có chủ đích**, không phải lỗi
chưa thấy.

### 46.4 Điều vẫn đúng từ phiên kia

Ba việc họ nêu vẫn có giá trị và không phụ thuộc chẩn đoán sai:
- **Mẫu số `r_typed` sai** — giáo trình dùng `1.1`/`2.3.1` nhất quán nhưng không dùng style, nên
  mẫu số bằng 0. Cùng dạng lỗi với `r_numpr` trước đây. Đây là lỗi thật.
- **Gạch đầu dòng mặc định là body** trừ khi in đậm.
- **Biến thể tiếng Anh** cho văn bản pháp quy. `ParagraphHeadingSplitter` của C# **đã xử lý sẵn**
  vì mốc là DẠNG "nhãn + số" chứ không phải bảng từ — có test `Article 4.` ghim điều đó.

### 46.5 Kỷ luật rút ra

Thêm vào §10: **không được suy về đầu vào từ đầu ra của chính pipeline đang nghi ngờ.** Pipeline
trả về rỗng có hai nguyên nhân không phân biệt được từ kết quả — đầu vào rỗng, hoặc pipeline hỏng.
Phải mở dữ liệu gốc ra đo. Ở đây khoảng cách giữa hai cách đọc là 50 file và một khuyến nghị
lấy lại toàn bộ nguồn.

## §47. Chạy bản Python đã sửa trên 95 file — dự đoán sai cả hai vế

Dự đoán của phiên kia sau khi sửa: `no_text_layer` tách riêng ~50 file, `UNCLASSIFIED` giảm từ
36% xuống dưới 10%. Chạy thật trên corpus:

```
UNCLASSIFIED       55   57.9%     <- KHÔNG ĐỔI một file nào
vn-administrative  18   18.9%
insufficient_text  10   10.5%
format-driven       6    6.3%
vn-legal            3    3.2%
numpr-driven        2    2.1%
typed-numbering     1    1.1%
no_text_layer       0    0.0%     <- nhánh không bao giờ chạy
```

### 47.1 Vì sao sửa mẫu số `r_typed` không ăn

**Cả 55 file UNCLASSIFIED đều có `n_typed == 0`.** Tử số bằng không thì mẫu số đúng hay sai đều
không đổi kết quả. Bản sửa nhắm vào mẫu số, trong khi thứ hỏng là tử số.

Tử số bằng 0 vì `TYPED.match` đòi mốc ở ĐẦU đoạn. Đo trên đúng 55 file đó:

| | |
|---|--:|
| mốc nằm ở **đầu** đoạn | 1.596 |
| mốc nằm **bên trong** đoạn | **24.220** |

**94% cấu trúc vô hình** với bộ phân loại, vì mọi luật đều neo `^` còn mỗi đoạn dài ~1.900 ký tự.
`006_Luat_Dat_dai`: 0 mốc ở đầu đoạn, 196 mốc bên trong. `062_Lectures_on_Probability`: 0 và 210.

Đây là **cùng một nguyên nhân gốc** với §45.2, chỉ khác nơi biểu hiện: ở tầng trích thì nó làm
mất heading, ở tầng phân loại thì nó làm mất chế độ. Sửa bất cứ ngưỡng nào ở tầng phân loại mà
chưa cắt đoạn đều không thể ăn.

### 47.2 Bộ phân loại C# — 0% "không phân loại" là con số gây hiểu nhầm

Lộ `mode` ra thuộc tính `<doc>` của XML tinh gọn rồi chạy 95 file:

```
FormatDriven               56
VietnameseAdministrative   19
OutlineLevelDriven         10
SemanticOnly                6
VietnameseLegal             4
```

Không file nào ra `Unknown`. Nhưng dòng cuối của `Decide` là
`return formatDiffers ? FormatDriven : SemanticOnly` — **hai nhánh cuối cùng, không phải chẩn
đoán**. Nên **62/95 = 65% tài liệu rơi vào nhánh dự phòng**, tương đương 57,9% UNCLASSIFIED của
bản Python. Cùng một thất bại, chỉ khác nhãn.

Không được báo cáo "C# phân loại được 100%". Nó gán nhãn được 100%; nó nhận dạng được 35%.

### 47.3 Ba lần neo sai vào `tier1_batch.py`

Tôi sửa `tier1_batch.py` bằng cách thay chuỗi lấy từ bản phiên kia dán, và trượt hai lần
(`r_typed = (sum(...TYPED.match...))` chứ không phải dạng họ có; không tồn tại `parse_docx`).
Lần thứ ba tôi đã **báo cáo nhầm kết quả cũ như thể là kết quả có cắt đoạn** — assertion ném ở
giữa nên `EXPLODE` chưa từng chạy, nhưng phần đuôi của lệnh vẫn in ra 57,9% cũ.

Bản trong repo và bản phiên kia sửa là **hai biến thể khác nhau của cùng một file**. Áp diff mù
giữa chúng đúng là lỗi §33/§36 đã ghi. Đã trả file về nguyên trạng.

Kỷ luật bổ sung: khi một lệnh gồm nhiều bước, **bước sửa file và bước đo phải tách rời**, để
bước đo không in ra số liệu của trạng thái mà bước sửa chưa kịp tạo ra.

### 47.4 Việc tiếp theo có giá trị nhất

Cắt đoạn phải chạy **trước** tầng phân loại, không chỉ trước tầng trích. Ở C# hiện tại
`DocumentModeClassifier.Measure` chạy trong `DocxSlimExtractor`, còn `ParagraphHeadingSplitter`
chạy trong `HeaderExtractionPipeline` — tức là bộ phân loại vẫn nhìn đoạn gộp. Đây là thay đổi
kiến trúc thật, và phải đo lại toàn bộ §45.3 sau khi làm.

## §48. Cắt đoạn TRƯỚC tầng phân loại — nửa thành công, và nửa kia phải nói ra

§47.4 kết luận bộ phân loại vẫn nhìn đoạn gộp. Đã sửa: các tỉ lệ THEO MỐC
(`adminRatio`, `legalRatio`, `typedRatio`) nay đo trên **lát cắt** qua
`ParagraphHeadingSplitter.Segments`, còn tín hiệu ĐỊNH DẠNG (style, `numPr`, đậm, cỡ chữ) vẫn đo
trên đoạn thật vì lát cắt không mang định dạng riêng. Chỉ số đoạn **không đổi**, `Mode` chỉ dùng
để in nên đây là thay đổi chẩn đoán thuần — bench giữ nguyên P 92,3 · R 100 · cấp 86,1. (Số test ghi lúc đó là 384 — **sai**, xem §50.)

Thêm đường vào thứ hai cho `TypedNumbering`: `typedCount >= 8 && typedRatio >= 0.08`, không đòi
tài liệu có style Heading — đường cũ `styledCount > 0` khiến 55/55 tài liệu không style không bao
giờ tới được.

### 48.1 Đo được

```
                          TRƯỚC   SAU
VietnameseAdministrative     19    46
VietnameseLegal               4    14
FormatDriven                 56    12
OutlineLevelDriven           10    10
TypedNumbering                0     7
SemanticOnly                  6     6
```

**Nhánh dự phòng (FormatDriven + SemanticOnly): 62/95 = 65% → 18/95 = 19%.**

### 48.2 Kiểm chứng bằng tín hiệu ngoài: thư mục thể loại do người xếp

```
01_phap_quy          25 | VietnameseLegal 14, SemanticOnly 6, VnAdmin 5
02_hop_dong_mua_sam  15 | OutlineLevelDriven 9, VnAdmin 6
03_tai_chinh_ke_toan 15 | VnAdmin 9, TypedNumbering 4, FormatDriven 2
04_giao_trinh        15 | VnAdmin 11, TypedNumbering 3, FormatDriven 1
05_bien_ban_hop      10 | FormatDriven 9, VnAdmin 1
06_dich_song_ngu     10 | VnAdmin 9, OutlineLevelDriven 1
07_system_generated   5 | VnAdmin 5
```

**Chiều tốt:** cả 14 `VietnameseLegal` rơi trọn vào `01_phap_quy`, không lọt ra thư mục nào khác.
Hợp đồng ra `OutlineLevelDriven`, biên bản họp ra `FormatDriven` — đều hợp lý.

**Chiều xấu, và đây mới là điều phải nói ra:** `VietnameseAdministrative` phình từ 19 lên
**46/95 = 48%** và đang nuốt cả giáo trình (11/15) lẫn tài liệu sinh tự động (5/5) lẫn bản dịch
song ngữ (9/10). **Tôi đã đổi một cái sọt quá rộng lấy một cái sọt quá rộng khác.**

### 48.3 Nguyên nhân xác định được, không cần đáp án

`AdministrativeMarkers[0]` là `^\s*\d{1,2}\.\d{1,2}\.?\s` còn `TypedNumber` là
`^\s*\d+(\.\d+)+`. **Hai mẫu này khớp cùng một chuỗi `1.1`.** Trong `Decide`, nhánh hành chính
đứng TRƯỚC nhánh số gõ tay, nên mọi tài liệu mà mốc chính là `1.1`/`2.3.1` — tức toàn bộ giáo
trình — luôn ra `VietnameseAdministrative` và không bao giờ tới được `TypedNumbering`.

Đây là lỗi logic chứng minh được bằng chính hai biểu thức, không cần dữ liệu. Nhưng SỬA nó thì
cần đáp án: phải quyết định tài liệu chỉ có `1.1` thuộc chế độ nào, và không tài liệu nào trong
`keys/` thuộc nhóm đó. Nên tôi dừng ở chỗ ghi nhận, không đoán.

### 48.4 Không được kết luận gì từ §48.1

Không có đáp án chế độ cho 95 file. "19% dự phòng" chỉ nói **luật nào kích hoạt**, không nói
**gán đúng hay sai**. Thư mục thể loại là tín hiệu ngoài yếu — nó xác nhận được `vn-legal` và bác
được `vn-administrative`, thế thôi. Con số duy nhất còn được bảo chứng bằng đáp án người kiểm vẫn
là ba tài liệu ở `keys/`.

## §49. Giả thuyết "tín hiệu rời nhau" — dự đoán đúng một nửa, cài đặt hỏng cả hai lần

Đề xuất: `1.1` xuất hiện ở CẢ hành chính lẫn số gõ tay nên sức phân biệt bằng 0; bỏ nó khỏi
`AdministrativeMarkers` thì hai tập tín hiệu rời nhau và thứ tự nhánh không còn ảnh hưởng. Kèm
sáu dự đoán đặt TRƯỚC khi chạy — đúng cách, vì nhờ vậy nó bác bỏ được.

### 49.1 Kết quả sáu dự đoán

| dự đoán | kết quả |
|---|---|
| `04_giao_trinh` → Typed đa số | ✅ **13/15 Typed** (trước: VnAdmin 11) |
| `05_bien_ban_hop` → không đổi | ✅ FormatDriven 10 |
| `01_phap_quy` → không đổi | ⚠️ Legal 14 và SemanticOnly 6 giữ nguyên, nhưng 5 VnAdmin bay đi |
| `06_dich_song_ngu` → Typed đa số | ❌ **FormatDriven 8**, Typed 1 |
| `03_tai_chinh_ke_toan` → chia đôi | ❌ **không chia** — Typed 13, VnAdmin 0 |
| `07_system_generated` → không chắc | Typed 5 |

Dự đoán quan trọng nhất **đúng**: giáo trình thoát khỏi `VietnameseAdministrative`. Nguyên lý
"tín hiệu dùng chung không mang thông tin phân biệt" được xác nhận.

### 49.2 Nhưng cả hai lần cài đều xoá sổ chế độ hành chính

`VietnameseAdministrative` = **0/95** ở cả hai lần thử, và nhánh dự phòng tăng ngược
19% → **34%**. Nguyên nhân đo được, không phải phỏng đoán:

```
bo 2 mau -> ty le hanh chinh cao nhat tren ca 95 tai lieu = 0,129  <  nguong 0,15
```

`AdministrativeThreshold = 0.15` được hiệu chỉnh cho bộ **BỐN** mẫu (chính docstring của nó ghi
"ngưỡng 0,15 bắt 14/18 tài liệu hành chính"). Bỏ hai mẫu số làm tử số giảm ~10 lần, nên ngưỡng
trở thành **bất khả thi** — không tài liệu nào CÓ THỂ đạt, chứ không phải không tài liệu nào
tình cờ đạt. `VietnameseAdministrative` thành nhánh chết.

Lần thử thứ hai tách vai trò (La Mã/chữ cái làm bộ PHÂN BIỆT, bộ bốn mẫu làm thước đo ĐỘ MẠNH,
giữ nguyên ngưỡng) cho phân bố **y hệt** — nên điều kiện `adminCount >= 3` mới là chỗ chặn, chứ
không phải ngưỡng tỉ lệ. Cả hai lần đều không dùng được.

### 49.3 Bài học: đổi định nghĩa không bao giờ miễn phí

Lập luận "đây là câu hỏi định nghĩa, trả lời từ spec, không cần đáp án" **đúng một nửa**:

- Phần **định nghĩa** đúng, và dự đoán giáo trình chứng minh điều đó.
- Phần **ngưỡng** vẫn là thực nghiệm. Mọi hằng số đã hiệu chỉnh đều sống trên một THANG do chính
  tập tín hiệu quy định. Đổi tập tín hiệu là đổi thang, tức làm mọi hằng số phía sau mất hiệu
  lực — kể cả khi định nghĩa mới đúng hơn định nghĩa cũ.

Nên "sửa định nghĩa" và "giữ nguyên ngưỡng" là **hai biến đổi cùng lúc**, dù nhìn như một.

### 49.4 Đã trả về trạng thái đã đo

`git checkout` `DocumentModeClassifier.cs` về `8258036`: VnAdmin 46, Legal 14, FormatDriven 12,
OutlineLevelDriven 10, Typed 7, SemanticOnly 6. (Số test ghi lúc đó là 384 — **sai**, xem §50.)
Trạng thái này vẫn có khuyết tật
đã ghi ở §48.2 (VnAdmin quá rộng), nhưng nó là khuyết tật ĐÃ ĐO, còn trạng thái kia là nhánh chết.

**Thứ mở khoá:** ba file giáo trình có nhãn chế độ người kiểm. Có chúng thì hiệu chỉnh lại được
ngưỡng trên tập tín hiệu mới, và giả thuyết §49 dùng được ngay — nó đã đúng ở phần khó nhất.

## §50. Kiểm lại chính mình: ba mảng mã KHÔNG có test, và một con số test sai

Câu hỏi "đã test hết mã cần test chưa" — trả lời sau khi đối chiếu, không phải sau khi nhớ lại.

### 50.1 Số test tôi báo cáo là sai

`dotnet test` gia tăng cho **384**; `dotnet clean` rồi chạy lại cho **397**. Đếm trong mã nguồn
test: 311 `[Fact]` + 85 `[InlineData]` ≈ 397. Vậy **397 mới đúng**, và mọi lần tôi ghi "384 test
xanh" đều chạy trên assembly thiếu 13 test.

**Tôi không truy ra được nguyên nhân chắc chắn** (working tree sạch, không commit nào thêm test
giữa hai lần chạy). Không đoán — đó đúng là lỗi §46. Nhưng hệ quả thì chắc chắn: *"384 test xanh"
không phải bằng chứng toàn bộ suite đã chạy.*

**Kỷ luật mới:** con số test dùng để kết luận phải lấy từ **build sạch**, và đối chiếu với số
`[Fact]`/`[InlineData]` đếm trong mã nguồn. Hai nguồn lệch nhau là dấu hiệu assembly cũ.

### 50.2 Ba mảng mã mới KHÔNG có một test nào

| mã | đã tạo ra | test trước khi kiểm |
|---|---|--:|
| `ParagraphHeadingSplitter.Segments()` | mọi con số §48 | **0** |
| cờ `--split-merged` + `MergedParagraphHeadings` | 3.712 → 6.357 mục (§45.3) | **0** |
| `mode=` trong `SlimXmlSerializer` | mọi bảng §47.2, §48.1 | **0** |

Đáng chú ý ở dòng thứ ba: chỗ đó **đã từng làm đỏ một test** (`doc.Mode.Mode` ném null với
`SlimDocument` dựng tay), tôi sửa null-safe rồi **không ghim lại**. Đúng lỗi ấy có thể tái phát
âm thầm.

Đã viết bù **17 test**, tổng **414**, build sạch. Bốn đột biến đều bị giết:
bỏ lát đầu tiên của `Segments` → 2 đỏ · `Segments` luôn trả rỗng → 7 đỏ ·
`--split-merged` mặc định bật → 1 đỏ · `mode=` luôn in `Unknown` → 1 đỏ.

### 50.3 Khuyết tật mục 11 nay là test chạy được, không còn là ghi chú

`DocumentModeTests.So_go_tay_thuan_bi_nhan_nham_thanh_hanh_chinh_KHUYET_TAT_DA_BIET` dựng tài liệu
thuần số gõ tay (20 đề mục `N.1`/`N.2`, 0 style Heading) và **assert giá trị SAI hiện tại**
(`VietnameseAdministrative`) kèm ghi rõ giá trị đúng phải là `TypedNumbering`.

Khi ai đó sửa mục 11, test này sẽ đỏ — đó là mục đích. Kèm theo là
`Ky_hieu_rieng_cua_hanh_chinh_khong_duoc_mat_khi_sua_muc_11`: nếu sửa bằng cách **đảo thứ tự
nhánh** thay vì tách tín hiệu, test đó đỏ và chỉ ra rằng sai lầm chỉ bị lật sang chiều kia (§49.2).

Viết khuyết tật thành test có giá trị hơn viết vào TODO: TODO không chạy.

### 50.4 Đối chiếu hằng số tài liệu ↔ mã nguồn

`MaxHeadingLength = 200` ✓ · `AdministrativeThreshold = 0.15` ✓ · `TypedNumberMinimum = 8` ✓ ·
`AdministrativeMarkers` 4 mẫu ✓. Không có chỗ nào tài liệu nói một đằng mã làm một nẻo.

### 50.5 Điều vẫn CHƯA test được, và vì sao

- **Hai đáp án 100% ở `keys/`** — tài liệu `.docx` không nằm trong repo (ràng buộc không để tài
  liệu người dùng lọt vào git). Chỉ chạy tay được.
- **`keys/plph1-dqp.outline`** (41 mục) — thiếu file nguồn, chưa chấm lần nào.
- **Mọi con số trên corpus 95 file** — không có đáp án, nên không thể thành test hồi quy. Chúng là
  phép đo một lần, không phải bất biến.

## §51. Bộ suy cấp tất định không chạy trên đường `--no-llm` — sửa, đúng cấp 86,1% → 100%

### 51.1 Lỗi

`StructuralHierarchyResolver.Apply` và `TableOfContentsAnchor.Apply` **đều tất định và không cần
mô hình**, nhưng cả hai nằm bên trong `RunModelAsync`. Đường `--no-llm` đi qua `HeuristicOnly` nên
**chưa bao giờ chạy chúng**. Bất đối xứng này là tai nạn vị trí mã, không phải lựa chọn thiết kế.

Hệ quả: **mọi con số `--no-llm` từ trước tới nay đều thiếu bước suy cấp** — kể cả toàn bộ loạt
95 file §45–§48, và kể cả bảng phân bố cấp ở §45.3.

### 51.2 Cách tìm ra, và một test suýt đánh lừa tôi

`bench/02-dinh-dang-thu-cong` có đúng cấp **28,6%**: 5/7 mục nông hơn đáp án đúng một cấp. Cấu
trúc là `PHẦN I → 1. → 1.1.`, đáp án `0→1, 2→2, 4→3, 6→3, 8→2, 10→1, 12→2`.

Tôi viết test cô lập dựng lại đúng tài liệu đó rồi gọi `StructuralHierarchyResolver.Apply` —
**test XANH ngay lượt đầu**. Nếu dừng ở đó thì kết luận "resolver không có lỗi" và bỏ qua lỗi thật.
Test cô lập gọi thẳng thành phần nên nó không đi qua chỗ hỏng; **chỗ hỏng là đường nối, không phải
thành phần**. Đã giữ cả hai test lại và ghi rõ điều này trong `NhanLabelledLamChaTests`.

### 51.3 Đo được — có đáp án, một biến số

```
bench (7 tài liệu có đáp án)      TẮT      BẬT
đúng cấp                        86,1%    100%
đúng cha                        91,7%    100%
tài liệu đạt tuyệt đối            5/7      6/7
precision                       92,3%   92,3%   (không đổi)
```

Trên 95 file: chạy hết 95/95, **số mục không đổi (6.357 cả hai)** — đúng bất biến mong đợi, cờ chỉ
đổi CẤP chứ không đổi tuyển chọn. Phân bố cấp 9 sập từ **221 → 20**; 221 mục đụng trần clamp là
dấu hiệu hỏng, 20 hợp lý hơn. Nhưng 95 file **không có đáp án**, nên đây là "hợp lý hơn", không
phải "đúng hơn".

### 51.4 Vì sao cờ này MẶC ĐỊNH BẬT, khác mọi cờ mới khác

§10.4 cấm lật mặc định CHỈ vì bench. Ở đây lý do khác:

- `StructuralHierarchyResolver` **không phải mã chưa kiểm chứng** — nó có bằng chứng đáp án NGƯỜI
  KIỂM từ §31 (đúng cấp 81,1% → 91,5% trên khoá luận thật).
- Đường có mô hình **đã chạy nó vô điều kiện** từ trước.
- Bật cho `--no-llm` là **sửa bất đối xứng**, không phải thêm một suy đoán mới.

Vẫn giữ `--no-deterministic-hierarchy` để đối chứng, và một test ghim mặc định BẬT để không ai tắt
nhầm khi dọn dẹp.

### 51.5 Tài liệu bench còn lại chưa đạt, và vì sao KHÔNG sửa

`04-bia-muc-luc-chu-thich`: 3 dương tính giả ở đoạn 0, 1, 2 — dòng tiêu ngữ trang bìa
(`BỘ KHOA HỌC VÀ CÔNG NGHỆ`, `VIỆN NGHIÊN CỨU ỨNG DỤNG`, `Hà Nội, tháng 6 năm 2026`).

Không sửa được sạch: `MỤC LỤC` ở đoạn 3 **cũng đậm, cũng in hoa, cũng canh giữa, cũng không có
`outlineLvl`** — mà nó là mục ĐÚNG trong đáp án. Không có tín hiệu cấu trúc nào tách được hai
nhóm. Tách bằng từ khoá thì vi phạm ràng buộc "không danh sách từ tiếng Việt trong luật"; tách
bằng cỡ chữ (14 với 13) là fit vào đúng một tài liệu bench.

Đây là TODO mục 1, §12 đã kết luận cần thêm tài liệu thật chứ không phải thêm luật. Giữ nguyên.

### 51.6 Điều này KHÔNG chứng minh

Bench có 7 tài liệu **sinh tự động**. "Đúng cấp 100%" nghĩa là 100% trên 7 tài liệu đó, cộng với
việc bộ suy cấp đã có bằng chứng độc lập trên một khoá luận thật. Nó **không** nghĩa là đúng 100%
trên mọi văn bản Việt Nam — 95 file corpus vẫn không có đáp án, và §45.5 vẫn đúng nguyên văn.

---

## §52. `dhx toc-keys` — mục lục Word là đáp án miễn phí, nhưng phải khớp với thân bài chứ không phải với chính pipeline

### 52.1 Vì sao cần

Bench có 7 tài liệu, `keys/` có 3 đáp án người kiểm. Mọi con số trong handoff đứng trên nền đó.
Mục lục do Word tự sinh (`w:hyperlink` neo `_Toc`, hoặc style `TOC1..TOC9`) là **tuyên bố của chính
tác giả** về heading + cấp — một nguồn đáp án gần như miễn phí, nếu khớp được đúng cách.

**Bẫy phải tránh:** khớp mục lục với chính đầu ra của pipeline (headings pipeline đã chọn) không đo
được gì độc lập — đúng nguyên tắc "không suy về ĐẦU VÀO từ ĐẦU RA của pipeline đang nghi ngờ" đã ghi
ở đầu TODO.md (§46.5). `TocAnswerKeyGenerator` (`Core/Eval/`) vì vậy khớp mục lục với **toàn bộ**
`SlimDocument.Paragraphs`, không lọc qua `Role`/`IsCandidate`.

### 52.2 Thiết kế

- File đạt ≥80% khớp mới ghi `.key`, dùng `@stableId` (không dùng index số — tránh lệch khi đổi tuỳ
  chọn trích xuất, cùng lý do `review-key` đã chọn `stableId`).
- Dùng chung `Normalize`/`DepthOf` với `TableOfContentsAnchor` đã có — một nguồn chuẩn hoá, không
  nhân đôi logic đã kiểm chứng.
- Ghi vào `keys/toc-derived/`, tách khỏi đáp án người kiểm; luôn đánh dấu `toc_derived`, không được
  lẫn với `keys/*.key`.

### 52.3 Một bug tự phát hiện khi chạy thử: "Danh mục hình ảnh" lẫn vào mục lục chương

Chạy trên chính tài liệu nguồn của `keys/bao-cao-thuc-tap.key`: chỉ 15/32 "mục lục" khớp (47%).
Soi bằng cờ `-v` mới thêm: **13/15 mục không tìm thấy đều là `Hình 1.1:`/`Bảng 1.2:`** — chú thích
hình/bảng, không phải đề mục. Word đánh dấu "Danh mục hình ảnh"/"Danh mục bảng biểu" bằng ĐÚNG cơ
chế TOC field/hyperlink `_Toc` như mục lục chương thật, nên `InTableOfContents` không phân biệt được
hai loại — 13 mục sai này pha loãng sai mẫu số, không phải lỗi khớp text.

Sửa: loại trước bằng `HeadingHeuristics.CaptionRx` đã có sẵn (đổi `private` → `internal`, không viết
lại luật). Sau khi lọc: 15/17 khớp (88%) — vượt ngưỡng 80%, `Accepted`.

### 52.4 Vẫn còn 0/95 file corpus `todo10_8` đạt ngưỡng — không phải lỗi công cụ

Chạy `dhx toc-keys` trên `todo10_8/heading_corpus_95_word` (95 file, không commit tài liệu gốc):
0/95 đạt 80%. Lý do đã xác minh, không phải bug: **86/95 là bản PDF→DOCX**, không giữ mục lục Word
thật; 9 file `.docx` gốc còn lại là mẫu hợp đồng World Bank có tiêu đề lặp lại nhiều nơi ("Request
for Proposals", "Phạm vi áp dụng"…) nên phần lớn bị loại vì **mơ hồ** (nhiều đoạn thân bài cùng
chuẩn hoá về một chuỗi) — đúng thiết kế: bỏ khi không rõ đoạn nào đúng, không đoán đại. Corpus này
không phù hợp để mở rộng bench bằng đường này; cần tài liệu văn xuôi có mục lục Word thật.

---

## §53. `TableOfContentsAnchor.Apply` pin sai cấp cho heading `numPr`-driven — sửa, đúng cấp +51,8 điểm (một biến, sau §51)

### 53.1 Phát hiện

Xác thực `dhx toc-keys` ở §52 xong, đối chiếu `keys/bao-cao-thuc-tap.key` phát hiện thêm một lớp
lỗi khác hẳn: `TableOfContentsAnchor.DepthOf` chỉ đọc SỐ nằm trong **TEXT** của dòng mục lục
(`"1.1. Giới thiệu…"` → depth 2). Nhưng heading `numPr`-driven (numbering do Word **vẽ**, không gõ
tay) không để số nào trong TEXT — dòng mục lục hiển thị `"Giới thiệu chung về Ngân hàng… 2"` (số 2
cuối là SỐ TRANG), số cấp thật (`"1.1"`) chỉ còn ở `SlimParagraph.NumberLabel`, một trường resolve
riêng từ `numbering.xml`. `DepthOf` không đọc trường đó → mặc định các mục này về cấp 1.

### 53.2 Test cô lập trước khi sửa

Dựng tài liệu tổng hợp tối thiểu tái hiện đúng điều kiện (mục lục mang `numPr` nhưng TEXT không có
số), gán heading đã ĐÚNG cấp 2 từ nguồn khác (mô phỏng `numPr`), rồi gọi thẳng
`TableOfContentsAnchor.Apply` — không đoán. Kết quả: `Apply` ghi đè cấp 2 đúng thành cấp 1 sai, đúng
cơ chế "mục lục phải nói lời cuối" mà chính code đã ghi chú (đứng trên mọi nguồn cấp khác trong
`HeaderExtractionPipeline`). Cơ chế lỗi có thật, tái lập 100% — `TableOfContentsAnchorNumberLabelTests`.

### 53.3 Sửa

`Apply` nay ưu tiên `NumberLabel` ("1.1.1" → đếm số đoạn cách nhau bởi dấu chấm → cấp 3), chỉ rơi về
đọc TEXT khi đoạn không mang `numPr`. Hàm dùng chung (`DepthFromNumberLabel`) đặt trong
`TableOfContentsAnchor`, cả nó lẫn `TocAnswerKeyGenerator` gọi cùng một chỗ.

### 53.4 Đo tác động, lần 1 — MẤT HIỆU LỰC THAM CHIẾU

Lượt đo đầu tiên (`dhx eval --no-llm` trên bench và trên báo cáo thực tập thật): **byte-identical
trước/sau bản sửa**, log không in dòng "Mục lục pin lại N cấp" ở cả hai lượt. Kết luận lúc đó:
`Apply` không chạm heading nào trên đường `--no-llm`, nên bản sửa "chưa đo được tác động thật".

Kết luận ấy **đúng với dữ kiện lúc đó** — nhưng dữ kiện sai. §51 (viết sau, cùng phiên) phát hiện
`TableOfContentsAnchor.Apply` **chưa từng được gọi trên đường `--no-llm`** trước khi sửa (nằm trong
`RunModelAsync`). "Byte-identical" chỉ phản ánh việc CẢ HAI lượt đều không gọi `Apply` — không nói
được gì về bản sửa `NumberLabel`. Bài học: một phép đo "không đổi gì" không tự động là bằng chứng an
toàn; phải biết chắc đường đo có thật sự đi qua đoạn code vừa sửa hay không (đúng tinh thần §33.3 —
dump dùng để suy luận phải sinh lại đúng cờ, mở rộng thêm: **đường đo phải được xác nhận có chạm tới
code đang đo**, không chỉ tin ở việc input/config giống nhau).

### 53.5 Đo lại sau §51 — một biến sạch

Giữ §51 cố định (đã có sẵn trong HEAD), chỉ cô lập RIÊNG đóng góp của bản sửa `NumberLabel`: tạm đổi
một dòng (`DepthFromNumberLabel(p.NumberLabel) ?? DepthOf(p.Text)` → `DepthOf(p.Text)`), build, đo,
rồi hoàn nguyên — không để lại trong lịch sử git.

```
báo cáo thực tập MBBank thật, --no-llm     đúng cấp    sai cấp
§51 + DepthOf cũ (không NumberLabel)        44,8%         16
§51 + NumberLabel (bản sửa)                 96,6%          1
```

**+51,8 điểm đúng cấp**, một biến, giữ mọi thứ khác cố định. Log giờ in thật `"Mục lục của tài liệu
pin lại 11 cấp"` và `"Hậu xử lý hierarchy (không mô hình): sửa 15 cấp"` — xác nhận cả
`TableOfContentsAnchor` lẫn `StructuralHierarchyResolver` đều chạy, không còn nghi ngờ như 53.4.

1 lỗi "sai cấp" còn lại (`i=701`, "Chức năng nhiệm vụ của từng vị trí") khớp đúng mục mà `dhx
toc-keys` (§52) từng báo "không tìm thấy" khi xác thực — nhiều khả năng cùng loại lệch nhẹ
TOC-vs-thân-bài như ca "CHƯƠNG 2" ở 53.6, chưa điều tra sâu thêm.

### 53.6 Tác dụng phụ: 12 lỗi "sai cấp" ban đầu tưởng là bug mới, hoá ra là thiếu cờ

Trước khi hiểu ra §53.4, đã điều tra 12 lỗi "trả về 2, đáp án 3" xuất hiện ở lượt đo đầu — nghi là
bug mới. Hoá ra lượt đo đó **thiếu cờ `--style-trust`**; bật lên thì đúng 12 lỗi biến mất (cơ chế
đối chiếu style-vs-độ-sâu-đánh-số ở §17 đã xử lý đúng). Nhưng bật `--style-trust` lại lộ ra **14 lỗi
khác** (front-matter/chương cấp 1 và mục cấp 2 bị đẩy +1 đều) — log tự in `"12/18 lệch so với độ sâu
đánh số (67%) ⇒ quyền chọn HẠ, quyền gán cấp HẠ"`, đúng nguyên văn lớp lỗi "hạ quyền style là chuyển
quyền cho một chỗ trống" đã ghi ở TODO mục 2/§13. Không phải bug mới — là bằng chứng thật thứ hai
(ngoài fixture tổng hợp `10-cap-style-thoai-hoa`) cho một vấn đề đã biết, còn chờ đo bằng LLM.

Cũng phát hiện luôn: TOC ghi `"CHƯƠNG 2. GIỚI THIỆU CÁC DỊCH VỤ…"` trong khi thân bài ghi `"…CÁC SẢN
PHẨM DỊCH VỤ…"` — **đúng nguyên văn ca "TOC lỗi thời" đã ghi ở §12** của spec cũ, công cụ tự tìm lại
được một cách độc lập trên chính tài liệu đã sinh ra phát hiện gốc.

### 53.7 Còn treo

Đo trên đường ĐẦY ĐỦ (có LLM, và/hoặc `--style-trust` sau §51) cần đúng Qwen3.5-9B — máy này chỉ có
Qwen2.5-7B/Llama-3.2-3B. Gộp chung với TODO mục 2 và nửa sau mục 7 (§54) — một lượt LLM trả lời được
cả ba câu hỏi nếu tách đúng biến.

---

## §54. `StructuralRecovery` nhận token `Labelled` — cứu được `PHỤ LỤC 1`/`PHỤ LỤC 2`

### 54.1 Vì sao

TODO mục 7: 4 đề mục thật bị đánh rơi ở tầng ứng viên trên khoá luận thật — `1294`, `1335` (`PHỤ LỤC
1`, `PHỤ LỤC 2`) có `role=Normal`, chưa từng tới được mô hình. Cờ `--bare-labels` (TODO mục 3) đã đọc
được chúng thành `NumberKind.Labelled`, nhưng token đó trước đây chỉ nuôi `HasStructuralEvidence` —
chỉ dùng để cứu đoạn ĐÃ TỪNG là ứng viên bị mô hình gắn nhãn sai `DocumentTitle`. `PHỤ LỤC 1`/`PHỤ
LỤC 2` chưa từng là ứng viên nên đường cứu đó không chạm tới — token đọc được nhưng không có tác dụng.

### 54.2 Sửa

Mở rộng `StructuralRecovery.Find` — trước đây chỉ nhận đường dẫn số Ả Rập nhiều cấp (`3.1` → `3.2`)
— sang cả `NumberKind.Labelled`. Khái quát hoá `IsNextSibling` thành một cặp `(nhóm anh em, giá trị
thứ tự)` dùng chung cho cả hai loại, thay vì viết lại logic cứu-anh-em riêng cho Labelled.

Không cần ràng buộc "độ sâu ≥ 2" như đường Ả Rập (ràng buộc đó tồn tại để tránh cứu nhầm dòng số
liệu hành chính mở đầu bằng số trần, xem `Khong_cuu_danh_so_mot_cap`): nhãn+số đòi một TỪ nhãn thật
đứng trước con số, nên đã tự loại trừ rủi ro đó theo đúng cấu trúc của chính `LabelledRx`/`BareLabelledRx`.

### 54.3 Đo được

5 test mới khoá lại: cứu `PHỤ LỤC 2` khi `PHỤ LỤC 1` đã nhận; dây chuyền `PHỤ LỤC 2 → 3`; khác nhãn
thì không cứu; dạng có tiêu đề (`Chương 1. Mở đầu`) không cần cờ `--bare-labels`; không bật cờ thì
giữ nguyên hành vi cũ (không cứu). `dhx eval bench --no-llm`: byte-identical trước/sau — không hồi
quy, nhưng bench không có fixture nhãn+số trần nên không đo thêm được gì từ đó.

### 54.4 Còn treo

**Chỉ nửa đầu của TODO mục 7 được giải quyết.** Hai mục còn lại (`1239`, `1256` — kết thúc bằng `:`
và là item bullet nên bị trừ điểm hai lần) **chưa đụng tới, chưa có hướng cụ thể**. Và "Cách nghiệm
thu" gốc của mục 7 đòi đo recall trên `key-human.key` — cần LLM, gộp vào §53.7.

## §55. Rà việc còn lại: hai việc đo ra là KHÔNG đáng làm, và một crash tôi tự tạo ra

### 55.1 Hai việc "làm được ngay" — đo trước khi cài, và cả hai đều dừng

`TODO` mục 6 (heading trải hai paragraph) ghi điều kiện mở lại là "đo được tần suất thật".
§45.2 phát hiện Nghị định 30/2020 **bắt buộc** dạng này nên tưởng đã đủ căn cứ. Đo:

```
đoạn CHỈ chứa "Chương II" (không kèm tiêu đề):  0 trên 0/95 file
```

**0/95.** Vì 83/95 file là bản chuyển PDF đã gộp hết, `Chương II` không bao giờ đứng riêng một
đoạn. Điều kiện mở lại KHÔNG thoả — mục 6 giữ đóng, giờ có con số thay cho "chưa có bằng chứng".

`TODO` mục 12 (La Mã thường `i. ii. iii.`):

```
lát cắt bắt đầu bằng La Mã thường: 19 trên 12/95 file
trong đó KHÔNG THỂ là chữ cái đơn (ii, iii, iv…): 5
```

19 mục trên 6.357, chỉ 5 mục chắc chắn. Đổi lại là rủi ro đọc nhầm `c.`/`d.`/`i.` trong dãy chữ
cái vốn có 601 mục. **Không đáng.** Giữ nguyên, cũng đã có con số.

*Ghi nhận cách làm:* đo tần suất TRƯỚC khi cài đã chặn hai lần viết mã vô ích. Cả hai lần trực
giác đều nói "đáng làm".

### 55.2 Nhãn + số KHÔNG có dấu ngắt — lỗi thật, tìm ra nhờ đo lại

§51 ghi rằng mọi bảng `--no-llm` trước đó đã lỗi thời. Đo lại thì lộ ra:
**cả 2.645 mục sinh từ đoạn gộp đều nằm cấp 1**, cấp 2..9 giống hệt hai lượt.

Truy tới `082_Bo_luat_Lao_dong_2019_EN`: **26 `Chapter` + 221 `Article`, tất cả cấp 1.** Không tài
liệu nào có 26 chương và 221 điều mà chỉ một cấp — bất biến này đúng mà không cần đáp án.

Nguyên nhân: `Chapter II EMPLOYMENT AND RECRUITMENT` **không có dấu ngắt sau số La Mã**, mà
`LabelledRx` bắt buộc `[\.\):\-–]`. Nên `Chapter` không parse → tài liệu chỉ còn MỘT chữ ký →
`SignatureTiers` đòi ≥2 chữ ký mới suy được lồng nhau → không làm gì.

Đây chính là dạng hai dòng của Nghị định 30 **bị bản chuyển PDF dán liền** — tức mục 6 không hề
vô nghĩa, nó chỉ biểu hiện ở hình dạng khác hoàn toàn với hình dạng mục 6 mô tả.

Sửa: cho phép **dấu cách** làm phân cách, với chốt chặn phần còn lại phải bắt đầu bằng **chữ HOA**.
Không có chốt đó thì `Điều 3 của Bộ luật này` và `khoản 2 Điều này` bị nhận thành đề mục. Chốt là
tín hiệu cấu trúc, không phải danh sách từ.

### 55.3 Crash tiềm ẩn do chính §51 tạo ra

Lát cắt của `--split-merged` **dùng chung một `Index`** (chủ đích, để đáp án trong `keys/` không
hỏng vì dịch chỉ số). `StructuralHierarchyResolver.Apply` mở đầu bằng
`ordered.ToDictionary(h => h.Index, …)` — trùng khoá thì **`ArgumentException`**.

Trước §51 hai thứ này không bao giờ gặp nhau: bộ suy cấp chỉ chạy ở đường có mô hình, còn
`--split-merged` dùng với `--no-llm`. **Lật mặc định ở §51 đã ghép chúng lại.**

Trên corpus nó chưa nổ vì mỗi đoạn gộp chỉ cho ra một lát đủ điều kiện làm tiêu đề (082: 300 mục /
300 chỉ số phân biệt). Nhưng "chưa nổ trên tập đang đo" không phải "không nổ" — test gọi trực tiếp
với hai lát cùng chỉ số thì ném ngay.

Sửa: khoá theo **tham chiếu `HeadingRecord`** thay vì theo `Index`, ở cả `paths`, `SignatureTiers`
và `StyleNestingDepths`. Hai bảng sau dùng indexer nên **không ném mà GHI ĐÈ** — hai lát cùng
`Index` khác chữ ký (`Chương I` và `Điều 1` cùng đoạn) nhận chung một tầng, sai không dấu hiệu.
Khoá theo tham chiếu cũng đúng nghĩa hơn: hai lát có text khác nhau thì phải có đường dẫn khác nhau.

### 55.4 Đo được

```
bench (có đáp án)   P 92,3 · R 100 · F1 96 · cấp 100% · cha 100% · 6/7   (không đổi — guard giữ)
95 file             6.357 mục (KHÔNG đổi) · cấp>1: 2.029 → 2.045
082 (26 Ch + 221 Art)   cấp>1: 13 → 21
9/95 file đổi cấp, 0 file mất mục, 0 crash
426 test xanh (build sạch)
```

Mutation: khoá lại theo `Index` → 1 đỏ · bỏ nhánh không-dấu-ngắt → 3 đỏ. Cả hai bị giết.

### 55.5 Một mutation test đầu tiên SỐNG SÓT, và đó là điều đáng ghi

Lượt mutation đầu tôi viết đột biến `h => h` cho `paths` — vẫn là khoá tham chiếu, nên **không phải
đột biến**. Nó "sống sót" vì tôi đột biến sai, không vì test yếu. Còn đột biến thứ hai thì sống sót
**thật**: tôi chưa có test nào cho dạng `Chương II QUY ĐỊNH CHUNG`. Đã bù 8 test, trong đó ba test
tham chiếu chéo là thứ giết đột biến "bỏ lookahead `\p{Lu}`".

Bài học: mutation sống sót có hai nguyên nhân — test yếu, hoặc đột biến không thật. Phải phân biệt
trước khi kết luận, nếu không thì hoặc bỏ qua lỗ hổng thật, hoặc đi viết test cho một thứ không hỏng.

### 55.6 Va chạm số hiệu mục: hai §52

Phiên khác đã đẩy §52–§54 (`dhx toc-keys`, sửa `TableOfContentsAnchor`, `StructuralRecovery` nhận
`Labelled`) trước khi tôi commit. Tôi thêm một §52 nữa, và **cả hai đều có tiểu mục 52.1–52.4**,
nên mọi tham chiếu thành nhập nhằng. Đã đánh số lại phần của tôi thành **§55** và sửa các chỗ trỏ
tới trong `TODO.md` và chú thích mã.

Kỷ luật: trước khi đánh số mục mới trong handoff, đọc `grep -oE "^## §[0-9]+" handoff.md | sort |
uniq -c` — nhánh có thể đã tiến lên trong lúc mình làm.

### 55.7 Nới `LabelledRx` suýt phá đúng thứ chú thích gốc đã cảnh báo

Chú thích ở đầu `NumberingAudit` viết sẵn từ trước: *"đòi dấu ngắt tường minh và đòi phần còn lại
bắt đầu bằng CHỮ — nếu không, `Bảng 1.2 Đối chiếu…` sẽ tách thành nhãn `Bảng` + số 1 và hậu kiểm
đi báo thiếu những mục không tồn tại."*

Nới nhánh không-dấu-ngắt đã phá đúng bảo đảm đó. Đo được sau khi nới:

```
Bảng 3 Thống kê số liệu       → Labelled   ✗
Hình 2 Sơ đồ tổng thể         → Labelled   ✗
Table 5 Summary Of Results    → Labelled   ✗
Figure 1 System Architecture  → Labelled   ✗
```

**Nguy hiểm hơn vẻ ngoài:** §54 vừa cho `StructuralRecovery` cứu MỌI đoạn có token `Labelled`, mà
`StructuralRecovery.Find` nằm trong `RunModelAsync` — tức `bench --no-llm`, phép đo tôi dùng suốt
loạt này, **không chạy tới đó**. Bench vẫn xanh 6/7 trong khi một lớp dương tính giả mới đã mở ra
trên đường người dùng thật sự đi.

Sửa bằng chốt CẤU TRÚC, không phải danh sách từ: nhánh không-dấu-ngắt đòi phần còn lại **không có
chữ thường nào**. Nghị định 30/2020 quy định tiêu đề phần và chương trình bày bằng *chữ in hoa,
đậm*, nên đây là căn cứ chứ không phải mẹo. Nó chặn cả hai nhóm cùng lúc:

| | |
|---|---|
| `Chương II QUY ĐỊNH CHUNG` · `Chapter II EMPLOYMENT AND RECRUITMENT` | nhận ✓ |
| `Bảng 3 Thống kê số liệu` · `Table 5 Summary Of Results` | loại ✓ |
| `Điều 3 của Bộ luật này` · `khoản 2 Điều này` | loại ✓ |

Giá phải trả: `Chương II Quy định chung` (không in hoa) bị bỏ qua. Đó là hướng sai ĐÚNG với hợp
đồng của file — hậu kiểm sai theo hướng HẸP.

Đo lại sau khi thêm chốt: 95 file **6.357 mục, cấp>1 = 2.045 — y hệt**, tức chốt không tốn gì trên
corpus trong khi đóng lại một lớp dương tính giả không đo được bằng bench. Bench giữ cấp 100% ·
cha 100% · 6/7. Mutation "bỏ chốt không-chữ-thường" → 5 đỏ. **431 test xanh.**

### 55.8 Bài học lớn nhất của mục này

Ba lỗi liên tiếp đều cùng một dạng: **thay đổi ở A làm hỏng B, mà phép đo thường dùng không chạy
qua B.**

- §55.3: lật mặc định `DeterministicHierarchy` (§51) ghép nó với `--split-merged` → crash trùng khoá.
- §55.7: nới `LabelledRx` ghép nó với `StructuralRecovery` (§54) → chú thích thành đề mục.
- Cả hai đều **xanh trên `bench --no-llm`**, vì bench không đi qua đường có mô hình.

Nên trước khi đổi một hàm dùng chung, phải liệt kê MỌI nơi gọi nó và hỏi *"phép đo của tôi có chạy
qua chỗ đó không"*. Nếu không, phải viết test cho chỗ đó — bench xanh không nói gì về nó.

### 55.9 Áp chính bài học 55.8: liệt kê nơi gọi, đo bán kính, bịt chỗ bench không tới

**Nơi gọi.** `NumberingAudit.Parse*` có **11 nơi gọi** ngoài chính nó. Tám nơi có file test riêng.
Bốn nơi nằm ngoài đường `--no-llm` nên bench mù hoàn toàn: `StructuralRecovery`,
`ModelHeadingCriticGate`, `PrecisionAcceptanceGate`, `EvidenceConfidenceCalibrator`.

**Bán kính.** Quét 127.006 lát cắt của corpus, so khớp regex cũ với mới:

```
khớp LabelledRx CŨ : 11.055
khớp LabelledRx MỚI: 11.070   (+15, tức 0,01%)
```

Cả 15 chuỗi mới khớp đều là đề mục thật, không một dương tính giả:

```
×8  ATTACHMENT 1 TO THE CODE OF CONDUCT FORM
    Chapter IX REORGANIZATION, DISSOLUTION AND BANKRUPTCY OF ENTERPRISES
    Chapter IV CYBER SECURITY PROTECTION ACTIVITIES
    Chapter VII IMPLEMENTATION PROVISIONS
    Section 2 HOUSES BEING PUBLIC PROPERTY
    Section 4 HOUSING DEVELOPMENT FOR THE PEOPLE'S ARMED FORCES
```

Chốt in-hoa biến một thay đổi regex thành thay đổi **phẫu thuật**.

**Chỗ bench không tới.** Thêm test ở chính `StructuralRecovery`: `Chương II QUYỀN VÀ NGHĨA VỤ` phải
được cứu, `Bảng 2 Thống kê sau điều chỉnh` thì không.

### 55.10 Mutation sống sót lần thứ hai — và lần này test yếu thật

Lượt mutation đầu ở `StructuralRecovery` **sống sót**. Theo đúng §55.5 tôi không kết luận vội mà
đọc mã: `Find` gom chuỗi theo `label:{Label}` rồi cứu theo **số liền kề**. Test của tôi dùng
`Bảng 1` → `Bảng 3`, cách hai số, nên luật cứu-anh-em không bao giờ chạy — **test vô hiệu bất kể
chốt có hay không**.

Sửa thành `Bảng 1` → `Bảng 2`, đột biến bị giết ngay (3 đỏ). Và điều đó **chứng minh rủi ro là
thật**: không có chốt in-hoa, `Bảng 2 Thống kê sau điều chỉnh` SẼ được cứu thành đề mục trên đường
có mô hình.

Hai lần mutation sống sót trong cùng một loạt, hai nguyên nhân khác nhau (§55.5 đột biến không
thật, §55.10 test yếu thật). Nếu lần này cũng kết luận "đột biến không thật" thì đã bỏ qua một lỗ
hổng có thật và tự tin sai.

**435 test xanh**, bench giữ P 92,3 · cấp 100% · cha 100% · 6/7.

### 55.11 Quét hết ba cổng còn lại — và một hợp đồng suýt bị bỏ qua

Ba nơi gọi cuối cùng mà bench mù: `ModelHeadingCriticGate`, `PrecisionAcceptanceGate`,
`EvidenceConfidenceCalibrator`. Cả ba dùng `NumberingAudit.Parse` làm bằng chứng "có đánh số", nên
§55.2 đổi hành vi cả ba **cùng lúc**.

Hành vi mới ở cả ba là ĐÚNG: `Chương II QUY ĐỊNH CHUNG` là đề mục có đánh số thật, nên bỏ critic
và xếp bucket `numbered` là đúng; `Bảng 2 Thống kê sau điều chỉnh` bị chốt in-hoa loại nên vẫn qua
critic và vẫn `unnumbered`. Đã ghim cả hai mặt.

**Hợp đồng suýt bị bỏ qua.** `PrecisionCalibrationProfile.CurrentPipelineSignature` có chú thích
viết sẵn từ trước: *"old holdout precision must not silently calibrate this pipeline"* khi phân
phối dự đoán đổi. §55.2 làm **15 mục chuyển từ bucket `model_*_unnumbered` sang `model_*_numbered`**
— đúng định nghĩa "đổi phân phối dự đoán". Không bump thì một profile holdout cũ vẫn được nạp và
hiệu chỉnh confidence theo phân phối không còn tồn tại.

Đã bump `2026-08-04-v2` → `2026-08-11-v3`, và **435 test vẫn xanh trước khi bump** — tức không test
nào canh hợp đồng này. Đã thêm hai test: một ghim chữ ký hiện tại, một ghim rằng profile chữ ký cũ
bị **từ chối** (`FormatException`) chứ không bị bỏ qua im lặng.

Test thứ nhất không kiểm được "có bump khi cần" một cách tổng quát — không máy nào biết điều đó. Nó
ghim rằng lần bump NÀY đã xảy ra, để ai hạ chữ ký về v2 phải giải trình.

Mutation: gỡ chốt in-hoa → 2 đỏ · bỏ hẳn nhánh không-dấu-ngắt → 1 đỏ · hạ chữ ký về v2 → 2 đỏ.

**440 test xanh** (build sạch), bench P 92,3 · R 100 · cấp 100% · cha 100% · 6/7.

### 55.12 Tổng kết loạt §55: một thay đổi regex, năm hệ quả ở nơi khác

| hệ quả | phát hiện bằng |
|---|---|
| crash trùng khoá `--split-merged` × `DeterministicHierarchy` | test gọi trực tiếp, KHÔNG phải bench |
| ghi đè im lặng ở `SignatureTiers`/`StyleNestingDepths` | đọc mã sau khi tìm ra crash |
| chú thích hình/bảng thành token cấu trúc | test trực tiếp `NumberingAudit.Parse` |
| chú thích được `StructuralRecovery` cứu thành đề mục | test tại chính nơi gọi, sau khi sửa test yếu |
| bucket calibration đổi ⇒ phải bump chữ ký | đọc chú thích của hợp đồng, không ai nhắc |

**Không hệ quả nào bị `bench --no-llm` bắt.** Bench xanh 6/7 suốt cả năm lần. Cách duy nhất tìm ra
chúng là liệt kê nơi gọi rồi hỏi từng nơi *"phép đo của tôi có chạy qua đây không"*.

## §56. Chạy bench CÓ MÔ HÌNH lần đầu sau loạt §55 — và hai lỗi chỉ đường đó mới lộ

§55.12 kết luận: mọi thay đổi đường có mô hình chỉ có unit test, chưa lần nào chạy đầu-cuối. Đã chạy.

### 56.1 Môi trường (§27)

Build `-p:UseVulkan=true`, chạy `-ngl 99`, đọc dòng xác nhận: *"Ngữ cảnh 8192 token, **GPU 99 lớp**"*.
Phép đo hợp lệ.

> **Bẫy mới ghi vào §27:** chạy `dotnet test` giữa chừng build lại solution KHÔNG kèm
> `-p:UseVulkan=true`, kéo `LLamaSharp.Backend.Cpu` về và **ghi đè native lib**. Lần chạy sau đó
> báo *"ĐÃ YÊU CẦU GPU 99 lớp nhưng thư viện native không hỗ trợ offload, đang chạy CPU"*. Luôn
> đọc lại dòng đó sau mỗi lần `dotnet test`.

### 56.2 Đường có mô hình vá được chỗ đường tất định hỏng, và ngược lại

| | `--no-llm` | có mô hình |
|---|--:|--:|
| Precision | 92,3% | **100%** |
| đúng cấp | **100%** | 97,2% |
| tuyệt đối | 6/7 | 6/7 |

Ba dương tính giả trang bìa ở `04-bia-muc-luc-chu-thich` — thứ §51.5 hai lần từ chối sửa vì không
có tín hiệu cấu trúc nào tách `MỤC LỤC` khỏi `BỘ KHOA HỌC VÀ CÔNG NGHỆ` — **mô hình loại sạch**.
Đó là câu trả lời cho TODO mục 1: nó không phải luật còn thiếu, nó là việc của tầng ngữ nghĩa.

### 56.3 Chuỗi mồ côi: `2.1` dưới `PHỤ LỤC A`

`07-mau-that` i=15 sai cấp chỉ trên đường có mô hình. Đáp án ghi sẵn rằng đây là *"mâu thuẫn có
thật trong file"*: `2.1 Kết quả thử nghiệm` nằm dưới `PHỤ LỤC A` (cấp 1) nên đáp án là **cấp 2**,
dù số hiệu gợi ý thuộc Chương 2.

Truy: đường dẫn `[2,1]` dài 2 nên `FindUnnumberedParentLevel` bỏ qua (nó chỉ nhận độ dài 1);
`FindParentLevel` không tìm thấy `[2]`; `FindSiblingLevel` khác cha. **Không luật tất định nào chạm
tới nó** — cấp giữ nguyên giá trị mô hình trả về, tình cờ đúng ở `--no-llm` (đoán theo độ sâu) và
sai ở đường có mô hình.

Kiểm chéo: chạy `--no-global-hierarchy` cho kết quả **y hệt**, nên không phải
`ReconcileHierarchyAsync` gây ra — loại được nghi phạm đầu tiên trước khi sửa.

Sửa: mở rộng `FindUnnumberedParentLevel` cho đường dẫn dài ≥ 2 **khi và chỉ khi chuỗi MỒ CÔI** —
không heading nào phía trên có đường dẫn mở đầu bằng cùng thành phần đầu. Lúc đó độ sâu dấu chấm
không nói được cấp vì cái cây nó tham chiếu tới **không tồn tại**. Điều kiện mồ côi giữ luật hẹp:
`1.1.1` nằm trong chuỗi `1.` → `1.1` vẫn đi đường cũ.

**Đo được:**

```
bench có mô hình   TRƯỚC: cấp 97,2 · cha 97,2 · 6/7
                    SAU: P 100 · R 100 · F1 100 · cấp 100 · cha 100 · 7/7 TUYỆT ĐỐI
bench --no-llm      không đổi (cấp 100%, 6/7)
440 test xanh
```

### 56.4 Context cố định 4096 — người dùng chỉ ra, và số liệu xác nhận

| | |
|---|--:|
| `qwen35.context_length` model khai báo | **262.144** |
| `LlamaOptions.ContextSize` mặc định | **4.096** |

Nhỏ hơn **64 lần**. Tệ hơn con số: `ApplyRecommendedModelProfile` nâng context bằng một **allowlist
theo tên model** ("Qwen2.5/Llama 3.2 → 8192"), nên model không nằm trong danh sách mắc kẹt ở 4096
vĩnh viễn — kể cả model mới hơn, mạnh hơn.

Sửa: đọc `{arch}.context_length` từ chính GGUF sau khi nạp weights. Không hardcode tên kiến trúc —
lấy `general.architecture` rồi ghép, nên chạy được với model chưa từng gặp.

Trần `MaxAutoContextSize = 32768`, **có căn cứ chứ không chọn bừa**: 262.144 token KV-cache của một
model 9B vượt xa VRAM mọi máy đang dùng, và nạp thất bại thì tệ hơn context nhỏ; 32768 đúng là cấu
hình đã đo ở §0.

Chỉ NÂNG, không bao giờ hạ. Truyền `--ctx` tường minh thì tự tắt — lựa chọn người dùng thắng, và
`ConfigurationFor` vẫn ghi đúng con số đã dùng nên phép đo tái lập được.

Kiểm chứng:

```
không cờ          → Ngữ cảnh 32768 token   (đọc từ GGUF, chạm trần)
--ctx 8192        → Ngữ cảnh  8192 token   (người dùng thắng)
Qwen2.5-7B khai 32768 → cùng luật, không cần thêm dòng nào
```

### 56.5 Auto-context suýt làm chữ ký cấu hình nói dối

`LoadAsync` mở đầu bằng `options = options.Clone()` — cố ý, để không sửa trạng thái của lời gọi.
Nhưng `PrecisionCalibrationProfile.ConfigurationFor(PipelineOptions o)` đọc `o.Llama.ContextSize`
của **bản gốc**. Nên nếu chỉ chỉnh bản clone, chữ ký sẽ ghi `ctx=4096` cho một lượt chạy thật sự
dùng 32768.

Đó là phá đúng kỷ luật đứng thứ hai trong `TODO.md`: *"Mọi con số ghi kèm cấu hình đo"* — và phá nó
theo cách tệ nhất, vì chữ ký vẫn trông hợp lệ. Hai lượt chạy ở hai context khác nhau sẽ sinh CÙNG
một chữ ký, đúng cái bẫy mà docstring của `ConfigurationFor` đã cảnh báo cho `gpuLayers`.

Sửa: giữ tham chiếu bản gốc trước khi clone, ghi context đã CHỐT trở lại. Kiểm đầu-cuối:

```
Ngữ cảnh 32768 token, GPU 99 lớp
ctx=32768                          <- chữ ký nói đúng con số đã dùng
P 100% · R 100% · F1 100% · cấp 100% · cha 100%
```

Hai test ghim: `Clone()` KHÔNG chia sẻ trạng thái (nếu chia sẻ thì việc ghi lại vô nghĩa), và chữ ký
lấy ctx từ chính trường đó (nên ghi lại là cách duy nhất làm nó trung thực). **452 test xanh.**

*Đáng ghi:* lỗi này là hệ quả cấp hai của một tính năng đúng. Tìm ra nó không phải nhờ test hay
bench mà nhờ hỏi *"ai đọc trường tôi vừa sửa?"* — cùng câu hỏi đã tìm ra năm hệ quả ở §55.12.
