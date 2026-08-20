# Việc cần làm

Xếp theo (giá trị đã chứng minh) / (rủi ro đo lường). Mỗi mục ghi kèm **cách nghiệm thu**, vì ở dự
án này phần khó không phải viết code mà là biết một thay đổi có thật sự tốt hơn không.

Hai kỷ luật áp cho mọi mục, cả hai đều từng bị vi phạm và làm sai kết luận:

- **Một biến mỗi vòng đo** (`handoff.md` §4.1). Gộp hai thay đổi vào một lượt là mất khả năng quy
  trách nhiệm cho từng cái.
- **Mọi con số ghi kèm cấu hình đo.** Báo cáo `dhx eval` nay in chữ ký đầy đủ (kể cả `gpuLayers`,
  `seed`); đọc dòng đó trước khi so với bất kỳ bảng nào trong `handoff.md`.
- **Xác minh MÔI TRƯỜNG trước khi tin con số** (§27). **`dotnet test` build lại solution KHÔNG kèm
  `-p:UseVulkan=true` và ghi đè native lib** (§56.1) — đọc lại dòng `Mô hình sẵn sàng…` sau mỗi lần test. Build kèm `-p:UseVulkan=true`, chạy kèm
  `-ngl 99`, và đọc dòng `Mô hình sẵn sàng…` phải nói **GPU N lớp**. Thiếu một trong hai là chạy CPU
  và phép đo vô hiệu — đã mất hai lượt chạy vì điều này.
- **Dump dùng để suy luận phải sinh lại bằng ĐÚNG cờ của lượt đang bàn, ngay trước khi đọc** (§33.3,
  §36.1). Đã sai hai lần liên tiếp: phân tích một artifact cũ rồi kết luận cho lượt chạy khác. File
  trong scratchpad không mang dấu vết cấu hình sinh ra nó.
- **Số test dùng để kết luận phải lấy từ BUILD SẠCH** (§50.1). `dotnet test` gia tăng từng cho
  **384** trong khi build sạch cho **397** — 13 test không chạy mà vẫn báo xanh. Đối chiếu với số
  `[Fact]`/`[InlineData]` đếm trong mã nguồn; hai nguồn lệch nhau là dấu hiệu assembly cũ.
- **Mã tạo ra số liệu báo cáo thì phải có test** (§50.2). Ba mảng mã của loạt §45–§48 sinh ra mọi
  bảng trong handoff mà không có một test nào, kể cả một chỗ đã từng ném null và được sửa nhưng
  không ghim lại.
- **Đổi một hàm DÙNG CHUNG thì phải liệt kê mọi nơi gọi nó, và hỏi phép đo có chạy qua đó không**
  (§55.8). Ba lỗi liên tiếp cùng một dạng: lật mặc định `DeterministicHierarchy` ghép nó với
  `--split-merged` → crash trùng khoá; nới `LabelledRx` ghép nó với `StructuralRecovery` → chú
  thích hình/bảng thành đề mục. **Cả hai đều xanh trên `bench --no-llm`** vì bench không đi qua
  đường có mô hình. Bench xanh không nói gì về nhánh bench không chạy.
- **Đổi phân phối dự đoán thì PHẢI bump `PrecisionCalibrationProfile.CurrentPipelineSignature`**
  (§55.11). Không test nào canh được điều này — 435 test vẫn xanh khi thiếu bump. Hỏi mỗi lần đổi
  luật đọc số: có mục nào chuyển giữa bucket `numbered`/`unnumbered` không?
- **Trước khi đánh số mục mới trong handoff, kiểm trùng** (§55.6): nhánh có thể đã tiến lên trong
  lúc mình làm — đã có hai §52 cùng lúc.
- **"Mutation sống sót" KHÔNG có nghĩa "thay đổi không ảnh hưởng gì"** (§59.2). Mutation chỉ nói
  bộ test hiện có không phân biệt được hai bản. Kết luận về HÀNH VI phải đo trên dữ liệu thật, đủ
  quy mô: trần độ dài "vô tác dụng" theo mutation và theo 12 file, nhưng chặn 70 đoạn trên 95 file.
- **Trước khi nói "không đo được", liệt kê MỌI bộ đáp án đang có** (§101.5). §100 kết luận từ một
  bộ (bench) rồi tuyên bố phần còn lại không đo được — trong khi `keys/` có 14 đáp án nữa và chúng
  nói ngược lại. `ls keys/*/` là một lệnh. *"Tôi chưa đo"* khác *"không đo được"*.
- **Không suy về ĐẦU VÀO từ ĐẦU RA của chính pipeline đang nghi ngờ** (§46.5). Pipeline trả về
  rỗng có hai nguyên nhân không phân biệt được từ kết quả: đầu vào rỗng, hoặc pipeline hỏng. Phải
  mở dữ liệu gốc ra đo. Khoảng cách giữa hai cách đọc ở §46 là 50 file và một khuyến nghị lấy lại
  toàn bộ nguồn — công toi, vì 0/95 file thiếu lớp text.
- **Đổi TẬP TÍN HIỆU là đổi THANG mà mọi hằng số đã hiệu chỉnh đang sống trên đó** (§49.3). Bỏ hai
  mẫu khỏi `AdministrativeMarkers` làm tử số giảm ~10 lần, ngưỡng 0,15 thành bất khả thi và cả chế
  độ thành nhánh chết. "Sửa định nghĩa" + "giữ nguyên ngưỡng" là **hai** biến đổi cùng lúc dù nhìn
  như một. Câu hỏi định nghĩa trả lời được từ spec; ngưỡng thì không.
- **Bước SỬA file và bước ĐO phải tách rời lệnh** (§47.3). Một lệnh gộp cả hai sẽ in ra số liệu của
  trạng thái cũ khi bước sửa ném lỗi ở giữa — đã suýt báo cáo 57,9% cũ như thể là kết quả sau khi cắt đoạn.
- **Phép đo sạch và đường người dùng đi là hai thứ khác nhau** (§35.3). Cờ bật để làm sạch phép đo
  có thể che một lỗi chỉ xuất hiện ở đường mặc định; ít nhất một lượt chạy phải đi đúng mặc định.
- **"Không đổi gì" không tự động là bằng chứng an toàn — phải biết đường đo có THẬT SỰ chạm tới code
  vừa sửa hay không** (§53.4). Đo `TableOfContentsAnchor` trên `--no-llm`: byte-identical trước/sau
  bản sửa, kết luận "chưa đo được tác động". Kết luận sai — nguyên nhân là `Apply` **chưa từng được
  gọi** trên đường đó (chỗ tắc ở §51, phát hiện SAU). Trước khi tin một phép đo "không đổi gì", xác
  nhận log/dấu vết cho thấy đoạn code đang sửa THỰC SỰ chạy, không chỉ tin input/config giống nhau.

---

> **§56.2 trả lời mục này:** đường CÓ MÔ HÌNH loại sạch cả ba dương tính giả trang bìa
> (`bench/04`, precision 92,3% → **100%**). Đây không phải luật cấu trúc còn thiếu — không tín hiệu
> nào tách `MỤC LỤC` khỏi `BỘ KHOA HỌC VÀ CÔNG NGHỆ` — mà là việc của tầng ngữ nghĩa.

---

## Việc ĐANG SỐNG

## 4. Đáp án có người xác nhận — **ĐÃ BẮT ĐẦU (§37), vẫn là thắt cổ chai**

> **Thứ rẻ nhất mở khoá nhiều nhất, tính đến §49.** Ba nhóm, xếp theo giá trị trên mỗi phút bạn bỏ ra:
>
> 1. **Ba file giáo trình trong `04_giao_trinh` + nhãn chế độ** (`typed-numbering` hay `vn-administrative`).
>    Chỉ cần nhãn, không cần outline. Mở khoá mục 11 — giả thuyết §49 đã đúng ở phần khó nhất, chỉ
>    thiếu ngưỡng hiệu chỉnh.
> 2. **Một file PDF→DOCX có outline người kiểm.** Mở khoá mục 10 và cho phép nới `MaxHeadingLength`.
> 3. **File `3.1.PLPH1-ĐQP.docx`** để chấm `keys/plph1-dqp.outline` (41 mục đã có, chưa chấm được).
>
> Ba tài liệu hiện có đáp án vẫn 100%, nhưng **ba tài liệu không đại diện cho mọi văn bản Việt Nam** —
> §45.5 và §48.4 nói rõ vì sao mọi con số trên 95 file chỉ cho biết *luật nào kích hoạt*, không cho
> biết *gán đúng hay sai*.


**Việc duy nhất không tự động hoá được**, và 2026-08-10 nó có bước đầu tiên: người dùng duyệt kết
quả trên UI và bác 5 mục mà **cả ba model đều xếp nhầm** thành đề mục (1447/1453/1460/1467/1473 —
tên người được phỏng vấn trong PHỤ LỤC 3). Đã chốt vào `key-human.key` (105 mục), thay đáp án đồng
thuận model làm số nền.

Tác động lập tức: P 83,5 → **79,5**, đúng cấp 91,5 → **96,0**. Và chính 5 mục đó phơi ra §37 — thang
confidence gán ngược, cổng tự nhận nhóm 62,5% còn bắt duyệt nhóm 82,0%. **Một câu "thừa này" của
người dùng bắt được một lỗi hệ thống mà ba model đồng thuận với nhau nên không ai thấy.**

Còn lại:
- Duyệt nốt khoá luận (127 mục trả về, 41 mục đang ở nhóm "bằng chứng yếu — nên xem").
- Vài chục tài liệu thật nữa cho calibration profile: cần n=52 mẫu mỗi bucket khi không lỗi, n=80
  nếu có một lỗi — bucket lớn nhất đang có 28. Tài liệu tổng hợp không thay được: nó chứng minh
  đường code, không sinh ra phân phối đúng của tài liệu thật.
- **Đáp án thật cho bench 10 tài liệu vẫn do agent gán.** Bench đang 10/10 tuyệt đối; nếu nó cũng
  chứa sai lệch kiểu 5 biên bản thì mọi "bench giữ 10/10" trong handoff đều đứng trên nền chưa kiểm.

---

## 6. Ứng viên đa block — heading trải qua nhiều paragraph

Một tiêu đề bị Enter thật cắt đôi hiện không biểu diễn được: lược đồ chỉ cho **một quyết định trên
một `i`** (§5). Cùng khe hở với việc `InlineHeadingSplitter` không tách nổi block dính hai heading.
`LineBreakOffsets` (`8b95302`) mới chỉ giải quyết ca Shift+Enter trong cùng paragraph.

**ĐÃ ĐO (§55.1): 0 trên 0/95 file.** Đoạn chỉ chứa `Chương II` không kèm tiêu đề không xuất hiện lần
nào — vì 83/95 file là bản chuyển PDF đã gộp hết. Điều kiện mở lại KHÔNG thoả.

Nhưng dạng này KHÔNG vô nghĩa, nó chỉ biểu hiện khác: PDF dán hai dòng thành
`Chương II QUY ĐỊNH CHUNG`. Đó là §55.2, đã sửa ở `NumberingAudit.LabelledRx`, không cần đổi lược đồ.

Đắt nhất, đổi lược đồ. Làm sau cùng, hoặc sau khi gặp một tài liệu hỏng đúng kiểu đó.

**Bằng chứng hiện có, và nó chưa đủ.** §5 ghi đúng một ca: báo cáo thật có `i=452` chứa cả hai đề
mục vì file gốc thiếu ngắt đoạn. Một ca trên hai tài liệu thật, và nó là ca NGƯỢC (hai heading trong
một block) chứ không phải ca mục này mô tả (một heading trải qua nhiều block). Khoá luận 1498 đoạn
đo ở §9 không có ca nào thuộc cả hai dạng.

Tức chi phí thì chắc chắn (đổi lược đồ, đổi mọi điểm đọc `i`), còn lợi ích vẫn là 1 mục trên hơn 240
heading của hai tài liệu thật. **Giữ nguyên vị trí cuối hàng.** Điều kiện mở lại: gặp một tài liệu
mà dạng này chiếm từ vài mục trở lên, hoặc mục 4 cho đủ tài liệu thật để đo được tần suất thật.

---

## 7. Bốn đề mục bị đánh rơi ở TẦNG ỨNG VIÊN — *recall, và đã biết chỗ* — **NỬA ĐẦU ĐÃ CÀI (2026-08-11), CHƯA ĐO BẰNG LLM**

*(Chi tiết đầy đủ về nửa đã cài: `handoff.md` §54.)*

Trên khoá luận, 4 mục đáp án mà pipeline không trả về: `1239`, `1256` (`Tài liệu trong nước:` /
`nước ngoài:`) và `1294`, `1335` (`PHỤ LỤC 1`, `PHỤ LỤC 2`). Sinh dump bằng ĐÚNG cờ của lượt đo cho
thấy **cả bốn đều `role="Normal"`** — không mục nào tới được mô hình (§36.3).

Nên chỗ phải sửa là tầng ứng viên, **không phải** prompt, mô hình hay thị giác — ba hướng đó đã đo
và đều cho số không (§19, §24.2, §25, §30.2).

Hai mục đầu kết thúc bằng `:` và là item bullet nên bị trừ điểm hai lần — **chưa đụng tới, việc
riêng**. Hai mục sau là dạng `NHÃN + SỐ + HẾT`; cờ `--bare-labels` (§36) đọc được chúng thành chuỗi
đánh số nhưng **chưa có đường tác động**: token chỉ nuôi `HasStructuralEvidence`, mà chỗ đó chỉ dùng
để cứu đoạn đã bị mô hình gắn nhãn `DocumentTitle` — tức đoạn phải TỪNG là ứng viên.

> **Đã cài phần "PHỤ LỤC 1"/"PHỤ LỤC 2"**: `StructuralRecovery` nay nhận cả `NumberKind.Labelled`
> (nhãn+số, kể cả dạng trần `PHỤ LỤC 1` khi bật `--bare-labels`), không chỉ đường dẫn Ả Rập nhiều
> cấp — cùng cơ chế cứu-anh-em, khái quát hoá `IsNextSibling` để dùng chung. 5 test mới khoá lại ca
> `PHỤ LỤC 1 → PHỤ LỤC 2 → PHỤ LỤC 3`, ca khác nhãn thì không cứu, ca có tiêu đề (`Chương 1. Mở
> đầu`) không cần cờ `--bare-labels`. 397/397 test xanh. `dhx eval bench --no-llm`: byte-identical
> trước/sau (P 88,1% · R 100% · F1 93,7% · đúng cấp 80,8% · 5/10 tuyệt đối — số nền của cấu hình
> KHÔNG `--style-trust`, không phải "10/10" đã chốt ở nơi khác, xem ghi chú TODO mục 2/13) — không
> hồi quy trên bench, nhưng bench không có fixture nào dùng dạng nhãn+số trần nên không đo được gì
> thêm từ đó.
>
> **Chưa đo theo đúng cách nghiệm thu ban đầu** (dưới đây) vì cần `key-human.key` + LLM — máy này
> chỉ có Qwen2.5-7B/Llama-3.2-3B, không phải Qwen3.5-9B đã dùng chốt các con số trong handoff.md.
> Gộp chung với việc cần đo ở TODO mục 2/13 — cùng một lượt LLM trả lời được cả ba câu hỏi nếu tách
> đúng biến (bật/tắt riêng từng thay đổi).

**Cách nghiệm thu (gốc).** Mở rộng `StructuralRecovery` sang token `Labelled` (hiện chỉ xử lý đường
dẫn số Ả Rập nhiều cấp). Recall trên `key-human.key` phải tăng, P không được giảm quá 1 điểm, bench
giữ 10/10. Đo riêng, không gộp với bất kỳ thay đổi nào khác.

---

## 8. Precision 79,5% — *con số kém nhất hiện nay*

Trên `key-human.key`: P 79,5 · R 96,2 · F1 87,1 · đúng cấp 96,0 · đúng cha 96,0.

Đã biết chắc: **0/21 dương tính giả mang style Heading** — toàn bộ đến từ ứng viên heuristic (§32.1).
Và chúng cần phán đoán NGỮ NGHĨA, không phải hình thức: 14/21 nằm trong danh sách, nên "thuộc danh
sách" không tách được (`1920 x 1080 pixels`, `Nguồn: Tik Tok`, `Nguyễn Hà Phương`).

Đã thử và **đo là hỏng**, đừng thử lại:
- thị giác làm tầng lọc: loại 8/19, giữ 7/8 đề mục thật, ~15 phút/tài liệu — không đáng (§32.2);
- văn bản lặp ≥ 3 lần: loại 2, mất 4 đề mục thật (§34.3);
- kề đoạn chứa ảnh: loại 0/21 (§34.3);
- kề dòng chú thích: loại 1, mất 6 đề mục thật (§34.3).

Còn lại và chưa thử: hạ cấp SAU khi mô hình quyết, bằng luật nhắm lớp nhiễu có hình dạng đo được.
Hoặc `--no-standalone-lines` (§33): P 83,5 → 92,8 nhưng R 96,4 → 93,6, làm rơi `Tiểu kết chương 2/3`.
Đổi 2,8 điểm recall lấy 9,3 điểm precision là **lựa chọn về sản phẩm**, thuộc về người dùng.

---

## 9. Đưa cấu hình đã đo thành mặc định — **NỬA ĐẦU XONG (§56.4)**

> **Context đã xong.** Web không còn đoán ctx từ tên file, và không còn tự điền ô ctx nên cơ chế
> đọc `{arch}.context_length` từ GGUF thật sự chạy. Bỏ trống → 32.768; gõ số → số của bạn thắng,
> và chữ ký cấu hình ghi đúng con số đã dùng (§56.5).
>
> **Còn lại:** `chunkTokens` (Web 5.000 với cấu hình đo 28.000) và ô "Bỏ luật từ ngữ" mặc định bật.
> Cả hai nằm trong chữ ký calibration nên đổi chúng cần đo lại, không phải sửa một dòng.



Mọi con số tốt trong handoff đo với `--style-trust --chunk-tokens 28000 --ctx 32768 -ngl 99`. Giao
diện Web mặc định `ctx 8192`, `5000 token/khối`, và người dùng còn tick "Bỏ luật từ ngữ". Hai đường
cho hai kết quả khác nhau — §36 cho thấy UI trả về `1296`/`1315` mà cấu hình đã đo thì không.

§35 đã chứng minh cái giá của việc để hai đường khác nhau: lỗi `NoKvSlot` chỉ tồn tại ở đường mặc
định và sống sót nhiều phiên vì mọi phép đo đều truyền `--no-reuse-prefix`.

**Cách nghiệm thu.** Đo cấu hình mặc định của Web trên `key-human.key` và bench; nếu kém hơn thì
đổi mặc định Web, không phải đổi phép đo. §10.4 cấm lật mặc định chỉ vì bench.

---

---

## 10. `--split-merged` — *mở khoá 83/95 tài liệu, nhưng chưa có đáp án để bật mặc định*

**Đã có (§45.2).** `ParagraphHeadingSplitter` cắt tiêu đề nằm lọt giữa paragraph. Cần vì 83/95 file
corpus là bản chuyển PDF→DOCX và **4.590/6.858 mục (67%) có ranh giới heading nằm giữa đoạn**.
`001_Bo_luat_Dan_su` trước đó ra **1 mục trên 151 đoạn**, và mục đó là *tên file PDF*.

Đo được: 95 file **3.712 → 6.357 mục**, 82/95 file đổi. Bench 10 tắt/bật **giống hệt** — không hồi quy.
Chất lượng vượt bản Python trên chính chỉ số phiên kia tự nhận là lỗi nặng nhất của họ:
heading > 300 ký tự **16,1% (Python) so với 0,4% (C#)**, dài nhất 4.444 so với 1.007.

**Vì sao vẫn mặc định TẮT.** Cờ này phá giả định "mỗi đoạn nhiều nhất một mục" mà phần còn lại của
pipeline và **mọi đáp án trong `keys/`** đang dựa vào (lát cắt dùng chung một `Index`).

**Giới hạn đã biết, có chủ đích.** `001` vẫn dừng ở 3 mục: thân các Điều là văn xuôi dài không có
khoản đánh số, nên mốc kết thúc tiêu đề nằm xa hơn `MaxHeadingLength = 200` và lát cắt bị **bỏ**.
Đánh đổi cố ý — mất recall để giữ precision. Nới nó cần đáp án để đo.

**Cách nghiệm thu.** Một tài liệu PDF→DOCX có đáp án người kiểm. Chưa có thì không bật mặc định.

---

---

## 11. `VietnameseAdministrative` là sọt quá rộng — ĐÃ SỬA PHẦN `TypedNumbering`

**Đo được (§48.2).** Sau khi cắt đoạn trước tầng phân loại, nhánh dự phòng tụt 65% → 19% và **cả 14
`VietnameseLegal` rơi trọn vào `01_phap_quy`**. Nhưng `VietnameseAdministrative` phình 19 → **46/95
(48%)**, nuốt giáo trình 11/15, tài liệu sinh tự động 5/5, bản dịch 9/10.

**Nguyên nhân chứng minh được bằng hai biểu thức, không cần dữ liệu:**

```
AdministrativeMarkers[0]  ^\s*\d{1,2}\.\d{1,2}\.?\s
TypedNumber               ^\s*\d+(\.\d+)+
```

Khớp cùng chuỗi `1.1`, và nhánh hành chính đứng **trước** trong `Decide`.

**Lịch sử trước khi sửa (§49).** Bỏ mẫu dùng chung → tỉ lệ cao nhất trên cả 95 tài liệu còn
**0,129 < ngưỡng 0,15**, chế độ thành **nhánh chết**. Tách vai trò phân biệt/độ mạnh → phân bố y
hệt, `adminCount >= 3` mới là chỗ chặn. Hướng sửa cuối cùng không xoá mẫu dùng chung, mà cho
`TypedNumbering` thắng trước khi xét nhánh hành chính.

Dự đoán quan trọng nhất của giả thuyết thì **đúng**: giáo trình 11 VnAdmin → **13/15 Typed**. Nguyên
lý dùng được, chỉ thiếu ngưỡng hiệu chỉnh trên tập tín hiệu mới.

**Đã sửa (2026-08-13).** `TypedNumbering` được kiểm trước nhánh hành chính, nên tài liệu thuần
`1.1`/`1.2` không còn bị nuốt vào `VietnameseAdministrative`. Test cũ đã đổi thành
`DocumentModeTests.So_go_tay_thuan_duoc_nhan_la_typed_numbering`, và test
`Ky_hieu_rieng_cua_hanh_chinh_khong_duoc_mat_khi_sua_muc_11` vẫn giữ chiều ngược lại: `I.`/`a)`
không bị mất nhánh hành chính.

**Đã sửa tiếp (2026-08-13).** `1.` đơn cấp cũng bị loại khỏi tử số chọn mode hành chính. Nó là
tín hiệu dùng chung xuất hiện trong văn bản pháp quy, giáo trình, tài chính và tiếng Anh; chỉ dùng
được như tín hiệu phụ sau khi đã biết chế độ, không dùng để chọn chế độ. Sau thay đổi này corpus
95 file không còn file nào rơi vào `VietnameseAdministrative`; nhóm song ngữ chuyển về
`VietnameseLegal`/legal-structured như kỳ vọng.

Phần còn cần dữ liệu: 40 file `TypedNumbering` vẫn chưa có answer key đại diện. Quét TOC field trên
95 file cho 0/95 file đủ ngưỡng 80% (86 thiếu mục lục, 9 dưới ngưỡng). Hạ ngưỡng xuống 40% và bật
`--toc-partial` lấy được **743 cặp exact-match** từ 9 file, nhưng cả 9 đều là `OutlineLevelDriven`
trong nhóm hợp đồng mua sắm. Kết luận: partial TOC có ích cho bench phụ, còn `TypedNumbering` vẫn
cần gán tay ít nhất 3 file trước khi báo độ chính xác. Đây là loại trừ cơ chế: tài liệu có TOC Word
thật thường đã có outline/style để Word sinh TOC, còn `TypedNumbering` là nhóm số gõ tay thuần.

Addendum 2026-08-13 (§79): đã đo 15 file tài chính `03_tai_chinh_ke_toan` với
`dhx extract --no-llm --split-merged -f json -q`. Nhóm này **không RFC-like**: candidate gốc thấp
(1-22), returned chủ yếu mục yếu, 13/15 file sâu nhất chỉ level 1, nhiễu `Table/Figure/Box/Note`,
footer/header và numeric row rõ. Việc Typed kế tiếp nên là tạo key độc lập cho `054` (giàu
`SECTION I..XXI`, tốt để thiết kế vùng) hoặc `041` (financial statement ngắn hơn), rồi đo tách
`exact-title`, `body/navigation usable`, và false-positive class. Trước khi có key, chỉ thêm
diagnostic/flag confidence thấp; chưa auto-demote rộng trong `TypedNumberingOutline`.

Addendum tiếp 2026-08-13 (§80): đã tạo key `054` section-level partial_human từ PDF text layer.
Đo source hiện tại: exact same-index 0/21, nhưng navigation-prefix same-index + đúng level **21/21**;
FP ngoài index truth **448** gồm `Table/Figure/Box/Note` 73, footer/header 7, numeric-row 158, other
210. Đã vá hai lỗi làm validator repair sai: span thiếu trong route typed/admin, và
`InlineHeadingSplitter` generic rewrite slice `typed_number_depth` gây duplicate rồi cách ly mất
`SECTION V`. Việc kế tiếp của Typed tài chính không còn là tạo key `054`, mà là một trong hai hướng
riêng: (1) chính thức hóa metric navigation-prefix trong eval/report; hoặc (2) giảm FP theo class,
mỗi lần chỉ một class/rule và phải đo lại `054` + các key typed hiện có (`056`, `092`).

Addendum tiếp (§81): đã làm class/rule đầu tiên, filter hẹp `Table/Figure/Box/Note` trong
`TypedNumberingOutline`. `054` returned 528→440, FP ngoài truth 448→379, caption FP 73→4, navigation
21/21 giữ nguyên. `056` exact vẫn 14/46, `092` exact vẫn 3/64. Việc giảm FP kế tiếp nếu tiếp tục
hướng này: xử lý `numeric-row` 158 FP, nhưng phải thiết kế/đo như một biến riêng.

Đo sâu hơn trên 743 cặp: `outlineLvl` là ranh giới tuyệt đối (337/337 có `outlineLvl` đều bắt đúng,
406/406 không có `outlineLvl` đều mất). Bảng là một phần lỗi (170/406 thiếu nằm trong bảng) nhưng
không đủ giải thích toàn bộ: 236/406 thiếu nằm ngoài bảng. Vì vậy chưa sửa bằng cách chỉ thêm
heading trong bảng; cần phân tích nguồn phụ của `OutlineLevelDriven` theo cụm/dãy trước.

Lát cắt score của 406 miss: 123 mục là `HeadingCandidate` score ≥0,65, nên nguồn phụ đơn giản sẽ
nâng recall tối đa lên khoảng 61,9%. Phần còn lại thấp điểm gồm 168 mục trong bảng score 0,20–0,35
và 101 mục ngoài bảng score 0; đó là bài toán chấm điểm/nhận dạng cụm riêng, không phải hạ threshold.

101 mục ngoài bảng score <0,25 đã soi thêm: không phải ALL CAPS (0/101), không phải SDT/textbox
(0/101), và 0/101 đứng trước đoạn dài ≥120 ký tự. Chúng chủ yếu là style tự đặt lặp lại trong
template World Bank (`SPDForm2`, `SPDForms1`, `SPD3EmployersRequirement`), ngắn, đậm/căn giữa, nằm
dưới anchor `outlineLvl` phía trước. Đây là lỗi giả định "heading mở ngay ra prose dài" trên cụm
form/section heading liên tiếp.

Code tương ứng là `DemoteRunsWithoutOwnProse`: luật prose-based này demote mọi ứng viên trong dãy
không có prose, trừ built-in Heading/numbering. Đã đo giao với nhóm 101 ngoài bảng score thấp:
101/101 từng là candidate trước demote, và 95/101 bị chính rule này cắt. Đã cài bản hẹp:
custom-style lặp >=3, avgLen<90, ngoài bảng, nằm dưới anchor `outlineLvl` được miễn trừ demote;
`auto:outline-level` ghép lại các candidate đã sống sót này với cấp = anchor gần nhất + 1.

Đo lại partial TOC 9 file: recall **45,4% → 74,2%**, đúng cấp/cha vẫn **100%** trên 743 cặp đã
xác thực. Đây chưa phải precision thật vì `partial_toc` không phạt heading ngoài vùng TOC khớp; cần
gán tay ít nhất 1 file trong nhóm này để đo precision đầy đủ. File 026 vẫn yếu (0% → 20,6%), nên
nhóm heading trong bảng/điều khoản vẫn là bài toán riêng.

Đếm thêm toàn corpus bằng worktree tạm tắt riêng rule này: net demote còn lại chỉ xuất hiện trong
`OutlineLevelDriven`/`02_hop_dong_mua_sam` (**328 candidate**, 8,0% của tập no-demote). Các nhóm
`TypedNumbering` và `VietnameseLegal` đều **0**, nên chưa có dấu hiệu rule này đang che lỗi ở hai
nhóm đó trong corpus 95. Nguyên tắc mới: miễn trừ phải hẹp nhất giải thích được dữ liệu; không mở
rộng kiểu "mọi outlineLvl" nếu test chưa ép.

VietnameseLegal đã kiểm lại sau nghi vấn `157 candidates / 23 files`: candidate thấp là triệu chứng
giả. Lỗi thật là `auto:vietnamese-legal` dùng nhầm `AdministrativeOutline.Build`, nên file chỉ có
`Điều`/`Article` không đủ hai signature hành chính và route rơi về fallback. Đã cài
`LegalStructuredOutline` riêng, validator cho phép nhiều heading cùng `Index` nếu khác `Text`, và
`InlineHeadingSplitter` không cắt lại `legal_marker_declared`. Đo lại corpus 95 với
`--no-llm --split-merged`: `VietnameseLegal/Normal` = **23 files, 3.455 headings, 150,2/file**.
Việc còn nợ của nhóm này không phải coverage nữa mà là **full answer key** cho 1 file pháp quy gộp
nặng (`001` hoặc `025`) để đo precision/cấp thật.

Checklist mới cho mọi lượt đo corpus: luôn ghi `avg candidates/file` và `avg headings/file` theo
`Mode + Status`. Đây là health check rẻ: chưa cần đáp án vẫn phát hiện được route chết sai hình dạng
(như `VietnameseLegal` 7 candidate/file).

---

---

## 12. Bốn ca của spec vẫn treo, ba trong đó không có dữ liệu

| ca | trạng thái |
|---|---|
| La Mã thường `i. ii. iii.` | **ĐÃ ĐO (§55.1): 19 mục trên 12/95 file, chỉ 5 mục chắc chắn** — không đáng, vì rủi ro đọc nhầm dãy chữ cái 601 mục. Và **không phải thêm regex**: `i.` vừa là La Mã 1 vừa là chữ cái thứ 9; `IgnoreCase` sẽ đọc mọi `c.`/`d.`/`i.` trong dãy chữ cái thành cấp 1. Luật đúng phải nhìn **cả dãy** tìm `ii`/`iii`/`iv` — cùng hình dạng với luật ba bảng chữ cái ở §45.1 |
| numbering restart theo chương | chưa gặp trong tài liệu nào có đáp án |
| phụ lục đánh số riêng | chưa gặp trong tài liệu nào có đáp án |
| heading bị Enter cắt đôi | mục 6; Nghị định 30/2020 **bắt buộc** dạng này (`Chương II` một dòng, tiêu đề dòng kế) nên nó phổ biến hơn dự đoán cũ |

**Cách nghiệm thu.** §10.4: cài luật cho ca chưa có dữ liệu là thêm mã không kiểm chứng được.

---

---

## 14. Đóng câu hỏi "95 file deterministic chưa?" — CHƯA, mới có metric Nav chính thức và 14/95 key

**Trạng thái sau handoff §82.** Đã thêm metric evaluator/report cho nghĩa người dùng vừa chốt:
"mục lục tìm kiếm / outline như mục lục sách".

- `Nav`: cùng paragraph/index và output bắt đầu bằng title trong comment key.
- `Nav cấp`: `Nav` đúng và level đúng.
- Test xanh: `dotnet test --no-restore` = 522/522.
- `054_IBRD_Information_Statement_FY25`: `Nav 100%`, `Nav cấp 100%` trên 21 section/appendix key; exact vẫn 0/21 vì output giữ title + body trong cùng paragraph.

**Không được nói 95/95 đã xong.** Corpus có 95 tài liệu, nhưng key trùng basename corpus hiện chỉ 14/95:

`010`, `025`, `026`, `027`, `031`, `033`, `036`, `037`, `038`, `039`, `040`, `054`, `056`, `092`.

**Việc cần làm tiếp để chốt thật:**

1. Sinh/duyệt key navigation cho 81 file còn thiếu, ưu tiên nguồn deterministic độc lập: PDF outline/bookmarks, TOC, hoặc text-layer heading rules có audit.
2. Chạy eval toàn bộ 95 bằng cùng cấu hình deterministic.
3. Báo bảng riêng cho `Nav`, `Nav cấp`, exact, FP. Với PDF/text-layout, kết luận outline/search dựa vào `Nav`, không dùng exact span làm thước đo chính.

**Kết quả thử mở rộng tự động (handoff §83):**

- Word TOC trên `heading_corpus_95_word`: 9/95 ghi được key, đều `partial_toc` dưới ngưỡng; 86/95 thiếu mục lục Word.
- PDF bookmark: 33/83 PDF có bookmark, nhưng 0 file match sang paragraph DOCX đạt 80%; tốt nhất khoảng 57.5%.
- Eval 14 key hiện có chỉ chấm được 5/14; 9 key `toc-derived` fail do duplicate key collapse về cùng paragraph/index.
- 5 file chấm được có micro `Nav 91.7%`, `Nav+cấp 91.7%`; riêng `010`, `025`, `054` đạt `Nav 100%`.

**Việc sát nhất nên làm tiếp:**

1. Sửa `AnswerKey`/`Evaluator` để key duplicate stableId/index có text comment không collapse/fail, rồi chấm lại 9 key `toc-derived`.
2. Sinh partial-review candidate từ PDF bookmark cho 32 file có hit > 0, nhưng đánh dấu chưa gold.
3. Sau đó mới quyết định cụm nào cần human key thật; không còn nguồn deterministic tự động nào đủ để đóng 95/95 ngay.

**Audit core trực tiếp trên 95 DOCX (handoff §84):**

- Core có `deterministicRoute` cho 73/95: `auto:typed-numbering` 40, `auto:vietnamese-legal` 23, `auto:outline-level` 10.
- 22/95 không có route.
- 48/95 trả số heading nhiều hơn số paragraph; 49/95 trả hơn 200 heading.
- 95/95 đều còn `RequiresReview`.

Kết luận giữ nguyên: chưa bao quát deterministic đủ để auto-trích mục lục tìm kiếm 100% cho cả 95 file.

**Typed filter an toàn đã thêm (handoff §85):**

- Bỏ Arabic path có component `0` (`0.85`, `1.0 samples...`) và decimal + đơn vị đo (`1.5 GHz...`).
- Full test xanh 524/524.
- Tổng heading trên audit 95 giảm 36.671 → 34.280, nhưng `heading > paragraph` vẫn 48/95 và 95/95 vẫn `RequiresReview`.
- `054` giữ `Nav 100%`; `056` giữ `Nav 60.9%`; `092` giữ `Nav 95.3%`.
- Đã thử filter mạnh "remainder phải giống title" nhưng gỡ vì làm `092` Nav tụt 95.3% → 68.8%.

Kết luận vẫn không đổi: chưa thể auto-100% cho 95 file; bước tiếp nên là sửa duplicate key evaluator hoặc xử lý over-extraction theo từng subtype typed, không dùng filter lowercase chung.

**Evaluator duplicate-output fix đã làm (handoff §86):**

- `Evaluator.Score` không còn `ToDictionary(h => h.Index)` trên output headings; duplicate output index không làm crash nữa.
- Test mới: `Output_trung_index_khong_lam_evaluator_no_khi_key_khong_trung`.
- Full test xanh 525/525.
- Eval 14 key trùng corpus giờ chấm đủ 14/14.
- Micro `Nav` trên 14 key hiện có: 78.2%; `Nav+cấp`: 78.2%; perfect 1/14.

Việc tiếp theo: dùng 14-key baseline này để ưu tiên sửa recall/navigation cho procurement partial keys (`026`, `036`, `037`) hoặc typed book/RFC (`056`, `092`) thay vì chỉ nhìn audit heading count.

**Phân rã mới sau handoff §87:**

- 14-key baseline: `Nav 78.2%` và `Nav+cấp 78.2%`; cấp không còn sai trên các mục đã chọn đúng.
- 21.8% nav-miss tập trung, không dàn đều: 182/217 miss nằm trong 4 file procurement/World Bank `026`, `037`, `036`, `027`.
- Theo route: `auto:vietnamese-legal` đạt 121/121 nav; `auto:typed-numbering` đạt 84.0%; `auto:outline-level` đạt 74.0% và là nguồn miss chính.
- 48/95 file có `heading > paragraph` chủ yếu là text-layout paragraph rất dài: 46/48 có median paragraph length >= 1000 chars; không nên coi toàn bộ là over-split do ngưỡng.
- OpenStax `056`: 18/46 nav-miss đều vẫn có output đúng index; lỗi là title text bị dính bullet/số trang/body (`2.1 • Negotiation 15 ...`), không phải candidate biến mất.
- Đã sửa cleaner hẹp cho `N.N • Title <page> body...` sau khi truy nguyên: lần tụt `056` `60.9% -> 37.0%` là do validator bác text sạch không còn equality với span nguồn rồi harness repair cách ly index, không phải do dedupe. Fix mới giữ `HeadingSpan` trỏ tới source bẩn và validator chấp nhận transform deterministic; `056` lên `Nav 93.5%`, `092` giữ `95.3%`, `054` giữ `100%`.

**Ưu tiên tiếp theo:**

1. Truy vết procurement outline-level bắt đầu từ `026_WB_RFB_Goods_One_Envelope_2017`, vì riêng file này tạo 54 nav-miss.
2. Thiết kế luật heading trong bảng/content dưới anchor cho nhóm World Bank, dựa trên 168 mục bảng score thấp đã đo trước đó.
3. Tiếp tục sinh/duyệt key cho 81 file chưa có gold nếu muốn kết luận thật cho 95/95.

**Addendum 2026-08-13 (§88): đã đóng `026` bằng luật table heading hẹp cho outline-level.**

- Nguyên nhân `026`: 54/54 nav-miss nằm trong bảng; phần lớn là custom style của World Bank (`Sec1-ClausesAfter10pt1`, `Sec8Clauses`, `SectionVHeader`, `SectionVIHeader`, `SectionHeading`) không có `w:outlineLvl`.
- Đã thêm `OutlineAnchorCustomStyles.FindTableStyles` và nhánh `outline_anchor_table_custom_style` trong `StyleDeclaredOutline.BuildFromOutlineLevel`.
- Luật giữ hẹp: chỉ table paragraph, không built-in Heading, không `Normal/ListParagraph/BodyText*` theo style-repeat; fallback shape chỉ cho `A. ...` bold-center hoặc `Section IX - ...` ngắn trong bảng.
- Ca `Scope of Bid` đứng trước `out` anchor thật đầu tiên trong bảng; đã cho table heading hợp lệ dùng level tạm `1` khi chưa có `currentAnchorLevel`, sau đó TOC pin vẫn chỉnh cấp cuối.
- Test xanh: `dotnet test --no-restore` = 528/528.
- Eval14 mới: micro `Nav 93.4%`, `Nav+cấp 93.4%`, perfect 2/14. Riêng `026_WB_RFB_Goods_One_Envelope_2017`: `68/68`, `Nav 100%`, `Nav+cấp 100%`, exact P/R 100%, không FP trong phạm vi partial key.
- Audit toàn bộ `heading_corpus_95_word`: 95/95 extract no-LLM không crash; deterministic route 73/95 (`typed-numbering` 40, `vietnamese-legal` 23, `outline-level` 10), 22/95 chưa có route.

Việc tiếp theo nên làm: ưu tiên các miss còn lại trong World Bank `027`, `036`, `037`, `033`, rồi mới quay lại precision/FP typed (`056`, `092`, lecture/RFC over-extraction). Vẫn **chưa được nói 95/95 100%** vì mới có key cho 14/95 file.

**Addendum 2026-08-13 (§89): đã kéo World Bank outline-level lên gần trần partial key.**

- Đóng `027`: nhận table style `Head22`, bullet `• Section IX - ...`, và sparse custom style ngoài bảng như `HeaderEvaCriteria`.
- Đóng `033`: nhận sparse custom style `Sec7Heading`, `SectionIXHeader`, và numbered heading candidate ngắn dưới anchor (`Evaluation of Technical Part...`).
- Sửa splitter generic: không cắt `outline_anchor_table_custom_style` và `outline_level_declared`, vì dấu `:`/`;` trong các route này là một phần title do Word/TOC khai, không phải ranh giới body.
- Đóng `038/040`: nhận table heading `Normal` nhưng bold + numbered short (`7. Confidentiality`).
- Test xanh: `dotnet test --no-restore` = 530/530.
- Eval14 mới: micro `Nav 98.8%`, `Nav+cấp 98.8%`, đúng cấp/cha 100%, perfect 7/14.
- Các World Bank partial key đã 100%: `026`, `027`, `031`, `033`, `038`, `039`, `040`.
- Còn lại trong World Bank: `036` thiếu 4 và `037` thiếu 2, đều là các dòng định nghĩa SEA/ES dài trong bảng `Normal`, không bold heading style. Chưa vá bằng keyword `means/is defined as` vì đó là luật nội dung/semantic dễ kéo prose definition vào outline.
- Audit 95 sau sửa: 95/95 extract no-LLM OK; route distribution không đổi (`typed-numbering` 40, `vietnamese-legal` 23, `outline-level` 10, no-route 22).

Việc tiếp theo hợp lý: hoặc xử lý thận trọng lớp SEA definition rows bằng tín hiệu OOXML mạnh hơn nếu tìm được, hoặc chuyển sang typed over-extraction (`056`, `092`, lecture/RFC). Chưa thể nói 95/95 100% vì vẫn chỉ có 14/95 key.

**Addendum 2026-08-13 (§90): thay whitelist tên style bằng phát hiện tự động.**

- Đã bỏ positive hardcode kiểu `Head22`, `SEC3h1`, `HeaderEvaCriteria`, `Sec...Clauses`, `SectionIXHeader` khỏi detector.
- `OutlineAnchorCustomStyles` giờ phát hiện custom heading style bằng phân bố/style-format trong chính tài liệu:
  đoạn dưới outline anchor, text ngắn, style không thuộc nhóm body/list/caption/footer/note, và có tín hiệu format như bold/center/font lớn/numbering.
- So sánh với bản whitelist trên các World Bank outline-level cho thấy bản auto ban đầu quá rộng; đã siết bằng negative generic-style filter và loại title ngắn kết thúc bằng `:`.
- Test xanh: `dotnet test --no-restore` = 530/530.
- Eval14 giữ nguyên: `Nav 98.8%`, `Nav+cấp 98.8%`, đúng cấp/cha 100%, perfect 7/14.
- Audit 95 sau auto-style: 95/95 OK, route distribution không đổi; tổng heading `35,264 -> 35,250` so với bản whitelist §89, tức không phình output.
- File-level delta chỉ còn trong outline-level World Bank: `026 +5`, `027 +1`, `039 +1`, `036 -1`, `033 -3`, `031 -7`, `040 -10`; các key đã có không giảm Nav.

Tiếp theo đúng ưu tiên: tạo/duyệt key cho `FormatDriven` 16 file và nhóm 22 no-route, vì hai vùng đó hiện chưa có điểm đo gold; không quay lại 036/037 SEA definition rows trừ khi tìm được tín hiệu OOXML không-semantic.

**Addendum 2026-08-14 (§91): bắt đầu key cho FormatDriven/no-route bằng nguồn độc lập.**

- `toc-keys` trên 22 file chưa có route/gold: cả 22 đều thiếu Word TOC; nguồn này không giúp thêm key.
- PDF bookmark chỉ có ở 6/22: `063`, `066`, `072`, `076`, `077`, `078`.
- Đã sinh key ứng viên từ PDF bookmark vào `.verify-build/pdf-bookmark-keys`: `063` khớp 42/103 bookmark (61 ambiguous do TOC/page/header/body lặp), còn `066`, `072`, `076`, `077`, `078` khớp đủ bookmark.
- Đo 6 key candidate với `--no-llm --split-merged`: micro `Nav 17.9%`, `Nav+cấp 2.8%`. Đây chưa phải gold, nhưng là tín hiệu độc lập cho thấy `FormatDriven` PDF-converted còn lỗi chính là heading nằm giữa paragraph dài và title bị cắt/chuẩn hoá khác bookmark.
- Mở rộng `ParagraphHeadingSplitter` nhận separator `:` sau marker label+Roman (`SESSION I:`), có test `Paragraph_splitter_accepts_colon_after_labelled_roman_marker`.
- Regressions: `dotnet test --no-restore` xanh 531/531; eval14 chính giữ `Nav 98.8%`, `Nav+cấp 98.8%`; audit 95 mặc định 95/95 OK, mode distribution không đổi (`typed` 40, `legal` 23, `outline` 10, `FormatDriven` 16, `SemanticOnly/insufficient` 6).

Kết luận mới: trong 22 file còn trống, chỉ 6 file có nguồn deterministic độc lập đủ để tạo key candidate; 16 file còn lại không có Word TOC/PDF bookmark nên muốn kết luận 95/95 vẫn cần human/review key hoặc nguồn layout/text-layer khác được kiểm riêng.

**Addendum 2026-08-14 (§92): kiểm offset cấp và World Bank holdout.**

- 6 key PDF bookmark: text bookmark xuất hiện trong DOCX `167/167` (100%), nhưng chủ yếu ambiguous: unique chỉ `28/167`, ambiguous `139/167`. Key dùng được để đo navigation/cắt title, chưa đủ chắc để làm gold occurrence đầy đủ.
- Offset cấp trên 6 bookmark key: `063` có 16 Nav-hit đều lệch `got-key = -1`; `072` có 2 hit offset `0`; `077` có 1 hit offset `0`; ba file còn lại không có Nav-hit. Cộng offset tốt nhất chỉ cứu `Nav+cấp` từ `2.8%` lên tối đa bằng `Nav 17.9%`, không biến nhóm này thành đạt.
- Sinh holdout World Bank từ PDF text-layer `Table of Contents/Summary` cho 6 file chưa có key (`028`, `029`, `030`, `032`, `034`, `035`) vào `.verify-build/wb-holdout-pdf-toc`. Đây là `partial_pdf_toc_holdout`, chỉ chấm `PART/Section`, không phải full outline.
- Holdout match DOCX `45/45` mục high-level. Eval với `--no-llm --split-merged`: micro `Nav 57.8%`, `Nav+cấp 2.2%`, exact recall 80% nhưng precision 0.6% do output text-layout over-extract rất lớn.
- Offset holdout: gần như mọi Nav-hit section lệch `got-key = -1` (`25/26` Nav-hit). Nếu chỉ sửa quy ước cấp thì `Nav+cấp` có thể lên gần `Nav`, nhưng `Nav 57.8%` vẫn thấp hơn nhiều so với Eval14 `98.8%`.

Kết luận mới: Eval14 `98.8%` **không được dùng như đại diện World Bank tổng quát**. Nó đúng trên 9 partial key đã tối ưu, nhưng holdout high-level mới cho thấy overfit/khác mẫu rõ. Việc tiếp theo đáng làm nhất: dùng holdout này để sửa chọn mục/cắt title cho `028/029/030/032/034/035`, bắt đầu từ các miss high-level trước khi mở rộng claim.

**Addendum 2026-08-14 (§93): Nav phải chuẩn hoá như tìm kiếm, không so chuỗi raw.**

- Root cause của phần lớn holdout miss: key PDF dùng `Section III - ...`, body DOCX/output dùng `SECTION III. ...` hoặc en dash; đây là cùng nhãn điều hướng theo nghĩa search/TOC, nhưng metric cũ chỉ normalize whitespace.
- Đã thêm `Evaluator.NormalizeForNavigation`: lower-case, chuẩn hoá dash Unicode, và coi `Section N -/. /:` + `Part N -/:` như cùng marker. Exact TP/FN không đổi; chỉ `Nav`/`Nav+cấp` dùng chuẩn hoá này.
- Test mới: `Navigation_metric_normalizes_case_dash_and_section_separator`.
- Test xanh: `dotnet test --no-restore` = 532/532.
- Holdout World Bank cũ: `Nav 57.8% -> 93.3%`; `Nav+cấp` vẫn `2.2%` vì key đang gán `Section=2` trong khi nhiều file partial không có `PART` trong key.
- Sinh thêm artifact kiểm tra `.verify-build/wb-holdout-pdf-toc-levels-fixed`: nếu key không có `PART`, đặt `Section=1`. Kết quả `Nav 93.3%`, `Nav+cấp 71.1%`. Đây là diagnostic key, không phải gold mới.
- Eval14 với metric canonical: `Nav 98.8% -> 99.1%`, `Nav+cấp 99.1%`; tăng chủ yếu do OpenStax/TOC punctuation, không thay exact.

Kết luận mới: holdout không còn chứng minh lỗi chọn mục lớn như số 57.8% ban đầu; nó chứng minh hai việc khác: (1) metric Nav raw quá khắt với search/TOC, và (2) cấp của FormatDriven/WorldBank text-layout vẫn chưa có luật phân cấp đáng tin nếu không có TOC/anchor thật. Việc tiếp theo nên là sinh holdout key từ PDF TOC đầy đủ hơn, giữ cả `PART`, rồi mới sửa cấp extractor.

**Addendum 2026-08-14 (§94): sinh World Bank holdout full có giữ `PART`.**

- Tạo artifact `.verify-build/wb-holdout-pdf-toc-full`: 6 file `028/029/030/032/034/035`, tổng `77` mục high-level (`PART` + `Section`), thay vì `45` mục Section-only ở holdout cũ.
- Key full dùng paragraph body thật bắt đầu bằng `PART/Section`; không dùng lại paragraph Summary đầu tài liệu trừ các ca PDF-converted không có occurrence tách rời rõ.
- Eval full với `--no-llm --split-merged`: micro `Nav 89.6%`, `Nav+cấp 15.6%`, exact recall `87.0%`, exact precision `1.1%`, candidate recall `89.6%`.
- Theo file: `028 Nav 100%`, `029 92.3%`, `030 66.7%`, `032 84.6%`, `034 100%`, `035 92.3%`.
- Lỗi cấp là thật của extractor: hầu hết Section high-level dưới PART đang ra level `1`, trong khi key full gán level `2`. Đây không còn là lỗi key Section-only nữa.
- `030` vẫn là ca xấu: DOCX conversion dính page header/TOC vào Section đầu, nên occurrence của `PART I`, `Section 1`, `PART II/III` cần review tay hoặc nguồn layout tốt hơn trước khi dùng làm gold.

Kết luận mới: holdout full làm rõ hơn claim Eval14 `99.1%`: chọn mục World Bank high-level khá tốt trên 5/6 file, nhưng cấp phân cấp `PART -> Section` chưa giải xong trên text-layout; không được quote `Nav+cấp` cao cho World Bank tổng quát cho tới khi có luật cấp và review riêng `030`.

**Addendum 2026-08-14 (§95): thử luật `PART -> Section` sau split và bác bằng Eval14.**

- Đã thử một luật rất trực diện: khi chuỗi heading đã chọn có `PART ...` level 1, các `Section ...` phía sau được hạ thành level 2. Luật còn chạy sau `MergedParagraphHeadings`, vì holdout full sinh nhiều Section từ paragraph PDF-converted bị gộp.
- Hiệu quả trên holdout full đúng như nghi ngờ: `Nav` giữ `89.6%`, nhưng `Nav+cấp` tăng từ `15.6%` lên `89.6%`; ví dụ `028` đạt `100%/100%`.
- Nhưng Eval14 chính bị phá nặng: `Nav` vẫn `99.1%`, còn `Nav+cấp` tụt `99.1% -> 53.8%`, đúng cấp micro tụt xuống `48.8%`; riêng `026` rơi từ `100%` xuống `16.2%` Nav+cấp.
- Đã gỡ luật khỏi production và gỡ hai test thử nghiệm. Xác nhận lại: `dotnet test --no-restore` = `532/532`; Eval14 phục hồi `Nav 99.1%`, `Nav+cấp 99.1%`, đúng cấp/cha `100%`.
- Holdout full sau khi gỡ quay về `Nav 89.6%`, `Nav+cấp 15.6%`. Đây là trạng thái đúng để giữ benchmark trung thực: code chính sạch regression, holdout vẫn treo đúng lỗi cấp cần giải.

Kết luận mới: cùng pattern hiển thị `Section ...` đang mang hai quy ước gold khác nhau. Holdout full high-level muốn `Section` là con của `PART`; Eval14 partial World Bank đang ghim nhiều mục/nhãn trong vùng Section ở level 1. Không thêm lại luật `PART -> Section` rộng cho tới khi có tín hiệu phân biệt high-level section boundary với page header/body section label, hoặc review/chuẩn hoá lại key World Bank theo cùng một quy ước.

**Addendum 2026-08-14 (§97): lọc TOC dense trong merged split mà không mất anchor 032.**

- `MergedParagraphHeadings` giờ bỏ paragraph document-level `Table of Contents` có nhiều dot-leader entry, để giảm FP do `--split-merged` chẻ mục lục dày đặc thành heading.
- Guard quan trọng: không dùng lại `TypedNumberingOutline.LooksLikeDenseTypedTableOfContents` cho route merged. Nó từng làm mất World Bank `032` index 33 vì body paragraph thật bắt đầu `Section I ... TABLE OF CONTENT ...` nhưng vẫn là anchor cần điều hướng.
- Thêm ngoại lệ anchor: paragraph bắt đầu bằng `Section ...` hoặc `Part ...` được giữ lại dù có cụm `TABLE OF CONTENT` bên trong.
- Test xanh: `dotnet test --no-restore` = 535/535.
- Eval14 giữ nguyên: `Nav 99.1%`, `Nav+cấp 99.1%`, đúng cấp/cha 100%.
- World Bank holdout full giữ nguyên: `Nav 89.6%`, `Nav+cấp 15.6%`; result count giảm ở các file TOC-heavy như `028`/`034`, tức giảm thừa an toàn nhưng chưa sửa cấp.

Tiếp theo: tiếp tục dùng `OriginalText` + `HeadingSpan` để phân biệt slice TOC/page-header/body trong paragraph PDF-converted. Không quay lại luật `PART -> Section` rộng; mọi filter/cleaner TOC mới phải đo lại Eval14 và `.verify-build/wb-holdout-pdf-toc-full`, đặc biệt kiểm `032` index 33.

**Addendum 2026-08-14 (§98): dot-leader slice filter an toàn sau khi bác bản rộng.**

- Audit holdout full: `MergedParagraphMarker` còn 151 slice, 104 slice có dot leader.
- Thử bỏ mọi dot-leader slice làm holdout tụt `Nav 89.6% -> 88.3%`; root cause là `032` index 302 cần qualifier `(GCC)` trong slice `SECTION VIII ... (GCC) Table of Clauses ...`.
- Bản giữ: chỉ bỏ dot-leader slice khi slice không bắt đầu bằng high-level `Section ...` hoặc `Part ...`.
- Test xanh: `dotnet test --no-restore` = 536/536.
- Eval14 giữ nguyên `Nav 99.1%`, `Nav+cấp 99.1%`; World Bank holdout full giữ `Nav 89.6%`, `Nav+cấp 15.6%`.
- Result count holdout giảm không mất Nav: `028 1096->1080`, `029 860->858`, `030 862->860`, `032 1360->1347`, `034 1195->1182`, `035 874->873`.

Tiếp theo: nếu giảm FP nữa, audit non-dot merged slices/page-header lặp; không được xóa high-level dot-leader slice chứa qualifier như `(GCC)`, `(PCC)`, `(ITP)`.

**Addendum 2026-08-14 (§99): page-header section lặp, giữ lần đầu.**

- Audit non-dot cho thấy page-header ngắn `Section ... <page>` lặp nhiều trang.
- Bỏ toàn bộ page-header ngắn bị bác bằng mô phỏng: mất 17 Nav-hit.
- Bản giữ: chuẩn hoá title section bỏ số trang, giữ lần đầu trong tài liệu, bỏ các lần lặp sau.
- Test xanh: `dotnet test --no-restore` = 537/537.
- Eval14 giữ `Nav 99.1%`, `Nav+cấp 99.1%`; World Bank holdout full giữ `Nav 89.6%`, `Nav+cấp 15.6%`.
- Result count holdout giảm thêm: `028 1080->1074`, `029 858->857`, `030 860->859`, `032 1347->1346`, `034 1182->1180`, `035 873->872`.

Tiếp theo: audit phần thừa còn lại là numbered prose/list trong body; chưa sửa cấp World Bank, và không dùng filter text ngắn rộng nếu chưa mô phỏng Nav-loss.

---

---

## Đã đóng — một dòng mỗi mục, giữ để không mở lại

- 1. Luật nhận được dòng bìa / khối chữ ký — **ĐÓNG (§56.2): việc của tầng ngữ nghĩa, không phải luật cấu trúc**
- 2. Đo nhánh `LevelTrusted` — **ĐÃ ĐO, kết quả ÂM; bị chặn bởi mục 3 (§13)**
- 3. `NumberingAudit` không đọc được "Chương 1." — **XONG (§14)**
- 3b. Cấp theo chuỗi đánh số khi style bất nhất theo phần — **XONG một phần (§17)**
- 5. Dùng `SlimSourceSegment` để mở khoá writeback — **XONG, phạm vi hẹp (§15)**
- 9b. Bộ suy cấp tất định không chạy trên `--no-llm` — **XONG (§51)**
- 9c. Bộ dựng `vn-administrative` — **XONG (§60)**
- 13. `TableOfContentsAnchor.Apply` pin sai cấp cho heading numPr-driven — ĐÃ SỬA, ĐÃ ĐO LẠI SAU §51: đúng cấp 44,8% → 96,6%

- **Luật R1 `auto_assign` theo style OOXML** — đã đo đầy đủ, §10. Trên bench F1 tăng 90,9% → 92,0%
  nhưng lợi ích **không đến từ nó**; trên fixture style bị áp sai nó tự nhận 3 mục sai ở confidence
  1.0 trong khi nhánh kia đẩy cả 9 sang cần duyệt. Cờ `--style-auto-assign` giữ lại để đối chứng,
  **mặc định tắt, không bật lên**.
- **`SkipStyledCandidates`** — precision 100% → 94,1%, §6.3. Mặc định tắt.
- **Bốn ý tưởng bị số liệu bác** — §9.6.
- **`VietnameseLegal` dùng builder hành chính** — đã thay bằng `LegalStructuredOutline`; không mở
  lại hướng thêm `EnglishLegal` riêng nếu chưa có bằng chứng cấu trúc khác ngôn ngữ.
**Addendum 2026-08-14 (sec100): gan cap PART/Section cho merged slice, khong bat route moi.**

- Thu route `auto:part-section` da bi thu va bac: raw regex occurrence selection chon nham front matter/page header, lam World Bank holdout Nav tut. Khong enable route nay.
- Giu phan an toan: `MergedParagraphHeadings` gan cap bang `PartSectionOutline.LevelForHeading`: `PART=1`, `Section=2`, con lai fallback level 1.
- Test xanh: `dotnet test --no-restore` = `540/540`.
- Eval14 giu nguyen `Nav 99.1%`, `Nav+cap 99.1%`.
- World Bank holdout full giu selection `Nav 89.6%`, va `Nav+cap` tang `15.6% -> 45.5%`.
- Tiep theo van la FP numbered prose/list trong body va occurrence body-anchor cho high-level section; khong quay lai route rieng neu chua co tin hieu body occurrence.

## 2026-08-14 next

- World Bank constant section levels are now closed on measured sets: Eval14 Nav+level 99.1%, WB holdout full level accuracy 100%. Continue with selection/coverage, starting from 030 holdout's 10 missing headings.

## 2026-08-19: `PdfBoldLabelOutline` — bug thật lộ ra khi đo `072` (xem handoff §107)

Đo `072_ICP_TAG_Minutes_Mar_2025` (đáp án 27 mục) ra **P 0% R 0%** dù pipeline trả 29 mục ở gần đúng
những đoạn kỳ vọng — không phải "route chưa bắt tới" như `080`, mà là bug thật, lần đầu lộ ra vì đây
là file `05_bien_ban_hop` đầu tiên có khối tiêu đề đa dòng in đậm (4 dòng PDF: tên chương trình / tên
nhóm / ngày / địa điểm). Ba vấn đề cụ thể:

1. Khối tiêu đề đa dòng bị tách thành 4-5 heading giả (mỗi dòng PDF một heading), thay vì được nhận
   ra là MỘT khối tiêu đề tài liệu cần bỏ qua.
2. `"Session I:"`/`"Session II:"` bị cắt cụt ngay tại dấu `:` đầu tiên, mất phần tiêu đề theo sau —
   khác hẳn mẫu `"Item N: Title."` của 073/074/075 vốn không bị cắt cụt tại `:`.
3. Nhiều trang bị bỏ sót heading cấp-2 hoàn toàn (không chỉ bold+nghiêng như hạn chế đã biết của
   `080`) — nghi vấn ngưỡng `≥60% alignment` bị kéo xuống dưới ngưỡng bởi chính đống heading giả ở
   mục (1)/(2) làm loãng tỉ lệ khớp toàn tài liệu.

**Chưa sửa** — cần đo trước: bao nhiêu file khác trong `05_bien_ban_hop`/`format-driven` có khối tiêu
đề đa dòng đậm tương tự trước khi viết luật sửa (kỷ luật "đo trước khi xây"). `.key` đáp án đã có ở
`keys/format-driven-human/072_ICP_TAG_Minutes_Mar_2025.key`, dùng ngay để đo khi sửa.

## 2026-08-19: nhóm C (`063/019/020`) — route hiện tại KHÔNG cắt ranh giới tiêu đề/thân bài (xem handoff §108)

Đọc trực tiếp output `dhx extract --no-llm` của `063_Advanced_Linear_Algebra`,
`019_TT_200-2014_Che_do_ke_toan_DN`, `020_TT_133-2016_Che_do_ke_toan_SME` (không cần `.key` — vấn đề
rõ ràng ngay khi đọc): **không file nào có dù một "mục" trông giống tiêu đề thật.** `019` trả header
trang công báo dán liền thân bài; `020`/`063` trả NGUYÊN CẢ đoạn/chương làm "heading text", không cắt
ranh giới tiêu đề. Route hiện tại thiếu cơ chế cắt ranh giới cho nhóm này — cùng họ vấn đề
`PdfBoldLabelOutline`/`SessionCodeOutline` đã giải cho `05_bien_ban_hop`, nhưng cần marker khác:
`Điều N.` cho 019/020 (thông tư kế toán), `CHAPTER N`/`Na.` cho 063 (giáo trình tiếng Anh). Chưa xây
— đo trước theo kỷ luật đo-trước-khi-xây: cần biết bao nhiêu file khác trong `01_phap_quy`/
`04_giao_trinh` có cùng triệu chứng "đoạn gộp không cắt ranh giới" trước khi thiết kế luật chung.

## 2026-08-19: `LlmBoundaryCutter` — đã đo đủ 55 ca cho backend SGLang/Qwen3.8-27B (55/55), CHƯA đo đủ cho Local (xem handoff §109/§111)

**ĐÃ XONG cho backend SGLang:** chạy lại TOÀN BỘ 55 ca gốc (21 pháp quy + 20 RFC + 14 biên bản) qua
`LlmBoundaryCutter.TryCutAsync` thật (không phải scratch harness), backend `SglangHeaderExtractor` trỏ
gateway Qwen3.8-27B (`http://192.168.68.20/v1`, xem [[sglang-qwen-gateway]]) — **55/55 (100%)**, prompt
giữ nguyên không tinh chỉnh lại cho Qwen. Xem handoff §111.

**CÒN THIẾU:** backend Local (Llama-3.2-3B, mặc định của `PipelineOptions.Backend`) mới có mẫu 9 ca
nhỏ (6/9, xem §109) — chưa chạy đủ 55 ca cho backend này. Không được suy diễn "55/55 trên Qwen" sang
"chắc cũng tốt trên Local" — hai backend khác model hoàn toàn, đúng bẫy dự án đã trả giá nhiều lần.

**Trước khi bật `LlmBoundaryCutFallback` mặc định:** (a) nếu triển khai này sẽ chạy backend=Sglang
mặc định (gateway Qwen3.8-27B), có căn cứ vững để bật — số đã đo sạch. (b) nếu backend=Local (mặc
định hiện tại của `PipelineOptions.Backend`) vẫn là đường chính, cần chạy đủ 55 ca cho Local trước —
chưa làm.

## 2026-08-19: nhóm báo cáo tài chính (`03_tai_chinh_ke_toan`) — BỐN lỗi khác nhau, chưa có đáp án, chưa xây luật (xem handoff §110)

Khảo sát cả 15 file `03_tai_chinh_ke_toan` (chỉ `054` có đáp án chính thức) lộ ra BỐN nhóm lỗi khác
hẳn nhau, không phải một vấn đề chung:

1. **`041-045`** (báo cáo kiểm toán đầy đủ năm, 5-6 ứng viên/file): 100% ứng viên là dòng đầu/chân
   trang lặp lại, không phải đề mục thật — đề mục thật KHÔNG lọt vào tập ứng viên. Lỗ hổng ở TẦNG
   PHÁT HIỆN, không phải cắt ranh giới.
2. **`046-050`** (báo cáo giữa kỳ, chỉ 2 ứng viên/file): cùng họ lỗ hổng phát hiện với (1), nặng hơn.
3. **`051-052`** (Trust Fund FIS, 31-35 mục cứu theo đánh số): lẫn đề mục thật + dòng bảng dashboard
   đọc nhầm thành đề mục (`"YoY change % 69% 48% 53% 10"`) — chưa đo tỉ lệ thật/rác trong 31-35 mục.
4. **`053`** (MD&A, 15 mục cứu theo đánh số): marker `"SECTION N: TITLE"` thật nhưng KHÔNG cắt ranh
   giới (nguyên cả đoạn làm heading text) — cùng họ bug với nhóm C 063/019/020 (xem mục 2026-08-19
   phía trên), nguồn khác (`TypedNumberingOutline`/`typed_number_depth`). `LlmBoundaryCutter` (đã nối
   hôm nay) KHÔNG chạm được vì `declared.Headings` của route này short-circuit trước `RunModelAsync`,
   và prompt RFC đã đo khác hình dạng `"SECTION N:"` — chưa chắc khớp mà không đo riêng.
5. **`055`** (External Review): chỉ 1 ứng viên/243 đoạn — tài liệu thực chất là 1 trang review + bảng
   dự án lặp lại hàng chục lần, có thể KHÔNG đại diện cho lớp "báo cáo tài chính" — cân nhắc loại khỏi
   phạm vi thay vì ép vào cùng một khuôn.

**Chưa xây luật** — bốn nhóm cần bốn hướng khác nhau, gộp lại thành "một luật báo cáo tài chính" ngay
là xây trước khi đo. Việc tiếp theo: chọn 1 file đại diện mỗi nhóm (1)/(2)/(3), đọc PDF đầy đủ, xây
`.key`, RỒI mới thiết kế luật — đúng thứ tự đã dùng cho `05_bien_ban_hop`.

## 2026-08-19: khảo sát TOÀN CORPUS (89 file, `--no-llm`) — ba lỗi hệ thống MỚI, ưu tiên theo bằng chứng (xem handoff §112)

Chạy `dhx extract --no-llm` trên cả 89 file `heading_corpus_95_word`, phân loại theo (mode, ứng viên,
tìm được, route). Ba phát hiện mới, CHƯA từng đo diện rộng trước đây:

1. **`07_system_generated` (RFC) hỏng 5/5 (100%)** — `candidates=1, found=1` cho cả 5 file. Domain
   `TypedNumbering`/RFC ĐÃ ĐO `LlmBoundaryCutter` 100% (20/20, §111) — ứng viên rõ nhất để thử nối
   thật, nhưng `candidates=1` nghĩa là nghi cùng họ "lỗ hổng phát hiện" (không chỉ lỗ hổng cắt ranh
   giới) — cần xác nhận trước khi kỳ vọng LlmBoundaryCutter một mình giải quyết được.
2. **`VietnameseLegal` gần như không hoạt động trên 13/29 file `01_phap_quy`+`06_dich_song_ngu`** —
   luật/nghị định 60-300 đoạn chỉ trích được 1-2 mục. Domain này CŨNG đã đo `LlmBoundaryCutter` 100%
   (21/21, §111). Hai loại lỗ hổng khác nhau trong cùng nhóm (xem danh sách đầy đủ ở handoff §112):
   - lỗ hổng PHÁT HIỆN (candidates≈found, cả hai nhỏ): `008/009/010/012/021` — cùng họ với `019/020/025`
     đã đóng ở §108 (PDF→DOCX gộp nhiều "Điều N." vào vài đoạn khổng lồ).
   - lỗ hổng LỌC/CẮT RANH GIỚI (candidates nhiều, found rơi về 1): `003(7→1) 013(8→1) 015(22→1)
     081(9→1) 083(6→1) 086(10→2) 090(6→1)` — chưa xác định cơ chế lọc cụ thể.
3. **`04_giao_trinh` (7/15 file): found VƯỢT candidates tới 10 lần** — hướng NGƯỢC (nghi thừa, không
   thiếu). Nghi StructuralHierarchyResolver "cứu theo đánh số" bắt nhầm số hiệu Theorem/Equation/
   Exercise trong giáo trình toán/CS làm heading. Chưa xác nhận bằng đọc trực tiếp — chỉ tín hiệu số.

**Chưa xây gì** — cần đọc trực tiếp xác nhận từng phát hiện trước khi thiết kế luật, đúng thứ tự đã
dùng hôm nay cho các nhóm khác. Thứ tự ưu tiên đề xuất: (1) RFC trước — nhóm nhỏ nhất, domain đã có
bảng cứng sẵn; (2) VietnameseLegal — nhóm lớn nhất, tác động cao nhất nếu sửa được, nhưng cần tách
hai loại lỗ hổng trước; (3) `04_giao_trinh` — đọc `062`/`070` (tỉ lệ cao nhất) để xác nhận giả thuyết
trước khi kết luận.

## 2026-08-19: ĐÃ LOẠI `SplitMergedParagraphs=true` làm luật chung — nổ rác trên 54/89 file (xem handoff §113)

Đo trên TOÀN CORPUS (không phải 1 file như §105): bật `--split-merged` làm `found` tăng implausible
(>50% số đoạn tài liệu) trên **54/89 file (61%)**, bao gồm cả nhóm `02_hop_dong_mua_sam` VỐN khoẻ
(dùng làm baseline hồi quy xuyên suốt dự án — ví dụ `032`: 263→1.346). `ParagraphHeadingSplitter`
đúng cơ chế cần cho Phát hiện 1/2 (§112) nhưng regex marker quá lỏng, khớp cả cross-reference/số
phương trình/điều khoản phụ — không phân biệt được heading thật chỉ bằng hình dạng. **Không dùng cờ
này làm luật chung.**

**Giả thuyết CHƯA đo, hướng đi tiếp theo:** dùng `ParagraphHeadingSplitter` để MỞ RỘNG TẬP ỨNG VIÊN
đưa cho tầng model xác minh (không dựng heading trực tiếp như hiện tại), để tầng ngữ nghĩa lọc bớt
rác mà luật tất định không phân biệt được — đặc biệt hợp với `07_system_generated`/`VietnameseLegal`,
đúng domain `LlmBoundaryCutter` đã đo 100% (§111). Cần test trên vài file đại diện (không phải cả 89
— tốn nhiều lượt suy luận LLM) trước khi tin, đúng kỷ luật đo-trước-khi-xây.

## 2026-08-19: đã test giả thuyết trên — kết quả LẪN, hai việc phải làm trước khi xây (xem handoff §114)

Test bằng Qwen3.8-27B, prompt HEADING/NOISE đơn giản (không few-shot), trên segment gộp của `092`
(RFC) và `010` (VietnameseLegal): `010` sạch (6/36 HEADING, bắt đúng 5/6 "Điều N." thật, 2 lỗi: 1 âm
tính giả bỏ sót `Điều 1.`, 1 dương tính giả nhận nhầm mục con đánh số); `092` rất nhiễu (53/116) —
nhưng lộ ra là confound của bài test, không phải LLM kém: 5 đoạn gộp đầu tiên của `092` chính là
TRANG MỤC LỤC (nhiều mốc liên tiếp gần như không thân bài xen giữa) — khác hẳn hình dạng "heading dán
liền thân bài" mà cả `ParagraphHeadingSplitter` lẫn `LlmBoundaryCutter` được thiết kế cho.

**Phát hiện kiến trúc quan trọng hơn giả thuyết ban đầu:** route declared tự động
(`auto:vietnamese-legal`/`auto:typed-numbering`...) CHỈ chạy khi `--no-llm`
(`TryBuildDeclaredOutline` dòng 630: `if (!manual && (!AutoDetectDocumentMode || !DisableLlm)) return
(null, null);`). Khi LLM bật (đường sản xuất thật), pipeline rơi thẳng vào `RunModelAsync`, và lỗ
hổng thật nằm ở tầng `slim.Candidates` (OpenXML/heuristic) — tầng đó KHÔNG dùng
`ParagraphHeadingSplitter`, chỉ xét nguyên đoạn. Toàn bộ số liệu §112/§113 (đo `--no-llm`) phản ánh
một nhánh code không chạy khi LLM bật.

**Hai việc phải làm trước khi xây production (chưa làm hôm nay):**
1. Luật nhận diện "đây là trang mục lục gộp" (nhiều mốc liên tiếp, gần như không thân bài) để KHÔNG
   trộn chung với đoạn thân bài thật khi đưa cho LLM phân loại — khác xử lý, không cùng một lượt.
2. Cơ chế ID candidate DƯỚI MỨC PARAGRAPH cho tầng `RunModelAsync`/`NeutralDocumentViewSerializer`
   (nhiều segment cùng chung một paragraph Index cần phân biệt được với model) — việc kỹ thuật đáng
   kể, tương đương quy mô đã làm cho `LlmBoundaryCutter` (§109).

## 2026-08-19 (phiên song song): bóc đầu trang + cổng chặn — xem handoff §115–§120

**Lưu ý đánh số:** phiên này viết §106–§111, trùng số với phiên kia. Khi hợp nhất đã dời xuống
**§115–§120**; thông điệp commit vẫn ghi số cũ, bảng tra nằm ở đầu §115 trong `handoff.md`.

### ĐÃ XONG, và nằm NGOÀI nhánh `--no-llm`

Ba thay đổi dưới đây chạy trên **mọi** đường, kể cả khi LLM bật — khác với số liệu §112/§113 vốn
chỉ phản ánh nhánh declared:

1. **`RunningHeaderAudit`** (§116/§117) — bóc dòng đầu trang bị dán vào thân bài ở bản chuyển PDF.
   Nằm trong `DocxSlimExtractor.Extract`, trước cả tầng ứng viên. Che chữ số → gom cụm thô 12 ký
   tự → tiền tố chung tự co giãn làm cổng. Đa lượt (tối đa 3) vì một tài liệu mang nhiều biến thể
   đầu trang. `019` sạch hoàn toàn dấu `CÔNG BÁO` (234 → 0). `ev-human` Nav 25,8 → 28,2 (F1 30,5 →
   29,8, do `092_RFC9111` lộ thêm ứng viên); ba bộ còn lại y hệt.
2. **Đăng ký bộ dựng deterministic vào `PrecisionAcceptanceGate`** (§118) — `PartSectionOutline` và
   `PdfBoldLabelOutline` tự đặt `AutoAcceptedEvidence` rồi bị chính cổng ghi đè xuống
   `RequiresReview`. Đã thêm **lưới phản chiếu**: mọi hằng `Basis` trong Core phải được cổng đăng
   ký, quên thì test đỏ.
3. **Cổng harness tách theo NGUỒN** (§119, người dùng quyết) — chỉ mục do `HeadingSource.Model`
   dựng mà thiếu bằng chứng mới chặn writeback. Năm tài liệu `NeedsHumanReview` → `Completed`.

### CÒN MỞ — nợ do (3) tạo ra

Cổng không còn chắn mục heuristic đoán sai, nên **phân biệt mốc cấu trúc với số thứ tự trong văn
xuôi** chuyển từ "nên làm" thành **bắt buộc**. Ba giả thuyết đã đo và BÁC, đừng thử lại (§120):

| giả thuyết | vì sao bác |
|---|---|
| nghịch thế trong dãy mốc | nghị định thật đạt 60%, cao hơn nhóm rác 38–55% — đánh số lại theo chương là bình thường |
| tỉ lệ bước liền bậc +1 | `001_Bo_luat_Dan_su` ra 0%, thấp hơn nhóm rác → dụng cụ đo hỏng, không tin cả hai số |
| mốc có nhãn = cấu trúc, số trần = văn xuôi | **đã cài, `toc` hồi quy F1 99,6 → 92,5**; `036` mất 47 mục, `037` mất 55 mục, precision vẫn 100% nên chúng là đề mục THẬT. Số trần là **cấp sâu hơn** dưới mốc có nhãn, không phải rác |

**Giả thuyết còn lại (H4), chưa kết luận:** khác biệt nằm ở **vị trí** chứ không ở hình dạng mốc —
mốc của `036` mở đầu đoạn riêng, mốc `3.13.` của `019` nằm giữa đoạn gộp dài trung bình 2.043 ký
tự. Đã thử dùng `BoundarySource == "MergedParagraphMarker"` làm tín hiệu: **đo ra 0% trên toàn bộ
16 file kiểm**, tức trường đó không được điền trên đường này → cần tín hiệu offset khác.

**Cảnh báo về dụng cụ đo:** bản kết xuất JSON toàn corpus cho `036` **370 mục**, trong khi `eval`
cùng file báo **117**. Hai đường dùng cấu hình khác nhau; đừng so số giữa hai nguồn này.

### Sự cố quy trình đã sửa

`handoff.md` nằm trong xung đột merge chưa giải quyết suốt **sáu commit**; `git add -A` đã commit
thẳng dấu `<<<<<<<`/`=======`/`>>>>>>>` vào lịch sử mà không ai nhận ra. Đã giải quyết ở `9d67aba`,
giữ trọn vẹn cả hai phần công việc. **Bài học:** `git add -A` khi `.git/MERGE_HEAD` còn tồn tại sẽ
âm thầm "giải quyết" xung đột bằng cách đóng gói nguyên dấu xung đột.

## 2026-08-20: kiến trúc "luật dựng khung, LLM bù phần còn lại" đã chạy (handoff §115–§137)

### TRẠNG THÁI HIỆN TẠI — số đo, không phải ước lượng

| bộ đáp án | F1 | Nav | khớp trọn |
|---|--:|--:|--:|
| bench (7) — `--no-llm` | 98,6% | 80,6% | 6/7 |
| **bench (7) — Qwen3.5-9B cục bộ** | **100%** | **83,3%** | **7/7** |
| ev-human (5) | 42,1% | 98,0% | 1/5 |
| toc (9) | 99,6% | 99,2% | 7/9 |
| fd (2) | 100% | 100% | 2/2 |

| độ phủ corpus | |
|---|---|
| file ra được outline | **89/89** (đầu phiên: 24 file dưới 8 mục) |
| tổng đề mục | **12.108** |
| chạy trọn corpus có mô hình | **105,6 s** cho 88 file |
| file phải dựng lại vì validator | **0/89** (trước: 2) |

Kết xuất nằm ở `outline_llm/` (có mô hình) và `outline_out/` (chỉ luật), cả hai gitignore.
Trang tra cứu: `outline_muc_luc.html`.

### VIỆC ĐÃ XONG, đáng nhớ nhất

1. **§131 — luật deterministic chạy cả khi LLM bật.** Một dòng điều kiện khiến toàn bộ tầng luật
   chỉ sống trên nhánh `--no-llm`; đường sản xuất chưa bao giờ dùng tới. `010_Luat_An_ninh_mang`:
   **39 mục/165,8 s → 50/50 khớp trọn đáp án trong 0,4 s.**
2. **§132 — luật dựng khung, mô hình bù đoạn luật không phủ, ghép theo đúng thứ tự.** Lỗi ghép (nối
   vào cuối, thứ tự `[7,10,13,3]`) làm validator bác cả kết quả. Sửa xong bench đạt **F1 100%, 7/7**.
3. **§121–§122 — tự bật tách đoạn gộp theo từng tài liệu** + chốt hậu kiểm. Đưa 24 file hỏng về 0.
4. **§136 — khử mục trùng nguyên văn**, hết hẳn lượt dựng lại. Số mục TĂNG vì trước đây lượt dựng
   lại cách ly nhầm đề mục thật.
5. **§130 — bỏ hardcode ngưỡng cắt**, thay bằng trung vị độ dài của chính tài liệu.

### LUẬT LÀM VIỆC rút ra từ phiên này — đọc trước khi sửa tiếp

- **§128 là luật trọng tài: Nav thắng.** Người dùng đã quyết. Mọi luật lọc nhiễu làm tụt Nav đều bị
  loại, kể cả khi F1 tăng mạnh. Muốn precision thì phải tìm luật KHÔNG đánh đổi Nav.
- **Không suy đoán được mục nào khớp Nav.** Ba lần trong phiên tôi suy từ hình dạng dữ liệu rằng
  "gỡ cái này là Nav-trung tính" và cả ba lần bị `eval` bác (§126, §129, §137). Chỉ chạy `eval`
  mới biết.
- **Không nhích số = luật chết, không phải luật an toàn** (§116, §128). Hai lần luật được cài vào
  nhánh không ai gọi tới và bốn bộ đáp án im lặng.
- **Bốn bộ đáp án mù với phần lớn corpus.** 14/89 file có đáp án; cải thiện trên 75 file còn lại
  không hiện lên bảng số. Phải ĐỌC đầu ra (§122, §133).
- **Thước đo có thể thiên vị theo cấu tạo.** Luật hardcode 200 tối ưu chính mốc 200 đang chấm nó;
  đổi sang thước tương đối thì kết luận đảo ngược (§130).

### CÒN MỞ

1. **`063_Advanced_Linear_Algebra`** — ca DUY NHẤT không route deterministic nào bắn. Mô hình phải
   chạy toàn phần: 803 đoạn, 11 khối context, **~175 s/khối** dù chỉ 3–5 ứng viên/khối, tổng vượt
   50 phút. Chi phí là của KHỐI CONTEXT chứ không phải số ứng viên, nên `TranUngVienBu = 32` (§134)
   **không chặn được ca này** — nó chặn nhầm biến số. Đang giao bằng kết quả luật (25 mục).
2. **Bằng chứng khai báo chỉ có ở 17/89 file** và CÓ THỂ THIẾU: `bench/04` khai `outlineLvl` 3 chỗ
   trong khi đáp án là 4. Nên "file tự khai thì lấy đúng cái nó khai" đã đo và làm bench tụt 7/7 →
   6/7; đã gỡ.
3. **`092_RFC9111` trả 289 mục cho đáp án 64** (precision 1,9%, Nav 95,3%). Thứ tự bắt buộc ghi ở
   §129: nhận cho đủ bản ở THÂN BÀI trước, rồi mới bỏ trang mục lục. Làm ngược thì bước sau luôn
   tụt Nav.
4. **19,4% mục còn dài gấp đôi trung vị tài liệu** — phần lớn là đơn vị KHÔNG có dấu kết câu bên
   trong (bảng biểu, công thức). Cần ranh giới nhan đề/thân không dựa vào dấu câu; đã bác hướng
   dùng định dạng (§127: `092` có `spans=1, bold=0`, bản chuyển PDF mất sạch định dạng).
