# Handoff — chuyển trích xuất heading sang hướng cấu trúc quyết định

Tài liệu này ghi lại một phiên làm việc: đổi kiến trúc quyết định heading, đo lại từng bước, và
những chỗ suýt kết luận sai. Viết cho người tiếp nhận, nên phần "vì sao" quan trọng hơn phần
"đã sửa gì".

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
