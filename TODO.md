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

## Promotion invariants

Project-wide decision rules, not a milestone's checklist. They were earned experimentally: M10.1e
investigated five plausible remedies for a document that looked catastrophically broken, and each
one failed in a different way. Written down because the pressure they resist - having investigated,
surely something must be fixed - is what produces over-engineering.

- **A diagnostic milestone may end with zero production changes.** Investigation creates evidence,
  not an obligation to patch. A milestone that removes four wrong explanations and changes nothing
  has done its job.
- **Observed defect is not causal owner, and causal owner is not safe remediation.** All three have
  to be established separately. A signal can be plainly mis-scoped and still recover nothing; a
  cause can be proven and its obvious repair still make the result worse.
- **Do not schedule remediation merely because a defect is observable.** Recorded debt is a located
  fact, not a backlog item.
- **Debt re-enters production only on a trigger:** a newly reviewed corpus reproduces material loss,
  a product requirement makes the failure material, or new evidence establishes a causal and
  testable remediation. Absent a trigger, it stays recorded and untouched.
- **A signal is measured on the population it classifies**, never on a subset chosen by a later
  stage - and both sides are measured, what it recovers and what it lets in.
- **Attribution is invalid until the evaluated occurrence is bridged occurrence-safely.** Text that
  matches is not the same occurrence, and gold that names the wrong one produces confident, wrong
  conclusions.

### Two gates, not one

Accuracy remediation must show observed loss, a causal owner, generalisation and collateral before it
is promoted. **Fail-closed and safety hardening uses a different gate**, because its purpose is to
prevent an unsafe state rather than to recover accuracy - it is not required to demonstrate a
historical material loss. It must instead show:

1. an explicit safety or authority invariant;
2. a reachable state currently accepted too permissively;
3. a deterministic, non-heuristic rejection condition;
4. unchanged behaviour for valid inputs;
5. no partial mutation on rejected inputs;
6. regression coverage on both the accept and the reject path.

This is not a relaxation. Fail-closed is not a licence for speculative validation: the rejected state
must be objectively inconsistent with an authority contract that already exists. A new check that
merely seems prudent does not qualify.

### A third gate: observability and provenance

Observability hardening does not require historical material loss either. It requires proof that an
**already-authoritative runtime fact is lost at an evidence boundary**, leaving operationally distinct
executions indistinguishable after the run. It must show:

1. the fact already exists and is already computed in production - no new classifier or status;
2. the fact is dropped where the artifact is materialised;
3. at least two operationally different states become indistinguishable from the artifact without it;
4. the artifact copies the fact exactly - no recompute, no inference, no renaming of its meaning;
5. behavioural neutrality: candidates, validation, final structure, decisions, product and writeback
   are unchanged;
6. coverage of both the healthy and the degraded case, deterministic serialisation, and an explicit
   answer for artifacts written before the field existed.

**Observability is not a licence to record everything.** A field is promoted only when it preserves an
existing authoritative fact needed to interpret, reproduce, audit or operate the artifact. "More
metadata is better" is not a trigger, and "we might want to see this later" is not point 3.

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

## 2026-08-20: ĐÃ ĐO — "LLM lọc precision cho 092" KHÔNG dùng được (xem handoff §139)

Đo trên đúng 288 heading đầu ra hiện tại của pipeline sản xuất (không phải segment thô), ground
truth dựng bằng đúng phép so khớp Nav của `Evaluator` (61/64 đáp án khớp trong 288, khớp đúng Nav
95,3% đã biết). Gọi Qwen3.8-27B phân loại HEADING/NOISE zero-shot cho cả 288 mục:

```
TP=23  FN=38 (mất heading thật)  FP=57 (rác sót lại)  TN=170
Mô phỏng lọc theo LLM: Precision 1% → 28,8%, nhưng Nav 95,3% → 35,9% (SẬP)
```

**Kết luận: không dùng được — đúng luật §128 (Nav thắng), không có ngoại lệ cho cơ chế LLM.** FN cao
vì nhiều heading thật trong 288 ứng viên đã bị CẮT VỤN bởi lỗi tách quá tay (§121) — hình dạng bề mặt
không còn đủ thông tin để phân biệt, kể cả cho model 27B. Muốn cải thiện phải sửa tận gốc lỗi tách
segment trước, không phải thêm tầng lọc phía sau. **Không xây gì** — đúng yêu cầu đo trước khi đề
xuất. Hệ quả cho câu hỏi sửa §128 (thêm sàn precision): với bằng chứng hiện có, `092` sẽ "không đạt
bàn giao" theo MỌI cách đã thử, kể cả LLM zero-shot.

## 2026-08-20: đã test "gốc lỗi ở quyết định tách" cho 092 — BÁC, Nav sập 95,3% → 9,4% (xem handoff §140)

Thêm cờ chẩn đoán `--no-auto-split-merged` (mặc định tắt, không đổi hành vi mặc định) để đo "nếu
092 KHÔNG được tách thì Nav còn bao nhiêu". Kết quả: Nav sập 95,3% → 9,4% (8 mục), tệ hơn cả hướng
LLM lọc đã bác ở §139 (35,9%). Khớp đúng con số lịch sử §116 dù đo qua nhiều commit khác nhau.

**Ba hướng đã bị loại cho `092`, không thử lại:** (1) luật lọc deterministic phía sau — Nav tụt cả
bốn lần (§126/§128/§129/§137); (2) LLM lọc phía sau — Nav sập 35,9% (§139); (3) tắt tách đoạn gộp —
Nav sập 9,4% (§140, mục này). Quyết định BẬT tách là ĐÚNG và CẦN THIẾT — không phải gốc lỗi.

**Việc còn lại, chưa thử:** sửa CHÍNH cơ chế tách (`ParagraphHeadingSplitter`/`MergedParagraphAutoSplit`)
để cắt sạch hơn ngay từ đầu — không phải thêm tầng lọc sau bước tách sai. Chưa có thiết kế cụ thể,
cần đo trước khi đề xuất.

## 2026-08-20: histogram mật độ mốc/đoạn cho 092 — KHÔNG có hai cụm rõ rệt (xem handoff §141)

Đo (rẻ, không LLM) số segment + độ dài trung bình mỗi segment cho 34 đoạn có ≥2 segment của `092`.
Kết quả: dot-leader vắng mặt HOÀN TOÀN (0/34) — tín hiệu này không dùng được cho file này. Không có
khoảng trống rõ giữa "đoạn mục lục" và "đoạn thân bài" — là một dải liên tục (n_seg 4→53+, avg_seg_len
giảm dần tương ứng 538→82), 17/34 đoạn nằm ở vùng mơ hồ (7–15 segment/đoạn).

**Không bác trực giác gốc** (tương quan nghịch n_seg/avg_seg_len vẫn rõ) **nhưng bác việc "tách được
bằng thống kê mật độ đơn giản"** — cần một ngưỡng cắt tay, rơi đúng vùng liên tục không có điểm gãy
tự nhiên. Chưa đề xuất xây gì.

## 2026-08-20: cơ chế "mục lục làm từ điển" (đề xuất phiên Claude.ai khác) — Nav 98,4% khi kiểm bằng evaluator thật (xem handoff §142)

Port trung thành từ Python (đã sửa 2 lỗi tự phát hiện: XREF thiếu ranh giới từ, chưa cắt "Standards
Track Page N" ở chân trang) sang C#, chạy qua `DocxSlimExtractor` thật + replicate đúng
`Evaluator.NavigationScore` cho `092`. Kết quả: Nav 63/64 = 98,4% (nền sản xuất hiện tại: 95,3% nhưng
trả 288 mục, P≈1%) — cơ chế mới chỉ trả 67 mục, gần khớp đáp án (64). Tốt hơn hẳn cả 4 hướng đã bác ở
§139–§141. 1 mục thiếu (`idx=8 "1. Introduction"`) do ranh giới mục lục/thân bài không sạch — đoạn 8
gộp cả đuôi mục lục và đầu thân bài.

## 2026-08-20: nâng key 054 lên TOC page-level + thử gộp trang tiếp nối cho 051/052 (đã bị thay thế)

Key `054` cũ (21 mục "SECTION I..XXI") thay bằng 24 mục TOC page-level, xác nhận từng mục bằng canonical-
match trực tiếp vào đoạn DOCX (không suy từ số trang). Lộ ra: route sản xuất cho 054 hiện đi qua
`auto:typed-numbering` chứ CHƯA chọn `auto:pdf-toc-dictionary` dù probe đã tìm đúng 24/24 — khoảng trống
route-selection, ngoài phạm vi việc nâng key lần này.

Đã từng thêm `PdfFinancialReportOutline.MergeContinuationPages` và gộp `051`: 30→25, `052`: 32→25.
Quyết định này bị thay thế bởi key người dùng chốt sau đó: page-title/duplicate và `(cont'd)` là dòng
outline riêng. Xem trạng thái hiện hành ở §176 trong `handoff.md`; không dùng 25 làm target evaluator.

## 2026-08-20: tổng quát hoá CorrectionMemory cho nhiều loại quyết định — không xây pool mới (xem handoff §170)

Định xây "correction-memory pool" mới cho vấn đề "mỗi ca lạ lại phải vá luật cứng" — kiểm trước thì phát
hiện `Learning/CorrectionMemory.cs` ĐÃ CÓ đúng cơ chế này (JSONL + retrieval code thuần không embedding),
đã nối vào `HeaderExtractionPipeline` thật, chỉ thưa dữ liệu (2 dòng chia sẻ) và thiếu flag CLI. Tránh
lặp sai lầm §138. Mở rộng bằng `DecisionCorrection` + `AppendDecisionAsync`/`FindDecisionExamples`/
`RevokeDecisionAsync` ngay trên lớp cũ (không viết lớp song song), hỗ trợ nhiều loại quyết định (không
chỉ "cấp heading"). Seed 2 ca thật từ việc gộp trang 051/052 hôm nay. Test suite `666/666` xanh. Còn
treo: chưa nối vào điểm quyết định thật nào, chưa thêm flag CLI.

## 2026-08-20: RepairDiagnosticGate — cổng chẩn đoán trước khi đưa duyệt (xem handoff §171)

Tỷ lệ mục "cần xem lại" của MỘT file vượt 3x trung vị corpus (và vượt sàn tuyệt đối 5%) → nghi lỗi tầng
đọc/tách phía dưới, không đưa review (tránh nhiễm correction-memory pool bằng entry vô nghĩa, đúng ca
092 FN=38/61). Kiểm trước: `RepairCandidateRunner`/`RepairValidationGate` đã có sẵn và đã nối vào
`RepairCorpusAudit` — không trùng việc. Đo trên corpus thật (89 file): trung vị 0%, 8 file bị gắn cờ,
gồm 5 file biên bản họp ICP + bài giảng chỉ 2-5 heading toàn bộ không chắc (nhóm mới lộ ra), và `055`
(42,9%) — khớp độc lập với kết luận đã có ở §167/§168 bằng con đường hoàn toàn khác. Test suite
`671/671` xanh. Còn treo: chưa nối vào `repair-key-package` để thực sự chặn sinh gói duyệt.

**Giới hạn:** chỉ đo Nav (chưa qua `Evaluator.Score` đầy đủ P/R/F1/cấp — cơ chế mới là script scratch,
chưa có `HeadingRecord` thật). Chỉ `092` có đáp án; 4 file RFC còn lại chưa kiểm chứng được bằng đáp
án thật. Ngưỡng mật độ ≥13 và độ dài tiêu đề 2–80 ký tự là hằng số tay, chưa đo độ nhạy.

**Chưa đề xuất xây/tích hợp vào pipeline** — đúng yêu cầu đo trước. Cần người dùng quyết định bước
tiếp theo.

## 2026-08-20 (chiều): cấu trúc PDF + khảo sát VLM — xem handoff §142–§143

### ĐÍNH CHÍNH quan trọng về công trạng

`ev-human` F1 42,1% → **84,8%** là công của **phiên song song** (§138–§141, route TOC-dictionary),
không phải phiên này. `git pull` fast-forward mang nó về. `092_RFC9111` từ 289 mục (P 1,9%) xuống
**67 mục / đáp án 64, P 89,6% · Nav 96,9%**.

### ĐÃ ĐO: PDF có gì tương đương `w:outlineLvl`

| tín hiệu | số PDF / 83 |
|---|--:|
| `/StructTreeRoot` (tagged) | 27 |
| … có ≥10 thẻ `/H*` **dùng được** | **5** |
| bookmark `/Outlines` | 33 |
| … ≥10 bookmark, không tagged dùng được | **14** |
| không có gì | **28** |

**19/83 file có đường đọc thẳng.** Hai bẫy đã đo: `Tagged=true` không đủ (`017` có 11.917 thẻ mà
0 heading); cây thẻ chỉ chứa `/MCID` chứ **không chứa chữ**, phải ghép với ký tự theo `(trang, mcid)`.

### ĐÃ THỬ VÀ GỠ: `PdfBookmarkOutline`

| vị trí đặt | kết quả |
|---|---|
| ưu tiên cao | `ev-human` F1 84,8% → **64,4%**, tuyệt đối 1/5 → **0/5** — bookmark gộp front matter, độ mịn khác, đè lên route đang đúng (`056` vốn 46/46) |
| lựa chọn cuối | bốn bộ y hệt nhưng **bắn 0/89 file** → mã chết theo §116 |

Muốn dùng lại phải có điều kiện kích hoạt tốt hơn "route khác trắng tay" — ví dụ *"route khác dựng
ít hơn một nửa số bookmark"*, **chưa đo**.

### VLM — khả thi, còn thiếu đúng một mảnh

| mảnh | trạng thái |
|---|---|
| mô hình | **có sẵn** `mmproj-Qwen3.5-9B-F16.gguf` (bộ chiếu cho đúng model đang dùng) |
| thư viện | **có** LLamaSharp 0.27 lộ `ClipModel` / `Multimodal` |
| **PDF → ảnh crop** | **CHƯA CÓ** — PdfPig không raster hoá. Đây là việc phải làm TRƯỚC |

Ba vai trò xếp theo độ khó thị giác: (1) chẩn đoán file hỏng — dễ; (2) phân xử cha–con — trung
bình; (3) trích outline từ scan — khó, và rủi ro thật là **OCR tiếng Việt có dấu**, không phải suy
luận.

Hợp đồng đầu ra bắt buộc tách ba: `verdict` cho pipeline · `evidence` cho grounder **kiểm chứng
lại bằng dữ liệu PDF** · `explanation` chỉ cho người đọc, không ảnh hưởng quyết định.

**Thí nghiệm đầu tiên, không xây gì trước khi có kết quả:** một ảnh crop, một câu hỏi cha–con,
chấm cả `verdict` lẫn `evidence`. Verdict đúng mà evidence sai cũng là kết quả đáng biết — nó nói
lời văn trôi chảy đang che phán đoán không cơ sở.

Ràng buộc phần cứng đã tính: crop vùng ~150px, DPI 100–120, một ảnh mỗi lượt. Lý do có số đo:
`063` chạy THUẦN VĂN BẢN đã tốn ~175 s/khối và vượt 50 phút (§135).

## 2026-08-21: thí nghiệm VLM đầu tiên — rasterizer đúng, verdict+evidence đều sai (xem handoff §172)

Xây `Vision/PdfRegionRasterizer.cs` (PDFtoImage/PDFium, render trực tiếp vùng crop, không render cả
trang) — mảnh duy nhất §143 xác định là thiếu. Kiểm bằng mắt: crop đúng chính xác ca cha–con "Cost
Recovery" thật trên `052` trang 27. Đính chính §143: model thật trên máy là `Qwen2.5-VL-7B-Instruct`
(không phải Qwen3.5/Qwen3-VL); API LLamaSharp 0.27 dùng `Mtmd*` (không phải `Clip`/`Llava`) nhưng cấp
cao hơn khảo sát mô tả (`InteractiveExecutor` có sẵn).

Chạy thí nghiệm (1 ảnh, 1 câu hỏi, CPU, 38s suy luận): model trả `verdict=same_level` — SAI (đây là ca
cha–con thật) — VÀ evidence khẳng định "không có đường kẻ ngang nào" — SAI, mâu thuẫn trực tiếp với
ảnh đã kiểm bằng mắt (có đường kẻ rõ). Cả verdict lẫn evidence đều sai, không phải chỉ evidence như
mức nhẹ §143 dự đoán. n=1, chưa kết luận cho cả vai trò 2. Nghi vấn chưa loại trừ: model 7B Q4 yếu về
chi tiết thị giác tinh, prompt zero-shot (đúng mẫu đã thấy ở LLM văn bản: 28,6%→85,7% khi có few-shot),
hoặc DPI 150 chưa đủ. Chưa xây thêm gì. Test suite `674/674` xanh.

## 2026-08-21: RepairDiagnosticGate nối vào repair-key-package — chặn thật, không chỉ báo cáo (xem handoff §173)

Vòng 1 chạy pipeline 1 lần/file thu review rate cả đợt, tính trung vị, vòng 2 bỏ qua file bị gắn cờ
(trừ khi có `--force-review-package`). Kiểm bằng chạy CLI thật (không phải chỉ đọc code): với 2 file
(076 + 017), 076 KHÔNG bị chặn — trung vị của 2 file bị chính outlier kéo lên (`median(9,5%,100%)≈
54,75%`, tỷ lệ chỉ 1,83x). Với 5 file đa dạng hơn, 076 bị chặn đúng như kỳ vọng (100% > 3x trung vị
4%). **Giới hạn thật cần nhớ:** cổng chỉ đáng tin khi gọi trên đợt đủ lớn/đa dạng — gọi trên 1-2 file
gần như không bảo vệ được gì, vì trung vị mất ý nghĩa khi mẫu nhỏ chứa chính outlier. Chưa xây: nạp
trung vị tham chiếu cố định từ một lần `repair-audit` trước đó cho các lượt gọi nhỏ. Test suite
`674/674` xanh (không thêm test — dự án chưa có tiền lệ test `Program.cs`, kiểm bằng chạy CLI thật).

## 2026-08-21: cổng chẩn đoán VLM cho is_doubled — 3 lỗi thật bắt được qua chạy live (xem handoff §174)

Xây `Vision/VlmImageQuestion.cs` (wrapper suy luận VLM dùng lại được) + `Repair/
CorruptParagraphVisualVerifier.cs` (định vị trang PDF qua đoạn lành gần nhất, render cả trang, hỏi VLM
so với text đã trích) + CLI `dhx verify-corrupt`. Ba lỗi CHỈ lộ ra khi chạy live trên file thật, không
lỗi nào bị unit test bắt: (1) quên thêm "verify-corrupt" vào whitelist lệnh riêng trong
`CommandLineOptions.Parse` — lệnh lặng lẽ rơi về "extract" mặc định, không exception, không cảnh báo;
(2) needle định vị trang dùng cả đoạn (1000+ ký tự) không khớp được trang nào — chốt tiền tố 80 ký tự
bằng đo thật (40 khớp nhầm trang, 120 không khớp); (3) không reset KV cache giữa các câu hỏi độc lập
→ lỗi native ở lượt gọi thứ 2 trở đi, chỉ lộ khi hỏi ≥2 câu liên tiếp trên cùng instance.

Quét corpus: 20 file có is_doubled=true, phần lớn là run ký tự lặp (gạch dưới điền form, dot-leader),
KHÔNG phải ca gốc "ký tự chữ lặp". Chạy thật trên `053` (4 đoạn gắn cờ): 3/4 nghi lỗi parser (không
phải lỗi nguồn) — cùng rơi vào bảng số liệu dày đặc, giả thuyết `is_doubled` có vấn đề hệ thống với
bảng số (CHƯA xác nhận). Phát hiện thêm: 2/4 lượt cuối model echo lại placeholder `"..."` trong prompt
thay vì sinh evidence thật — verdict của đoạn còn lại (ConfirmedSourceCorruption) vì vậy không có bằng
chứng kiểm được, chưa nên tin. Test suite `685/685` xanh. Chưa làm: sửa prompt, quét 19 file còn lại,
quyết định hành động tự động khi xác nhận parser bug.

## 2026-08-21: gốc rễ is_doubled — 601/601 dương giả do đếm cả dấu câu, sửa còn 0 (xem handoff §175)

Cổng VLM §174 chỉ đúng chỗ hỏng: không phải file hỏng mà chính heuristic hỏng. Đo phân loại cặp ký tự
khớp trong 4 đoạn `053`: **98% là dấu chấm dot-leader**, chữ cái 0%, chữ số 1-2% — nên đề xuất "loại
chữ số" là sai hướng (thử thì tỷ lệ còn tăng 64,6%→76,3%). Quét toàn corpus: **601/601 đoạn bị gắn cờ
(100%) là dương giả** do chuỗi ký tự lặp hợp lệ (dot-leader, gạch dưới điền form, dấu chấm lửng),
**0 đoạn** giống ca gốc `HHììnnhh`. Nặng nhất nhóm hợp đồng WB (036: 114 đoạn, 037: 108).

Sửa một dòng trong `CorruptParagraphDetector.IsDoubled`: chỉ đếm cặp CHỮ CÁI/CHỮ SỐ, loại dấu câu khỏi
cả tử số lẫn mẫu số. **601 → 0**. Benchmark không đổi (051/052 100%, 054 45,8%) dù 601 đoạn nay được
đưa lại vào tập ứng viên. Test khoá ca gốc + 4 dạng dương giả thật + ca gốc lẫn dot-leader.

Hai lỗi nữa trong cổng VLM, lộ ra khi chạy `064`: (1) `ParseVerdict` dùng `Contains` nên
`"abnormal_in_source"` khớp nhầm `normal_in_source` → đọc NGƯỢC verdict, lỗi im lặng; sửa thành đọc
theo giá trị trường, giá trị lạ ⇒ Inconclusive. (2) Validator bác nhầm evidence THẬT vì JSON bị cắt cụt
(tiếng Việt tốn token, maxTokens=300 quá ít); sửa regex chấp nhận thiếu ngoặc đóng + nâng lên 600.
(3) Model hallucinate "nhìn thấy" đúng chuỗi `HHììnnhh` từ ví dụ trong prompt — bỏ mọi chuỗi mẫu cụ thể
khỏi prompt. Bài học chung cho mọi vai trò VLM: thứ gì đưa vào prompt đều có thể quay lại làm "bằng
chứng". Test suite `702/702` xanh.

## 2026-08-21: tách đúng trách nhiệm `is_doubled` và artifact heading filter (xem handoff §176)

Người dùng chọn hướng giữ `is_doubled` đúng nghĩa và thêm filter riêng. Đã thêm
`HeadingArtifactFilter` để loại `toc-blob`, `form-fill-heading`, `pure-filler` sau khi route dựng
heading, không dùng nó để giả làm lỗi nguồn. `CorruptParagraphDetector` tiếp tục chỉ bắt ký tự chữ/số
nhân đôi thật.

Đã phục hồi key chuẩn 051/052 theo yêu cầu user: giữ từng page-title và 4 nhãn nhóm; `(cont'd)`/
duplicate là dòng riêng (`051` 30, `052` 32). Bỏ gộp continuation khỏi output thật của
`PdfFinancialReportOutline`.

Đo nhanh: `036` loại 5 artifact, `037` loại 5, `028` loại 12 TOC blob; `064` giữ 74 heading, không xoá
2 heading thật được cứu. 051/052 vẫn P/R/F1/Nav/Nav+cấp 100%. Full suite xanh `709/709`.

## 2026-08-21: PDF-first broad candidate lane - audit trước, chưa production

- [x] Thêm lệnh audit `dhx pdf-stage-eval`: đo từng tầng `candidate`, analyst role, PDF grounding,
  DOCX alignment, title, level và final outcome bằng source key độc lập.
- [x] Chạy `010_Luat_An_ninh_mang_24-2018-QH14` với Qwen `Qwen3.8-27B`: generic strict route chỉ tạo
  `4/50` key candidates và kết thúc `route-not-applicable` (`analyst-grounded-too-few:0/4`). Không có
  metric giả cho các tầng không chạy.
- [x] Đưa broad PDF lane vào chế độ audit-only: toàn bộ PDF lines -> semantic blocks -> broad
  candidates -> Qwen 9B role pass -> validator -> PDF grounding -> canonical DOCX writeback.
  `SourceFacts` và local context không cho model ghi đè text/geometry; marker-span reconstruction
  chỉ trả text từ DOCX span đã ground. Tuyệt đối chưa thay route production hay fallback sang DOCX
  trước khi có holdout key.
- [ ] Chạy stage audit trên `010`, `051`, `052`, `053`, `054`, `056`, `076`, `078` và minutes; lưu
  `RouteExecutionAudit` để biết chính xác candidate nào bị mất ở tầng nào.
- [ ] Chỉ mở guarded production khi broad lane đạt các key đại diện về candidate recall, title,
  anchor và hierarchy. `010` đang được đường `auto:vietnamese-legal` DOCX xử lý 50/50, nhưng đó không
  chứng minh kiến trúc PDF-first tổng quát.

## 2026-08-22: PDF-first 9B contract - việc đang sống

- [x] Tách contract `SourceFacts -> CandidateContext -> ModelProposal(role) -> Validator` cho broad
  PDF audit. Model 9B chỉ phân loại role; output title/span là fact từ source, không phải text model sinh.
- [x] Thêm span reconstruction generic `label + numeral` cho PDF text corrupt nhưng DOCX còn source
  span, và hierarchy marker document-local (không danh sách từ khoá/tên file).
- [ ] Chạy holdout độc lập ngoài `010` trước khi thay bất kỳ production routing nào. `025` không có
  PDF sibling nên không phải holdout hợp lệ; cần key/review cho một DOCX có PDF thật. Đo riêng raw
  retrieval, role, grounding, title exact, anchor, hierarchy tương đối và false positive.
- [ ] Thêm critic/gate trên proposal đã ground để giảm false positive (`010`: 70 role heading / 43 key
  hit), sau khi phân loại lỗi trên nhiều key; không tune theo một file hay số heading đáp án.
- [ ] Chỉ dùng OCR/VLM cho candidate bị mất thật ở PDF text layer sau retrieval/marker-span; không đưa
  VLM làm tầng đầu hoặc nguồn chân lý cuối.

## 2026-08-24: M7 PDF visual lane

- [x] M7.1-M7.7 visual representation, MarkerLine, 2/2 recovery on 010.
- [x] M7.8 offline multi-producer provenance/dedupe.
- [x] M7.9 source-fact scheduler benchmark.
- [x] M7.10 guarded combined scheduled slice (43 of 140 regions).
- [ ] Per-attempt VLM audit for `v-marker-line-8-39`.
- [ ] Multi-domain scheduler and combined holdouts: legal, procurement, financial, minutes, RFC.
- [ ] M7.11 full text + visual 010 benchmark after repeatability measurement.
- [ ] Production calibration blocked: combined 010 is 37/50 title exact.

- [x] M7.11 isolate `v-marker-line-8-39`: A-D pass; per-region attempt outcomes persisted.
- [x] Qwen visual availability is now measured independently on the frozen 43-region run (`43/43`
  HTTP 200, no retry). NVIDIA is not a prerequisite for calibration or the next holdout.

## 2026-08-24: M7.11 OpenRouter Qwen3.5-9B availability gate

- [x] Add the provider-neutral OpenRouter visual adapter; it accepts the same image/evidence contract
  as the NVIDIA adapter and records per-attempt outcomes.
- [x] Run the frozen `010` combined slice (`wide + supplement`, scheduler, 43 visual regions). The
  raw retrieval lane remained measurable (`48/50` key titles among `1,616` candidates), but hosted
  inference was not: every one of the 43 budgeted visual requests returned HTTP `402` once; 97 other
  regions were intentionally budget-excluded. This is `billing`, not a role/OCR/grounding result.
- [x] Emit structured availability in `pdf-stage-eval`: `state`, `failureClass`, `httpStatus`,
  `retryable`, and actual visual attempt counts. HTTP `402`, `401`, and `403` are non-retryable.
- [x] Paid gate run in that order, pinned to `qwen/qwen3.5-9b`: text `200/OK`, both regression
  crops passed, then the frozen 43-region run completed with `43/43` HTTP 200 and no retry.

## 2026-08-24: Qwen3.5-9B paid gate result

- [x] Text gate: HTTP `200`, pinned `qwen/qwen3.5-9b`, returned `OK`. Qwen uses hidden reasoning by
  default; `max_tokens=8` was insufficient. Both OpenRouter semantic and visual adapters now request
  `reasoning.effort=none`, and semantic boundary calls explicitly request `json_object` output.
- [x] Visual regressions: `Điều 11` (`v-marker-line-8-39`) and `Điều 25`
  (`v-marker-line-24-20`) passed transcription, role, structured proposal, and canonical mapping.
- [x] Bounded combined run: `160/1,616` ranked candidates plus the frozen 43 visual regions. All
  visual attempts succeeded once with HTTP `200`; 41 source spans passed canonical mapping, 2 were
  rejected by the source validator, and 97 regions were budget-excluded. Title-only result is
  `38/50` key titles from 41 recovered headings (92.7% precision, 76.0% recall, 83.5% F1).
- [x] Rebase the 010 evaluation key from its reviewed title comments and regenerated DOCX only;
  `keys/rebased/010_Luat_An_ninh_mang_24-2018-QH14.v2-regenerated-docx.key` maps `50/50` entries.
  Its adjacent provenance JSON records `goldVersion=010-v2-regenerated-docx`, previous version,
  source SHA-256, and `modelOutputUsed=false`; the legacy key remains untouched.
- [x] Replay the immutable 43-call artifact offline against the rebased key. Title-only is still
  `38/41` output / `38/50` gold (P/R/F1 `92.7/76.0/83.5`); span anchor is `40/41` / `40/50`
  (P/R/F1 `97.6/80.0/87.9`). Level, parent, and final structural F1 are explicitly `not-measured`
  because this visual artifact has role/span facts but no replayable hierarchy proposals.
- [x] Audit the three non-exact outputs: `6. Hành vi...` has no gold anchor and remains a body-list
  false-positive candidate; `Điều 17` and `Điều 18` map to the correct rebased anchors but their
  visible title is truncated, so they are title-exact misses rather than anchor misses.
- [ ] M7.12 Qwen9B cross-domain holdout: run the same SourceFacts -> role/span proposal -> validator
  contract on financial, procurement, meeting-minutes, and RFC keys. Report every layer separately;
  do not promote Qwen from legal-only evidence until these runs exist.
- [x] M7.13 scheduler/routing: execution isolation is implemented and passed the real RFC `092`
  acceptance run. `SemanticLaneOptions` now has request/batch/lane deadlines and bounded semantic
  concurrency; a timed-out batch materializes each affected block as `Uncertain` with
  `semantic_batch_timeout`. Visual source-fact recovery starts independently, checkpoints every
  region, and is not classified as provider-unavailable when semantic is partial. `pdf-stage-eval`
  writes `semanticLane`/`visualLane` counters and flushes its manifest in `finally`. Resume uses
  `--pdf-stage-checkpoint <run.jsonl> --pdf-stage-resume`; run RFC only after the focused timeout
  invariant remains green.

  First RFC acceptance run is now **Case A pass**: Qwen `qwen/qwen3.5-9b`, frozen semantic
  concurrency `2`, `160/160` semantic blocks completed, and `43/43` visual HTTP outcomes were
  checkpointed in `.verify-build/092-m713.jsonl`; `.verify-build/092-m713-manifest.json` exists.
  The initial manifest exposed a counter-only defect (`visualLane.completed=162` included 119
  `visual-budget-excluded` traces); the lane counter now excludes them, so future manifests report
  the correct `43`. This run proves execution boundedness/artifact durability, not RFC F1 quality.
  Resume smoke test against the same JSONL added no records (`20` semantic batch + `43` visual
  region remain), rehydrated the canonical visual maps, and reports `43/43` visual completion.
- [x] M7.14 first-loss audit is now per-gold and occurrence-aware (`054`, no model). Raw PDF
  SourceFacts represent `24/24` gold (`3` exact-line, `20` fragmented, `1` table-context).
  Containment-only pool/top-160 figures are `22/24` and `16/24`, but they are **not yet heading
  selection metrics**: `16/24` have multiple candidate occurrences. The audit records each
  occurrence with page, scope, score and signal components, a Recall@K curve, and the rank
  140–180 window. It separates `ambiguous_short_title` (`Overview`, `Appendix`) from
  `ambiguous_candidate_occurrence`; only `2` entries are unique selected, and `4` are clear
  ranking/budget losses. Next is page/order-aware occurrence reconciliation, not rank tuning.
  Artifact: `.verify-build/054-m714-first-loss.json`.
- [x] M7.15 initial minutes comparison (`076`, no model): representation `24/24`, pool/top-148
  `19/24`; losses are `semantic_block_grouping=2`, `candidate_producer=1`, two ambiguous short
  titles, and one ambiguous occurrence. This is a different failure shape from `054`: minutes
  still has real pre-ranking coverage loss, so do not add a global financial rank rule. Artifact:
  `.verify-build/076-m715-first-loss.json`.
- [x] M7.16 evaluation-only occurrence resolver: `pdf-occurrence-eval` uses the reviewed/rebased
  key anchor plus PDF-line page evidence to score candidate identity; it is explicitly prohibited
  from production routing. `054`: CorrectOccurrenceRecall `@100=4/24`, `@160=8/24`, full pool
  `17/24` versus containment `16/24 @160` / `22/24` full. `076`: `@25=3/24`, `@50=12/24`,
  `@100=15/24`, full `15/24`. These are page-anchored occurrence metrics, not title containment.
- [x] M7.17 production occurrence resolver is source-fact-only: `PdfProductionOccurrenceResolver`
  groups candidates by their first rendered PDF line, then returns `unique`, `preferred`,
  `ambiguous`, or `rejected` with scope/layout evidence. It cannot receive a key, DOCX anchor,
  M7.16 result, or emit a heading/change rank. `pdf-occurrence-counterfactual-eval` runs it first,
  then uses M7.16 only outside runtime to score the decision. On `054`, `17/24` correct
  occurrences exist in the pool: `9` are unique and `7` are source-fact-preferred; one remains
  unresolved. On `076`, `15/24` are in the pool: `12` are unique, `0` are preferred, and the
  `DAY 2-4` family remains explicitly ambiguous. This is the intended outcome: do not invent a
  minutes-specific selection rule, and do not promote the resolver to heading authority yet.
  Artifacts: `.verify-build/054-m717-occurrence-counterfactual.json`,
  `.verify-build/076-m717-occurrence-counterfactual.json`.
- [x] M7.18 076 construction trace (`pdf-candidate-construction-audit`, no model, no production
  change) separates the previously conflated loss classes. `Annex 1: Meeting Agenda` and `DAY 1`
  are `line_filter_gate` / `filtered_before_grouping`: both raw lines carry
  `header-footer-zone,table-like`, so grouping never sees them. `Eurostat-OECD PPP Program` is
  `candidate_producer`: its one semantic block is preserved, but the kerning-fragmented text
  (`E u rostat-O ECD P P P P rogra m`) is rejected by all broad/wide/supplement candidate shapes.
  Do not repair grouping for the first pair and do not add a meeting-specific producer yet. Next:
  audit the source annotation/gate evidence for the first pair, and evaluate canonical matching
  at producer boundaries for the third. Artifact:
  `.verify-build/076-m718-candidate-construction.json`.
- [x] M7.19 first-loss repairs by evidence class, not title/domain: candidate grouping now treats
  a geometric header/footer zone as evidence only until repetition confirms a running artifact;
  a non-repeated table-like source fact is retained only when it has a structural marker. Matching
  additionally carries a geometry-derived `MatchText`/canonical form without mutating observed
  source text. The follow-up M7.20 remeasurement rejected a first atomic kerning-producer
  experiment: it inflated total ranked candidates `148 -> 180` and displaced existing titles,
  so it was removed from production. The retained header/table repair recovers `Annex 1` and
  `DAY 1` with total candidates only `148 -> 149`, correct occurrences `15 -> 17`, and no change
  through `@100`; both recovered occurrences are in the configured 160 budget (ranks 137/135).
  `Eurostat-OECD PPP Program` remains an explicit `candidate_producer` miss pending an independently
  measured style/geometry discontinuity. Artifacts:
  `.verify-build/076-m720-first-loss-final.json`,
  `.verify-build/076-m720-occurrence-final.json`, and
  `.verify-build/076-m720-occurrence-counterfactual-final.json`.
- [x] M7.21 producer-boundary diagnostic (`076`, no model/ranker): expanded construction trace to
  retain each line's `MatchText`, font, boldness, X/Y. The remaining `Eurostat-OECD PPP Program`
  block has no measurable structural boundary: all four lines are Calibri `1pt`, bold ratio `0`,
  same left margin, and regular vertical spacing. The title/body distinction is semantic only.
  Therefore no sub-span/atomic producer is promoted; retain `candidate_producer` as an honest miss.
  Artifact: `.verify-build/076-m721-producer-boundary.json`.
- [x] M7.23 semantic recovery experiment (`076`, Qwen `qwen/qwen3.5-9b`, audit-only): add a thin
  source-only selector after deterministic candidate admission. It sees no key/gold, excludes hard
  scopes, preserves occurrence identity, and materializes only an existing first PDF line as the
  immutable source pointer; following lines are context, never model-created text. Existing
  `PdfBlockAnalyst` supplies role then pointer proposal, and the existing canonical/validator path
  remains the only authority. The first broad trial exposed a transport defect: OpenRouter's fixed
  `120` output-token limit truncated batched JSON roles into invisible missing decisions. The adapter
  now budgets output by supplied IDs (bounded by model configuration), with raw audit responses.
  Final frozen run: `N=15` eligible, `A=1` heading-role proposal, `B=1` canonical-unique,
  `C=1` validator accepted. Offline-only comparison to the rebased key finds `D=1` correct
  recovery (`Western Asia`, PDF page 4), `E=0` false positives, improving the measured deterministic
  occurrence ceiling from `17/24` to `18/24`. `Eurostat-OECD PPP Program` was returned as
  `body_text` in the frozen multi-case context and remains unresolved; no retry-to-pass or special
  rule is added. Artifact: `.verify-build/076-m723-semantic-recovery-v6-raw.json`.
- [x] M7.24 frozen recovery replay evaluation (`076`, no model/selector rerun):
  `pdf-semantic-recovery-result-eval` consumes only the immutable M7.23 artifact, frozen M7.20
  occurrence baseline, and rebased key. It rejects artifacts marked `usesGold=true`, never mutates
  recovery routing, and emits per-item first outcomes plus aggregate recovery metrics. Final
  artifact: `.verify-build/076-m724-semantic-recovery-result.json`.

  Baseline correct occurrence is `17`; recovery has `eligible=15`, `usable=1`,
  `canonical-unique=1`, `validated=1`, `gold-correct=1`, `false-positive=0`, yielding net gain
  `+1` and combined observable occurrence `18/24`. The eligible gold opportunity count is `2`, so
  gold opportunity recall is `1/2`; `RecoveryCoverage=1/15`, canonical rate `1/1`, and validated
  precision `1/1` apply only to this single accepted artifact, not as a general Qwen claim.
  `b35/line0` (`Eurostat-OECD PPP Program`) is explicitly `semantic_false_negative` after a
  `body_text` model verdict; `b50/line0` (`Western Asia`) is
  `validated_true_recovery`. Hierarchy remains `not_measured`. Next: M7.25 cross-domain semantic
  recovery only if this small-sample evaluation justifies it; do not tune/retry Eurostat to pass.
- [x] M7.25 context/batch stability experiment (`076`, Qwen9B, source-only): freeze the exact
  same `15` eligible identities for all three profiles (shared SHA-256
  `41340a9f053904f5081a275524d5e54e3328c19149a183e906eefabd8ac25365`) and alter only context
  window/sibling source facts and role-batch size: `current_v6` (`2` role requests),
  `neighborhood_microbatch` (`5`), and `neighborhood_single` (`15`). Each artifact retains raw
  verdicts plus request/context fingerprints; gold is applied only by the existing offline result
  evaluator.

  The outcome is unstable and is **not promoted**. `current_v6` and one-item request both return
  `0` proposals / `0` true recovery, so combined remains `17/24`. Neighborhood micro-batch returns
  `5` role proposals and `2` validator acceptances: `Western Asia` is one true recovery but
  `World Bank` is one false positive, so it also reaches only `18/24` with `FP=1`.
  `Eurostat-OECD PPP Program` changes from `body_text` to `heading_topic` in that profile, but
  returns no source pointer and is therefore `canonical_unresolved`, not recovered. This is
  context/batch sensitivity, not a reason to tune its wording or add a rule. Artifacts:
  `.verify-build/076-m725-*-raw.json`, `076-m725-*-result.json`, and
  `.verify-build/076-m725-context-batch-summary.json`.

  Decision: do not open M7.26 cross-domain recovery and do not promote semantic recovery to
  production authority. Retain the source-only recovery branch as audit evidence, keep unresolved
  cases available for review/optional stronger-model escalation, and move to M8 hierarchy.
- [x] M8.1a hierarchy facts inventory/materialization: implemented as `PdfHierarchyFactsInventory` over only
  `PdfValidatedHeading` + immutable source contexts. It records marker family/depth/path, scope,
  document regime, preceding validated identity, and a same-scope/regime
  `marker_prefix_parent_candidate` in `RouteExecutionAudit.hierarchyFacts`; it cannot create a
  heading, alter a hierarchy, or call a model. A resolved level is emitted only for a top-level
  numeric path or a path whose prefix parent is present in the same scope/regime. Regression locks
  `1. -> 1.1` marker parent resolution, forbids a TOC-to-body prefix relation, and preserves an
  unmarked heading as `relationship_unresolved`. `pdf-stage-eval` now materializes these facts and
  coverage counters in its route artifact. The first real artifact, `.verify-build/076-m81-hierarchy-facts.json`,
  has 19 validated headings, 14 marker-path facts, 14 deterministic levels, zero safe deterministic
  parents, and 19 unresolved relationships. It predates the output-schema correction and therefore
  records `conflicts: 0`; it is retained unchanged as run provenance. New artifacts explicitly emit
  `conflicts.state: not_measured` rather than implying a conflict detector exists. Semantic hierarchy
  fallback is opt-in and was disabled for this run (`0` semantic proposals).
- [x] M8.1b offline hierarchy evaluator: `pdf-hierarchy-facts-eval` consumes only a frozen route
  artifact plus versioned `--hierarchy-gold` JSON. Identity joins by exact `sourceFactId`, with a
  stable DOCX `sourceAnchor` retained in gold; it never falls back to title matching. It separates
  coverage from accuracy-given-resolved and writes per-heading outcomes (`correct`, `incorrect`,
  `unresolved`, `gold_missing`). Parent edges use the entire gold graph as their recall denominator;
  with zero predicted edges, precision/F1 are `null` and status is `no_predicted_edges`, rather than
  false zeroes. The first 076 replay is `.verify-build/076-m81b-hierarchy-evaluation.json`, against
  `keys/hierarchy/076_ICP_IACG08_Minutes_2023.hierarchy.json`: 19 inventory facts / 24 gold headings.
  `goldIdentityResolved=10/24` and `goldIdentityUnresolved=14/24` are bridge coverage, explicitly
  not extraction coverage. It reports all `14` resolved levels, of which only `6` bridge to gold;
  `resolvedLevelsNotGoldMatched=8` exposes upstream pollution. The conditional level result is
  `correctResolvedLevels=6`, `LevelAccuracyGivenResolvedGold=6/6`. Parent coverage remains `0/19`,
  parent accuracy `not_measured`, and edge recall `0/10`. Gold graph sanity passes as a forest:
  `14 roots + 10 edges = 24 headings`. The package deliberately leaves 14 unconfirmed source-fact
  mappings null; they count as bridge missing rather than title-matched guesses. No production resolver change.
- [x] M8.1c cross-domain hierarchy inventory baseline: frozen semantic-only route artifacts (all
  semantic `160/160`, visual `0`, semantic parent fallback off) are
  `.verify-build/010-m81c-hierarchy-facts-bg.json`, `054-m81c-hierarchy-facts.json`, and
  `092-m81c-hierarchy-facts.json`; summary is `.verify-build/m81c-hierarchy-inventory-summary.json`.
  Shapes differ: legal `010` has 116 validated / 35 marker paths / 0 parents; financial `054` has
  29 validated / 0 marker paths / 0 parents; RFC `092` has 31 validated / 30 marker paths / 0 parents.
  This does **not** open M8.2: 25/30 RFC marker paths have `markerDepth != parsed path segment count`
  (`4.3.2` observed as path `4`), so explicit ancestry is lost in inventory before a resolver could
  relate it. Legal has 3/35 of the same mismatch; financial has no numeric marker-path evidence.
  Reviewed gold bridges/evaluator replays are deliberately deferred until marker representation is
  faithful. No averaging, resolver tuning, or extraction repair is performed here.
- [x] M8.1d marker representation trace/repair: the seam, not a parser bug. `PdfMarkerFactsParser`
  recognises `4 3 2` as depth `3` (family `spaced_arabic`) but `PdfMarkerFact` had nowhere to keep
  the components, so the inventory re-derived a path with the stricter dotted grammar
  (`NumberingAudit.ParseArabicPath`), which reads only `4`. Traced across layers: with dots every
  layer agrees; without dots only the strict layer truncates. `NumberingAudit` is unchanged.
  - d-1 safety fixtures first (`MarkerHierarchySafetyFixtureTests`). A probe over the real parser
    showed `09:00 - 09:10` is classified strict `arabic` depth 1 (`:` is an `ArabicRx` separator) and
    `13 00 14 00` / `192 168 1 1` reach `spaced_arabic` depth 4, so family alone is not a safety gate
    and no lexical rule separates `4 3 2 Handling…` from `13 00 14 00 Lunch`.
  - d-2 representation only: `PdfMarkerFact.Components` (`ImmutableArray<int>`, invariant
    `Depth == Components.Length`) plus `markerComponents` in the audit. `MarkerPath`, `ResolvedLevel`,
    the observed ancestor pool, and `FindMarkerPrefixParent` deliberately still run on the strict
    path, so the commit adds no ancestry authority (`RecoveredComponentsDoNotCreateAncestryOnTheirOwn`).
  - d-3 counterfactual (`PdfMarkerAncestryCounterfactual`, `pdf-hierarchy-marker-counterfactual`):
    offline, gold-free, writes no decision. Promoting components to ancestry would add `+12` parent
    candidates (092 `+9`, 010 `+3`) and change 12 levels, but on 092 five of nine supported parents
    are TOC-internal and on 010 all three link a definition clause to a sibling clause. Full-chain
    support equals immediate-prefix support on both corpora, so it filters nothing.
  - d-4 production promotion: **REJECTED**. Structural support is not protective while the facts it
    gates on are contaminated. Artifacts: `.verify-build/{092,010}-m81d3-counterfactual.json`.
- [x] M8.1e structural-scope first-loss (audit only, no production change): `092` and `054` both have
  a real table of contents and both classify **0** TOC blocks. Dot-leader evidence survives in
  neither (`0/31` and `0/14`), so `TocEntryRx` is dead corpus-wide, not RFC-specific; `054` also puts
  its TOC on page 170/170, falsifying the early-page-window assumption. `076` has no TOC at all — its
  dense region is an agenda table, a separate table-classification owner. A multi-entry-per-block
  signal looked clean on 092's validated headings but was **rejected**: over all 544 source blocks it
  false-positives at `4.7%`, and on 076 it inverts (`AGENDA 9.5%` vs `OTHER 14.2%`).
- [x] M8.1f missing intermediate heading first-loss: seven gold nodes block `41/54` gold hierarchy
  edges on 092. Six of seven were represented, produced, ranked (`23-89` of budget `160`) and
  `selected`; only `5. Field Definitions` is a real representation loss. The occurrence bridge had to
  be reviewed first — the audit had matched `4.3. Validation` to the TOC block `b27` (p2) rather than
  the body block `b220` (p15). A diagnostic model rerun was **cancelled**: the kill chain is
  deterministic and model-independent, so no tokens were spent.
  - Owner level 1: `PdfProposalValidator` rejects on domain role, not on the analyst verdict.
  - Owner level 2: `LooksLikeTableLine`'s `short_numbered` branch (`len <= 32 && words <= 4 &&
    contains a digit && no terminal punctuation`) — text-only, no geometry. A short numbered heading
    is exactly that shape. Reimplementation agrees with the real flag on `2192/2192` lines.
  - Children survive because they are `appendix` scope; parents die because they are `appendix_table`.
- [x] B1 `short_numbered` population audit: all `125/125` lines labelled from PDF page context, no
  sampling, no answer key. Artifact `.verify-build/092-b1-short-numbered-labels.json`
  (`usesGold=false`, `labelSource=manual`). True table-like `32/125` (`25.6%`); false `93/125`; real
  outline headings blocked `37/125`. A leading numeric run separates the classes almost perfectly
  (`outline_heading 37/37`, `toc_entry 35/35`, `table_cell 0/32`, `body_prose 0/15`), with one false
  positive (`345 Park Ave`). Labels are versioned as evaluation data at
  `eval/manual-labels/092-short-numbered-line-labels.v1.json` with a provenance sidecar.
- [ ] **DEFECT A — TOC miss enables persistent appendix scope.** Owner: TOC classification /
  `StructuralScopeTracker`. In 092 a TOC line naming the appendices flips `_appendix` at page 4 and
  the flag is never reset, so `417/544` blocks take `appendix`/`appendix_table` scope while the real
  appendix starts at page 32. CONFIRMED DEBT; production change none. Promotion gate: require
  extraction-surviving, cross-domain TOC evidence; evaluate on the full block population; runtime
  classification stays gold-free; no title- or corpus-specific rules.
- [ ] **DEFECT B — `short_numbered` false `TableLike`.** Owner: the scope/domain path consumes raw
  `TableLike` while the grouping path already protects structural markers via `HasStructuralMarker`
  in the same record. Deterministic chain: `short_numbered` -> `TableLike` -> `table`/`appendix_table`
  -> `PdfDomainRole.TableTitle` -> outline exclusion -> validator reject; the model verdict cannot
  rescue these nodes. Candidate invariant: apply the existing `HasStructuralMarker` protection
  consistently; no new lexical regex. CONFIRMED DEBT; production change none.
  Promotion gate: **do not promote while DEFECT A is unresolved** — TOC entries carry structural
  markers and would otherwise gain body/outline authority; and measure cross-domain blast radius on
  the population the predicate actually classifies.
  A and B are independent owners with a constrained promotion order, not one root cause; they should
  not be merged into a single patch, because that destroys regression attribution.
- [ ] Other bounded defects, recorded and not scheduled: 010 numbered definition prose validated as a
  heading; `5. Field Definitions` absent from generated source facts; PDF text spacing corruption
  (`13 :00`, `G loba l`, `Ca ches`) remains upstream debt — note it is space injection, not
  punctuation stripping, and DEFECT B is independent of it.
- [ ] M8.2 deterministic hierarchy resolver: **still not justified.** Across M8.1d-f the parent
  resolver was never shown to be the first loss; the ceiling is set by validated-structure quality
  upstream. Open only when the required parent nodes exist and structural scope is correct, and a
  benchmark then still shows the same-scope/regime, explicit-prefix, earlier-unique-ancestor relation
  failing. Semantic parent proposals remain deferred.
- [ ] M8.1x STOP RULE: do not reopen M8.1 diagnostics unless (1) A or B is deliberately scheduled for
  remediation, or (2) a new hierarchy benchmark shows parent-resolver failure after required parent
  nodes and structural scope are correct.
- [ ] Evaluation invariants earned in M8.1x, and cheaper to keep than any heuristic they replaced:
  - A classifier signal must be evaluated on the population that classifier actually operates on.
    Measuring the multi-entry signal on validated headings reported `0` false positives; the same
    signal over all source blocks reported `4.7%`.
  - First-loss attribution is invalid until the evaluated occurrence is bridged occurrence-safely.
    `4.3` was attributed to a table-of-contents occurrence before the bridge was reviewed.
## M9 — productization

Discovery is closed. M9 packages what the system already knows, honestly, and does not pretend the
hierarchy is complete. Nothing in M9 may create a heading, revive a rejected candidate, rewrite
source text, or fill an unresolved relation.

- [x] M9.1a FinalStructure projection (`PdfFinalStructureProjection`): materializes validated facts
  into a product-consumable shape. No model, no candidate, no hierarchy resolution. An absent parent
  or level is a result, not a gap: a preceding heading is never adopted as a parent, and a parent
  that does not resolve inside the emitted set is dropped rather than left dangling. A level is
  emitted only where the strict dotted path and the observed marker components agree; where the
  source lost its separators they disagree, and the conflict is reported as
  `marker_representation_conflict` instead of a confident wrong level.
- [x] M9.1b canonical grounding correction: identity was anchored to the PDF block that observed a
  heading, which is the wrong authority for a DOCX product. A heading is now identified by its
  `DocxSourceAnchor` (paragraph index, stable id, paragraph-relative span) and its text is a slice of
  that paragraph, so the product shows what the document says even where PDF extraction damaged the
  rendered line. The observed block, span, text and line ids remain as `PdfEvidenceAnchor`
  provenance, and parents are referenced by canonical identity. `DocxTextSpan` and `PdfTextSpan` are
  distinct types, so the two coordinate systems - both previously called `HeadingSpan` - can no
  longer be passed for one another.

  Grounding is materialized from the reconciliation the route already performed; nothing is matched
  by title in M9. An unreconciled fact stays `grounding_unresolved` and is not emitted, while
  remaining in the structure for review.
- [x] M9.2 output decision policy (`PdfOutputDecisionPolicy`): the outline inclusion rules now run
  over the materialized structure and return one decision per heading instead of a filtered list, so
  an excluded fact stays visible to an audit. This is a change of input, not of policy: a
  differential lock runs it and the legacy projection over the same validated input
  and asserts they emit the same canonical occurrences. Emission and review are independent - an
  unresolved hierarchy is a reason code and never suppresses a heading the validator accepted.
- [x] M9.3 serializer / writeback over the new lane. Consumes FinalStructure plus OutputDecisions
  only; it may not read `HeadingRecord`, `ValidatedStructures`, or the legacy policy to fill a
  missing field. A field the serializer needs and the projection does not carry is a contract gap in
  M9.1, fixed there explicitly - as `ValidationDecision` and the canonical grounding were.
  Writeback must act on the canonical occurrence, never on a title search.

  **Serializer half (`PdfProductOutputSerializer`).** Consumes exactly `PdfFinalStructure` +
  `IReadOnlyList<PdfOutputDecision>`, nothing else. Emits one `PdfProductHeading` per `Emit=true`
  decision, in `FinalStructure` source order: canonical id, `DocxSourceAnchor` fields (paragraph
  index, stable id, span), grounded text, role, level/parentId carried verbatim (null stays null),
  and `RequiresReview`/`Reasons` passed through from the decision unchanged. It re-checks
  `SourceAnchor is not null` itself rather than trusting the decision's `Emit` blindly, since a
  record without a canonical occurrence can never be written back. 7 tests lock these invariants
  (`PdfProductOutputSerializerTests`).

  **Writeback half (`PdfProductWriteback`).** Same shape as the legacy `OutlineWriteback` - copy the
  source, mutate only `w:outlineLvl`/`w:pStyle` in the copy, read the target back and verify before
  returning - but reads only `PdfProductOutput`: paragraph index, stable id and span address every
  paragraph, never a title search, and `PdfEvidenceAnchor` never enters this file at all. A heading
  with an unresolved `Level` is skipped (`level_unresolved`) rather than assigned one here, and no
  parent is written - `w:outlineLvl` encodes depth, not a relation, so there is nothing to invent.
  Before mutating, it re-slices the *current* source paragraph at the anchor's span and rejects a
  mismatch (`anchor_text_mismatched`) instead of trusting the stored text, because the source may have
  moved on since `FinalStructure` was materialized. The split mechanics
  (`OutlineWriteback.TrySplitPoint`/`SplitParagraph`, widened from `private` to `internal` for this)
  are shared verbatim with the writeback this replaces, so both routes rearrange runs identically at
  the one place content actually moves; a span that starts after another paragraph position is
  rejected outright (`leading_text_not_splittable`) since only the trailing-body split is implemented.
  Deliberately does *not* gate on `RequiresReview` the way the legacy writeback gates on
  `HeadingDecisionStatus.RequiresReview` - in `PdfOutputDecisionPolicy`, `RequiresReview` always equals
  `Emit`, so that gate would skip every heading. Review state in M9 marks a row for a human without
  suppressing it (`PdfOutputDecisionPolicy`'s own doc comment); `PdfProductOutput` is already filtered
  to `Emit=true` by the serializer, so this layer has nothing further to gate on that input already
  didn't decide. 13 tests lock these invariants (`PdfProductWritebackTests`), including that applying
  the same output twice against a fresh copy of the same source is byte-identical.

  Full suite after both halves: 949 passing, same 15 pre-existing fixture-dependent failures as
  before this work (missing external PDF/DOCX corpus files in this environment, unrelated to M9).
- [ ] M9.4 shadow end-to-end comparison, both lanes over the same frozen upstream result so a
  difference cannot be provider variability. Report the diff occurrence-aware and split by kind:
  - legacy-compatible: occurrence, source text, ordering, emit and review semantics;
  - intentional authority migration: level, parent, hierarchy status.

  Level and parent mismatches are expected. The legacy PDF route derives its product level from
  style clusters and `PdfMarkerHierarchyResolver`, while the DOCX authority route uses
  `ValidatedStructure.Level` - two parallel hierarchy authorities that M9 converges into one. The
  legacy lane is a regression reference, not gold: migration deltas are graded against hierarchy
  gold, never against old output.

  **Engine built and tested; corpus run not done - gate stays open.**

  `PdfShadowLaneComparison` (`DocxHeaderExtractor.Core.Eval`) does the compatibility and hierarchy
  halves. It joins the two lanes on the shared source fact id (legacy `HeadingRecord.SourceId`, M9
  `PdfFinalHeading.PdfEvidence.BlockId`) - never on the DOCX anchor either lane resolved to, so a
  disagreement about WHICH occurrence a fact grounds to surfaces as `AnchorMismatch` instead of
  silently joining two different paragraphs. Diff classes: `MissingInNew`, `ExtraInNew`,
  `AnchorMismatch`, `TextMismatch`, `OrderMismatch` (pairwise inversion between legacy paragraph
  order and the frozen fact order), `ReviewMismatch`; any non-empty class is a regression by the gate
  unless reviewed as intentional (`HasUnexplainedDiff`). `CompareHierarchy` grades M9's level/parent
  against `PdfHierarchyGold` only (matched by `SourceAnchor`, the same identity the gold format
  already keys on) and reuses `PdfHierarchyEdgeEvaluation` from the M8.1b evaluator for edge P/R/F1 -
  the legacy lane never enters this half. 8 tests (`PdfShadowLaneComparisonTests`).

  `PdfShadowWritebackComparison` (`DocxHeaderExtractor.Core.Pipeline`) runs both `OutlineWriteback` and
  `PdfProductWriteback` against fresh copies of the same source and compares which ORIGINAL paragraphs
  each touched (`LegacyModifiedParagraphs`/`NewModifiedParagraphs`/`SameSemanticMutations`), plus
  `NewAnchorFailures` and `NewLevelUnresolvedSkips` from the new lane's skip reasons. It does not
  redo either writeback's own text-corruption check: both `Apply` calls already throw and roll back
  the target on any mismatch, so `UnexpectedTextChanges` is a fail-closed sentinel (0 unless one of
  the two calls throws), not an independently computed cross-lane text diff - documented on the record
  itself so the number isn't read as a stronger guarantee than it is. 3 tests
  (`PdfShadowWritebackComparisonTests`).

  **Canary 076 (2026-08-24): PASS on the mechanical gate, real findings on substance.** One live
  `dhx pdf-hierarchy-facts 076_ICP_IACG08_Minutes_2023.docx --openrouter` call (model
  `qwen/qwen3.5-9b`, since no local model server was running and `--openrouter` was the runtime the
  user chose to authorize for this pass) produced a frozen artifact carrying both the facts/
  structures/groundings AND, from the same call, `legacyProductHeadings` (the
  legacy projection snapshot). `dhx pdf-shadow-compare` then replayed
  both lanes fully offline. Artifact stamped: `sourceDocumentSha256`, `model=qwen/qwen3.5-9b`,
  `backend=OpenRouter`, `promptSha256`, `routeConfigSha256`, `schemaVersion=4`.

  Stop-rule checklist, all five satisfied - proceed, do not stop:
  1. artifact v4 present: yes. 2. legacy `HeadingRecord[]` present: yes (12 emitted).
  3. M9 `FinalStructure` present: yes (12 emitted). 4. sourceFact bridge resolved: yes, 12/12 by fact
  id, zero `MissingInNew`/`ExtraInNew`/`AnchorMismatch`. 5. comparator ran end-to-end: yes, all three
  sections populated.

  Two real bugs surfaced only by running this for real (fixed same commit,
  `8406138`): `PdfValidatedStructure` had no `JsonPropertyName` attributes, so an offline
  case-sensitive `Deserialize<PdfHierarchyFactsRow>` silently left `SourceId` null instead of
  erroring - `PdfFinalStructureProjection.Project` then threw a bare `ArgumentNullException` nowhere
  near the cause. And `CompareHierarchy` compared `DocxSourceAnchor.StableId` (no `@`, as
  `ParagraphWalker` actually produces it) against `PdfHierarchyGold.SourceAnchor` (always `@`-prefixed,
  the hand-authored key-file convention) with no normalization - `GoldMatched` was silently stuck at 0
  on the first real run. Both are exactly the kind of gap only a live run exposes; the engine's own
  unit tests were self-consistent because their fixtures used the same convention on both sides.

  **Compatibility: 12/12 matched, 11/12 `TextMismatch` - verified intentional, not a regression.**
  Spot-checked 3 of the 11 (`b1`, `b3`, `b144`) directly in the artifact: legacy's `Text` is the raw
  PDF-observed rendering with spurious mid-word spaces (`"M I NUTES OF TH E INTE RNATIONAL COM PARISON
  PROG RAM"`), while legacy's OWN `OriginalText` and M9's `Text` both hold the clean canonical DOCX
  paragraph (`"MINUTES OF THE INTERNATIONAL COMPARISON PROGRAM"`). This is exactly the M9.1b design
  point working as intended on a real document - PDF extraction damaged the rendered line, and the
  product now shows what the document says instead. Occurrence, order, emit set and review semantics
  all matched with zero diffs. Classified as an intentional, reviewed delta, not a compatibility
  regression.

  **Hierarchy: 6/20 gold headings matched, 0 resolved level or parent among them.** Consistent with
  the artifact's own counters (`deterministicParentResolved: 0` for the whole document) - this
  specific meeting-minutes document has no numbered marker structure for `PdfMarkerHierarchyResolver`
  to find, at the fact level, before either lane even runs. Not a comparator defect: `EdgeMetrics`
  correctly reports `predictedEdges: 0` against `goldEdges: 10` rather than fabricating a match. Low
  gold coverage (6/20) means this canary does not yet support a strong hierarchy-quality claim for
  076; it confirms the grading path works, not that the level/parent resolver is good on this
  document.

  **Writeback: legacy wrote 0 paragraphs (by its own design), M9 wrote 2/12, skipped 10 honestly.**
  `OutlineWriteback` gates on `HeadingDecisionStatus.RequiresReview`, and
  the legacy projection sets that status on every PDF-first-authority
  heading unconditionally - so the legacy writeback for this whole route always writes 0, confirming
  by direct measurement (not just design reasoning) why `PdfProductWriteback` was deliberately built
  to not replicate that gate. `SameSemanticMutations: 0` follows directly from legacy writing nothing,
  not from a real placement disagreement. M9: 2 headings had a resolved level and were written,
  10 skipped `level_unresolved`, 0 anchor failures, 0 unexpected text changes.

  Report: `.verify-build/m9.4-canary-076/076-shadow-compare.json` (gitignored, local only), built from
  `.verify-build/m9.4-canary-076/076-hierarchy-facts.json`.

  **Canary 010 (2026-08-24): PASS on the mechanical gate; one real upstream bug found, explained, not
  fixed here.** Same protocol - one live `dhx pdf-hierarchy-facts` call (OpenRouter, `qwen/qwen3.5-9b`,
  unchanged flags/budget), `dhx pdf-shadow-compare` replaying offline. `keys/hierarchy/` has no gold
  for 010 either (`keys/legal-human/010...key` has the same merged-paragraph identity problem as 092 -
  `@body[1]/p[4]` alone holds "Chương I", "Điều 1", and "Điều 2" - so it was not auto-converted);
  flagged before running. Hierarchy migration: `not_measured`, same as planned for 092.

  Stop-rule: 54/54 matched by fact id, 0 `MissingInNew`/`ExtraInNew`/`AnchorMismatch` - bridge holds on
  a document 4x the size of 076. Proceed.

  **TextMismatch (most of 54): confirmed via spot checks as the same `canonical_source_improvement`
  as 076.** E.g. `b3`: legacy text `"Độclập-Tựdo-Hạnhphúc"` (PDF-observed, words run together, no
  spaces) vs M9 text `"Độc lập - Tự do - Hạnh phúc"` (clean canonical DOCX, exact 27-char span). Not a
  regression.

  **OrderMismatch flagged 52/54 - but the root cause is exactly 2 facts, not 52.** Sorting the legacy
  lane's own `(sourceId, paragraphIndex)` pairs shows a near-perfectly monotonic sequence (b3@7,
  b6@13, b9@14, b10@15, b12@16, b43@43, ... b514@489) with exactly one break: **b4 and b5 both ground
  to paragraph 417** (`"1.Tuân thủ quy định của pháp luật về an ninh mạng."`, a numbered sub-clause
  near the end of the document) instead of somewhere near paragraph 8-12, where their fact ids place
  them chronologically and where the document's title page actually is. Their legacy text (`"LUẬT"`,
  `"AN NINH MẠNG"`) is a coincidental substring match inside that unrelated clause's own text - the
  document's real title. Because `OrderMismatch` is computed as pairwise inversions, these 2
  badly-grounded facts each invert against nearly every fact that lies between their true and actual
  position, which is almost the whole document - inflating the reported count to 52 without there
  being 52 independent ordering defects.

  This is upstream canonical-grounding debt shared by both lanes (M9's grounding is built from the
  same `result.Headings` legacy already consumed via `PdfCanonicalGrounding.FromGroundedHeadings`, so
  both lanes agree on the same wrong paragraph - hence `AnchorMismatch: 0`, not a migration-introduced
  disagreement). Per M9.4's scope, not fixed in this pass - recorded as upstream debt, not reopened as
  M8 remediation this round. `PdfShadowLaneComparison.OrderMismatch`'s pairwise-inversion metric is
  itself worth revisiting later (e.g. reporting a minimal "elements to remove for monotonicity" count
  instead of every inverted pair) since it visibly overstates blast radius here - flagged, not changed
  mid-canary to avoid tuning the comparator around one run's numbers.

  **Hierarchy resolved nothing at the fact level either** (`markerPath=0`, `levelResolved=0`,
  `parentResolved=0` for all 55 facts) despite this being the "marker-heavy" document in the plan.
  Plausible same root cause as the text damage: `markerFamily` shows some `loose_labelled` hits, but
  `MarkerPath` (the strict parser) never fires - consistent with "Điều 1 Phạmviđiềuchỉnh" (no space
  after the number, words run together) not matching whatever separator the strict marker parser
  expects. Not confirmed further this pass; recorded as upstream debt alongside the b4/b5 grounding
  bug, both pointing at the same PDF-extraction damage as a plausible shared cause.

  **Writeback: legacy wrote 0 (same `RequiresReview` gate as 076), M9 also wrote 0** - all 54 facts hit
  `level_unresolved`, consistent with the fact-level finding above; there was nothing for the new
  writeback to contribute on this document either.

  Report: `.verify-build/m9.4-canary-010/010-shadow-compare.json` (gitignored, local only).

  **Canary 092 (2026-08-24): PASS, compatibility-only as planned.** Same protocol, OpenRouter
  `qwen/qwen3.5-9b`. No `--hierarchy-gold` passed - `keys/hierarchy/` still has no reviewed gold for
  092, and per the earlier decision it was not fabricated from `keys/typed-human/092...key` (same
  merged-paragraph identity problem as 010's key). `hierarchyMigration: {"status": "not_measured",
  "reason": "reviewed_hierarchy_gold_unavailable"}`.

  Stop-rule: 27/27 matched by fact id, 0 `MissingInNew`/`ExtraInNew`/`AnchorMismatch`. Proceed.

  **TextMismatch (20/27): spot-checked (`b73`) as the same `canonical_source_improvement` pattern.**
  `b73`'s canonical paragraph is itself a merged TOC line (`"2.Overview of Cache Operation
  3.Storing Responses in Caches 3.1.Storing Header and Trailer Fields ..."`, several section titles
  concatenated into one DOCX paragraph by the PDF→DOCX conversion); M9 slices exactly
  `"2.Overview of Cache Operation"` out of it (`span(0,29)`), while legacy's PDF-observed text reads
  `"2 Overview of Cache Operation"` - the familiar spacing/punctuation drift from PDF rendering, not a
  content difference. Not a regression.

  **OrderMismatch (26/27): different failure shape from 010, but the same verdict - shared upstream
  grounding, not a migration regression.** 010's break was 2 outlier facts; 092's is structural: most
  section-title facts (`sourceOrder` 9-21, e.g. `b73` through `b444`) all ground into a tight
  paragraph cluster (61-97) - the document's own merged Table of Contents block - rather than their
  real body occurrence, while a handful (`b29`, `b40`, `b44`, and the tail past `sourceOrder` 21) do
  land on the real body/back-matter position (verified: `b40` -> `"7.3. Caching of Sensitive
  Information"` correctly grounds to paragraph 1093, deep in the body). `AnchorMismatch: 0` again:
  both lanes agree on every one of these groundings, right or wrong - this is the same
  `parity != correctness` shape as 010's b4/b5, at a larger scale, and the same verdict applies: shared
  upstream debt (TOC-vs-body reconciliation for this RFC-style document), not fixed in this pass, not
  reopened as M8/TOC-dictionary remediation here.

  **Writeback:** legacy wrote 0 (same gate), M9 wrote 2, 24 skipped `level_unresolved`, 0 anchor
  failures, 0 unexpected text changes - fail-closed behavior held.

  Report: `.verify-build/m9.4-canary-092/092-shadow-compare.json` (gitignored, local only).

  **Emerging cross-corpus pattern worth carrying into the final summary:** across 010 and 092,
  `AnchorMismatch` has been 0 in every case where a shared grounding defect exists - the metric proves
  lane *parity*, not occurrence *correctness*, and the final M9.4 report must say so explicitly rather
  than let a clean `AnchorMismatch` column be read as "groundings are correct."

  **Canary 054 (2026-08-24): vacuous pass - 0 facts, nothing to compare.** Same protocol, OpenRouter.
  The pdf-first-authority route ran (not `skipped`, has a real `sourceDocumentSha256`) but validated
  **0 headings** for this ~170-page financial statement - `items: []`, `validatedStructures: []`,
  `legacyProductHeadings: []`. `pdf-shadow-compare` reports `legacyEmitted: 0`, `newEmitted: 0`, every
  diff class empty, `hasUnexplainedDiff: false` - true, but trivially so. Consistent with the earlier
  `03_tai_chinh_ke_toan` finding recorded elsewhere in this file (§79-ish: candidate counts low,
  mostly weak evidence for this document class) - not a new defect, and not something M9.4 has any
  business fixing. This document contributes no migration evidence either way.

  Report: `.verify-build/m9.4-canary-054/054-shadow-compare.json` (gitignored, local only).

  **Cross-corpus summary, all four canaries done.**

  | doc | facts | bridge | Missing/Extra/Anchor | TextMismatch | OrderMismatch | hierarchy | writeback |
  |---|---|---|---|---|---|---|---|
  | 076 | 12 | clean | 0/0/0 | 11/12, reviewed=improvement | 0/12 | gold: 6/20 matched, 0 resolved | legacy 0, M9 2/12 |
  | 010 | 54 | clean | 0/0/0 | 49/54, reviewed=improvement | 52/54 (root cause: 2 facts) | not_measured (no gold) | legacy 0, M9 0/54 |
  | 092 | 27 | clean | 0/0/0 | 20/27, reviewed=improvement | 26/27 (root cause: TOC cluster) | not_measured (no gold) | legacy 0, M9 2/27 |
  | 054 | 0 | vacuous | n/a | n/a | n/a | not_measured | n/a |

  Reading against the ten M9.5 conditions set before this run:

  1. Same frozen upstream input - yes, one live call per document, both lanes forked from it.
  2. Canonical occurrence parity, no unexplained diff - yes: `AnchorMismatch` is 0 in every non-vacuous
     run. Caveat carried forward from 010/092: this proves *parity*, not *correctness* - both lanes
     agreed on a demonstrably wrong paragraph in 010 (b4/b5) and on a TOC-cluster paragraph instead of
     the real body heading for most of 092. The M9.5 decision has to read "0 AnchorMismatch" as "the
     migration didn't make grounding worse," not as "grounding is right."
  3. Text parity, no unexplained diff - yes: every spot-checked `TextMismatch` (076, 010, 092, at least
     one document each) traced to the same root cause, PDF-observed text with damaged spacing/
     punctuation on the legacy side vs. clean canonical DOCX text on the M9 side - the exact M9.1b
     design point. None were unexplained.
  4. Output ordering parity - yes, with the same caveat as #2: `OrderMismatch` counts are inflated by
     shared upstream grounding debt (2 root-cause facts in 010, a TOC cluster in 092), not independent
     per-fact ordering defects. Flagged as a metric-design issue for `PdfShadowLaneComparison`, not
     fixed mid-canary.
  5. Emit/review semantics parity - yes, `ReviewMismatch` was 0 throughout every run.
  6. New writeback touches only canonical-anchored paragraphs - yes, `NewAnchorFailures` was 0 in every
     non-vacuous run; the one deliberate anchor-mismatch case was exercised in the writeback test
     suite, not needed live.
  7. No unexpected document-text mutation - yes, `UnexpectedTextChanges` was 0 throughout (fail-closed
     sentinel: no `Apply` call threw in any of the four runs).
  8. Hierarchy graded by gold, never by legacy - yes by construction: only 076 had gold and was graded;
     010/054/092 correctly reported `not_measured` instead of a fabricated number.
  9. Replay deterministic - yes, at the mechanism level (`ProjectsIdenticallyFromFactsReconstructedOffTheRowItemsAlone`,
     `ApplyingTheSameOutputTwiceIsDeterministic`); each live canary itself was run once per document, as
     the protocol required (one model call, not two).
  10. Cross-domain, no unexplained regression - yes across four different document classes (meeting
      minutes, dense legal, RFC/technical, and a financial statement that produced nothing at all).
      Every real diff found traces to either an intentional, reviewed M9.1b improvement or shared
      upstream debt that predates the migration and equally affects the legacy lane.

  **All ten conditions read as satisfied by this evidence.** Two things this run does NOT establish,
  and the M9.5 write-up should not imply otherwise: hierarchy correctness has real gold backing for
  only one of four documents (076, and even there only 6/20 headings matched with 0 resolved), and the
  two upstream-debt findings (010's b4/b5 mis-grounding, 092's TOC-cluster grounding) are real product
  gaps that remain open regardless of what M9.5 decides - migration parity is not the same claim as
  extraction quality. The M9.5 go/no-go call itself is left to explicit review rather than executed
  here.
- [ ] M9.5 cutover, only after M9.4 passes: route `HeaderExtractionPipeline` through FinalStructure
  and remove the legacy path in the same change. No feature flag - the dual lane exists once to
  prove the migration, then goes away.

  **Opened 2026-08-24, split into three steps so a regression can be localised to the right one:
  M9.5a product contract migration, M9.5b production route cutover, M9.5c remove the legacy path.**

  **Intentional Migration Contract - frozen before any cutover code, so a post-cutover reader never
  mistakes an intended M9 behavior for a regression:**
  1. *Text authority.* Legacy can surface PDF-observed text damaged by extraction (missing
     inter-word spaces, dropped punctuation); M9 always shows the canonical DOCX paragraph slice.
     Confirmed empirically across all three non-vacuous M9.4 canaries (076, 010, 092) - every spot-checked
     `TextMismatch` was this, never the reverse.
  2. *Hierarchy authority.* Legacy derives level from style clusters and falls back rather than
     abstain; M9's level is `int?` and abstains (`null`) rather than guess when the evidence
     conflicts or is absent. A resolved M9 level is evidence-backed; an unresolved one is an honest
     "not yet known," not a defect.
  3. *Writeback gating.* Legacy's `OutlineWriteback` blocks on `HeadingDecisionStatus.RequiresReview`,
     and the legacy projection sets that status unconditionally - so legacy writeback for the
     pdf-first-authority route always writes 0 by construction (confirmed by all three M9.4 writeback
     measurements: 076, 010, 092). M9's writeback gates on `Emit=true` + a resolved `Level` instead.
     A jump from "legacy wrote 0" to "M9 wrote N" is this contract taking effect, not a new capability
     smuggled in.

  **Nuance on M9 writeback targets (076/092): fail-closed and canonical, not asserted "hierarchy
  correct."** 076's gold matched only 6/20 headings with 0 resolved among them, yet M9 writeback still
  wrote 2 headings - those 2 are outside the gold-matched subset, which is not a contradiction, but the
  claim has to stay narrow: **M9 writeback targets are proven canonical and fail-closed; the hierarchy
  correctness of what gets written is not proven by gold.** Do not describe them as "correct hierarchy
  improvements." Same caveat applies to 092.

  ---

  **M9.5a - product contract migration (done, this commit). Type widening only; production routing
  unchanged.** `HeadingRecord.Level` changed from `required int` to `required int?`
  (`Models/DocumentOutline.cs`) - `required` still forces every caller to state the field, `?` lets
  that statement honestly be "unresolved." No fallback, no sentinel (`0`/`-1`), no dropping the
  heading when `Level` is null - exactly the four rejected options and why they were rejected: a fake
  level fabricates hierarchy, a legacy-value fallback reopens dual authority, dropping the heading
  turns `Emit=true` into a false `emit=false`, and a sentinel is an undocumented magic value someone
  will eventually misread as a real level.

  Ripple fixed across every consumer the compiler found (28 initial errors, all in `src/`, none in
  production wiring logic itself): `OutlineFormatter` (json/markdown/text/xml/csv all format `null`
  without crashing - locked by `UnresolvedLevelFormatsWithoutCrashingInEveryShape`), `ReviewBundle`/
  `CorrectionMemory` (`PredictedLevel` now `int?`, distinct from the existing `0 = non-heading`
  convention), `Evaluator` (`Parents()` treats an unresolved level as "cannot be placed in the tree,"
  giving it no parent and never letting it parent anyone - not a heuristic level), `OutlineWriteback`
  (`Skip()` now returns `"level_unresolved"` for a null level instead of falling through to a crash in
  `OutlineLevel = heading.Level - 1` - locked by `Heading_with_unresolved_level_is_skipped_not_defaulted`),
  `StructuralRecovery`/`StructuralHierarchyResolver`/`PdfMarkerHierarchyResolver`/`SiblingShapeAudit`/
  `PdfTaggedEvidenceOutline`/`PdfVisualTextRecovery` (internal hierarchy-inference helpers, all
  currently fed only by routes that always set a real level - defensive null-handling added, no
  behavior change), `McpHeadingResult.Level` and `Stats.MaxLevel` (Web/MCP contracts widened /
  null-coalesced), and `wwwroot/index.html` (tree view shows "H?" and skips the indent-math crash for
  a null level; gold-eval diff shows "chưa xác định" instead of the literal string "null").

  `OutlineGroundingValidator` (`AgentHarness/DocumentAgentValidator.cs:78`) needed **no change**:
  `heading.Level is < 1 or > 9` is a C# relational pattern, and relational patterns never match `null`
  - so a null level was already treated as valid before this migration touched anything, satisfying
  "validator accepts null as unresolved" for free.

  Verified invariants 1-10 from the plan:
  1-2 (existing/new values serialize and stay null respectively) - `UnresolvedLevelFormatsWithoutCrashingInEveryShape`.
  3-4 (no default-to-0/1, heading never disappears because of null) - by construction, no `??` default
  was added anywhere a *real* production value could reach; every `??` fallback added is in a
  display/grouping-only path (`OutlineFormatter`'s indentation math, `NavigationCollapseReport`'s
  sibling-grouping key) that never writes back into `HeadingRecord.Level` itself.
  5 (validator accepts null) - confirmed above, no code change needed.
  6 (writeback never emits `outlineLvl` for null) - `Heading_with_unresolved_level_is_skipped_not_defaulted`.
  7 (Web/MCP/CLI don't crash) - `WebUiScriptSyntaxTests` still green; full solution + Web + Mcp all
  compile with the widened type.
  8 (non-pdf-first routes keep current output) - confirmed by full suite: **964 passing, same 15
  pre-existing fixture-dependent failures as every prior M9 commit, zero new failures.**
  9 (no production routing change) - `RunPdfFirstAuthorityPipelineAsync` and every other route
  untouched in this commit; only the type each already passed through got wider.
  10 (full suite, no new failures) - confirmed above.

  M9.4's frozen artifacts/replay tests need no re-run for this step - it is type-widening only, and
  `PdfProductHeading.Level` was already `int?` since M9.1, so nothing in the M9 lane itself changed
  shape.

  Next: **M9.5b**, wire `RunPdfFirstAuthorityPipelineAsync` to build its `HeadingRecord[]` from
  `PdfFinalStructureProjection` → `PdfOutputDecisionPolicy` → `PdfProductOutputSerializer` instead of
  the legacy projection, and swap this route's writeback tool to `PdfProductWriteback`. Per
  investigation before this commit: `PdfFirstValidatedFallback` defaults `false` and neither Web nor
  MCP ever sets it, so M9.5b's live blast radius is zero until something opts in - the whole
  `DocumentAgentHarness`/CLI-formatter/Web-frontend/MCP stack is built directly on
  `DocumentOutline`/`HeadingRecord` with no abstraction layer over it, which is exactly why M9.5a kept
  that contract's shape (widened, not replaced) rather than swapping the route's return type to
  `PdfProductOutput` directly.

  **M9.5b - production route cutover (done, this commit). Pure wiring; no legacy code deleted, no
  upstream debt touched.** `RunPdfFirstAuthorityPipelineAsync` (`HeaderExtractionPipeline.cs`) no
  longer calls the legacy projection at all. Both its branches (`pdf-first-authority-v1` with a
  sibling PDF, `docx-authority-v1` without one) already return a `RouteExecutionAudit` carrying
  `ValidatedStructures` and `HierarchyFacts` - confirmed before writing any code, since M9's pipeline
  needs both and a missing one on either branch would have forced a design change. `PdfProductOutput`
  is materialized exactly once per request:
  ```
  audit.ValidatedStructures + audit.HierarchyFacts + PdfCanonicalGrounding.FromGroundedHeadings(rawHeadings)
      -> PdfFinalStructureProjection.Project
      -> PdfOutputDecisionPolicy.Decide
      -> PdfProductOutputSerializer.Serialize
      -> PdfProductOutput
  ```
  `rawHeadings` is `result.Headings` straight off the route (before any legacy projection ever
  touched it) - the same source `PdfCanonicalGrounding.FromGroundedHeadings` was already reading in
  the `pdf-hierarchy-facts` diagnostic command, so both the diagnostic snapshot path and the
  production path derive groundings identically.

  That one `PdfProductOutput` forks two ways, neither reconstructing the other:
  - `PdfProductOutlineAdapter.ToHeadingRecords` (new, `internal`, pure structural copy - no
    re-derivation of `RequiresReview`/`Level`/`Text`) builds the `HeadingRecord[]` for the
    `DocumentOutline` compatibility shell every existing consumer still reads unchanged. Fields
    `HeadingRecord` has no M9 authority for (`OriginalText`, `InlineBody`/`InlineBodySpan`, `StyleId`,
    `Evidence`, `ModelConfirmed`/`CriticConfirmed`, `Disputed`, `CalibrationSamples`) are left at their
    honest default rather than filled from anywhere else - not "0 confidence", an explicit
    `ConfidenceBasis = "pdf-final-structure-validated"` label so a reader knows why. 6 tests
    (`PdfProductOutlineAdapterTests`).
  - `DocumentOutline.ProductOutput` (new, `[JsonIgnore]`, never part of the JSON contract) carries the
    same `PdfProductOutput` instance forward so a later writeback step acts on it directly.
    `PdfProductWritebackTool` (new, `AgentHarness/DocumentActionTool.cs`, mirrors
    `OutlineWritebackTool`'s shape) reads `outline.ProductOutput` and calls `PdfProductWriteback.Apply`
    - never `HeadingRecord`, never a reconstruction. Throws `InvalidOperationException` if
    `ProductOutput` is null (a route other than pdf-first-authority produced this outline) rather than
    silently doing nothing. 2 tests (`PdfProductWritebackToolTests`), including that a null
    `ProductOutput` fails closed instead of writing.

  CLI wiring (`Program.cs`): the action-tool choice for `--write-docx` is now conditional on
  `o.Pipeline.PdfFirstValidatedFallback` - `PdfProductWritebackTool` for `--pdf-first`,
  `OutlineWritebackTool` unchanged for every other route (Web's own tool selection untouched, since
  Web never sets that flag).

  A missing `audit` (route found nothing to validate) produces `PdfProductOutput("sha", [])` directly
  - an honest empty result, not a fallback into the legacy projection. No `try`/`catch` was added
  around the M9 calls: a real projection failure propagates and fails the request, per the explicit
  "no catch → legacy fallback" condition - dual authority was exactly what M9 exists to remove.

  **A pre-existing policy interacts with this, worth stating precisely so it is not later misread as
  an M9.5b defect.** `DocumentAgentHarness` blocks any write action while a model-sourced heading still
  has `DecisionStatus == RequiresReview` (`AgentSkillRequirements.HumanReviewBeforeWriteback`, default
  `true` - a deliberate SKILL.md-level safety gate, unrelated to M9). `PdfProductOutlineAdapter` sets
  `Source = Model` and `DecisionStatus` from the real `RequiresReview` value, which
  `PdfOutputDecisionPolicy` sets to `true` for every heading it emits today. So through the CLI's
  `--write-docx` harness path specifically, this gate still blocks the action-tool call before
  `PdfProductWriteback` is ever invoked - which is consistent with, not different from, every M9.4
  canary's writeback measurement (legacy wrote 0 on this same route for the same structural reason,
  via `OutlineWriteback.Skip`'s own `RequiresReview` check). `PdfProductWritebackTool` is exercised
  directly by its own tests (bypassing this specific harness gate the same way the legacy
  `OutlineWritebackToolTests` already does, by using `AutoAcceptedCalibrated` in the test fixture) to
  prove the writeback mechanism itself is correct; whether/when that harness-level policy should treat
  M9's `RequiresReview` differently from legacy's is a separate, unopened question - not touched here.

  Verified invariants from the plan: `RunPdfFirstAuthorityPipelineAsync` has zero
  legacy-projection call sites left (`grep` confirms); `Headings` is a projection of
  `PdfProductOutput`, never legacy `HeadingRecord[]`; `Level = null` passes through unchanged;
  `Text`/anchor fields come from `DocxSourceAnchor` via the product heading; ungrounded facts are
  never emitted (M9.2's own invariant, unchanged); `Level == null` still serializes to the API/CLI
  while `PdfProductWriteback` skips it `level_unresolved`; no `catch`-to-legacy-fallback exists; with
  `--pdf-first` off, Web/MCP/CLI behavior is unchanged (confirmed: 972 passing, same 15 pre-existing
  failures, zero new); `pdf-hierarchy-facts` still calls the legacy projection for its
  `legacyProductHeadings` snapshot, untouched.

  Next: **M9.5c**. After this commit, the legacy projection has zero production callers left -
  `grep` confirms its only remaining reference is the `pdf-hierarchy-facts` diagnostic command's
  `legacyProductHeadings` snapshot for M9.4. Decide whether it stays as an evaluation-only helper for
  that command and `PdfShadowLaneComparison`, or whether M9.4's frozen artifacts should be re-captured
  so it can be deleted outright.

## M9.5c - legacy authority cleanup (done, this commit)

The former policy no longer exists in the production pipeline namespace. Its unchanged
historical projection now lives at `Eval/PdfLegacyValidatedOutputPolicy` and is reachable only from
`dhx pdf-hierarchy-facts`, where it creates the `legacyProductHeadings` M9.4 comparison snapshot.
The new name and namespace make the dependency direction explicit: it is evaluation-only and cannot
be a fallback authority for `extract --pdf-first`.

The production PDF-first route remains exactly one authority chain:
`ValidatedStructure -> PdfFinalStructure -> PdfOutputDecision -> PdfProductOutput`, with
`DocumentOutline` only as a compatibility shell and `PdfProductWritebackTool` consuming that same
`ProductOutput`. There is no catch/retry path from M9 back to the legacy projection. Normal DOCX/Web
writeback still uses `OutlineWritebackTool` unchanged.

Automatic PDF-first writeback is **not** enabled by this cleanup. The writeback implementation and
authority have migrated, but existing `HumanReviewBeforeWriteback` still blocks a model-sourced
`RequiresReview` result in `DocumentAgentHarness`. Changing that permission is a separate product
policy milestone, intentionally outside M9.5c.

Verification: M9 authority/writeback locks pass 18/18. Full solution test remains at 972 passing
with the same 15 pre-existing fixture/route failures and zero new failures.

## M9 CLOSED (2026-08-25)

Production authority converged onto exactly one path:

```
ValidatedStructure
    -> FinalStructure
    -> OutputDecision
    -> ProductOutput
        +- DocumentOutline compatibility adapter
        +- PdfProductWriteback
```

Legacy is reachable only as `Eval/PdfLegacyValidatedOutputPolicy`, called solely by the
`pdf-hierarchy-facts` diagnostic for M9.4 historical comparison - it has no path back into
production authority or fallback.

- [x] M9.1  FinalStructure
- [x] M9.1b canonical DOCX grounding
- [x] M9.2  OutputDecisionPolicy
- [x] M9.3  serializer + product writeback
- [x] M9.4  shadow migration benchmark
- [x] M9.5a nullable hierarchy contract
- [x] M9.5b production routing cutover
- [x] M9.5c legacy production cleanup

**Baseline at close:** M9 locks 18/18 pass. Full suite 972 passing / **15 known pre-existing
failures** (fixture/route-dependent, unrelated to M9, present before M9 started and unchanged by
every M9 commit - not "all tests green"). Zero new regressions introduced by M9.

Extraction-quality findings surfaced during M9.4 (054 recall=0, 010 b4/b5 grounding, 092 TOC/scope,
low hierarchy coverage) and the pre-existing `HumanReviewBeforeWriteback` product-safety question are
deliberately **not** M9's to fix - recorded as debt, carried into M10.

## M10 - Quality & Accuracy Improvement (opened 2026-08-25, not started)

Different question from M7-M9. M7-M9 asked *does the pipeline have correct authority, audit, replay,
validation and production wiring* - answered, closed. M10 asks *are headings found, completely and
correctly*, measured first-loss along the real path:

```
gold heading -> candidate generation -> filter -> ranking/budget -> LLM/VLM proposal
   -> validator -> grounding -> scope -> hierarchy -> output
```

Priority order (evidence-based, from M9.4's canaries - not re-litigated here, see M9.4 write-up
above for the underlying findings):

1. **054** - heading recall/zero-output failure (the pdf-first-authority route validated 0 headings
   for this ~170-page financial statement; nothing to compare, nothing written).
2. **010** - canonical grounding ambiguity (`b4`/`b5` both ground to paragraph 417, a numbered
   sub-clause unrelated to their true title-page position - shared upstream debt, not migration-
   introduced, confirmed in the M9.4 write-up).
3. **092** - TOC/scope/appendix cascade (most section-title facts ground into the document's own
   merged Table-of-Contents paragraph cluster instead of their real body occurrence).
4. TableLike short-numbered false positives.
5. Hierarchy level/parent coverage.

Deliberately NOT starting with hierarchy: M8/M9 already showed most hierarchy loss traces back to an
upstream cause (grounding, recall, scope) rather than the hierarchy resolver itself - fixing the
resolver first would very likely just move the same loss downstream. No M10 work has started as of
this entry; the above is the agreed priority order only.

## M10 — quality and accuracy

M10 does not deterministically replace the model lane. It uses deterministic, replayable measurement
to find which layer actually loses a heading first, so the layer that gets changed is the one at
fault. The remedy that follows may be deterministic, or it may be prompt/context/model work - that
is an outcome of the measurement, not an assumption of it.

- [x] M10.1a/b 054 provenance and stage census. The route and profile differ between the frozen M8.1c
  stage-eval run (wide+supplement, budget 160, 29 validated) and the live M9.4 production canary
  (0 validated), but both agree where it matters: no gold heading had ever been validated. Note for
  future premises - the M9.4 canary evidence existed on the pushed branch while the local tree lacked
  it, so absence in a working tree is not absence in project history.
- [x] M10.1c/d first-loss classification, then two corrections that changed the answer:
  - `present_fragmented_pdf_text_fact` does not mean the characters were fragmented; it means the
    gold text was found in a raw window but not as an exact source line. Zero of the sixteen
    ambiguous cases had a broken heading line.
  - The ambiguity was caused by the gold, not the document. The heading reads `SECTION II: EXECUTIVE
    SUMMARY` while gold stored `Executive Summary`, so containment matched every cross-reference,
    table-of-contents line and merged window that quoted it.
- [x] M10.1e-0a 054 gold repaired to occurrence-safe entries (`054-v3-occurrence-reviewed`). The
  previous generation was rebased by canonical title in document order, which put 13 of 24 entries on
  body prose or contents lines and dropped the `SECTION N:` label from 10 more. Measured with no
  production change: ambiguous-occurrence first loss 16 -> 0, headings reaching selection 2 -> 18 of
  23. The "catastrophic recall" reading of this document was largely an evaluation defect. One node
  stays unresolved: its literal title exists only in the contents table, and the body node it refers
  to is rendered across separate paragraphs, so no single stable id describes it.
- [x] M10.1e-0b evaluator made occurrence-safe:
  - versioned gold keys resolve by shape (`.v{N}-{label}`) instead of one hard-coded label, and two
    generations sharing a stem stay two matches so the caller reports an ambiguous key. Until this
    was fixed the reviewed gold did not resolve at all while the superseded generation did, and was
    measured silently.
  - a reviewed PDF occurrence bridge records which source lines each gold heading occupies. Twenty-one
    of twenty-three matched exactly one line and nothing else; two headings the renderer broke across
    lines were reviewed by hand. Required coverage is derived as the lines carrying text, so a
    candidate that omits a punctuation-only line still represents the heading.
  - first loss ranks a heading by candidates built from those lines, never by text containment.
    Candidate provenance is taken from a diagnostic snapshot at the ranking build, so evaluation does
    not rebuild the candidate graph and cannot drift from it.

**054 baseline, occurrence-reviewed gold (23 entries):**

| measure | value |
|---|---|
| selected at budget 160 | 18/23 |
| first loss `ranking_or_budget` | 5/23 |
| representation `standard_block` | 21/23 |
| representation `window_only` | 2/23 |

- [x] M10.1e-A1 rank decomposition for the three standard-block misses. Three different mechanisms,
  so they are not one fix:
  - `AVAILABILITY OF INFORMATION` - rank 166, score 0.44, no negative evidence at all; the top-160
    cut sits near 0.54. A separate scoring question, not a penalty question.
  - `SUMMARY INFORMATION` - rank 1054, score 0.29. Identical positive and ambiguity signals to
    `AVAILABILITY`; the only difference is a `header_footer_zone` penalty.
  - `XXI: APPENDIX` - rank 1029, score 0.39, also penalised by `header_footer_zone`. Its second
    representation is a window scoring 0, so multiplicity is not causal here.
  - `SECTION XIV`/`SECTION XIX` - represented only as windows at rank 2013/2015; a grouping question,
    handled separately.

  Correction to an earlier reading: the correct candidates for XIV/XIX are in the ranked population,
  so a large enough budget would reach them. The honest statement is that budget 160 does not, and
  raising it far enough would be a poor remedy - and `selected` only means a candidate reaches the
  model lane, not that the model and validator then accept it.
- [x] M10.1e-A2 `header_footer_zone` population audit. The signal is genuinely mis-scoped and is
  nonetheless **not** what blocks these headings.

  `IsHeaderFooterZone` is purely geometric - the top or bottom 8% of a page - and asks for no
  repetition, so a section heading printed at the top of its page is penalised for being there. It
  marks 1466 of 11481 lines in 054; 93 are page numbers, 23 repeat across pages, 781 are already
  table-like, and **662 are penalised on position alone**. Those 662 are content: `SUMMARY
  INFORMATION`, `SECTION I: OVERVIEW`, `Equity and Borrowings`, `Introduction`, body prose.

  Removing the penalty and re-scoring with the existing ranker moves the section headings sharply
  within the group that was already selected (`SECTION I` 96 -> 25, `II` 98 -> 27, `III` 99 -> 30),
  costs little (6 candidates enter the top 160, 6 leave), and **recovers nothing**: selected at
  budget 160 stays 18/23, every one of the five misses stays outside, and `AVAILABILITY` gets worse
  (166 -> 174) because others rise faster.

  So this is recorded as its own defect, not as an M10.1e remedy. Fixing it would change a signal
  that touches 1466 lines and would not have recovered a single heading. Any future repair needs a
  cross-domain population audit of its own; there is no evidence for one from here.
- [ ] M10.1e-A2 follow-up, deferred: classify every line the signal marks in 054,
  then measure both sides of removing the penalty - which real headings it recovers, and how many
  running headers, footers and page numbers it would let into the selected budget. A signal that
  penalises two headings is not thereby wrong; measure it on the population it classifies, which is
  the lesson the multi-entry signal taught in M8.1e. Cross-domain holdout only if 054 shows material
  harm.
- [x] M10.1e-B1 grouping trace and clean-block counterfactual for `SECTION XIV`/`SECTION XIX`.

  Both headings are printed across two lines. The grouper makes one block per line, so the block
  holding the first line ranks 128 and 133 - inside the budget - while the only candidate carrying
  the whole heading is a four-line window at rank 2013/2015, crushed to score 0.01 by
  `long_marker_body_window`. Rebuilt as one clean block from the reviewed occurrence and scored by
  the existing ranker, both come out at 0.53 and rank 166: better than the window, **worse than the
  truncated first line at 0.71/rank 128**, because completeness adds a `multi_line_boundary`
  ambiguity.

  Grouping fragmentation: PROVEN. Scoring interaction: PROVEN on both. Grouping-only remediation:
  REJECTED - merging the heading correctly makes it rank worse, so nobody should reach for the
  grouper on seeing `window_only` alone.

  Coverage is now reported on its own axis (`CandidateCoverage`, `BestPartialCoverageRank`,
  `SelectedCoverage`) so the evaluator can say what actually reached the model - here, a truncated
  heading - without renaming where the complete occurrence was blocked.
- [ ] M10.1e-B1 superseded note:
  the same reviewed occurrence represented as one clean standard block, scored by the existing
  ranking code. A large rank recovery would make representation the causal owner; a small one would
  mean window-only representation is not what is holding them back.
- [x] M10.1e-A3.1 unnumbered heading ceiling. **PROVEN, cross-domain.** The best score any candidate
  without a numbering marker can reach is `0.44` - base 0.10 plus standalone 0.18 plus layout
  prominence 0.16 - and that ceiling holds identically on all four corpora. Whether it excludes
  depends on how crowded the marker-bearing candidates are: with a cut of 0.54, 054 (financial, 1680
  unmarked candidates) and 010 (Vietnamese legal, 104) select **zero** of them, while 092 (cut 0.44)
  and 076 (cut 0.00, pool smaller than the budget) let them through. Two independent domains
  reproduce the exclusion, so this is a property of the scorer rather than of financial documents.

  No candidate anywhere combines "no marker" with `opens_content`, which is more suspicious than the
  weight itself: the remaining positive path appears unreachable without a marker. Next is a
  reachability audit of the existing positive signals, then a collateral rerank - and any remedy must
  rest on evidence a heading actually has. Absence of a marker is not evidence, so no bonus may be
  paid for it.
- [x] M10.1e-A3.1-C0 reachability audit. `opens_content` has **never fired**: 0 of 2043 candidates
  on 054, 0 of 1065 on 010, 0 of 499 on 092 - 0 of 3607, with or without a marker.

  The predicate asks whether the next block's text is longer than 70 characters and ends in a full
  stop, but it reads `PdfCandidateContext.NextBlocks`, which is built by `PromptExcerpt` and cut at
  180 characters for the model prompt. A real body paragraph is longer than that, so the excerpt is
  cut mid-sentence and never ends in a stop: every excerpt that hit the 180 cap - 352 on 054, 107 on
  010, 100 on 092 - failed the test, without exception. The few excerpts that do end in a stop are
  too short to pass the length test.

  So the positive path for an unnumbered heading was designed and is dead code, and
  `no_body_opening_evidence` is attached to the entire population, where it distinguishes nothing.
  With the signal alive the ceiling would be 0.10 + 0.18 + 0.16 + 0.12 = 0.56, above the 0.54 cut
  that excludes unnumbered headings on 054 and 010. That makes the remedy a repair of an existing
  owner rather than a new signal, and it still rests on evidence the heading has - a body paragraph
  follows it - not on the absence of a marker.

  **Contract leak, not a missing stage.** `PdfCandidateContextBuilder` serves two needs from one
  field: `NextBlocks` is the right shape for a prompt and the wrong shape for structural evidence,
  and the scorer reads it for the latter. Serialization is the last consumer of a fact and must not
  become the source of truth for deterministic logic - the same rule that keeps a model proposal
  from becoming a source fact. The same shape explains several findings in this milestone: a
  geometric zone used as artifact evidence, a grouping shortcut driving ranking, and text similarity
  used as occurrence identity.

  The repair belongs inside the existing owner: let the scorer read the next block's own text and
  leave `NextBlocks` alone so the prompt contract does not move. No new stage, no detector service -
  and `opens_content` must stay a ranking signal that gives a true candidate a chance to reach the
  model, never a classifier that decides what a heading is.

  Not fixed here: C1 must first measure what reviving the signal costs, since +0.12 would reach every
  candidate that precedes a paragraph, including paragraphs themselves, and the ambiguity
  distribution feeding escalation tiers changes with it.
- [x] M10.1e-A3.2 multi-line penalty. **NOT PROMOTED.** Candidates carrying `multi_line_boundary`
  reach the selected budget on all four corpora (2.2%, 2.8%, 100%, 1.7%), so the signal is not
  broadly mis-scoped the way the header/footer one is. `SECTION XIV`/`XIX` remain a representation
  and scoring interaction - complete at 0.53 against a 0.54 cut - and are recorded as coupled debt
  rather than a reason to change a global weight.

- [x] M10.1e-A3.1-C1 collateral counterfactual. Changing only the evidence source - the next block's
  own text instead of the prompt excerpt, with the predicate, thresholds and weight untouched -
  changes **nothing**: `opens_content` still fires 0 times on all four corpora, the cut does not
  move, no candidate enters or leaves the budget.

  The truncation was real but was not what stopped the signal. No block ends in a full stop: of the
  blocks longer than 70 characters, 0 of 1013 on 054, 0 of 582 on 010 and 0 of 306 on 092 end in one,
  because PDF extraction emits sentence-final punctuation as its own line. The predicate assumes the
  punctuation survives block construction, and on these corpora it does not.

  Repairing the owner is therefore **not sufficient**, and the remaining repair would be a new
  predicate - a new rule, which has to earn its place through the same population audit, collateral
  measurement and holdout as any other. With benefit measured at zero there is nothing to weigh
  against that cost, so nothing is promoted.

## M10.1e — closed, diagnostic only, no production change

Five hypotheses were investigated and none was promoted; no production line changed in this
milestone. Three things were kept apart throughout, and the distinction is the result:

> an observed defect is not the causal owner of the current loss, and a causal owner is not
> automatically a safe remediation.

**Proven.** 054's reviewed gold has 23 occurrences and 18 reach the budget with full coverage. Two
of the five misses reach it truncated, so a partial occurrence is inside the budget while the
complete one is not. The score ceiling for a candidate without a numbering marker is 0.44 on 4/4
corpora. `opens_content` is unreachable: 0 of 3607 candidates across three corpora. Two headings are
fragmented by grouping, and their complete representation scores below their own truncated first
line. `header_footer_zone` is geometrically mis-scoped and penalises 662 lines of ordinary content
on 054 alone.

**Rejected or not promoted.** Raising the candidate budget - 6.9x the workload for three headings,
and two of the five cannot be reached that way at a sensible cost. Repairing `header_footer_zone`
for these misses - it recovers none of them. Repairing grouping alone - merging a split heading
correctly makes it rank worse. Changing the `multi_line_boundary` weight globally - multi-line
candidates reach the budget on 4/4 corpora. Feeding `opens_content` the full block text - zero
behavioural delta. Any bonus for the absence of a marker - absence is not evidence.

**Remaining debt, none scheduled.** The `opens_content` predicate assumes sentence-final punctuation
survives block extraction, which is false on every corpus observed. `no_body_opening_evidence` is
attached to the whole population and distinguishes nothing. The scorer has a structural 0.44 ceiling
without a marker. Fragmented multi-line headings are a grouping-and-scoring interaction, not either
alone. And deterministic scoring still reads a string built for the model prompt - a real
responsibility leak, but demonstrably not the cause of the failure it was suspected of, so it
belongs to an architectural refactor with its own behaviour-preservation evidence, not to an
accuracy fix that would then claim credit for recall it did not deliver.

**Reopen only** on new evidence, a new corpus, or a product requirement that forces unnumbered-heading
recall. Not to keep looking for a fix.

M10.1e is closed at `69750cd`. No remaining work is implied by the recorded debts; the re-entry rules
are in *Promotion invariants* above.

## M10.2 — 010 canonical grounding, audit only

`AlignToDocx` is the model-free step that decides which DOCX paragraph a PDF block is grounded to.
The M9.4 010 canary (`171c603`) recorded that `b4` and `b5` both grounded to paragraph 417 - an
unrelated numbered sub-clause - and that both lanes agreed, so it is upstream grounding debt rather
than a migration regression. M10.2 asks what the matcher actually does, and changes nothing.

- [x] M10.2-0 internal grounding snapshot. `AlignToDocx` gained an optional passive sink recording,
  per block, the needle it searched for, the paragraph and span it chose, and which of the matcher's
  four existing attempts produced the match. Production keeps its own signature and delegates to the
  same core, so there is no second alignment implementation to drift. Parity locks: accepted traces
  reconcile one-to-one with the aligned headings on paragraph index and span, every candidate
  considered is accounted for (unmatched blocks are recorded, not dropped), and repeated runs
  describe the same run. The canonical paragraph texts the matcher searched are recorded too, so an
  audit of how ambiguous a needle was cannot answer from a canonicalisation that has drifted.
- [x] M10.2-1 010 measured. **The exact canary outcome was not reproduced, and the audit says so.**
  The canary ran the model-backed PDF-first lane, whose accepted-block set is decided by a model and
  is not on disk; the model-free routes align a different population. Two populations were measured:
  - the narrow production route (`TryBuild`) retrieves only 4 candidates on 010 and aligns all 4.
  - the retrieval superset - the same matcher over every candidate retrieval produces - aligns 479 of
    1065. Rates here describe the matcher, not production, which aligns a subset of it.

  **Retrieval-population result (479 accepted matches):**

  | measure | value |
  |---|---|
  | branch `CursorFresh` | 468 |
  | branch `CursorRelaxed` / `FromZeroFresh` / marker reconstruction | 7 / 2 / 2 |
  | match shape `substring_word_bounded` | 438 |
  | match shape `whole_paragraph` | 39 |
  | match shape starting or ending inside a word | 2 |
  | needle occurs in exactly one paragraph | 438 |
  | needle occurs in 2-3 / 4-10 / more than 10 paragraphs | 27 / 9 / 5 |
  | blocks landing before their predecessor | 2/478 |

  Two readings, kept separate:
  - **Many blocks per paragraph is not the defect.** 70 paragraphs received more than one block, up to
    9. 010's DOCX merges a whole article into one paragraph while the PDF renders each sub-clause
    separately, so the spans are disjoint and in order. This is the one-to-many case working.
  - **The defect shape is an unranked first fit.** 41 of 479 matches had more than one paragraph
    containing the needle, and the matcher has no tie-break beyond "first paragraph at or after the
    cursor". `b5`'s needle canonicalises to `anninhmạng`, present in 211 of the document's
    paragraphs; `b4`'s canonicalises to `luật` (4 characters, the guard rejects only below 4),
    present in 63. In the retrieval population they land on the title page - p9 and p11 - because the
    cursor is still low there. The canary's paragraph 417 quotes both words in one clause, so the same
    first-fit rule reaches it whenever the preceding accepted blocks have pushed the cursor past the
    title page. Which paragraph a short needle wins is therefore a function of which blocks preceded
    it, and in the analyst lanes a model decides that.

  Locked as **current production behaviour faithfully observed**, not as desired behaviour. No
  remedy follows from this milestone.

### M10.2-A — CLOSED / TRIGGER-GATED

Closed at `2c117cf` with zero production change. "The defect is real" was too loose a claim to close
on, so the evidence is split by what it actually licenses:

**Proven**
- the matcher grounds by first-fit substring occurrence.
- occurrence multiplicity can be very high: `anninhmạng` occurs in 211 paragraphs, `luật` in 63.
- for an ambiguous needle the result depends on cursor position, and so on block order.
- one DOCX paragraph receiving many PDF blocks can be legitimate grounding (010 merges an article
  into one paragraph while the PDF renders each clause separately).

**Not proven**
- that the production analyst lane currently misgrounds `b4`/`b5` to paragraph 417. Not reproduced.
- that first-fit ambiguity causes material product loss today. The audit measured the retrieval
  superset; production aligns a model-chosen subset of it, so nothing here can be read backwards
  into a production failure rate.

This split is what forbids the two tempting fixes. Requiring an exact whole-paragraph match, or
rejecting short needles outright, would both break the 70 paragraphs that legitimately receive more
than one block. A weak identity mechanism is not licence for a blunt rule against the valid case.

**Reopen triggers**
- a persisted analyst-lane artifact reproduces a wrong canonical anchor.
- reviewed occurrence gold exposes material grounding errors.
- another corpus reproduces ambiguous first-fit misgrounding.

Until one fires: no `GroundingResolver`, no change to the `IndexOf` search, no ranking or tie-break.

## M10.3 — 092 scope lifecycle, audit only

092's hierarchy is limited by what reaches the validator, so a better resolver cannot help while the
parent headings are still being filtered upstream. M10.3 asks one question: does an appendix scope
opened from a contents line stay open over real body, and does that mislabel body headings? It does
not touch `TableLike`, hierarchy, or any model lane.

- [x] M10.3-A1 scope lifecycle trace, model-free. `StructuralScopeTracker` gained an optional passive
  sink recording, per block, the scope it arrived with, the scope it left with, and the latch state
  that decided the difference. `PdfCandidateContextBuilder.Build` passes the sink through. No new
  resolver, no behaviour change; the tracker's state machine is unchanged and is not copied anywhere.

  **092 measured over 499 candidate blocks, pages 1-35:**

  | measure | value |
  |---|---|
  | `document_body` -> `appendix` | 287 |
  | `table` -> `appendix_table` | 59 |
  | `document_body` -> `quoted_replacement` | 92 |
  | `document_body` -> `document_body` | 43 (all on pages 1-4) |
  | blocks matching the appendix pattern | 3 |
  | times the appendix latch reset | 0 |

  **Scope-lifecycle defect: proven.** The latch is set on page 4 by block `b43`, whose text is
  `Appendix A Collected ABNF Appendix B Changes from RFC 7234 Acknowledgements Index` - a contents
  line, arriving with scope `document_body`. `_appendix` has no reset path anywhere in the tracker,
  so it stays on for the remaining 443 blocks, pages 4 through 35. The document's real appendices
  only start on page 32 (`b485`, `b490`). Pages 5-31 are normative RFC body relabelled as appendix.

  This is not a near-miss reading: on page 5 the blocks relabelled `appendix_table` are
  `1 Introduction`, `1 1 Requirements Notation`, `1 2 Syntax Notation` and `1 2 1 Imported Rules` -
  precisely the parent-capable section headings 092 is missing. After page 4, no block anywhere in
  the document retains `document_body`.

  Two further facts, recorded and kept separate from the above:
  - **The contents block was never recognised as one.** No block in 092 received scope
    `table_of_contents`; `DetectTocBlockIds` returned nothing. The appendix trigger would have fired
    regardless, because it tests the text without consulting the incoming scope - so this is a second
    contributing defect, not the same one.
  - **The quote latch leaks the same way.** From page 28 onward 92 blocks become
    `quoted_replacement`, covering pages 28-35 and swallowing the real appendices on page 32.
    `_insideQuote` is set by an unbalanced quote character and only cleared by a closing one. Same
    shape - a latch with no independently justified exit - but a different trigger, so it is a
    separate finding and not evidence for the appendix one.

- [x] M10.3-A2 reset counterfactual, diagnostic only. The tracker gained an evaluation-only set of
  withheld appendix entries - reviewed source ids, not a predicate, because inventing a TOC-shape
  classifier or a body-boundary rule here would be a remediation wearing a counterfactual's clothes.
  Exactly one transition was withheld, `b43`. Everything else, including the real appendix triggers
  on page 32, was left alone.

  **The scope labels recover completely. The headings do not.**

  | measure | actual | withholding b43 |
  |---|---|---|
  | `appendix` / `appendix_table` | 287 / 59 | 0 / 0 |
  | `document_body` | 43 | 330 |
  | pages 5-31 `document_body` | 0 | 284 |
  | blocks scoring `scope_conflict` | 456 | 169 |
  | excluded by scope, whole population | 151 | 107 |
  | emittable at budget 160 | 98 | 86 |

  **Causality refuted for the loss it was opened to explain.** Not one of the four body headings
  becomes emittable:

  | block | scope | rank | score |
  |---|---|---|---|
  | `b45` `1 Introduction` | `appendix_table` -> `table` | 32 -> 452 | 0.54 -> 0.00 |
  | `b53` `1 1 Requirements Notation` | `appendix_table` -> `table` | 33 -> 454 | 0.54 -> 0.00 |
  | `b58` `1 2 Syntax Notation` | `appendix_table` -> `table` | 34 -> 455 | 0.54 -> 0.00 |
  | `b61` `1 2 1 Imported Rules` | `appendix_table` -> `table` | 35 -> 456 | 0.54 -> 0.00 |

  The loss is overdetermined, and the false scope was shielding them. `appendix_table` carries no
  negative ranking signal, so these blocks ranked 32-35 and were then dropped because
  `appendix_table` is an excluded output scope. Corrected to `table` they lose the shield, take the
  `table_scope` penalty of -0.60, score 0.00 and fall to rank 452-456 - outside the budget. Whichever
  scope they carry they are lost, and the binding constraint is the `TableLike` annotation upstream
  of scope, not the appendix leak.

  Two further observations, recorded:
  - the net emittable change is -12, and its composition matters more than its sign: 25 body prose
    sentences left (they were false positives) and 13 entered, mostly page-1 front matter
    (`Category:`, `Authors:`, `Copyright Notice`) alongside `Abstract` and `Status of This Memo`.
    There is no benefit here to promote.
  - the real appendices on page 32 do **not** become `appendix` in the counterfactual either. The
    quote latch has already taken pages 28-35, so withholding `b43` cannot restore correct appendix
    labelling. The expectation that the genuine trigger would simply fire normally did not hold, for
    a reason independent of this intervention.

### M10.3-A - CLOSED, zero production change

**Proven**
- the appendix latch enters from a contents line and has no reset path; pages 5-31 of 092 are body
  labelled appendix.
- withholding that single transition restores every scope label on those pages.

**Refuted**
- that the appendix leak is what costs 092 its parent-capable headings. It is not. They fail in both
  worlds, and rank *worse* once the scope is correct.

**Not measured**
- whether the leak costs anything elsewhere in the corpus. 092 is one document, and the four headings
  were the reason to look.

No trigger fix, no lifecycle fix, no `DetectTocBlockIds` change. A3-trigger and A3-lifecycle are not
opened: the counterfactual that would have justified choosing between them came back negative. The
quote leak stays a separate finding with a separate trigger and is not generalised into a shared
scope-lifecycle invariant on the strength of one shape resemblance.

- [x] M10.3-B1 `TableLike` first-loss audit, on the corrected-scope population. `LooksLikeTableLine`
  now delegates to `ClassifyTableLine`, which names which of its own branches fired. Same conditions
  in the same order; the branch name is recorded, never recomputed, so evaluation reads the rule's
  decision instead of keeping a copy of it. The names describe the branch, not a verdict.

  **Correction to what was written when B was opened.** The old ~74% figure was called unusable. That
  was too broad. The rule is line-level and the reviewed labels are line-level, so the *classification*
  figure survives the scope correction untouched: 93 of 125 `short_numbered` lines should not have
  been marked, which is where 74% came from. What did not survive is any *downstream* loss rate
  measured while pages 5-31 were labelled appendix. Only the second kind was re-measured here.

  | branch | lines |
  |---|---|
  | `not_table_like` | 1060 |
  | `no_alphanumeric` | 956 |
  | `short_numbered` | 125 |
  | `numeric_density` | 51 |

  72 of 499 candidate blocks have every line marked - 52 dominated by `short_numbered`, 20 by
  `numeric_density`. Of those, 57 land in scope `table`, all 57 carry the `table_scope` penalty, 47
  score exactly 0.00, and 15 survive to the budget.

  **Causal first loss: proven, and it repeats.** Joining the 125 reviewed line labels to the
  corrected-scope population, 35 of the 37 lines a reviewer called outline-eligible join to a
  candidate block, and the same chain runs for every one of them:

  `short_numbered` -> block all-marked -> scope `table` -> `table_scope` -0.60 -> score 0.00 ->
  rank 452-497 -> outside budget 160.

  27 of the 35 die exactly that way, selected = false. The remaining 8 are *selected* - at ranks
  26-36 with score 0.54 - only because the quote latch put them in `quoted_replacement`, which dodges
  `table` scope and its penalty. But `quoted_replacement` is an excluded output scope, so they are
  selected and still not emittable. That is the same shield-then-kill shape as the appendix case,
  arriving by a different route. **None of the 37 reaches output.**

- [x] M10.3-B2 reviewed withholding counterfactual. `PdfLineBlockFilter.Analyze` gained an
  evaluation-only set of line indexes whose table-like mark is withheld, threaded through the audit
  context. Lines are addressed by index, not by text: all 37 reviewed labels resolved to exactly one
  line, none ambiguous, none unresolved. No general rule was applied - no `HasStructuralMarker`
  exemption, no branch removal, no scoring change, no scope change.

  **Causality confirmed. The chain reverses exactly where B1 said it would.**

  | | before | after |
  |---|---|---|
  | blocks in scope `table` | 57 | 30 |
  | blocks carrying `table_scope` | 57 | 30 |
  | blocks scoring 0.00 | 67 | 42 |
  | candidate blocks | 499 | 535 |
  | emittable at budget 160 | 86 | 84 |

  `b45` `1 Introduction`: scope `table` -> `document_body`, score 0.00 -> 0.54, rank 452 -> 46,
  selected false -> true, emittable false -> true. The same reversal runs for `b53`, `b58`, `b61` and
  the rest.

  Of the 37 reviewed occurrences: **27 enter selection and become emittable**, 8 remain selected but
  non-emittable because they sit in `quoted_replacement` from the separate quote leak, and 2 are
  still not selected.

  Three things this measurement is not allowed to claim:
  - **It is not a collateral measurement.** The intervention has an oracle - it knows which lines are
    real headings. B1's population figure remains the honest collateral number: 93 of 125
    `short_numbered` lines are not outline-eligible.
  - **The population changed.** Withholding the mark also changes `ExcludeFromCandidateGrouping`, so
    candidates went 499 -> 535 and some supplement lines left the population entirely. This is not
    pure displacement within a fixed set.
  - **Net emittable fell by 2.** 44 left and 42 joined. What left is overwhelmingly body prose
    fragments and ABNF lines, which is a gain in quality the raw count hides - but five front-matter
    headings (`HTTP Caching`, `Abstract`, `Status of This Memo`, `Table of Contents`,
    `Authors' Addresses`) were pushed from ranks 130-135 to 161-170, just outside the budget. Real
    headings displaced by other real headings at a fixed budget is a budget finding, not a defect of
    the intervention, and it is recorded rather than acted on.

### M10.3-B — causal owner PROVEN, remediation NOT JUSTIFIED

**Proven**
- the `short_numbered` branch marks 37 reviewed outline headings on 092.
- that mark is the first fact in the loss: withholding it for exactly those occurrences reverses
  scope, penalty, score, rank and selection, and 27 reach output.

**Not proven**
- that any available fact separates those 37 from the other 88 lines the branch marks.

**Not justified**
- any production change. `if (HasStructuralMarker) TableLike = false;` remains unwritten: it was
  held back before because of scope contamination, that contamination is now removed, and it is
  still unmeasured against the corrected population.

- [x] M10.3-B3 existing-fact separability, measured over the fixed 125 reviewed lines. Only facts
  the pipeline already produces; no new predicate, no regex, no feature combination.

  **Correction, and it matters.** When B2 closed, this note read: the marker parser does not
  recognise `1`, `1 1`, `1 2 1` as structural markers, inferred from the `no_labelled_structural_marker`
  ambiguity signal on `b45`. That was wrong, and it was an inference from a signal name rather than a
  measurement. `PdfMarkerFactsParser.Parse` succeeds on **37 of 37** outline headings.
  `no_labelled_structural_marker` means no *labelled* marker such as `Section 5`, which is a
  different fact. The expectation that B3 would find nothing was built on that mistake.

  | existing fact | outline (37) | non-outline (88) | toc | table (32) | prose | meta | caption |
  |---|---|---|---|---|---|---|---|
  | `HasStructuralMarker` | **37** | 44 | 35 | **0** | 6 | 1 | 2 |
  | strict marker parse | **37** | 44 | 35 | **0** | 6 | 1 | 2 |
  | generic numbering parse | **37** | 35 | 35 | **0** | 0 | 0 | 0 |
  | block is a single line | 35 | 10 | 2 | **0** | 6 | 0 | 2 |
  | loose labelled marker | 0 | 9 | 0 | 0 | 6 | 1 | 2 |
  | header/footer zone | 1 | 8 | 2 | 5 | 1 | 0 | 0 |

  **A discriminator exists for the distinction that matters.** `HasStructuralMarker` catches every
  one of the 37 outline headings and **none of the 32 genuine tabular values**. The separation
  between real headings and real tables inside this branch is clean, not marginal.

  **What it does not separate is contents entries: 35 of 35 collide.** That is not a surprise and not
  a defect of the fact - a contents line quotes the heading it points at, so it carries the same
  numbering. Telling those apart is `DetectTocBlockIds`' job, and that returns nothing on 092. The
  collision therefore belongs to the already-recorded TOC debt rather than to this branch.

  **The sharpest finding is that production already trusts this exact fact one layer up.**
  `ExcludeFromCandidateGrouping` reads `TableLike && (Repeated || !HasStructuralMarker)`, with a
  comment stating that geometric table evidence "is not by itself proof that the source fact is table
  body" and that a non-repeated structural marker may enter grouping "for later scope-aware
  validation". So the judgement is already made and already encoded; scope derivation simply does not
  consult it. That reframes any remedy from inventing a discriminator to resolving an inconsistency
  between two layers that already disagree.

  Measurement limits recorded with the result: 79 of 125 lines join to a candidate block, so the
  block-level rows (`single line`, ranking signals) are defined on that subset, while the line-level
  facts are defined on all 125. Unjoined lines read as false for block-level facts, which biases the
  `single line` row downward for exactly the lines that were excluded from grouping.

- [x] M10.3-B4 safe-remediation counterfactual. Hypothesis, applied to every line with no gold and
  no tuning: a line the `short_numbered` branch marks that also carries the existing
  `HasStructuralMarker` fact is diagnostically not table-like. The intervention sits at the fact
  boundary and every existing consumer then runs unchanged - grouping, scope, ranking, selection,
  output - so it asks what happens when two layers that already disagree are made to agree, and does
  not decide where a repair would live.

  **092, fate by reviewed role (corrected-scope world, 81 of 2192 lines affected):**

  | role | n | selected before/after | emittable before/after |
  |---|---|---|---|
  | outline_heading | 37 | 8 -> 35 | **0 -> 27** |
  | toc_entry | 35 | 5 -> 22 | **5 -> 22** |
  | table_cell_or_tabular_value | 32 | 0 -> 0 | **0 -> 0** |
  | body_prose | 15 | 0 -> 6 | 0 -> 6 |
  | metadata | 4 | 0 -> 0 | 0 -> 0 |
  | caption | 2 | 2 -> 2 | 0 -> 0 |

  Blocks in scope `table` fall 57 -> 11; emittable rises 86 -> 93. In the shipped world (appendix leak
  present) the same intervention moves emittable 98 -> 129, with the identical role breakdown.

  **Cross-domain holdout, same intervention unchanged:**

  | document | lines affected | scope `table` | emittable |
  |---|---|---|---|
  | 010 | 0 of 1842 | 0 -> 0 | 160 -> 160 |
  | 054 | 46 of 11481 | 118 -> 84 | 160 -> 160 |
  | 076 | 3 of 767 | 12 -> 10 | 149 -> 150 |

  No cross-domain harm was found, but that is weaker evidence than it looks: 010 is untouched by the
  predicate entirely, and 010/054 saturate the budget, so emittable cannot move there regardless.
  Absence of harm on those two is not evidence of safety.

### M10.3-B — CLOSED, zero production change

**Proven**
- causal owner: the `short_numbered` branch is the first fact in the loss (B1, B2).
- an existing discriminator separates outline headings from genuine tables perfectly, and production
  already applies it one layer up (B3).
- making the two layers agree recovers 27 of 37 headings and protects every one of the 32 genuine
  tabular values, in both the shipped and corrected-scope worlds (B4).

**Not justified**
- promotion. Alongside the 27 recovered headings the same intervention makes **17 further contents
  entries** and **6 prose lines** emittable. Of the newly emittable reviewed lines, roughly half are
  not headings. That is exactly the outcome the gate named in advance: recovery real, discriminator
  real, remediation blocked by a different unresolved owner.

The blocking owner is `DetectTocBlockIds`, silent on this document. **It is deliberately not opened
to rescue this remedy.** Fixing TableLike because it needs TOC, then fixing TOC because it needs
something else, is the chained overengineering this project has been avoiding; the TOC debt keeps its
own independent trigger. If it is ever repaired on its own evidence, B4's measurement can be re-run
as-is and the gate re-read - the probe takes no gold and needs no change.

## M10.4 - 092 quote scope lifecycle, audit only

Chosen over the TOC debt because it already shows on the output path: the quote latch holds pages
28-35, leaves 8 reviewed headings selected but not emittable, and swallows the real appendices on
page 32 so that withholding the false appendix entry could not restore them (M10.3-A2).

- [x] M10.4-A1 quote state trace, model-free. The existing tracker gained passive fields recording the
  open and close conditions it evaluated and the raw quote-character counts behind them. The two
  conditions do not read the same characters, so booleans alone could not distinguish a block that
  failed to close from one that could never close. No new quote boundary was defined.

  **1. Trigger.** Six blocks satisfy the open condition; the first is `s-line-830` on page 28:

  `1 1" (Section 11 of [HTTP/1 1]) and "HTTP Semantics" (Section 17 of [HTTP])`

  Three straight quotes, no curly ones. The condition is
  `leftCurly + (straightQuotes % 2) > 0`, so it fires on the *parity* of straight quotes within one
  line. The line opens `1 1"`, which is the tail of `"HTTP/1 1"` broken across the line boundary -
  so on the evidence the odd parity is produced by line segmentation rather than by unbalanced text
  in the source. That reading needs reviewed confirmation before the transition is called false.

  **2. Persistence.** From that block to the end of the document: 107 blocks, pages 28-35, of which
  92 resolve to `quoted_replacement`. Not one block after it satisfies the close condition.

  **3. Exit - and this is the part that differs from the appendix latch.** The appendix latch has no
  exit at all. The quote latch *has* one, and it is unreachable on this document:

  | | count |
  |---|---|
  | blocks whose close condition held, anywhere | **0** |
  | curly closing quotes in the document | **0** |
  | curly opening quotes in the document | 0 |
  | straight quote characters in the document | 208 |

  The open condition reads straight quotes; the close condition reads only `U+201D`/`U+201F`. **The
  two conditions read disjoint character sets**, so in a document that uses straight quotes
  throughout, the latch can be set and can never be cleared. This is an asymmetry, not a missing
  reset, and the distinction matters because it changes which interventions are even meaningful.

  **4. Why page 32 loses its appendices.** In the tracker's branch order the quote branch is tested
  before the appendix branch. `b485` `Appendix A Collected ABNF` and `b490`
  `Appendix B Changes from RFC 7234` both arrive with the quote latch already set, so they resolve to
  `quoted_replacement` rather than `appendix`. Their own trigger fires; it simply never reaches the
  branch that would use it.

- [x] M10.4-A2a suppress the reviewed quote-open transition. Reviewed first, as required: the source
  sentence quotes `"HTTP/1 1"` and `"HTTP Semantics"`; line 1772 ends `..."HTTP/` with one straight
  quote and line 1774 carries the remaining three. Page 28 holds **eight** straight quotes in total,
  an even count, so the source quoting is balanced and the odd parity is produced by line
  segmentation. The transition is confirmed as an artifact.

  **First attempt, and a mistake worth recording.** Withholding block `s-line-830` alone changed
  almost nothing - `quoted_replacement` 92 -> 91 - because the latch reopened immediately on
  `s-window-2004`, the *window representation of the same source line*, on the same page. The
  intervention had been addressed to a block when the reviewed unit is an occurrence. That is the
  same identity error this project has now hit at four layers, and it is a property of the pipeline
  worth stating plainly: **a source line reaches scope through more than one candidate
  representation, and each satisfies the trigger independently.**

  **Corrected intervention: the whole occurrence, still one reviewed transition.**

  | | before | after |
  |---|---|---|
  | `quoted_replacement` | 92 | 49 |
  | `appendix` | 287 | 314 |
  | `reference_list` | 0 | 16 |
  | pages 28-35 `appendix` | 8 | 35 |
  | emittable at budget | 98 | 102 |

  **Result: single bad transition is not a sufficient causal owner - twice over.**

  - *It relatches.* The next opening block is `b479` on page 32, a bibliography line reading
    `[RFC 7234] ... "Hypertext Transfer` - one straight quote, because the quoted title is again split
    across two lines. The same segmentation mechanism, in a different place. Pages 32-35 therefore
    never leave quote scope, and `b485` `Appendix A Collected ABNF` and `b490`
    `Appendix B Changes from RFC 7234` stay `quoted_replacement`, unchanged, still not emittable.
  - *Even where scope recovers, the headings do not.* Pages 28-31 largely leave quote scope, and
    **not one of the 37 reviewed outline headings becomes emittable** - the count stays at zero. The
    four things that do become emittable are a DOI line, the withheld line itself, and two page-30
    fragments. The headings are held by the `TableLike` chain behind the quote scope, which M10.3-B4
    already demonstrated from the other side: intervening on `TableLike` alone recovered 27 of them
    **while the quote leak was still present**.

### M10.4-A - CLOSED, zero production change

**Proven**
- the page 28 transition is a segmentation artifact, on reviewed evidence.
- the quote latch's exit is unreachable on this document: open reads straight quotes, close reads only
  curly ones, and the document has 208 straight and zero curly (A1).
- page 32 loses its appendices to branch order - the quote branch is tested before the appendix
  branch - not to missing evidence (A1).

**Refuted**
- that the quote leak is the causal owner of 092's heading loss. Removing it recovers no heading.
  The binding constraint remains `TableLike`, which is where M10.3-B already left it.

**Not measured**
- whether the unreachable close condition costs anything on a corpus that uses curly quotes.

A2b on the open/close asymmetry is **not opened**. It is a real defect and a strong one, but A2a shows
the product loss it was suspected of causing belongs elsewhere, and fixing it now would be repairing
a defect because it is visible rather than because it is costing anything measured. It keeps its own
trigger, like the TOC debt.

- [ ] Debts kept separate. Quote scope, appendix scope and the TOC detector share a subject and
  nothing else: three different triggers, three different owners, three different failure shapes. No
  `ScopeLifecycleManager` follows from the fact that all three are scope-related.

- [ ] Hierarchy stays last. 092's hierarchy is bounded by what reaches the validator; a better
  resolver cannot invent a parent fact that upstream filtered out. Reassess the ceiling only after
  the upstream blockers are either repaired or proven unrepairable, and only if parent facts are then
  complete while parent/level are still wrong.

- [ ] M10.3 debts kept separate, not merged into a scope-lifecycle abstraction:
  - the quote latch leak from page 28 (its own trigger, its own owner).
  - `DetectTocBlockIds` returning nothing on 092.
  Neither is evidence for the other, and neither is opened by B.

## M10.5 - TableLike cross-document boundary

- [x] M10.5-A cross-document discovery, gold-free/model-free. The 95-file sweep is frozen at
  `.verify-build/m105a-tablelike-cross-document-discovery.json`; it measures only the existing
  `short_numbered` branch, the existing `HasStructuralMarker` fact, affected candidate blocks,
  downstream table scope/penalty, and whether the pool exceeds the fixed 160 budget. It does not
  load keys, inspect heading outcomes, call a model, apply a counterfactual, or change production.
  Exposure is broad: 67/95 documents have at least one structural-marked short-numbered line and
  66/95 have at least one affected block carrying `table_scope`. 092 is therefore not the only
  document exercising the interaction, but it remains the only causal outcome trace.
  The independent M10.5-B review population is selected before outcome review from the frozen
  ranking: procurement `032` (227 exposed lines / 61 table-penalised blocks), financial `043`
  (52 / 32), textbook `063` (678 / 549), and RFC sibling `091` (425 / 50). These are exposures,
  not predicted defects. Review only a small sample of `short_numbered && HasStructuralMarker`
  occurrences and trace the same downstream chain.
- [x] M10.5-B reviewed occurrence audit on the preselected documents. The population was frozen and
  committed before any line was read (`eval/manual-labels/m105b-cross-document-review-sample.v1.json`):
  ten occurrences per stratum per document, ordered by a SHA-256 of the occurrence identity,
  stratified by whether the exposed line's best-ranked block actually carries the `table_scope`
  penalty. Stratified rather than uniform because 091 exposes 425 lines and penalises 110 - a small
  uniform draw would have under-sampled the stratum the question is about.

  **What the exposed population actually is (80 reviewed occurrences, penalised / clean):**

  | document | outline | toc | table cell | caption | running header | form field | math/index |
  |---|---|---|---|---|---|---|---|
  | 032 procurement | 1 / 7 | - | - | - | 7 / 1 | 2 / 2 | - |
  | 043 financial | 0 / 0 | - | 5 / 3 | 5 / 7 | - | - | - |
  | 063 textbook | 0 / 0 | 4 / 0 | - | - | 5 / 3 | - | 1 / 7 |
  | 091 RFC sibling | 0 / 6 | 10 / 2 | - | 0 / 2 | - | - | - |

  In 043 and 063 the branch is largely doing its job: what it penalises is genuine tabular values,
  captions, running headers, contents lines and mathematics. **Not one real heading appears in either
  document's sample.**

  **The 092 chain does not reproduce.** Fourteen reviewed outline headings appear, in 032 and 091:

  | measure | of 14 |
  |---|---|
  | carrying the `table_scope` penalty | **1** |
  | scoring 0.00 | 1 |
  | within budget 160 | 3 |
  | within budget **and** in an allowed output scope | **0** |

  Thirteen of the fourteen never take the penalty at all. They score 0.38-0.70 and rank as high as
  53 and 115 - and every one of them still fails to reach output, because they land in
  `appendix_table`, which is an excluded output scope.

### M10.5 gate - failure class NOT generalised

**Proven**
- exposure generalises: 67 of 95 documents exercise the interaction (M10.5-A).
- the 092 causal chain does not: `short_numbered -> table -> table_scope -> rank collapse` reaches
  1 of 14 reviewed headings outside 092.
- in the financial and textbook documents the branch harms no reviewed heading at all.

**Redirected, and deliberately not acted on**
- what actually blocks real headings in 032 and 091 is `appendix_table` output exclusion - 14 of 14.
  That points at the appendix scope leak, which M10.3-A closed as real but non-causal *on 092*. It
  may be the more general owner, and that is a new hypothesis with its own population, not a
  conclusion this audit is entitled to draw. It needs its own milestone and its own measurement.

**Consequence**
- the `TableLike` remediation stays closed. It was already blocked by the TOC owner on 092; it is now
  also shown to address a chain that does not repeat. Fixing it would be fitting a repair to one
  document.

- [x] M10.6 precheck - appendix mechanism equivalence. Passive state trace only, no counterfactual.

  **The mechanism is identical in all three documents.** Each acquires its appendix latch from a
  contents-like line arriving with scope `document_body`, and none ever resets it:

  | | 032 procurement | 091 RFC | 092 RFC (reference) |
  |---|---|---|---|
  | first transition | `s-block-28` p121 | `b113` p12 | `b43` p4 |
  | trigger text | `Annex 1 . Request for Change Proposal .....` | `Appendix A Collected ABNF Appendix B ...` | `Appendix A Collected ABNF ... Index` |
  | incoming scope | `document_body` | `document_body` | `document_body` |
  | latch persists | 2984 / 2984 | 2372 / 2372 | 443 / 443 |
  | **reset count** | **0** | **0** | **0** |
  | pages held | 121-285 | 12-194 | 4-35 |
  | scopes produced | appendix 2847, appendix_table 132 | appendix 50, appendix_table 269 | appendix 287, appendix_table 59 |

  Two domains, three documents, one shape. 032's trigger even carries dot leaders
  (`.....................`), which is about as unambiguous a contents line as a document produces.
  091 adds a second trigger shape worth recording: `b443` on page 35 is body prose reading
  `Appendix A shows the collected ABNF for senders after the list constructs have been expanded` -
  a sentence *about* an appendix, firing the same trigger.

  **But the product loss it directly causes is small, and the split matters more than the total.**
  Of the 14 reviewed headings:

  | | 032 | 091 | total |
  |---|---|---|---|
  | A - selected, then excluded by appendix scope | 2 | 1 | **3** |
  | B - already outside budget before scope applies | 6 | 5 | **11** |

  Saying "14 of 14 carry `appendix_table`" would have been true and misleading. Appendix scope is the
  direct first loss for three headings. The other eleven would not have been emitted whatever scope
  they carried.

  **The eleven are the more interesting finding.** They score 0.38-0.70, thirteen of the fourteen
  carry no `table_scope` penalty at all, and they still rank 161-1011. Whatever keeps them out of the
  budget is neither the table branch nor the appendix scope, and it has not been identified.

### M10.6 gate

**Mechanism equivalence: CONFIRMED.** A milestone on appendix lifecycle is now open to being opened -
this is no longer three documents sharing a symptom.

**Material impact: 3 of 14 sampled headings.** Small enough that the milestone should be scoped to
what it can actually recover, and small enough that it is a fair question whether it is the best next
target at all.

## M10.7 - how rank is formed for headings that never reach the budget

Prioritised over appendix remediation by measured loss: appendix scope is the direct first loss for
3 of 14 sampled headings, the ranking boundary for 11.

- [x] M10.7-A rank formation audit over the eleven, passive. The ranker orders by score descending,
  then escalation descending, then page, then source id - no tier participates in the ordering.
  Parity holds everywhere: no candidate with a lower score stands above any of the eleven.

  **The eleven split into two mechanisms, and they are not the same owner.**

  **1. Outscored (032, and 054's deferred case).** These headings score 0.38-0.48 and hundreds of
  candidates score genuinely higher.

  | occurrence | score | rank | higher above | equal above |
  |---|---|---|---|---|
  | 032 `22.2.8 Funeral Arrangements` | 0.38 | 772 | 590 | 181 |
  | 032 `3.4 Amendment` | 0.38 | 662 | 590 | 71 |
  | 032 `2. Change Order Log` | 0.48 | 458 | 455 | 2 |
  | 054 `AVAILABILITY OF INFORMATION` | 0.44 | 166 | 165 | 0 |

  No tie-break would save them. The question is why a standalone numbered contract heading scores
  0.38 with no negative signal - all four carry `unlabelled_numbering`,
  `no_labelled_structural_marker`, `no_body_opening_evidence` and `scope_conflict`, and nothing else.

  **2. Tie density and a positional tie-break (091, all five).** Every one of them scores **0.54**,
  and in the whole document only **142** candidates score above 0.54 - the same number for all five.
  The budget of 160 therefore has room for eighteen more, and which eighteen is decided by
  `ThenBy(page)`.

  | occurrence | page | rank | higher above | equal above |
  |---|---|---|---|---|
  | `3 6 Origin Server` | 20 | **161** | 142 | 18 |
  | `4 2 1 http URI Scheme` | 25 | 169 | 142 | 26 |
  | `8 8 3 1 Generation` | 69 | 248 | 142 | 105 |
  | `12 4 2 Quality Values` | 99 | 287 | 142 | 144 |
  | `18 7 Range Unit Registration` | 166 | 451 | 142 | 308 |

  Rank rises monotonically with page number while the score stays identical. The tie-break is not
  arbitrary but systematic: later pages lose every tie. `3 6 Origin Server` misses the budget by one
  place.

  **Re-representation is not the driver.** Inflation above these occurrences is 8-15 candidates out
  of hundreds, roughly 2%. The one exception is 032 `6. Personnel` at 539 of 4657, and that occurrence
  is also the only one of the eleven carrying `table_scope`.

  **The finding that cuts across both, and the sharpest one here.** For `3 6 Origin Server`, the 160
  candidates above it are: `quoted_replacement` 74, `appendix_table` 58, `document_body` 26,
  `appendix` 2. **132 of the 160 budget slots are held by candidates the output policy will discard.**
  The same holds for the other four. Selection happens before output policy is consulted, so a
  document with a scope leak spends most of its budget on candidates that cannot be emitted.

  Stated carefully: those candidates would occupy the same slots whatever scope they carried, since
  scope affects rank only through the `table_scope` penalty. So this is not the leaks pushing them
  up - it is that selection and output exclusion apply different criteria in sequence, and nothing
  reconciles them.

### M10.7 gate

**No `RankingFix`.** The eleven divide into two mechanisms with different owners, exactly the outcome
that forbids a single repair.

- [ ] M10.7-B (candidate) scoring competitiveness for standalone numbered headings - why 0.38 with no
  negative evidence. Owner is the scorer, not the ranker. Population: 032's headings plus 054's
  deferred `AVAILABILITY` case, which this audit confirms fails the same way rather than by ties.
- [ ] M10.7-C (candidate) tie density at the budget boundary. Owner is the tie-break, or the budget,
  or the scorer's resolution - 142 candidates above and five headings sharing one score suggests the
  score is too coarse to order them, which is not the same defect as being outscored. Note in advance
  that raising the budget is not the remedy; 054 already settled that.
- [x] M10.7-D1 selection-budget waste, measured. A slot counts as wasted only when the candidate's
  current scope is already in the output policy's own excluded set - a fact available before the
  model. A candidate that reaches the model and is then rejected does not count; treating it as waste
  would let selection chase an outcome that has not happened yet. The excluded set is read from
  `PdfOutputDecisionPolicy` rather than restated, so the two cannot drift.

  | document | candidates | selected | wasted | share | first eligible below budget |
  |---|---|---|---|---|---|
  | 091 RFC 9110 | 2544 | 160 | 132 | **82%** | **476** |
  | 092 RFC 9111 | 499 | 160 | 62 | 39% | 161 |
  | 032 procurement | 5311 | 160 | 26 | 16% | 162 |
  | 054 IBRD | 2043 | 160 | 0 | 0% | 161 |
  | 076 minutes | 149 | 149 | 0 | 0% | - |

  **Corpus sweep, 77 documents with candidates:** median waste 1%, mean 15%, maximum 89%. It is not a
  universal condition - 38 of 77 waste nothing at all - but there is a clear high-waste family: 9
  documents above 50% and 9 more between 25% and 50%, concentrated in the RFCs (091, 093, 094, 095 at
  72-89%), financial statements (041, 044, 045, 055 at 39-89%), Vietnamese amending decrees (018, 090
  at 77-82%) and one lecture series (057 at 87%).

### Correction to the M10.6/M10.7 prioritisation

**The claim that appendix scope has a 3-of-14 ceiling was wrong, and the error was mine.** The A/B
split reported which blocker bites *first*, which was accurate, but it was then read as a bound on
what appendix remediation could recover. Measuring the scopes directly: **13 of the 14 reviewed
headings carry `appendix_table`**, and the fourteenth carries `table`. Appendix scope disqualifies
thirteen of them. It is *sufficient on its own* for three; for the other ten it is necessary but not
sufficient, because those are also outside the budget.

So ten of the fourteen are **overdetermined**: repairing the appendix lifecycle alone recovers none of
them, and moving the ranking boundary alone recovers none of them either. That is the same shape this
project has met at every layer, and it should have been checked before recommending an order of work.

**D2 is refuted before it runs.** The proposed counterfactual - skip candidates already known to be
output-ineligible and keep taking ranked candidates until 160 eligible slots are filled - cannot
recover 091's five headings, because those headings are themselves `appendix_table` and would be
skipped by the same rule. 091's first eligible candidate below the budget is at rank 476, and all
five sit inside that dead band at ranks 161-451. An eligibility-aware selection would step over them.

- [ ] M10.7-D2 is **not opened** as a heading-recovery remedy; the measurement above shows it cannot
  be one. Budget waste remains a real and sometimes large inefficiency - 89% on 094 - and if it is
  ever revisited the justification has to be model cost or throughput, measured as such, not heading
  recall. Do not re-open it on the strength of the 82% figure alone.

- [ ] M10.7-C tie density is **not opened** either, and for the same reason: 091's five are
  disqualified by scope regardless of where the tie-break puts them.

### Where this actually leaves the work

The one blocker that is *necessary* for 13 of 14 reviewed headings is the appendix lifecycle, whose
mechanism the M10.6 precheck already proved identical across three documents and two domains. It is
not sufficient alone for ten of them, so it cannot be promoted on a recovery argument by itself - but
it is no longer a 3-of-14 side issue, and the earlier recommendation to prioritise ranking over it was
based on my misreading rather than on the data.

### M10.6-A - appendix-only counterfactual: necessary, and nowhere near sufficient

One variable. The reviewed false transitions were withheld and nothing else changed - `TableLike`,
scorer, comparator, budget, output policy, TOC detector and quote logic all as they are. Ranks were
allowed to move as a consequence of scope changing, which is the effect, not a second intervention.

The false transitions were reviewed and listed as occurrences, in three shapes: contents listings with
dot leaders (032 pages 121 and 254), the same listings merged into one block (pages 133 and 265), and
body prose referring to an appendix (pages 166, 209, 219, 269; 091 page 35). Every genuine annex or
appendix opening kept its trigger.

**Recovery: zero of fourteen. Emittable is unchanged in both documents - 032 stays at 134, 091 at 28.**

**091 - the loss moves to a scope that was hidden underneath.** Appendix scope collapses as intended:
`appendix` 50 -> 0, `appendix_table` 269 -> 7. All six headings then land in `quoted_replacement`.
Three of them are now *selected* - ranks 114, 150, 158, inside the budget - and excluded at output
anyway. 091 has a quote latch leak of its own, and the appendix scope was masking it. This is the
third document in which correcting one scope reveals another underneath, and the second time a wrong
scope turns out to have been shielding the heading from a different death.

**032 - a genuine trigger contaminates exactly like a false one.** The first surviving transition is
`b2124` on page 135, `Annex 1. Request for Change Proposal` - a real annex opening, correctly
detected. It latches, never exits, and the General Conditions clauses on pages 157-186 remain
`appendix_table`. Four headings fall back to `ranking_or_budget`, two stay `appendix_table` behind
the genuine trigger, and two now carry `table_scope`.

**And one heading is made worse.** `2. Change Order Log` moves from `appendix_table` at rank 458 to
`table` at rank **4711**: correcting its scope removed the shield that `appendix_table` provided
against the `table_scope` penalty. The same effect M10.3-A2 found on 092, reproduced on a second
document and a different domain.

#### What this settles

**The missing exit is the deeper defect, not the false trigger.** M10.3-A framed appendix leakage as a
contents line firing a trigger it should not have. 032 shows that a *correct* trigger produces the
same contamination, because the latch has no end. No amount of trigger review fixes that, and a
remediation aimed at triggers would have left the larger half of the problem untouched.

**Appendix scope is necessary for 13 of 14 and sufficient for none.** The earlier reading that it was
sufficient for three was drawn from the shipped state; with the appendix scope corrected, those three
are blocked by `quoted_replacement` or by the budget instead. Every one of the fourteen is
overdetermined.

- [ ] M10.6 promotion gate: **not met, and not met on a recall argument at all.** A lifecycle repair
  cannot be justified by heading recovery, because the measured recovery of a correct appendix scope
  is zero. If it is ever promoted it will be as a correctness repair to a source fact - scope is
  wrong, systematically, across three documents and two domains - with collateral measured on its own
  terms. That is a legitimate case but it is a different one, and it needs its own gate.

- [ ] Recorded, not opened: 091 quote latch leak. Same shape as 092's, different document, and it now
  blocks three headings that reach the budget. It inherits M10.4's finding that the quote latch's exit
  condition reads a character class the document never uses.

- [x] M10.7-B1 scorer fact mapping. Facts and signals observed; no score recomputed, no weight
  touched. Denominator is the four 032 headings whose scope blocker was independently removed in
  M10.6-A. 054's `AVAILABILITY OF INFORMATION` is a comparison and stays out of that count.

  **The four 032 headings are identical, and the mismatch is exact.**

  | fact | produced? |
  |---|---|
  | `PdfMarkerFactsParser` (strict structural) | **yes** - `family=arabic`, `isPath=true`, all four |
  | `HasStructuralMarker` | **true**, all four |
  | `ParseLooseLabelledMarkerForAudit` | none |
  | `NumberingAudit` (generic) | true |

  The scorer credits `unlabelled_numbering_prefix` and `standalone` and nothing else, then adds
  `unlabelled_numbering` and `no_labelled_structural_marker` as ambiguity. Score 0.38.

  Reading the scorer confirms why: it asks `ParseLooseLabelledMarkerForAudit` (worth +0.42) and
  `NumberingAudit` (worth +0.10). **It never asks `PdfMarkerFactsParser` or `HasStructuralMarker`**,
  except indirectly inside `IsMarkerTitleComposite`, which requires two or three lines and so cannot
  fire for a standalone heading.

  **This is the second place the same fact is produced, used once, and then ignored.** M10.7-B3 found
  `HasStructuralMarker` gating candidate grouping and not consulted by scope derivation; it is not
  consulted by scoring either. The pipeline computes a fact that separates real headings from real
  tables perfectly on the reviewed population, and spends it at exactly one gate.

  **The budget is effectively reserved for labelled markers.** Of 032's selected 160, **159 carry
  `labelled_numbering_marker`** and one carries `unlabelled_numbering_prefix`. The score at the budget
  edge is 0.55. A standalone heading whose marker is structural but not labelled reaches 0.38, or 0.54
  with layout prominence - below the edge either way. It is not competing and losing; it cannot reach
  the boundary.

  A detail worth recording rather than smoothing over: the strict parser fires with `depth=1` on
  `22. 2. 8 Funeral Arrangements`, not depth 3, because extraction spaces the marker into `22. 2. 8`.
  The fact is present but its depth is already damaged by the same space-insertion that produced the
  `5. Field Definitions` debt. Any use of this fact downstream inherits that.

  **054 is a different mechanism and must not be merged in.** Strict parser: none.
  `HasStructuralMarker`: false. There is no marker fact to propagate - the heading is genuinely
  unnumbered, and `standalone` plus `layout_prominence` caps it at 0.44 against a budget edge of 0.54.
  That is the no-marker ceiling already recorded from M10.1e, and it is an evidence-generation
  question, not a mapping one.

### M10.7-B gate

**Two owners, confirmed distinct.**
- 032/091 - a produced fact does not reach the scorer. Owner is feature propagation, not weight.
- 054 - no fact exists to reach it. Owner is evidence generation.

**Not opened, and the temptation is explicit:** crediting `HasStructuralMarker` in the scorer looks
like a small, well-founded change - the fact exists, it discriminates, and production already trusts
it elsewhere. It is still unmeasured. B3 showed the same fact holds for 35 of 35 contents entries on
092, so crediting it would raise every contents line in the corpus by the same amount, and the four
headings it would help are on one document.

  **091 traced, not assumed.** B1 originally named the owner "032/091" before 091's scorer had been
  looked at. Traced since: all five 091 headings produce a strict structural marker
  (`family=spaced_arabic`, depth 2-4, correct) with `HasStructuralMarker` true, and the scorer credits
  only `unlabelled_numbering_prefix`. Same mismatch, now on evidence.

- [x] M10.7-B2 equivalence counterfactual. One hypothesis: does the strict structural-marker fact
  qualify for the existing strong-marker path? The existing fact was admitted to the existing path -
  no weight tuned, no signal invented, the parser untouched, and 032's known marker-depth damage
  deliberately left in place so the run stays isolated.

  | | 032 | 091 | 092 | 054 |
  |---|---|---|---|---|
  | candidates with strong marker | 426 -> **2268** | 119 -> **794** | 22 -> 157 | 326 -> 363 |
  | score at rank 160 | 0.55 -> **0.82** | 0.54 -> **0.86** | 0.44 -> 0.44 | 0.54 -> 0.68 |
  | emittable at budget | 134 -> 151 | 28 -> **27** | 98 -> 97 | 160 -> 160 |
  | top-160 churn | 133 in / 133 out | 84 / 84 | 3 / 3 | 7 / 7 |

  **Recovery: none on the denominator.** All four 032 headings rise 0.38 -> 0.70 and move up several
  hundred places - 772 to 378, 662 to 268 - and **not one becomes selected**. On 091 all five rise
  0.54 -> 0.86 and two are selected (`3 6 Origin Server` 161 -> 65, `4 2 1 http URI Scheme` 169 -> 77),
  while emittable falls by one.

  **The reason it fails is not the collateral that was feared.** 092 was the stress case because all
  35 of its reviewed contents entries carry the fact - and its role breakdown does not move at all:
  outline 35 -> 35, contents 5 -> 5, prose 6 -> 6, tables 0 -> 0. The predicted contents-entry flood
  did not happen.

  **What fails is selectivity.** `HasStructuralMarker` is true for 2268 of 032's 5311 candidates -
  43% of the population. Admitting all of them to the strong-marker path does not lift the headings
  relative to the field; it re-baselines the field, and the budget edge climbs with it, 0.55 to 0.82.
  The headings gain 0.32 and so does almost everything around them.

  **Negative control passes on the axis that matters.** 054's `AVAILABILITY OF INFORMATION` has no
  structural marker and its score is unchanged at 0.44. Its rank moves 166 -> 177 because the field
  around it rose, which is the intervention working as described rather than reaching past its
  hypothesis.

### M10.7-B gate

| | |
|---|---|
| feature-propagation mismatch | **PROVEN** (032 and 091, both traced) |
| marker-equivalence hypothesis | **REFUTED** |
| production remediation | **NOT JUSTIFIED** |

The fact is real, it is produced, the scorer does not consume it, and consuming it *as strong marker
evidence* does not work - not because it admits the wrong things, but because it admits too much of
the population to change any candidate's standing. A discriminator that holds for 43% of candidates
cannot order them.

- [ ] Not opened, and named so it is not rediscovered: the mismatch could also be addressed by a
  weight between +0.10 and +0.42, or by a fact narrower than `HasStructuralMarker`. The first is
  weight search and is refused. The second would be a new classifier, which M10.3-B already refused
  on its own evidence. Neither has a trigger.

- [ ] Recorded: 032's strict parser reports `depth=1` for `22. 2. 8` while 091's reports depth 2-4
  correctly for `3 6`. The difference is the extraction's space insertion interacting with the dotted
  family. It is a parser-fidelity debt, it was excluded from B2 on purpose, and it would have to be
  measured before any use of marker depth downstream.

- [ ] Recorded: the marker-depth damage above. If a fact whose depth is wrong is promoted into
  scoring, the damage propagates. Worth measuring before, not after.
  - [x] B1 sample frozen at `.verify-build/m105b1-tablelike-reviewed-exposure-sample.json` by
    SHA-256 ordering of exact source-line identity: 10 `table_scope_penalized` + 10
    `not_table_scope_penalized` occurrences per preselected document, 80 total. Population/sample:
    `032` 61/148/20; `043` 24/27/20; `063` 599/54/20; `091` 110/305/20
    (penalized/non-penalized/sample). Every `reviewRole` is null. The artifact freezes source file
    SHA-256, exact PDF geometry/text identity, all carrier blocks, and the current primary carrier's
    scope, penalty, score, rank, selected, and emittable state before role review.
  - [ ] B2 reviewed role labels: annotate exactly those frozen identities in a separate review
    artifact; do not replace a source occurrence, select a prettier line, or infer role from title
    containment. B3 consumes only the completed review artifact plus this frozen actual trace.

## M10 - CLOSED

**Production accuracy remediations promoted: zero.** Regression set unchanged throughout - the same
frozen 15 failures from `dbd65a6` at every checkpoint.

That is the result, not a shortfall. M10 was asked to find why headings are lost and to change only
what the evidence justified. What it produced is a list of repairs that looked well-founded and were
refused on measurement:

| candidate repair | refused because |
|---|---|
| `header_footer_zone` penalty removal | counterfactual delta at budget 160 was +0 |
| raising the candidate budget | reaches candidates the model and validator still reject |
| grouping repair for window-only representation | representation was not the binding constraint |
| multi-line weight change | not measured on the population it classifies |
| `opens_content` source repair | predicate is unreachable; no block ends with a terminal stop |
| exact-paragraph or short-needle grounding rules | would break 70 paragraphs on 010 that legitimately take several blocks |
| appendix trigger repair | a *correct* trigger contaminates identically; the missing exit is the defect |
| quote latch repair | removing the leak recovers no heading |
| `TableLike` / `short_numbered` repair | causal on 092, does not generalise; blocked by the contents owner |
| selection reordering around output-ineligible candidates | the headings it would skip are themselves ineligible |
| tie-break repair | the candidates it would promote are excluded by scope anyway |
| strong-marker propagation | the fact holds for 43% of candidates and re-baselines the field |

**What did improve**, and it is all evaluation-side: occurrence identity replacing text containment at
four layers; gold repaired to occurrence-safe entries on 054; a versioned key resolver; a reviewed
occurrence bridge; passive provenance seams on candidate ranking, DOCX alignment, scope transitions,
the table-like rule and the marker path, each locked so evaluation cannot drift from production; and
cross-document generalisation gates that stopped two document-specific findings from becoming corpus
rules.

**The lesson worth keeping from B2**, because it is the least obvious: a fact being correct does not
make it usable at every stage. `HasStructuralMarker` separates headings from tables perfectly on the
reviewed population and is exactly the right gate for candidate grouping - and it is true of 43% of
candidates, which makes it useless for ordering them. "The fact already exists, propagate it
downstream" is not a safe default.

### Debts frozen, all trigger-gated

None of these is a backlog item. An observed defect is not scheduled work.

| debt | reopen only when |
|---|---|
| appendix latch has no exit (032/091/092) | promoted as source-fact correctness with its own collateral gate, never on recall |
| quote latch open/close read disjoint character sets | a corpus using curly quotes shows measured loss |
| `DetectTocBlockIds` silent on 092 | material loss it alone explains |
| marker depth damaged by extraction spacing | anything downstream proposes to consume marker depth |
| selection spends budget on output-ineligible candidates | justified as model cost or throughput, measured as cost |
| no-marker score ceiling of 0.44 | see below |

**054 no-marker ceiling.** The temptation is to open it because it is the last unexplored branch. It
is already substantially measured: the 0.44 ceiling is proven, exclusion above a 0.44 cut is proven on
054 and 010, `opens_content` is proven unreachable, and a fuller prompt excerpt showed zero benefit.
What is missing is not measurement but a hypothesis - no evidence-generation idea exists that is
specific enough to justify a counterfactual. Trying body-opening v2, then heading-shape evidence, then
typography evidence would be inventing features to find a patch, which is the failure mode M10 spent
itself avoiding. Reopen only on: a reviewed corpus showing material loss from unnumbered-heading
evidence; a product requirement naming an unnumbered-heading recall target; or an existing source or
layout fact shown to discriminate without a new classifier.

## M11 - validated structure and hierarchy, product acceptance

Not accuracy archaeology. The question changes: for headings that survive upstream *legitimately*,
is the final product right?

- [x] M11-A0 eligibility gate, model-free. Upper bound only: validation is a model lane, so what is
  counted here is what *could* reach output, and the true eligible population is a subset of it.

  | document | reviewed | has candidate | selected@160 | scope allows emission |
  |---|---|---|---|---|
  | 092 | 37 | 35 | 35 | **0** |
  | 032 | 8 | 8 | 2 | **0** |
  | 091 | 6 | 6 | 1 | **0** |
  | **054** | 23 | 23 | 20 | **20** |

  **Three of the four documents yield nothing, and not because their headings are unreachable.** 092
  gets 35 of 37 all the way to selection and loses every one at scope; 032 and 091 the same. The
  blocker is `appendix_table` and `quoted_replacement` - the scope leaks frozen as trigger-gated debts
  at the close of M10. M11's population is therefore limited by defects that were deliberately not
  repaired, which is a consequence worth stating plainly rather than discovering later.

  **054 is the only viable population: 20 of 23.** It is also the only document in this project with
  occurrence-safe reviewed gold, so it is the natural place to assess hierarchy - and it is one
  document, which is exactly the position M10.5 was opened to correct.

### M11-A0 CLOSED - hierarchy evaluation is not generalizable

| document | upstream-clean reviewed headings |
|---|---|
| 092 | 0 |
| 032 | 0 |
| 091 | 0 |
| 054 | 20 |

Population available: **yes**. Cross-document: **no**. Generalizable: **no**.
Reason: the upstream scope and output debts deliberately remain frozen.

Not "blocked for lack of data" - the stage is known exactly. Reviewed occurrence, candidate,
selection all pass; 032, 091 and 092 die at scope and output eligibility, before validation is ever
reached. Assessing hierarchy on those documents would measure a later stage on a population upstream
has already deformed, which is how M10 started.

**Neither remaining option is taken.** A live pass on 054 would be a real canary but a
single-document, single-domain one: whichever way it came out, the conclusion would have to carry the
"not generalized" label that M10.5 taught us to attach - so the model spend buys a conclusion whose
status is known in advance. And reviewing a second document's occurrences would risk spending 50-100
reviews before discovering that none of them clear eligibility either, which is evaluation work
committed before the population is known to be usable.

- [ ] Hierarchy reopens when **two or more independent reviewed populations** exist that pass
  occurrence -> candidate -> selected -> validated -> scope/output eligible, and level or parent is
  still wrong. A whole document is not required; an independent, upstream-clean occurrence population
  is enough. Do not manufacture that population because a milestone slot is empty.

## M11-B - product acceptance and release hardening

The question stops being why a heading is lost and becomes: is what the system decides to emit
deterministic, grounded, reproducible, fail-closed and safe to write back? None of it needs new
accuracy work, and most of the infrastructure already exists - the goal is acceptance proof, not
redesign.

- [x] M11-B1 contract acceptance, audited rather than rebuilt. **Almost every invariant on the list is
  already locked by existing tests**, which is the right outcome for an acceptance pass - the work was
  to find out, not to write a second set.

  | invariant | locked by |
  |---|---|
  | only `Emit = true` becomes a product record | `OnlyEmitTrueDecisionsAreSerialized` |
  | no canonical anchor -> never emitted, even when marked emit | `AHeadingWithoutCanonicalGroundingIsNeverEmittedEvenIfMarkedEmit` |
  | text comes from the grounded source, not the observed PDF text | `TextComesFromTheGroundedHeadingTextNotTheObservedPdfText` |
  | unresolved hierarchy carried verbatim, never filled | `UnresolvedHierarchyIsCarriedVerbatimNeverFilled`, `UnresolvedHierarchyStaysUnresolved` |
  | no implicit parent adopted from the preceding heading | `PrecedingHeadingIsNeverAdoptedAsParent` |
  | parent outside the emitted set is dropped, not repointed | `ParentPointingOutsideTheEmittedSetIsDropped` |
  | ungrounded fact never emitted | `UngroundedFactIsNeverEmitted` |
  | scope and role carried without normalisation | `ScopeAndRoleAreCarriedWithoutNormalisation` |
  | unresolved level skipped at writeback, never guessed | `HeadingWithoutAResolvedLevelIsSkipped` |
  | stale stable id skipped rather than misapplied | `StaleStableIdIsSkippedRatherThanMisapplied` |
  | span no longer matching source text rejected | `SpanThatNoLongerMatchesTheSourceTextIsRejected` |
  | writeback refuses the source path; source never touched | `WritingOntoTheSourcePathIsRefused`, `SourceDocumentIsNeverTouched` |
  | determinism of serialization and of writeback | `SerializationIsDeterministicOnTheSameFrozenInput`, `ApplyingTheSameOutputTwiceIsDeterministic` |

  **Legacy revival is guarded in code and is not covered by a test.** `HeaderExtractionPipeline`
  builds an empty `PdfProductOutput` when the audit is null, with the comment that this is "an honest
  empty result, not something to fall back to the legacy projection for". The behaviour is correct;
  nothing locks it, so a future edit could reintroduce a fallback silently. Reaching it in a test
  needs the pipeline, and the pipeline needs a model, so this is a real coverage gap rather than an
  oversight to fix in passing.

  **One genuine fail-closed gap, reported and not repaired.** `PdfProductOutput` carries
  `SourceDocumentSha256`, and `PdfProductWriteback.Apply` **never compares it to the document it is
  writing into**. Per-heading protection is strong - stable id must match and the span text must still
  read exactly as captured - so applying an output to an unrelated document would skip nearly
  everything. But "nearly everything" is not the same as refusing, and the document-level fingerprint
  the output already carries is the obvious check. Adding it is a production behaviour change, which
  is a decision rather than a tidy-up, and it belongs to M11-B3's fail-closed set.
- [x] M11-B2 deterministic replay. **Determinism is claimed from the validated authority boundary
  downward and no further.** The same validated structures, hierarchy facts and canonical groundings
  always yield the same final structure, decisions and product output. Nothing here claims the same
  DOCX yields the same model result; the model lane is upstream of this boundary and is not re-run.

  Audited before adding anything, and most stages were already locked individually:

  | stage | existing lock |
  |---|---|
  | projection | `SameInputProducesTheSameFingerprints` |
  | projection from a frozen artifact | `ProjectsIdenticallyFromASerialisedArtifact` |
  | projection from reconstructed facts | `ProjectsIdenticallyFromFactsReconstructedOffTheRowItemsAlone` |
  | output decisions | `DecisionsAreDeterministic` |
  | serialization | `SerializationIsDeterministicOnTheSameFrozenInput` |
  | writeback | `ApplyingTheSameOutputTwiceIsDeterministic`, `ValidWritebackRemainsDeterministic` |

  **What was missing is the composition.** A stage can be deterministic in isolation and still perturb
  what follows it, and only running the whole chain and comparing the end product catches that. Four
  locks added over the three required shapes plus the restart case: an emittable product, unresolved
  hierarchy, an empty validated set, and the full chain replayed off a frozen artifact through
  decisions and serialization - which is what a resumed run actually executes, and which projection
  alone did not cover.

  **A fixture mistake worth recording, because it was mine and not the code's.** The unresolved-
  hierarchy test first asserted that both level and parent come back null, using a structure that
  declared `parentId` with resolution `marker-resolved`. The parent was legitimately resolved; only
  the level was not. **Level and parent are independent facts**, and an assertion that treats
  "unresolved hierarchy" as one thing is asserting something the contract never said. The fixture now
  leaves both unresolved so the assertion means what it claims.

  Writeback replay is covered by the existing pair plus B3.1's, and byte-level package identity is
  deliberately **not** asserted: the DOCX writer's packaging is not contracted to be reproducible at
  the byte level, and semantic document state is what the product promises. Regression unchanged at
  the frozen 15; 1028 passing.

  Neither B3.2 debt is touched: ambiguous occurrence stays NOT_PROVEN and first fit is not converted
  to unique matching here, and legacy revival stays a coverage gap with no pipeline refactor to reach
  it.
- [x] M11-B3.1 writeback bound to the revision the output declares. **The first production change
  since M10 opened**, and it is fail-closed hardening rather than accuracy remediation - no heading is
  recovered by it.

  Against the six-point gate: the invariant is that a product output may only authorise writeback
  against the exact source revision it was derived from; the permissive state was reachable, because
  `PdfProductWriteback.Apply` never compared `PdfProductOutput.SourceDocumentSha256` to the document
  it was writing into; the rejection is an exact hash comparison, not a heuristic; valid inputs behave
  as before; a refused writeback is refused before the copy is made, so nothing is written at all; and
  both paths are covered.

  The check runs after the existing preflight and before `File.Copy`, and refuses the whole operation
  rather than deferring to the per-heading checks. Those verify that a particular anchor still holds,
  which is a different claim from "this is the document the anchors came from" - and
  `FingerprintMismatchIsNotRescuedByAnchorsThatStillMatch` pins exactly that, using two documents
  built identically so every per-heading check would otherwise pass.

  **Enforcing it exposed four fixtures that were relying on the field being unread.** Three writeback
  tests built their own source document and then bound the output to the shared one; the shadow
  comparison and the harness tool test passed the literal `"sha"`. All were rebound to the real
  fingerprint. That is not incidental cleanup - the guard's first act was to catch test code applying
  an output to a document it did not describe.

  Regression: 15 failures before and after, the frozen set unchanged; 1024 passing, up 5.

- [x] M11-B3.2 failure-mode inventory, audit only. Each mode was traced to the authority contract it
  would violate before asking whether a guard is needed - not the reverse.

  | failure mode | existing authority | current behaviour | locked | status |
  |---|---|---|---|---|
  | source SHA mismatch | document fingerprint the output declares | whole writeback refused | yes | **promoted in B3.1** |
  | null hierarchy level | resolved level or none | writeback skips `level_unresolved`; serializer carries null verbatim | yes | ALREADY_FAIL_CLOSED |
  | stale stable id | canonical anchor | skipped, never re-matched | yes | ALREADY_FAIL_CLOSED |
  | text/span mismatch | canonical span and text | rejected before mutation | yes | ALREADY_FAIL_CLOSED |
  | empty validated set | validator authority | honest empty product output | yes | ALREADY_FAIL_CLOSED |
  | partial model result | per-fact validation | timeout yields `Uncertain` decisions, which fail `IsEligibleHeading` | yes | ALREADY_FAIL_CLOSED |
  | legacy revival | product authority | correct in code, unguarded | **no** | COVERAGE_GAP |
  | ambiguous occurrence | see below | first fit, committed as canonical | no | **NOT_PROVEN** |

  **Partial model result closes cleanly, and the reason is worth stating.** A semantic timeout does not
  produce missing facts that something later fills in; it produces `PdfBlockRole.Uncertain` decisions
  carrying `semantic_batch_timeout`, `semantic_lane_timeout` or `semantic_request_timeout`, and
  `IsEligibleHeading` requires `HeadingTopic`. A timed-out block therefore cannot be validated and
  cannot reach the product. The lane records itself as `partial_timeout`. Each surviving fact earned
  its own authority - option A of the two the contract allowed. Withholding the *whole* product on a
  partial run is option B; it is a design choice, not a violation of any contract that exists, so it
  is not opened.

  **Ambiguous occurrence does not qualify, and it is the interesting one.** The route commits to a
  first fit in two places: `FindMatch` takes the first paragraph at or after the cursor (M10.2-A,
  measured - one needle occurs in 211 paragraphs), and `PdfCanonicalGrounding.FromGroundedHeadings`
  resolves duplicate source ids with `GroupBy(...).Select(g => g.First())`. Both are silent picks.

  But the six-point gate fails at point 1. The grounding contract says an *unmatched* fact stays
  ungrounded rather than being guessed; it does not say a match must be unique. Declaring uniqueness
  as an invariant in order to justify a guard would be inventing the contract the guard needs, which
  is precisely the speculative validation the gate forbids. Making this a gap requires a **design
  decision** that grounding must be unique - and that decision belongs with M10.2-A's finding, which
  proved the matcher weak and could not prove production misgrounds anything.

  **A note on how B3.1 should be read.** The guard turned four fixtures red on its first run. That
  proves the test suite contained calls violating the authority contract. It does **not** show that
  production ever wrote into the wrong document, and it should not be cited as if it did.

  **Result: 1 promoted, 5 already protected, 1 coverage gap, 1 not proven.** No further guard is
  justified, and the seven-guard shape the mode list suggested would have been wrong.

- [ ] Fingerprint enforcement is **not** to be spread further on principle. Each fingerprint is
  enforced at the boundary where its artifact could be used outside the revision it describes: the
  occurrence bridge already hard-fails `stale_occurrence_bridge`, and the product output now hard-fails
  at writeback. "This object has a fingerprint, therefore every method should check it" is defensive
  overengineering and is refused.

- [x] M11-B4.1 broad release smoke, model-free. 95 documents through the pipeline with the model lane
  disabled, checking operational and contract invariants only - no gold, no accuracy claim.

  | measure | value |
  |---|---|
  | documents | 95 |
  | fatal errors | **0** |
  | contract violations | **0** |
  | deterministic routes exercised | 11 |
  | outline headings produced | 70,318 |
  | levels outside 1-9, negative indexes | 0 |
  | requires-review headings | 163 |

  Routes: `auto:vietnamese-legal` 21, `auto:pdf-financial-report` 20, `auto:outline-level` 19,
  `auto:pdf-bold-label` 9, `auto:pdf-tagged-structure` 6, `auto:typed-numbering` 5,
  `auto:pdf-textbook-layout` 4, `auto:vietnamese-administrative` 2, `auto:pdf-toc-dictionary` 2, and
  7 documents with no route (6 of which produce no headings at all).

  **The limitation is larger than the result and must not be buried.** `ProductOutput` came back
  **null on all 95 documents**. It is attached only on the M9 authority route, and on this corpus the
  `auto:*` deterministic routes claim every document before that path is reached. So the product-side
  invariants this probe was written to check - canonical grounding on every emitted record, level
  range, fingerprint matching its own source - **never executed**. What B4.1 actually establishes is
  that eleven deterministic routes complete without crashing and produce well-formed outlines at
  corpus breadth. It establishes nothing about the product chain.

  **And that is itself the release finding.** The contract locked by B1, B2 and B3 - final structure,
  output decisions, product output, fingerprint-bound writeback - sits on a path that, for these 95
  documents in this configuration, is never taken without a model. B4.2 is therefore not a
  nice-to-have canary; it is the only way this project has to smoke the chain it spent M11 hardening.

- [x] M11-B4.2 live OpenRouter product canary. Preflight resolved the provider without logging any
  credential: backend OpenRouter, model `qwen/qwen3.5-9b` (the configured default), key present.

  **A process note first, because it nearly produced a false result.** The first invocation was made
  without `--openrouter` and the CLI silently defaulted to the Local llama.cpp backend - the artifact
  recorded `backend: Local`, `Llama-3.2-3B-Instruct-Q4_K_M.gguf`. It was discarded rather than
  reported; it consumed no OpenRouter call, so the runs below remain one per document.

  | | 054 | 092 |
  |---|---|---|
  | validated structures | **0** | **24** |
  | hierarchy facts | 0 | 24 |
  | canonical groundings | 0 | 24 |
  | legacy lane headings | 0 | 24 |

  **The roles inverted.** 054 was chosen as the positive path and returned an honest empty artifact;
  092, chosen as the adverse case, is the run that actually exercised the chain. 054's empty result is
  consistent with what M10.1a/b already recorded - the live production canary yields 0 validated on
  054 while the frozen M8.1c stage-eval at wide+supplement budget 160 yielded 29 - so this is a route
  and profile outcome, **not a new failure**. Re-running 054 with those flags would be both a second
  live run and a configuration chosen to obtain a better result; neither is taken.

  **092 replayed offline from its own artifact, with no second inference:**

  | check | result |
  |---|---|
  | final headings / grounded | 24 / **24** |
  | emit / requires review | 24 / 24 |
  | product records | 24 |
  | level resolved / parent resolved | 3 / 10 |
  | product fingerprint matches its row | **true** |
  | ungrounded but emitted | **0** |
  | replay-stable fingerprint and product | **true** |
  | every record carries an anchor | **true** |
  | level in range or null | **true** |

  21 of the 24 carry a null level, so a writeback would skip them as `level_unresolved`. That is the
  fail-closed behaviour working, and an expected limitation rather than a blocker.

### M11 release readiness

| | |
|---|---|
| product contract acceptance | **PASS** (B1) |
| fail-closed acceptance | **PASS** (B3, one hardening promoted) |
| post-validation determinism | **PASS** (B2) |
| broad deterministic-route smoke | **PASS**, 95 documents, 0 fatal, 0 violations (B4.1) |
| live product-chain integration | **PASS** on 092 via OpenRouter (B4.2) |

Explicitly **not** established by M11: any accuracy target; hierarchy generalisation, which stays
deferred; and the frozen trigger-gated debts, which remain exactly as frozen. The live canary is
integration evidence for one document and is not cross-document accuracy evidence.

One honest gap remains in the evidence: the product chain has now been smoked live on **one**
document. 054 did not reach it, and B4.1 could not reach it at all because the deterministic routes
claim every corpus document before the M9 path. A second document reaching a non-empty product would
strengthen this materially, and is the obvious next thing if release confidence needs raising - but it
needs a route/profile decision, not another default-flag run.



## M11 - CLOSED, PASS WITH KNOWN LIMITATIONS

| | |
|---|---|
| B1 product contract acceptance | PASS |
| B2 post-validation determinism | PASS |
| B3 fail-closed acceptance | PASS, 1 production hardening promoted |
| B4.1 broad deterministic smoke | PASS - 95 documents, 0 fatal, 0 violations |
| B4.2 OpenRouter live integration | PASS |

**Both live runs are kept as acceptance evidence, and 054 is not a failed canary.** It exercised the
empty path: zero validated structures, zero product records, no fallback, no fabricated output - an
honest empty result is a behaviour worth having tested. 092 exercised the non-empty path end to end,
24 grounded records, replayed offline identically. Two runs, two different shapes.

**No second document was run to obtain another non-empty canary.** Doing so would have meant either
re-choosing a route and profile after seeing the outcome, or running documents until one happened to
take the M9 path. Both turn a release smoke into a search for a good result.

### What may and may not be claimed

Claimable: *the product pipeline is release-ready with respect to the product authority contract,
fail-closed behaviour, deterministic post-validation processing, and a bounded OpenRouter integration
smoke.*

Not claimable: a heading accuracy figure; that hierarchy generalises; that all document families use
the M9 route; that any trigger-gated debt is resolved; that the OpenRouter product path is proven
cross-document. Non-empty live product-chain coverage is **one document** - integration evidence, not
accuracy evidence.

The 21 of 24 null levels on 092 are a known limitation with correct behaviour: unresolved is
preserved as null and the writeback skips, rather than a level being invented to complete the write.

### OpenRouter release requirement

A production or acceptance smoke invocation must explicitly resolve `backend = OpenRouter` and
`model = qwen/qwen3.5-9b`. **A run is invalid as OpenRouter acceptance evidence if the artifact
reports another backend**, and a Local run is never reinterpreted as OpenRouter evidence.

This is recorded because a CLI invocation without `--openrouter` silently defaults to the Local
llama.cpp backend. It is not a product-contract failure - the artifact declares `backend: Local` and
the model file honestly, so the system does not misreport its provider - but it is an operational
configuration hazard wherever OpenRouter is the intended deployment. Making the provider mandatory in
the CLI is **not** done here; it would need a deployment contract that says so.

## M12 - release and operational readiness

Deliberately not another accuracy milestone. No scorer, no hierarchy, no TOC, no marker work. Scope is
configuration, deployment, provider selection, secrets, logging and redaction, artifact retention,
checkpoint and replay, timeouts, error reporting, writeback output location, the release runbook, and
version and fingerprint reporting.

The forgotten `--openrouter` is the evidence that this is now worth more than another accuracy
experiment.

- [x] M12-A1 configuration acceptance, audited before anything was added.

  | a run should be able to state | present? |
  |---|---|
  | backend | yes - artifact `generation.backend` |
  | model | yes - `generation.model`, resolved per backend |
  | source document SHA | yes - per row |
  | prompt identity | yes - `promptSha256` |
  | route configuration identity | yes - `routeConfigSha256` |
  | occurrence fingerprint | yes - per row |
  | artifact kind, schema version, `usesGold` | yes |
  | secret handling | **safe** - the key is read from `OPENROUTER_API_KEY` and used only in the Authorization header; it is never logged |
  | application version / git revision | **no** |
  | endpoint host | **no** |
  | timeout values | **no** - hashed into `routeConfigSha256`, not legible |
  | execution status, partial or timeout | **no** |

  **The provenance gap: a run cannot say what code produced it.** `generation.codeRevision` is read
  from the `DHX_CODE_REVISION` environment variable and nothing sets it, so it came back null on both
  live canaries. There is no assembly version metadata, no `SourceRevisionId`, and no `--version`
  command, so nothing else can answer the question either. The artifact is honest - it reports null
  rather than guessing - but for release acceptance an artifact that cannot identify its own build is
  weak evidence.

  **The sharper gap, and it lands on a result we just produced.** The artifact records **no execution
  status and no partial-or-timeout flag**. 054's live run returned zero validated structures, and from
  the artifact alone it is impossible to tell whether that is because nothing validated or because the
  semantic lane timed out. The distinction is made correctly *inside* the pipeline - B3.2 traced it,
  timeouts become `Uncertain` and fail `IsEligibleHeading`, and the lane computes a `partial_timeout`
  status - but that status stays in the route summary and never reaches the facts artifact. An empty
  product and a degraded product are indistinguishable to whoever reads the artifact later, which is
  precisely the person a release runbook is written for.

- [x] M12-A2 execution provenance carried into the artifact. Promoted on the observability gate, not
  the fail-closed one: nothing is refused, no behaviour changes, and the only claim is that evidence
  survives the boundary.

  The route already computes `SemanticLane.Status` and the CLI already holds it on the audit; the
  artifact simply did not write it. It is now copied verbatim onto the row - `complete` or
  `partial_timeout`, the producer's own vocabulary, with no `isPartial`, `timedOut` or
  `executionHealth` derived from it.

  **Schema decision, audited rather than reflexive.** No code anywhere validates
  `PdfHierarchyFactsArtifact.SchemaVersion`, the field is additive and optional, and an artifact
  written before it existed deserialises to null - which is unambiguous, because a new artifact always
  carries a status. So the version is **not bumped**, and the row documents that null means unknown
  provenance rather than a completed run.

  It goes on the **row**, not the envelope, because the envelope's own contract says documents may be
  produced in separate runs; a single per-artifact status would be wrong the moment two documents with
  different lane outcomes share a file.

  Six locks: the healthy status is preserved, the degraded status is preserved, **an empty successful
  run and a partial-timeout run stay distinguishable while carrying identical counters and identical
  occurrence fingerprints**, a legacy artifact reads as unknown and never as complete, the product
  projection is unchanged by either status, and serialisation is deterministic across a round trip.
  Regression unchanged at the frozen 15; 1036 passing.

  **What this does not claim about 054.** Its artifact does not show that run timed out. It shows that
  if a run does time out, the artifact as it stood could not be told apart from an honest empty one.
  That was the gap; the 054 result is what made it visible.

- [x] M12-A3 `codeRevision` from the binary. Deployment contract chosen first: **the binary is the
  authority for which code ran.** An operator-supplied value can be wrong, honestly or otherwise, and
  an artifact trusting it would claim a revision the binary never contained.

  **Audit answered all three questions before any code was written.**
  - Does the build already embed a revision? **Yes.** The release assembly reports
    `ProductVersion = 1.0.0+047b16976c141c4139ff08c1a894b5cc6f41f3ae` - the SDK writes
    `SourceRevisionId` into `AssemblyInformationalVersion` when the build can see the repository, with
    no package reference or build change needed.
  - Is it the exact source revision? **Yes** - `047b169` is the commit that build was made from.
  - Is it non-null in the release build? **Yes here**, and null by construction for a build made
    without repository access, which is reported as null rather than guessed.

  **The nuance mattered.** The informational version is `{Version}+{SourceRevisionId}`, so only the
  segment after the `+` is the revision. Writing the whole string would put `1.0.0+` into a field whose
  contract says "commit"; `1.0.0` with no `+` yields null. No runtime `git rev-parse`: a release
  machine may have no `.git`, and a source checkout is not the authority for the binary executing.

  Precedence is `embedded ?? environment`, never the reverse, so `DHX_CODE_REVISION` can fill a gap
  but can never override what the binary carries. No `codeRevisionSource` field was added - "it might
  be useful to know which one won" does not meet the ambiguity point of the observability gate.

  **Ordinary execution and release acceptance are separated.** A run may carry a null revision and its
  artifact stays valid with incomplete provenance; it is simply **not eligible as release evidence**.
  That contract is locked at the acceptance boundary, not enforced as a runtime refusal - turning
  observability into a fail-closed check would be the wrong gate.

  Seven locks: extraction from the assembly, a product version without a revision segment yielding
  null, embedded winning over the environment, the environment used only when nothing is embedded,
  missing staying null, stability across reads, and release acceptance rejecting a null revision.

  **Not verified end to end.** The producing command requires an LLM, and no further live run was
  spent to watch the field populate in a real artifact. What is verified is that the built assembly
  carries the revision and that extraction and precedence are locked - the input is confirmed present
  and the reader is confirmed correct, which is the claim being made and no more.

- [x] M12-A4 endpoint host and timeouts, audited **separately** because bundling them as "runtime
  config" would have hidden that they reach opposite conclusions.

  **First, what `routeConfigSha256` actually covers.** It hashes
  `analystBudget|wide|supplement|semanticHierarchy|semanticConcurrency`. **The timeouts are not in
  it** - not their values, not even their identity. So two runs with different thresholds produce the
  same route-config hash.

  **Endpoint host: NOT JUSTIFIED.** `OpenRouterOptions.Endpoint` defaults to the OpenRouter chat
  completions URL and is never set from the CLI or the environment - `FromEnvironment` reads only the
  key and the model, and there is no `--openrouter-endpoint` flag. Under `backend = OpenRouter` the
  endpoint is therefore fixed by the binary and already determined by `backend` plus `codeRevision`.
  Recording it would duplicate a fact derivable from the build, which is exactly what the
  observability gate's third point exists to refuse.

  Scoped honestly: this holds **for OpenRouter**. LmStudio and SGLang do take `--*-endpoint` flags, so
  the same question would answer differently for them - and that is a separate audit, not a reason to
  record the field everywhere now.

  **Timeouts: PROMOTED.** All three are CLI-configurable
  (`--pdf-stage-semantic-request-timeout` 90s, `--pdf-stage-semantic-batch-timeout` 120s,
  `--pdf-stage-semantic-lane-deadline` 300s, clamped to 1..3600 and 1..14400), and none of them
  reaches the artifact in any form. So `partial_timeout` - a fact M12-A2 had just promoted - could not
  be interpreted: two runs identical in source, model, revision, route-config hash and status mean
  opposite things at a 10-second threshold and a 300-second one, and the artifacts were byte-identical.
  A slow service and a tight policy call for opposite responses.

  The three values are copied from the `SemanticLaneOptions` the lane was handed, in seconds, in the
  producer's own terms. **No** `timeoutPolicy`, `isAggressiveTimeout` or `effectiveTimeout` is derived,
  because the route computes no such fact. Absent thresholds read as null - unknown, never "the
  defaults". Four further locks, including two artifacts sharing a status and an occurrence
  fingerprint while remaining distinguishable, and product projection unchanged either way.

  Regression unchanged at the frozen 15; 1047 passing.

### M12-A status

| candidate | outcome |
|---|---|
| `semanticLaneStatus` | promoted (A2) |
| `codeRevision` | promoted (A3), binary is the authority |
| semantic lane timeouts | promoted (A4) |
| endpoint host | **not justified** - derivable from backend and revision for OpenRouter |

No further provenance field is proposed. The remaining gap is not metadata: it is that
`routeConfigSha256` proves identity without preserving anything readable behind it, so a hash can say
two runs differed without saying how. That is a **candidate for a different milestone** - retained
route configuration - and needs a real question before it is opened, not the observation that a hash
is opaque.

## M12-A - CLOSED, release provenance sufficient for the current contract

| | |
|---|---|
| promoted | `semanticLaneStatus`, `codeRevision`, semantic request / batch / lane-deadline timeouts |
| rejected | OpenRouter endpoint host - derivable from backend and revision |
| deferred, trigger-gated | retained readable route configuration |

**No further provenance field is proposed.** This is the point to stop asking what metadata an
artifact is missing.

The one open observation is that `routeConfigSha256` proves identity without retaining anything
readable behind it: it can say two runs differed, not how. That is **not** a backlog item. It opens
only on a real question - an incident response requirement that effective configuration be
reconstructable from an artifact alone, a release-evidence requirement that every
result-affecting setting be independently recoverable, or an actual incident that could not be
explained because only the hash survived.

## M12-B - release execution and deployment contract

The question changes from "what can an artifact say about a run" to "does the operator or deployment
actually run what we intend to ship".

- [x] M12-B1 release invocation audit, **audit only, no code**. Prompted by a real event rather than a
  hypothetical: during B4.2 an invocation without `--openrouter` silently ran the Local llama.cpp
  backend.

  **What enforces the OpenRouter release intent today: nothing.**
  - `dhx.cmd` is a path resolver. It picks the first binary that exists - `out-vulkan`, then
    `out-cuda`, then `bin/Release`, then `bin/Debug` - and forwards its arguments unchanged. It pins
    no backend and no model.
  - There is no Dockerfile, no compose file, no CI workflow, no service configuration and no
    deployment script anywhere in the repository.
  - `scripts/` holds development and evaluation helpers - model download, benchmarking, holdout
    management, an LM Studio MCP publish - and nothing that selects a provider for a release run.

  So this is case **B**: the release contract, as it stands, rests on whoever types the command.

  **What that does and does not prove.** It proves the CLI default admits Local and that a person can
  omit the flag; the artifact then reports `backend: Local` truthfully, so nothing is hidden. It does
  **not** prove that a production deployment could silently use the wrong backend - there is no
  deployment path to audit, and that absence is the finding rather than a conclusion about one.

  **Remediation is not "change the CLI default".** The enforcement that already exists and already
  works is the release-acceptance rule used in B4.2: a run is eligible as release evidence only if the
  artifact reports `backend = OpenRouter`, `model = qwen/qwen3.5-9b`, and a non-null `codeRevision`.
  Making the provider mandatory in the CLI needs a deployment contract stating that an invocation
  without an explicit provider is invalid - case **C** - and no such contract exists yet.

  **A side observation worth keeping**, because it vindicates M12-A3: `dhx.cmd` prefers a published
  GPU build over the freshly compiled one, so it can launch a binary older than the working tree. The
  artifact reports that binary's own embedded revision, which is exactly what makes a stale launcher
  visible after the fact. Had `codeRevision` come from the operator or the environment, the artifact
  would have claimed the revision someone intended to run rather than the one that ran.

### M12-B1 CLOSED

| | |
|---|---|
| deployment / provider enforcement | **none found** |
| CLI default may select Local | PROVEN |
| artifact reports the actual backend | PROVEN |
| release acceptance can reject Local | PROVEN |
| production wrong-provider incident | **NOT PROVEN** |
| mandatory-provider CLI contract | does not exist |
| CLI remediation | **NOT JUSTIFIED** |

- [x] M12-B2 executed binary identity, **audit only**. The question is not "should `dhx.cmd` run the
  newest build" - no contract says `out-vulkan` must lose to `bin/Release`, or that the working tree
  is what should execute. The question is whether release evidence needs the revision confirmed
  *before* the run, or whether the artifact declaring it afterwards is enough.

  **The hazard is live, and measured rather than imagined:**

  | binary | embedded revision | |
  |---|---|---|
  | `out-vulkan\dhx.dll` (what `dhx.cmd` selects first) | `7b8ad20` | 2026-07-31, **403 commits behind HEAD** |
  | `bin/Release/net9.0/dhx.dll` | `bc5ceb9` | this session |
  | working tree HEAD | `1b977f4` | |

  Typing `dhx` today runs a binary from before M7. The B4.2 canaries did not, because they went
  through `dotnet run --project`, which is why their artifacts reported `047b169`.

  Also recorded without prescribing anything: `dhx-ui.cmd` republishes before every run, `dhx.cmd`
  does not. That asymmetry is why one launcher can go stale and the other cannot.

  **Post-run detection works.** A run through `dhx.cmd` today would produce an artifact declaring
  `7b8ad20`, and the acceptance rule - compare against the intended release revision - would reject it
  as evidence. Nothing is hidden, and this is precisely the ambiguity M12-A3 was chosen to resolve:
  the operator's intent and the working tree both say `1b977f4`, and only the binary knows otherwise.

  **What post-run detection cannot do is prevent the spend.** The rejection happens after the
  OpenRouter calls have been made. Whether that matters is a release-process question, not a technical
  one, and it decides between two contracts:

  - *post-run acceptance is sufficient* - then pre-run enforcement is **NOT JUSTIFIED**, the launcher
    is left alone, and **M12 can close with no further production change**.
  - *the model call may only begin once the approved revision is confirmed* - then post-run detection
    is too late, and a preflight comparing the selected binary's revision against an expected one has
    an authority contract behind it.

  **This audit does not choose.** The cost of an OpenRouter call is real, but a cost existing does not
  create a contract; the release process has to say what it requires.

  If the second contract is ever adopted, the remediation stays small: acceptance already carries three
  independent facts (`backend`, `model`, `codeRevision != null`), and an expected revision would be a
  fourth. It does **not** call for a `ReleaseManifest`, `ReleaseProfile` or `DeploymentDescriptor`
  unless several requirements genuinely need one authority object.

### M12-B2 CLOSED - post-run acceptance is the chosen contract

| | |
|---|---|
| stale launcher binary | PROVEN |
| selected binary != working HEAD | PROVEN |
| embedded revision exposes the truth | PROVEN |
| post-run release rejection works | PROVEN |
| data integrity impact | **none proven** |
| writeback safety impact | **none proven** |
| current material cost | wasted model calls only |
| pre-run revision enforcement | **NOT JUSTIFIED** |

**A measurable operational cost is not, by itself, an authority contract.**

**Reopen pre-run revision enforcement if** OpenRouter cost or latency becomes material enough that
invalid release executions must be prevented before inference; or deployment policy explicitly
requires that only an approved revision may contact the model provider; or a stale binary can cause
effects before post-run acceptance can reject them; or release automation needs a pre-execution
approval gate for compliance or audit. None of these is currently proven.

## M12 - CLOSED, PASS WITH KNOWN OPERATIONAL LIMITATIONS

| area | outcome |
|---|---|
| A - `semanticLaneStatus`, `codeRevision`, semantic timeouts | promoted |
| A - OpenRouter endpoint host | not justified |
| A - retained readable route configuration | trigger-gated |
| B1 - provider enforcement | none in deployment; artifact acceptance catches it; CLI change not justified |
| B2 - binary identity | launcher may select a stale binary; embedded revision exposes it; pre-run enforcement not justified |

**Known operational limitation, recorded rather than fixed:** `dhx.cmd` may execute an older published
binary in preference to `bin/Release`. This can waste an OpenRouter run before release acceptance
rejects the artifact. It is not treated as a release blocker.

Not done, deliberately: the `dhx.cmd` selection order is unchanged, the provider is not made
mandatory, no `expectedRevision` is introduced, and no release manifest type is created.

Artifact retention, checkpoint retention, failure reporting, output and writeback location, and secret
redaction tests were **not** worked through as a checklist because the milestone is called release
readiness. Each opens on its own evidence or not at all.

### Release position after M11 and M12

Claimable: the pipeline is release-ready with respect to product authority, fail-closed behaviour,
deterministic post-validation processing, OpenRouter integration, and operational provenance.

Not claimable: that any accuracy target is met; that hierarchy generalises; that the scope debts are
resolved; or that every launcher selects the newest build.

**The next step is a release candidate and a runbook, not M13.** Diagnostic and architecture
milestones stop here; something remaining that could be improved is not a reason to open another one.

## Release candidate - ACCEPTED

Approved revision `c31f0954cfd1af4ad45b8a490b33b71c6cf93b96`. Run per `docs/release-runbook.md`, one
OpenRouter execution, no retry, no second inference.

**The runbook's binary check earned its place immediately.** `bin/Release` carried the approved
revision; `out-vulkan` - which `dhx.cmd` selects first - still carried `7b8ad20`, 403 commits behind.
The acceptance run was invoked through `dotnet run --project` for exactly that reason.

**Acceptance checks**

| check | value | |
|---|---|---|
| `generation.backend` | `OpenRouter` | PASS |
| `generation.model` | `qwen/qwen3.5-9b` | PASS |
| `generation.codeRevision` | `c31f095…` | PASS |
| revision == approved | | PASS |

**Artifact**

| field | value |
|---|---|
| `sourceDocumentSha256` | `be530ba0606f…` |
| `semanticLaneStatus` | `complete` |
| `semanticLaneTimeouts` | 90 / 120 / 300 seconds |
| validated / facts / groundings | 29 / 29 / **28** |

**Offline replay, no second inference**

| check | result |
|---|---|
| final headings, grounded | 29, **28** |
| ungrounded | **1** |
| emit | **28** |
| product records | 28 |
| ungrounded but emitted | **0** |
| product fingerprint matches its row | true |
| replay-stable fingerprint and product | true |
| every record carries an anchor | true |
| level in range or null | true |

**This run exercised the fail-closed path with real data**, which the earlier canary did not: one
validated fact reached no canonical grounding, and it was refused at output rather than emitted
without an anchor. 3 of 28 have a resolved level, so a writeback would skip the remaining 25 as
`level_unresolved` - contract, not fault.

**It also closes a caveat left open in M12-A3.** That milestone recorded that `codeRevision` was not
re-verified in a live artifact, because verifying it would have cost a model run for something the
input and the reader already proved. This acceptance run needed to happen anyway, and all three
promoted provenance fields - `codeRevision`, `semanticLaneStatus`, `semanticLaneTimeouts` - populated
correctly in a real artifact for the first time.

### Scope of this acceptance

| in scope | |
|---|---|
| product authority | PASS |
| fail-closed behaviour | PASS |
| post-validation determinism | PASS |
| OpenRouter integration | PASS |
| operational provenance | PASS |

| out of scope | |
|---|---|
| extraction accuracy target | not established |
| hierarchy generalisation | deferred |
| trigger-gated accuracy and scope debts | unchanged |

Non-empty live product-chain coverage remains **one document**. This is an acceptance of safety and
operability, not of extraction quality.

## Extractor quality benchmark - measurement only

Opened after the RC was accepted, and deliberately **not** an end-to-end product benchmark. The
question is narrow: does the extractor find and confirm headings, before the frozen output and scope
debts remove them?

**Progress is tracked on five axes, and the fifth is not "missing data":**

| axis | state |
|---|---|
| pipeline architecture and authority | done |
| fail-closed, determinism, release | done |
| candidate extraction quality | **benchmark in progress** |
| semantic validation quality | needs benchmark |
| end-to-end product and hierarchy quality | **blocked by known frozen debts** |

An end-to-end benchmark today would mostly measure the appendix leak, the quote leak and `TableLike` -
all proven, all deliberately frozen - rather than the extractor. That is why the scope stops before
output eligibility.

- [x] A0/A1 candidate recall and selection, model-free, over the reviewed occurrences that already
  exist. No new gold, no model call.

  | document | reviewed | full candidate | rank <= 160 |
  |---|---|---|---|
  | 054 | 23 | 23 | 18 |
  | 092 | 37 | 35 (see below) | 35 |
  | 032 | 8 | 8 | 2 |
  | 091 | 6 | 6 | 1 |
  | **all** | **74** | **72** | **56** |

  Candidate recall uses the project's occurrence definition - a candidate counts only if it covers
  **every** semantic-bearing line. 054's two multi-line occurrences pass under that stricter rule, so
  the figure survives the definition rather than depending on the looser one M11-A0 used.

  **Correction: the two 092 misses are genuine candidate-construction gaps, not a probe join defect.**
  The occurrence-id join (`PdfReviewedOccurrenceBridge` for 054, `PdfTextUtilities.CanonicalForMatch`
  elsewhere, replacing the probe's earlier weaker whitespace-collapse) was fixed and shipped
  (`80389c1`), and the honest result held at **72/74, not 74/74**. `BENCH_A1_DEBUG=1` confirms: at the
  resolved source line, no candidate covers or even touches it. The earlier claim that candidate `b22`
  already exists nearby (rank 434) was a **stale reference** - `b22` exists, but at a different line
  index (rank 471) from a prior code revision; candidate ids are discovery-order, not stable identity
  across a revision, and must never be quoted across one. 72/74 is recorded as the true figure,
  finding two unowned candidate-construction losses (recorded, no causal owner assigned).

  **Selection is reported beside recall and never folded into it.** 56 of 72 reach the budget, and the
  gap is entirely 032 (2 of 8) and 091 (1 of 6) - documents where the candidate demonstrably exists
  and is not selected. A single "extractor recall" number would have hidden that first loss.

- [x] A2 conditional validation survival, live OpenRouter (`qwen/qwen3.5-9b`), one run each for 054 and
  092, frozen profile (`--pdf-stage-wide --pdf-stage-supplement --pdf-stage-blocks 160`, matching A1's
  exact candidate pool and rank<=160 boundary). 054: 18/18 selected@160 validated (100%). 092: 0/35
  (0%) - investigated below, not left as a raw number.
- [x] A2b 092 first-loss audit, artifact-only, no new model call. Ruled out: evaluator join failure
  (0/35, page+text checked, not text alone - an early text-only check produced one false positive,
  corrected). Found instead: all 35 carry `StructuralScope=appendix_table` -> `DomainRole.TableTitle`
  -> `DocumentDomainPolicy.IsExcludedFromOutline` = true, a fully deterministic, model-output-
  independent exclusion applied by `PdfProposalValidator.Validate` *after* the analyst already ran
  (`PdfLayoutEvidenceOutline.cs` dispatches the full `selected` set with no domain pre-filter, so the
  analyst was genuinely exposed to all 35 - its answer just could never change the outcome). Same
  mechanism as the already-closed M10.3-A/M10.6/M10.7-D1 appendix-latch/`TableLike` investigation (same
  candidate ids: `b45`, `b53`, `b58`, `b61`); M10.3-A already ran the fix-the-latch counterfactual and
  refuted it as recoverable (corrected scope just fails differently, via `TableLike` rank collapse). Not
  reopened here. A greenlit checkpoint-instrumented rerun was cancelled once this was proven exhaustive
  (35/35, not a sample) and the analyst's actual decision was shown to be provably irrelevant to the
  outcome.
- [x] A2c model-free deterministic-eligibility census, all four reviewed populations, zero model calls
  (`PdfDeterministicEligibilityCensusProbe`). Added `DeterministicExclusionReason` as one shared
  implementation on `PdfExtractorQualityBenchmarkProbe.Classify`, reused by A2b/A2c rather than
  reimplemented, to avoid the join-drift class of bug this session already hit once.

  **Locked decomposition, current reviewed population (74 occurrences, 4 documents):**

  | stage | count | rate |
  |---|---|---|
  | reviewed | 74 | - |
  | full candidate | 72 | 97.3% |
  | selected@160 | 56 | 77.8% of candidates |
  | deterministically answer-irrelevant | 38 | 67.9% of selected |
  | decision-relevant | 18 | - |
  | validated (054 only) | 18/18 | 100% of decision-relevant |

  032's both selected occurrences and 091's one carry the identical `domainRole:TableTitle` exclusion
  as 092 - not a new finding, the same gate, confirmed by census rather than assumed. The
  semantic-evaluable population under the current reviewed set is, in practice, 054 alone.

  **Reading discipline, stated precisely:** 18/18 is *conditional survival on 054's 18 decision-relevant
  reviewed true-heading occurrences* - not "semantic validator accuracy". Precision (false positives
  among validated output) was not measured by A2 itself; see the validated-output precision review
  below for the actual figures (054 = 92.6%, 092 = 51.6%-58.1%).

  **Standing conclusion, at the evidence actually gathered:**
  - candidate construction: 72/74 = 97.3%, generally strong on this reviewed population.
  - selection: material loss (56/72), concentrated in 032/091, unrelated to the finding below.
  - deterministic scope/domain eligibility policy: **the dominant measured blocker** on this
    population - 38/56 selected occurrences lost before an analyst decision could matter, all within
    the already-known, already-frozen appendix/`TableLike` mechanism.
  - semantic validation quality, recall side: 054 = 18/18 (conditional survival, not accuracy);
    032/091/092 = not measured; cross-document semantic recall = **not established**.
  - semantic validation quality, precision side: 054 = 25/27 = 92.6%; 092 = 16/31 = 51.6%-58.1%,
    dominated by TOC-window artifacts validating in place of the body headings the domain-role gate
    excludes. See below for detail.
  - this does not reopen the appendix/`TableLike` debt: M10 already proved these losses overdetermined
    and single-variable remediation unsafe. Finding the same mechanism again via a live benchmark is
    confirmation, not new cause for action.

- [ ] No further live calls scheduled for 032/091/092 - A2c already proved their remaining
  selected-but-untested occurrences carry zero semantic-sample value under the current reviewed
  population.

- [x] First-loss ledger, all 74 reviewed occurrences, one row each, stamped with the single stage that
  first cost it (`PdfFirstLossLedgerProbe`, model-free, reuses `Classify`/`DeterministicExclusionReason`
  rather than a second eligibility implementation). Reconciles exactly against A0-A2c:

  | first loss | count |
  |---|---|
  | deterministic scope/domain eligibility | 38 |
  | survived to validated | 18 |
  | ranking/budget | 16 |
  | candidate construction | 2 |
  | **total** | **74** |

  Bonus finding the ledger surfaces that the earlier per-stage counts did not: of the 16
  ranking/budget losses, **11 also carry a deterministic-exclusion reason underneath** - fixing
  ranking alone could recover at most 5 of the 16, the rest are overdetermined the same way M10
  already found other losses to be. Not a new debt, not reopened; recorded as evidence for future
  prioritisation only.

- [x] Validated-output precision review, 054 and 092, manual classification against each item's full
  `sourceBlockText`/page/geometry (page/source-line identity, never text-only - the project's now
  twice-learned lesson). Categories: `TRUE_HEADING`, `TOC_ENTRY`, `MULTI_HEADING_COMPOSITE`,
  `NON_HEADING`, `UNCERTAIN`.

  **054 (27 validated): 25 TRUE_HEADING, 2 NON_HEADING.** The 2: `b1945`/`b1947`, headingText
  "Level 1 :"/"Level 2 :", full block text is a fair-value-hierarchy accounting definition sentence
  ("Level 1 : Financial assets and liabilities whose values are based on unadjusted quoted prices for
  identical assets or..."), not a section heading - the `labelled` marker heuristic matched the
  "Word N :" shape of body prose. **Validated precision = 25/27 = 92.6%.** This is new: A2 already
  established 054's conditional recall at 18/18 = 100%, and that number was never a precision claim -
  it still is not, and now has an actual precision figure beside it.

  **092 (31 validated): 16 TRUE_HEADING, 12 MULTI_HEADING_COMPOSITE, 1 TOC_ENTRY, 2 UNCERTAIN, 0
  NON_HEADING.** The 16 true headings are pages 6-30 with monotonically increasing section numbers
  (2 -> 3 -> 3.1 -> ... -> 8.2), a coherent document sequence. The 12 composites and 1 TOC entry are
  all pages 2-3, `sourceBlockText` showing 2-3 concatenated titles per block (e.g. `s-window-74`:
  "3 1 Storing Hea der and Trailer Fields 3 2 Updating Stored Hea der Fields") - this is the TOC-region
  window-candidate pattern A2b already flagged as "observed, plausible" and this review promotes it to
  **confirmed and quantified**: 12/31 (38.7%) of everything 092's live run validated is a TOC-window
  artifact, not a body heading. The 2 uncertain (`b40`/`b44`, both page 4) are left uncertain rather
  than forced - their section number/back-matter position doesn't fit the surrounding document order
  and no page image was available to settle it. **Validated precision = 16/31 = 51.6%, range
  51.6%-58.1%.**

  **Reading discipline:** 092's already-known recall-side finding (deterministic domain-role gate
  discards its entire reviewed cohort before an analyst decision matters) and this precision finding
  are independent losses at different stages, not the same fact twice - the domain-role gate blocks
  *body* headings from validating; the TOC-window pattern separately causes *front-matter* text to
  validate *in their place*. Neither explains the other; both point at the same already-frozen
  mechanism (M10.3-A/M10.6/M10.7) from different sides.

- [x] A3 (redefined from the stub below) - semantic benchmark population screening, corpus-wide,
  model-free (`PdfA3PopulationScreeningProbe`, all 95 documents, no gold, no model call). Frozen rule,
  stated before any result was read: usable = selected@160 >= 20 and decisionRelevant >= 15.

  **76 of 95 documents are usable**, spread across every domain family: `01_phap_quy` 18,
  `02_hop_dong_mua_sam` 6, `03_tai_chinh_ke_toan` 15, `04_giao_trinh` 15, `05_bien_ban_hop` 9,
  `06_dich_song_ngu` 8, `07_system_generated` 5. The 19 non-usable documents mostly have 0 candidates
  (PDF conversion or extraction not applicable for that file - not an exception, not a silent zero
  hidden as success; see the full report for the list) rather than a low ratio.

  **Reading discipline: this is a document-level candidate-pool health metric, not the same
  denominator as A2c.** A2c measured decision-relevance among a small set of *already-reviewed gold
  occurrences* per document (e.g. 092: 0/35). This screening measures it across the document's *entire*
  selected@160 pool. 092 shows 82/160 = 51% decision-relevant here - not a contradiction: the 35
  reviewed occurrences all happened to land in 092's bad half (front matter/TOC region), while most of
  the document's other candidates are fine. A document passing this screen is a candidate for *new*
  occurrence review, not a retroactive excuse for the old reviewed set.

  Financial statements (`03_tai_chinh_ke_toan`) and the WB procurement family
  (`02_hop_dong_mua_sam`) show the widest range (17%-100%, 36%-100%) - the same
  appendix/`TableLike`-family mechanism this session traced on 032/054/092 likely explains the low end
  there too, though that is inference from the pattern, not re-verified per document here.

  **Not yet done, and deliberately separate:** actually selecting 3-5 documents (stratified,
  deterministic order, not cherry-picked on looks) and reviewing their occurrence gold. Screening
  answers "worth reviewing"; it does not select or review by itself. Full 95-document table:
  `.verify-build/a3-screening.txt` (not committed - regenerate via `BENCH_A3_REPORT=<path>`, the probe
  that produced it is committed).

- [x] B0/B1 - cross-document benchmark round 2: selection rule frozen and applied, before looking at
  which documents it would produce. Eligible pool = A3's existing `usable` set (76 documents, unchanged
  threshold). One document per shape - financial statement, procurement, legal/administrative,
  textbook/technical - tie-broken within each domain folder by **lowest document ID number among the
  eligible set**, nothing accuracy-related. Result:

  | shape | domain | document |
  |---|---|---|
  | legal/administrative | `01_phap_quy` | `001_Bo_luat_Dan_su_91-2015-QH13` |
  | procurement | `02_hop_dong_mua_sam` | `028_WB_RFB_Works_Without_Prequal_2017` |
  | financial statement | `03_tai_chinh_ke_toan` | `041_IBRD_Financial_Statements_June_2025` |
  | textbook/technical | `04_giao_trinh` | `056_OpenStax_Business_Law_I_Essentials` |

  Interpretive call, flagged rather than silently made: the fourth shape was specified as
  "textbook/RFC/technical" - `04_giao_trinh` (textbook) was picked over `07_system_generated` (RFC,
  already home to 091/092, both deeply investigated this session already) specifically to get a
  genuinely uninvestigated domain rather than a third document from a family with established debt.
  041 is a new financial-statement document, distinct from the already-reviewed 054 - a second data
  point in that family, not a repeat.

  Frozen manifest committed at `keys/benchmark-round-2/manifest.json`: document SHA256s, selection
  rule, route profile (identical to A2/A2b - not tuned per document), blind-packet rule, and the B4
  precision label set. No field in it may change after B2 results are seen.

- [x] Lane A (parallel with B2) - blind, source-first review packet generator
  (`PdfBlindReviewPacketProbe`), one packet per B1 document, every raw source line (page + lineId +
  text, the same identity A0-A3 already use) in reading order, nothing else - no candidate id, rank,
  selected status, scope, or domain role. Sampled from the full source-line population, never the
  candidate pool, so a candidate-construction miss cannot vanish from the recall denominator the way
  sampling from candidates would let it. Packets generated for all four documents (8.6k-20k lines each
  in `.verify-build/benchmark-round-2-packets/`, not committed - regenerate via
  `BENCH_PACKET_DOCX`/`BENCH_PACKET_REPORT`). **Packet generation only - the actual line-by-line
  labelling pass (B2 itself) has not started; ~7000-10000 source lines per document is a real effort,
  not something to rush into unprompted.**
- [x] Lane C (parallel with B2) - precision-review worksheet, formalized as reusable tooling
  (`PdfValidatedOutputWorksheetProbe`) rather than the by-hand PowerShell inspection used for 054/092.
  Dry-run against both existing artifacts reproduces the known findings exactly: 054's `b1945`
  ("Level 1 :") false positive shows with its full disambiguating context
  (`sourceBlockText`, not the truncated `headingText` span), and 092's `s-window-74` shows its
  concatenated multi-heading text. Confirms the machinery works before spending review effort on new
  documents - nothing new needs inventing for B4.
- [x] B2.0 pilot - full blind review of document 001 (Bo_luat_Dan_su_91-2015-QH13), chosen only because
  it is first in the frozen B1 order, not for expected difficulty or accuracy. Purpose was strictly
  process validation before spending the same effort on the other three - not an early accuracy read,
  and the numbers below did not change the review rule for 028/041/056.

  **Scale correction: this document has 761 genuine headings (6 Phan + 27 Chuong + 39 Muc + 689 Dieu),
  not ~20-40.** That estimate assumed a document shaped like the earlier reviewed set (054/092/032/091);
  a civil code's own TOC-equivalent structure is every article, verified exhaustively (689 unique
  article numbers, sequence 1-689, zero gaps).

  **Method, entirely from the blind packet, no pipeline signal:** regex-pattern review of raw source
  text (`Phần thứ N`, `Chương [roman numeral]`, `Mục N.`, `Điều N.`), each rule validated against real
  edge cases before being trusted at scale, not assumed:
  - 6 cross-reference false positives found and excluded (`Điều 47 của Bộ luật này...` - a citation that
    coincidentally starts its own wrapped PDF line, discriminated by "period immediately after the
    number" - a citation never has one, a real heading always does; checked against all 6 instances).
  - 1 false positive on a common Vietnamese word ("Mục đích..." = "purpose", not a `Mục N.` section
    marker) - caught by requiring the digit+period, not just the word.

  **Two objective tooling bugs found and fixed during self-audit** (multiline-span representation, not
  accuracy-motivated rule changes - explicitly the only kind of change the user allowed after seeing
  001's numbers): a width-based wrap-continuation heuristic missed a real 2-line Điều title wrap
  (`Điều 142`) and a real 3-line Mục title wrap (`Mục 5`) because ordinary body-line widths overlap with
  wrapped-title widths; replaced with an orthographic rule (Phần/Chương/Mục titles are ALL-CAPS and
  reuse the existing all-caps continuation check; Điều titles are sentence-case, so continuation is
  detected by the next line starting lowercase - a citation or new clause never does). A second bug in
  that same fix stopped at a lone-comma line instead of skipping past it, truncating one title
  mid-word (`Điều 202`, a long entity-type enumeration) - fixed by skipping punctuation-only lines when
  looking for the next letter-bearing line, matching how the ALL-CAPS rule already tolerates them.

  **Self-audit:** 10 pages sampled by a fixed rule (every 15th page, deterministic, not chosen after
  seeing results) - 6.7% of the document's 149 pages, independently re-derived from the raw packet and
  compared against the gold file. Exact match on every sampled page: no missing heading, no duplicate,
  no wrong span.

  **Gold artifact:** `keys/occurrence-bridge/001_Bo_luat_Dan_su_91-2015-QH13.occurrence-bridge.json`
  (committed, same schema as 054's bridge - built by `PdfDocument001GoldBuilderProbe`, wired into
  `PdfExtractorQualityBenchmarkProbe.Populations` via a generalized `Bridge(stem)` helper so no new
  join logic exists anywhere). Level/parent deliberately not reviewed this round, per instruction - the
  benchmark question is heading extraction/semantic quality, not hierarchy.

  **Pre-model metrics (no OpenRouter call), from the existing A2c machinery unchanged:**

  | stage | 001 |
  |---|---|
  | reviewed | 761 |
  | full candidate | 761 (100%) |
  | selected@160 | 165 |
  | deterministically answer-irrelevant | 3 (`InlineClauseReference` x2, `AmendmentAnnotation` x1 - a
    different domain-role trigger than 092's `TableTitle`, evidence the mechanism generalizes rather
    than being one document's quirk) |
  | decision-relevant | 162 |

  **Protocol verdict: clean.** Proceeding to review 028/041/056 with the same validated rules and
  tooling (adapted per document's own structural conventions, not per document's numbers).

- [x] B2 - document 028 (WB procurement RFB, 314 pages). Scaling the 001-validated method immediately
  surfaced three genuinely new complications - handled by manual review/reconciliation, not by writing
  more general regex (the explicit stop-condition: "if every section needs its own rule, automation has
  become a second extractor, not a review aid" - not crossed here, but close, and named as the line).

  1. **Numbered prose is not a heading.** Section II's "1. the terms of the Bidding Documents; and / 2.
     the Employer's decision..." and all of Section VII's ESHS/Code-of-Conduct commitments are sentence
     enumerations after a lead-in ("may challenge any of the following:", "the policy... commitments
     to:"), confirmed by reading the lead-in directly. Excluded by index as a reviewed classification -
     no new production heuristic, no general list-vs-heading classifier.
  2. **Numbering restarts within one section are real, not duplicates.** Section III has two local
     scopes (Margin of Preference/Evaluation, then the qualification-criteria table's own category
     headers restarting at 1). Section VIII has three: the main GC clauses, "APPENDIX A - General
     Conditions of Dispute Board Agreement" (clauses 1-9), "APPENDIX B - Fraud and Corruption" (clauses
     1-2, identical boilerplate to Section VI). Recorded as an optional scope tag embedded in
     `goldStableId` (e.g. `028/GCC/4`, `028/DBA/4`) - review-only annotation, no schema change, no
     scope-resolver component.
  3. **GC clauses 13, 14, 15, 19 have no standalone heading anywhere in this document's own source** -
     verified directly: the TOC jumps 12.4 -> 13.1, 13.8 -> 14.1, 14.15 -> 15.1, and 18.x -> 20 with no
     "N. [Title]" line in either the TOC or the body. Not inferred from outside FIDIC knowledge of what
     a standard edition "should" contain - simply absent, so gold contains nothing for them, no
     fabrication.

  **Method reused unchanged from 001** (`PdfReviewedOccurrenceBridge`, `Bridge(stem)` loader) plus one
  real bug the pilot caught before it reached gold: a section's own local TOC lines ("1. Scope of
  Bid........6") structurally match the same "N. Title" body-heading pattern, so once the pilot's
  boundary bug was fixed to include a section's own TOC in its scan range, every TOC line was being
  added to gold a second time as a fake body occurrence - fixed by excluding dot-leader-shaped lines
  from the body scan.

  Section IV (Bidding Forms) and Section X (Contract Forms) confirmed form-field-only (spot-checked,
  no numbered clause structure at all) and excluded from clause-level gold, per 032's own already-
  reviewed labels (same WB procurement family: numbered form entries there are `form_field`, not
  `outline_heading`).

  Self-audit: the three hardest regions (Section VII, Section VIII's three scopes, the GC 12-20
  neighborhood) traced by hand as the validation set before trusting the method at scale, plus a
  fixed-interval page sample (45, 90, 135, 180, 225, 270) checked against gold with no discrepancy.
  One known, accepted gap: a short unlabelled sub-caption ("Code of Conduct (ESHS)") inside a BDS
  instruction block was noticed but not added - a single borderline case, not a pattern, left as a
  documented limitation rather than triggering a new rule.

  **Gold: 113 occurrences** (`keys/occurrence-bridge/028_WB_RFB_Works_Without_Prequal_2017.occurrence-bridge.json`).
  Pre-model census: reviewed=113, full candidate=84 (74.3%), selected@160=1, decision-relevant=1.

  **Reading discipline, held deliberately narrow:** large candidate-dense document + severe rank/budget
  survival loss is *consistent with* the already-measured M10.7-D1 ranking/budget mechanism - not
  promoted to "M10.7-D1 is the causal owner of 028's loss," which would need an exact first-loss trace
  on 028 itself that was not run. Recorded as consistency, not attributed as cause.

  **028 is not a semantic-benchmark candidate - decision-relevant=1 is not a usable denominator** -
  though it remains a genuinely valuable candidate-construction/selection benchmark document
  (source->candidate and candidate->selection are both well exercised here; selection->semantic has
  almost no cohort to measure). A live OpenRouter call for 028 would not answer a semantic-quality
  question with n=1; not run for that reason, not because its numbers looked unfavorable - no model
  result has been seen for 028 at any point in B2. See the semantic-eligibility gate below, frozen
  before 041/056 are reviewed.

- [x] Semantic-benchmark eligibility gate, frozen before 041/056 are reviewed (so it cannot be tuned to
  their numbers): **decision-relevant >= 15 -> eligible for a B3 live run; below that -> semantic metric
  recorded as "insufficient cohort," no live call spent on it.** 15 is not an accuracy threshold - it
  only guards against spending a live call on a near-empty denominator the way 028's n=1 would have
  been. 001 (162) clears it; 028 (1) does not.

  | doc | reviewed | full candidate | selected@160 | decision-relevant | semantic eligible? |
  |---|---|---|---|---|---|
  | 001 | 761 | 761 | 165 | 162 | yes |
  | 028 | 113 | 84 | 1 | 1 | no - insufficient cohort |
  | 041 | 62 | 61 | 15 | 15 | yes |
  | 056 | 46 | 32 | 15 | 15 | yes |

- [x] B2 - document 041 (financial statements), closed model-free. The reviewed bridge has **62**
  source-grounded occurrences: 14 MD&A section headings, 30 TOC-listed MD&A subtopics reconciled to
  body source, 4 primary statements, and 14 note headings. It excludes caption indexes, table row
  labels, narrative references, and unlisted note-internal structure by source/context only; no 028
  rule or prior semantic behavior was used. Twelve duplicate-text cases were resolved by their raw
  source neighborhood (for example `Credit Risk`: body heading, not table header/prose; `Net Income`:
  two separate body occurrences); one truncated TOC label has no exact canonical source span and was
  deliberately not fabricated.

  Fixed self-audit is locked in `PdfDocument041GoldBuilderProbe`: document SHA, extraction
  fingerprint, reviewed status, unique occurrence identity, and every `(index, lineId, text)` source
  binding must match current raw extraction. It caught and corrected a generator-only issue before
  freeze: punctuation-only `,` was incorrectly accepted as an ALL-CAPS continuation of Note B.
  The frozen bridge is `keys/occurrence-bridge/041_IBRD_Financial_Statements_June_2025.occurrence-bridge.json`.

  Pre-model census: reviewed=62; full candidate=61 (98.4%); selected@160=15;
  deterministically answer-irrelevant=0; decision-relevant=15. Thus 041 clears the already-frozen
  semantic-cohort threshold exactly. This says nothing yet about semantic quality: **no model call has
  been made.**
- [x] B2 - document 056 (textbook), closed model-free. A source-only Contents checklist (14
  chapters + 32 numbered sections) was reconciled to the distinct body occurrence that introduces
  prose, explicitly excluding Contents, local Chapter Outline repetitions, figures, running headers,
  assessment/endnote material, and unnumbered subheadings without a complete source checklist. The
  two duplicate-title cases (`10.2`, `12.2`) were resolved by local source context to their body
  occurrence; the chapter-outline sibling is not gold. No typed key, candidate/rank/scope signal, or
  model output participated in this review.

  Frozen bridge: `keys/occurrence-bridge/056_OpenStax_Business_Law_I_Essentials.occurrence-bridge.json`.
  The fixed integrity audit locks the source document hash, extraction fingerprint, 46 unique stable
  ids, and every raw `(index, lineId, text)` binding. Pre-model census: reviewed=46;
  full candidate=32 (69.6%); partial=6; absent=8; selected@160=15;
  deterministically answer-irrelevant=0; decision-relevant=15. Thus 056 clears the frozen semantic
  cohort threshold exactly, while its 14 pre-semantic misses remain candidate-construction evidence,
  not a reason to tune or repair the production route during B2.

- [x] B2 CLOSED - occurrence-safe, source-grounded gold plus model-free census now exists for all
  four preselected documents. This is not a cross-domain accuracy result: B3 has not run.

  | doc | reviewed | full candidate | selected@160 | decision-relevant | semantic eligible? |
  |---|---:|---:|---:|---:|---|
  | 001 | 761 | 761 | 165 | 162 | yes |
  | 028 | 113 | 84 | 1 | 1 | no - insufficient cohort |
  | 041 | 62 | 61 | 15 | 15 | yes |
  | 056 | 46 | 32 | 15 | 15 | yes |

  `028` remains a candidate-construction/selection benchmark but contributes no semantic metric. B3
  therefore has exactly three prospective calls (`001`, `041`, `056`), each one frozen-profile
  OpenRouter run with no retry and no tuning; this work item intentionally stops before those calls.
- [x] B3 - frozen-profile OpenRouter semantic benchmark. After B2 was committed/pushed at `f514ddc`,
  exactly one valid `pdf-hierarchy-facts` run was frozen for each semantic-eligible document using
  `OpenRouter`, `qwen/qwen3.5-9b`, wide+supplement candidates, 160 blocks, default concurrency, and
  the manifest timeouts. `028` was not called. The valid artifacts have matching source SHA, backend,
  model, prompt/profile, and build revision `f514ddc`; an earlier `001` preflight built at `80389c1`
  with zero facts is retained only as invalid provenance and is not evaluated.

  | doc | decision-relevant | validated reviewed | conditional semantic recall |
  |---|---:|---:|---:|
  | 001 legal | 162 | 0 | 0/162 (0%) |
  | 028 procurement | 1 | N/A | N/A - insufficient cohort |
  | 041 financial | 15 | 15 | 15/15 (100%) |
  | 056 textbook | 15 | 15 | 15/15 (100%) |

  This proves a semantic-stage loss for 001 only; it does not distinguish analyst proposal, span
  mapping, or validator owner. No investigation of 001 is opened in this round.
- [x] B4 - full validated-output precision census, no sampling. The frozen worksheet was reviewed by
  source fact identity, page, validated span, and full source-block context using only
  `TRUE_HEADING`, `TOC_ENTRY`, `MULTI_HEADING_COMPOSITE`, `NON_HEADING`, and `UNCERTAIN`.

  | doc | reviewed | TRUE | TOC | composite | non-heading | uncertain | confirmed precision |
  |---|---:|---:|---:|---:|---:|---:|---:|
  | 041 financial | 29 | 16 | 12 | 1 | 0 | 0 | 16/29 (55.2%) |
  | 056 textbook | 46 | 17 | 0 | 18 | 11 | 0 | 17/46 (37.0%) |
  | 001 legal | 0 | N/A | N/A | N/A | N/A | N/A | N/A |

  These are per-document precision figures, not a pooled product/corpus precision.
- [x] B4.1/B5 - precision first-owner audit, artifact-only before any remediation. B5.1 replayed B2's
  exact occurrence -> covering candidate -> rank -> selected@160 -> deterministic-eligibility join for
  every 041 TRUE output: 15 match the 15 decision-relevant reviewed occurrences; the +1
  (`s-line-2337`, Section XII) is a real `TRUE_HEADING_OUTSIDE_GOLD_SCOPE`, not a census or selection
  defect. In 056, `About OpenStax` and `Where Did the Concept Originate?` are likewise real TRUE
  headings outside the 46-item frozen gold scope.

  B5 traces all 42 false accepts: 041 has 12 TOC/context-fact misses (page 3 persisted as
  `document_body`) and one composite boundary; 056 has 18 composite-boundary failures and 11 numbered
  review questions. The 11 questions pass every existing deterministic scope/domain/evidence-origin
  predicate, so a single checkpoint-instrumented **replication** of 056 was justified after the
  model-free audit. It was added as opt-in diagnostics at `6a57759` and is not a replay of the B3 run:
  all 11 received analyst role `HeadingTopic` (0.9-1.0), establishing `ANALYST_WRONG_ROLE` for that
  failure shape. The replication produced 45 rather than B3's 46 validated outputs; record that as
  instability and do not run a third call.

  **Decision:** no common semantic-precision owner is proven and no production remediation is opened.
  041 remains TOC/context-shaped; 056 splits into representation-boundary and analyst-role failures.
  Do not reopen 001, ranking, candidate construction, old TOC debt, or composite grouping until a
  separate owner-specific remediation hypothesis and safety population are defined.

**The end-to-end product figure (C) stays closed** until a product-facing question requires it, and it
must then be reported with the known debts attached rather than as an extractor score.

## C1 - 001's zero semantic survival: input census and glue provenance

Opened as a **narrow follow-up to find an owner**, not to fix anything. B3 measured 0 of 162
conditional semantic recall on 001 while 041 and 056 both returned 15 of 15.

### C1.1 - model-free input census (162 decision-relevant occurrences)

The structural input is uniform and correct, which rules out the easy answers: 162/162 carry a
structural marker and a loose labelled marker, 160/162 parse as the `labelled` family, 162/162 sit in
`document_body` under the `legal` regime, 155/162 carry a recognised legal domain role, 162/162 have
trusted evidence origins, and 161/162 receive an identical context shape. Nothing malformed reaches
the deterministic gates - by construction, since this cohort is the one those gates would not discard.

What separates the cohort is token shape: **147/162 (90.7%)** carry a token longer than twelve
characters, against 2/15 on 041, 3/15 on 056 and 3/18 on 054 - the three cohorts the semantic lane did
survive. The heading text reads `Điều 1. Phạmviđiềuchỉnh` where the title is `Phạm vi điều chỉnh`.

### C1.2a - the glue is ours, and it is not in extraction

Provenance only, occurrence identity, no repair attempted. Comparing the PDF text layer, the source
line and the analyst-facing text over the cohort's 171 source lines: **169/171 are `NO_GLUE` at the
source line**. `Điều 1. Phạm vi điều chỉnh` extracts correctly, longest token 5.

So `PdfLineExtraction` is **not** the producer, and the PDF is not damaged. This corrects the reading
C1.1 invited: the glue appears later, in `PdfSemanticBlock.DisplayText`, which is
`PdfTextUtilities.HeadingReadable(Text)`.

**The producer, exactly.** `HeadingReadable` repairs kerning-fragmented words by joining runs of short
letter-tokens with `string.Concat`. A token counts as a fragment when it is at most five letters, all
lowercase, and **absent from a hard-coded English stop-list** (`a, an, and, as, at, by, for, from, in,
into, is, must, not, of, on, or, should, that, the, these, this, those, to, vs, with`). Vietnamese
words are mostly at most five letters and no Vietnamese function word is in that list, so
`Phạm | vi | điều | chỉnh` is read as one fragmented word and glued.

### C1.2b - control: the transform is language-dependent, measured

Same population definition in every document - source lines carrying a structural marker - documents
fixed before looking at any result.

| doc | language | marked lines | glued (clean before, long token after) | rate |
|---|---|---:|---:|---:|
| 001 | vi | 2590 | 2398 | **92.6%** |
| 010 | vi | 421 | 386 | **91.7%** |
| 032 | en | 1480 | 146 | 9.9% |
| 041 | en | 840 | 45 | 5.4% |
| 056 | en | 1085 | 77 | 7.1% |
| 092 | en | 230 | 18 | 7.8% |

**Not 001-specific:** 010 is a second Vietnamese document at 91.7%. Where the transform fires on
English it is doing its intended job on genuine kerning damage
(`Ind ivid ua l pri nt` -> `Individualprint`), imperfectly.

### What this establishes, and what it does not

| | |
|---|---|
| source text quality | **not the owner** - source lines are clean |
| `PdfLineExtraction` | **not the owner** - spaces present in line text |
| deterministic producer | **identified**: `HeadingReadable` fragment repair, English-shaped heuristic |
| language dependence | **PROVEN by measurement**, two Vietnamese documents at ~92% vs four English at 5-10% |
| cause of 0/162 | **NOT PROVEN** |

The last row is the important one. Separating "the glue broke the analyst" from "the analyst is weak
on Vietnamese" is still impossible from artifacts: both predict the same outcome, and every Vietnamese
document reaching the analyst is glued, so there is no natural control.

### C1 status, frozen

| C1.1 | |
|---|---|
| 001 semantic loss | **PROVEN** - 0/162 |
| deterministic gate as owner | **RULED OUT** - the cohort is by definition what the gates would not discard |
| main analyst text damaged | **REFUTED** - see C1.3 preflight |

| C1.2 | |
|---|---|
| `HeadingReadable` Vietnamese gluing | **PROVEN** |
| source / extraction damage | **RULED OUT** - 169/171 source lines clean |
| causal link to 0/162 | **NOT PROVEN** |
| context-only contribution | **NOT PROVEN** |

**C1.3 preflight refuted the counterfactual before it was run.** `PdfBlockAnalyst` sends
`PromptSourceText(block.Text)` - a length truncation and nothing more. Over the 162-occurrence cohort,
`block.Text` carries a glued token in **3**, while `DisplayText` carries one in **147**. The analyst
judges clean text; the glue reaches the prompt only through neighbouring-context excerpts. Bypassing
the repair at that boundary would have changed the surroundings and not the text under judgement, so
the approved call was not spent on it. Context-only contribution remains a secondary hypothesis, not
strong enough to justify its own counterfactual.

### C1.4 - checkpoint-instrumented replication of 001

One run, frozen B3 profile, **only** `--pdf-stage-checkpoint` added. Build revision `8ec3ed5`,
backend OpenRouter, model `qwen/qwen3.5-9b`, wide+supplement, 160 blocks, manifest timeouts.

**The zero reproduces.** `validatedHeadings: 0` again, so B3's result was not a one-off.

**The analyst is not the owner.** The checkpoint holds 20 batches, all `completed`, carrying **160
analyst decisions**: **158 `HeadingTopic`** and 2 `BodySentence`, at confidence 0.95-1.0, no reason
strings. The role pass ran 02:32:34 to 02:34:21 - 107 seconds of a 300-second lane deadline.

| owner | verdict |
|---|---|
| `ANALYST_NOT_PROPOSED` | **refuted** - 160 decisions returned |
| `ANALYST_WRONG_ROLE` | **refuted** - 158 of 160 are `HeadingTopic` |
| `ANALYST_WRONG_SPAN` / span lane | **remaining, and not distinguishable** |
| `VALIDATOR_REJECTED` on scope/role/evidence | already ruled out - this cohort is decision-relevant |

`IsEligibleHeading` needs `ValidateSpan` to pass, and that needs `decision.HeadingSpan`, which the
role pass does not produce - spans come from a separate `ResolveHeadingSpansAsync` lane. So the loss
is between a correct role answer and a validated structure, in the span lane. Whether that lane timed
out or returned spans that failed validation **cannot be told from either artifact**.

### The span lane is invisible, and the status field says "complete" anyway

Two facts, both from reading the production path:

- The span lane is **not checkpointed**. Every record in the checkpoint is the role batch lane, so a
  span-lane outcome leaves no trace.
- `semanticLaneStatus` is computed as
  `semanticTimedOut || (includeSemanticHierarchyFallback && hierarchyRun.TimedOut)`. The **span run's
  timeout is not in that expression.** A span lane that times out converts every decision to
  `Uncertain` - and the artifact still reports `complete`.

This replication shows exactly that shape: `semanticLaneStatus: complete`, timeouts 90/120/300, and
zero validated headings from 158 correct role answers.

That is the same defect M12-A2 promoted a fix for, one lane over: an authoritative runtime fact lost
at the evidence boundary, leaving a degraded execution indistinguishable from a healthy one. It meets
the observability gate's ambiguity test on its face, and it is **not implemented here** - the finding
is recorded and the decision is open.

### C1.4a/b - span-lane observability, promoted

Two observability hardenings, kept independent, and **no model call**.

**C1.4a - `spanLaneStatus`, a new field beside `semanticLaneStatus`, never merged into it.** That
field already means something and is already in the runbook; widening it silently would break every
reader relying on it. The span run's own outcome is captured where it happens and copied verbatim in
the route's vocabulary - `complete`, `partial_timeout`, or `not_run` when the semantic lane timed out
first and the span lane never executed. A lane that never ran is not reported as complete, and null on
an older artifact means unknown.

**C1.4b - the span lane is checkpointed like the role lane**, under the existing
`--pdf-stage-checkpoint` flag: per batch, each block's resolved span or its absence, identified by
source authority (page and line id) as well as candidate id, because candidate ids shift between
revisions and must not be the identity a later comparison rests on.

**A second silent path was found while wiring it.** `ResolveHeadingSpansAsync` catches every
per-batch exception with a bare `catch { continue; }`, so a failed span batch left its blocks with no
span and no trace - `missing-pointer-span` at validation, indistinguishable from a healthy run. The
swallow is **not changed** here, since that is a behaviour question; the batch now records the
exception type, which is a fact the frame already holds.

Seven locks, including the one that pins the defect: **`semanticLaneStatus` may remain `complete`
while `spanLaneStatus` is `partial_timeout`**, so nobody later merges the two semantics by accident.
Behavioural neutrality is locked too - the product projection is identical either way. Regression
unchanged at the frozen 15; 1075 passing.

### C1.5 - frozen one-run protocol (prepared, not yet called)

The next and only approved C1 call is an **instrumented B3 replication of 001**. Its profile is
unchanged: OpenRouter `qwen/qwen3.5-9b`, wide + supplement, 160 blocks, B3's 90/120/300 second
timeouts, and default semantic concurrency. The sole runtime addition is `--pdf-stage-checkpoint`.
No retry, prompt/context change, `HeadingReadable` change, candidate/ranking change, or second run.

Before the call, `PdfC15SpanLanePreflightProbe` freezes the source-identity denominator from the
reviewed bridge: **761 reviewed, 162 decision-relevant**. Its identity is `goldStableId + page + all
required PDF line ids`; candidate id/rank are explicitly debug-only. This prevents the prior 160 role
answers from silently shrinking the 162-occurrence denominator.

The evaluator taxonomy is frozen in production order:
`ROLE_NO_DECISION`, `ROLE_NON_HEADING`, `SPAN_NOT_RUN`, `SPAN_TIMEOUT`,
`SPAN_BATCH_EXCEPTION`, `SPAN_UNRESOLVED`, `SPAN_RESOLVED`,
`SPAN_RESOLVED_BUT_INVALID`, `VALIDATED`.

Static path audit, now regression-locked: a non-cancellation span exception records its **whole
four-block batch** with `failureClass`, leaves those spans null, and continues later batches;
`OperationCanceledException` escapes to the lane wrapper (`partial_timeout`); a semantic timeout
before span starts yields `not_run`. Checkpoint payloads now carry every block's exact source line ids
for both role and span batches, while retaining the old first `lineId` for old readers. This is
provenance only; it does not change span behaviour.

**C1.5 result - one and only one replication (`401a030`).** The source hash, prompt/profile and B3
route settings match the frozen manifest. Semantic completed 20 batches / 160 blocks (158
`HeadingTopic`, 2 `BodySentence`). Span completed 20 batches / 80 blocks, then the lane reported
`partial_timeout`; no span batch recorded an exception. The frozen offline evaluator reconciled **all
162** source occurrences by page + required line ids:

| first loss | occurrences |
|---|---:|
| `ROLE_NO_DECISION` / `ROLE_NON_HEADING` | 0 |
| `SPAN_BATCH_EXCEPTION` | 0 |
| `SPAN_TIMEOUT` | **68** |
| `SPAN_RESOLVED` (checkpoint only) | **94** |
| `VALIDATED` | 0 |

Thus C1's legal collapse is not analyst role failure. At this point the 94 resolved checkpoint spans
were **not yet known invalid**: a partial timeout discards the whole `spanAnalysis` value before the
product runs `PdfProposalValidator`. C1.6 therefore replays actual validator predicates offline rather
than calling the checkpoint result invalid.

### C1.6 - offline span validation and throughput audit

**Lane A (94 resolved checkpoint pointers):** all **94/94** replay as `ValidateSpan == valid`, with
no reason, and `PdfProposalValidator.Trace` reports `eligible`. They were not rejected by a pointer
or scope predicate. The production wrapper replaces even these completed results with role decisions
marked `Uncertain` when the enclosing span lane returns `partial_timeout`; their runtime disposition
is therefore `discarded_by_span_partial_timeout`.

**Lane B (68 without a recorded pointer):** the checkpoint has 20 semantic batches / 160 blocks and
20 completed span batches / 80 blocks. Semantic's last completion is 10:03:57; span's first is
10:03:59 and last is 10:05:16. The completed-span checkpoint window is 77.2 seconds; consecutive
batch intervals have median 3.48 seconds, min 1.61, max 9.11. The old checkpoint has no request-start
or timeout timestamp, so it cannot prove the precise timeout instant or distinguish a pre-first-batch
stall. It does prove no exception and normal completed-batch throughput before the lane ended.

**Corrected C1 owner statement:** role classification is refuted as the dominant owner. On this run,
the **span-lane deadline plus its all-or-nothing timeout wrapper is causal for all 162 first losses**:
68 never receive a checkpointed pointer; 94 receive valid pointers which the wrapper discards. This
does **not** by itself justify a timeout increase: at observed median throughput, 40 heading batches
would need roughly 139 seconds, and changing the deadline needs a separate cost and cross-document
gate. Do not change the validator or exception swallow; neither was causal here.

### C1.7 - partial-result preservation gate (offline, 001 only)

**C1.7A exact counterfactual.** The replay uses only C1.5's persisted role/span facts. It preserves
the 80 completed span blocks, marks every block without a pointer `Uncertain`, then calls the current
`PdfProposalValidator`, `PdfBlockGrounder`, and production `AlignToDocx` matcher - no model, no retry,
no new candidate ids, and no reimplemented alignment. The measured effect is:

| stage | blocks | decision-relevant reviewed occurrences |
|---|---:|---:|
| current route | 0 | 0/162 |
| validated | 79 | **94/162** |
| grounded | 58 | **56/162** |
| emitted after alignment | 55 | **54/162** |

The exhaustive 001 reviewed bridge matches 54 of the 55 emitted source blocks; one does not map to a
reviewed gold occurrence. This is a 001-only offline precision signal, not a production acceptance
claim. It proves the directly causal gain is preserving completed results, not purchasing more time
to chase the 68 uncompleted occurrences.

**C1.7B independence proof.** `ResolveHeadingSpansAsync` prompts/parses one four-block batch at a
time and writes each pointer directly by source id. `PdfProposalValidator.ValidateSpan` tests only that
pointer and that candidate's `RawText`; there is no final cross-batch normalisation or reconciliation.
A regression locks the operational form: a completed first batch retains its pointers when a later
batch throws. The sole all-batch invalidation is the outer `PdfLaneExecution` timeout wrapper, which
replaces `spanAnalysis` with `roleAnalysis` marked `Uncertain`.

**Gate outcome:** partial preservation is a legitimate **remediation candidate**, but remains an
accuracy behavior change requiring recurrence and collateral evidence. Timeout increase remains deferred.

### C1.8 - cross-document partial-timeout recurrence audit (artifact-only)

`PdfC18PartialTimeoutRecurrenceAuditProbe` scans the whole existing `.verify-build` inventory without
calling a provider or changing production. It requires the full mechanism, not a timeout alone:
`partial_timeout + completed valid spans + wrapper discard + material downstream loss`. It uses the
span checkpoint only within its own run; cross-run identity is document SHA / source occurrence, never
candidate id.

The inventory has exactly one fully evidenced positive: **001**. Its one span checkpoint contains 80
completed source blocks; C1.6 proves 94 reviewed occurrences have valid completed pointers and C1.7
proves that **54 reviewed emitted outputs are lost under the current wrapper and recovered only by the
offline partial-preserve counterfactual**. No second
`partial_timeout` artifact carries a C1.6-equivalent validation replay plus an output denominator.
There is also no post-provenance `spanLaneStatus=complete` artifact with comparable checkpoints, so the
complete-lane no-op control is specified and unit-tested but not yet observed in this artifact cohort.

### C1 - CLOSED

| partial span preservation promotion state | evidence |
|---|---|
| causal on 001 | **PROVEN** - `partial_timeout` plus all-or-nothing span discard |
| material on 001 | **PROVEN** - 94 valid spans discarded; 54 reviewed outputs recover offline |
| batch independence | **PROVEN** |
| cross-document recurrence | **NOT PROVEN** |
| collateral gate | **INSUFFICIENT EVIDENCE** |
| production promotion | **BLOCKED** |
| timeout increase | **NOT JUSTIFIED** |

Do not manufacture a second live call merely to make the count two. Partial-result preservation is now
trigger-gated debt, not mandatory backlog. Reopen only when an independently justified run or incident
provides the complete mechanism, a product incident shows material loss, a naturally scheduled run
supplies a usable positive/negative control, or a product requirement explicitly changes from
lane-level fail-closed to best-effort partial semantic output. Validator changes and exception-swallow
remediation remain outside C1's causal evidence.

## C2 - composite-boundary evidence recovery gate

The B4 precision census recorded aggregate composite recurrence across two independent domains: one
`MULTI_HEADING_COMPOSITE` false accept in 041 financial and eighteen in 056 textbook. That establishes
an aggregate recurrence signal, **not** a common producer. C2.1 must start from the reviewed source
occurrences, not recreate a 19-case population from the `1 + 18` total.

### C2.0 - evidence recovery (closed, no model/no production change)

Recovery searched all reachable Git branches/tags and object paths for B4/B5, precision, worksheet,
reconciliation/owner artifacts and for content such as `MULTI_HEADING_COMPOSITE`, `sourceFactId`, 041,
and 056. It then searched the available `.verify-build` tree, the only registered Git worktree, and
available historical source/download directories by the B3/B4/B5 lineage. `6a57759` contains only the
B5 probes; both require external `BENCH_B5*_ARTIFACT` and review paths and do not embed their inputs.
No B3 artifact, B4 worksheet, or B4.1/B5 per-case review package was recovered.

| C2 state | result |
|---|---|
| cross-domain composite recurrence (aggregate) | **PROVEN** - 041=1, 056=18 |
| exact historical 19-case occurrence identity | **MISSING** |
| common first producer | **NOT PROVEN** |
| safe boundary invariant | **NOT PROVEN** |
| production remediation | **NOT JUSTIFIED** |
| C2.1 historical producer census | **BLOCKED** |

Do not regenerate historical labels from current output, aggregate counts, or legacy outline artifacts.
If a future benchmark is independently justified, freeze and review it as **NEW COMPOSITE REVIEW
POPULATION**, not as recovered B4 evidence. Until then C2 is trigger-gated debt: reopen only if the
original packet reappears, a naturally scheduled run preserves an exact review packet, or a product
incident supplies occurrence-safe composite evidence.

## NEXT.0 - owner evidence availability inventory

Before opening another historical owner investigation, its occurrence-level authority packet must be
verified present. Aggregate B4/B5 counts are not a substitute for the reviewed source identities,
model checkpoint, or source-context trace that an owner-specific audit needs.

### Inventory result (closed, model-free, no production change)

The inventory searched all reachable Git history/object paths, the available `.verify-build` tree,
the registered worktree, and available historical source/download/temp directories. B5's committed
probes (`6a57759`) are harnesses only: `PdfB51SelectionIdentityReconciliationProbe` requires external
`BENCH_B51_ARTIFACT` plus `BENCH_B51_REVIEW`; `PdfB52ReviewQuestionDeterministicAuditProbe` likewise
requires external `BENCH_B52_ARTIFACT` plus `BENCH_B52_REVIEW`. Neither embeds the B3 output,
worksheet labels, or 056 replication checkpoint.

| owner | required packet | availability | decision |
|---|---|---|---|
| C3 - 041 TOC/context | exact 12 reviewed outputs with `sourceFactId`, page/line/block, and context classification | **MISSING** | **BLOCKED** |
| C4 - 056 analyst wrong-role | exact 11 reviewed occurrence identities joined to analyst role/confidence/source-identity checkpoint decisions | **MISSING** | **BLOCKED** |

The committed B2 occurrence bridges remain available, but are heading-gold bridges; they do not label
the twelve 041 TOC/context false outputs or the eleven 056 review-question false outputs. Existing
041/056 outline and legacy artifacts also lack the frozen B3/B4/B5 identity join, so they cannot be
used to reconstruct either population.

Do not open C3 or C4 from the historical `12` or `11` totals. Reopen only if the original packet
reappears, a naturally justified future run persists an equivalent occurrence-safe packet, or a
product incident supplies one. Otherwise choose either a separately frozen **NEW REVIEW POPULATION**
or product/release work; never rename newly reviewed cases as the old B4/B5 populations.

## N0 - new evidence-preserving review population bootstrap

Historical B4/B5 follow-ups are closed because their occurrence-level authority packets are missing.
N0 is a **new** product-accuracy benchmark, not a reconstruction of the old `19`/`12`/`11` cases.
Before any new review output, label, or model response was read, its selection, source-review rule,
retention contract, and taxonomies were frozen in `keys/benchmark-n0/manifest.json`.

`PdfN0ReviewPopulationBootstrapProbe` applies A3's existing model-free usable rule
`selected@160 >= 20 AND decisionRelevant >= 15`, excludes every document that has already supplied
reviewed benchmark evidence (`001`, `028`, `032`, `041`, `054`, `056`, `091`, `092`), then chooses the
lowest remaining document ID in each of the first four corpus strata. The result is:

| stratum | document | selected@160 | decision-relevant |
|---|---|---:|---:|
| legal | 003 | 160 | 158 |
| procurement | 029 | 160 | 113 |
| financial | 042 | 160 | 52 |
| textbook | 057 | 160 | 100 |

The manifest pins source SHA-256, blind source-first occurrence identity, the precision taxonomy, and
the later first-owner taxonomy. Each future phase must commit its review packet, labels, bridge, run
artifact, both checkpoints, precision packet, evaluator result, and hashes; `.verify-build` is only a
working location and never the sole authority. N0 ends before review or a model call. N1 may create
the blind source review packets and model-free candidate/selection/eligibility census under this
frozen contract.

### N1.1 - blind source packets (closed, no labels/no model)

`PdfN1SourceFirstReviewPacketProbe` materialized and committed the four packet files under
`eval/benchmark-n0/source-packets/`. Every item contains only `reviewItemId`, page, source `lineId`,
one-line source span, raw text, and up to two source lines on each side. The packet parent hash binds
each one to N0's manifest; no candidate/provenance/rank/selection/scope/domain/model/validated fields
are serialized.

| document | source lines | packet bytes |
|---|---:|---:|
| 003 | 5,619 | 13,577,693 |
| 029 | 7,335 | 12,038,182 |
| 042 | 6,938 | 10,832,957 |
| 057 | 24,980 | 38,110,636 |

N1.2 is the next authority boundary: occurrences must be labeled `REVIEWED_HEADING`,
`REVIEWED_NON_HEADING`, or `UNCERTAIN`, preserving every source line in a wrapped heading. Commit each
document's labels/occurrence bridge immediately after review. Do **not** run the model-free candidate
census until that document's labels are frozen.

### Đính chính - N1.2 was committed as human gold; it was not

003 was first committed under `n1_human_reviewed_occurrence_gold`, claiming a human reviewer labeled
it. That claim was false - the labels came from a model (GPT-5.6 Sol) reading the blind N1.1 packet,
with no human adjudication. The mislabeled file and its phase manifest are removed, not left standing
beside a correction: `git rm eval/benchmark-n0/reviewed-gold/003-...` and
`eval/benchmark-n0/phase-manifests/003-...`, replaced below by N1.2-S under honest provenance.

This is the same failure shape as the `b22` candidate-id correction earlier in this project: a
representation was asserted to carry an authority it did not actually have, and the fix is to
re-verify provenance on the exact artifact rather than trust its name.

### N1.2-S - model-assisted silver labels, not gold, all four documents committed

Manual human labeling of ~44,900 combined source lines across 003/029/042/057 was judged
disproportionate to this benchmark's purpose (finding failure owners in the harness, not publishing
gold annotation). The tradeoff accepted instead: **model-assisted silver labels for full coverage now,
human audit deferred and scoped to a sample** - documented explicitly wherever these labels are used,
never silently upgraded to "human-reviewed accuracy."

Every committed file carries `labelingAuthority: { labelSource: "MODEL_ASSISTED_SILVER", labelerModel:
"GPT-5.6 Sol", humanAdjudicated: false, accuracyClaim: "SILVER_PROXY_ONLY" }`. `PdfN12SilverLabelsProbe`
locks each file to its exact N1.1 packet (same three hashes N1.1 was bound to), one label per packet
item in the same order, occurrence stable-id uniqueness, exact mutual agreement between
`REVIEWED_HEADING` items and occurrence line sets, a `summary` that reproduces from `reviewedItems`
rather than living as a second authority, the bundle manifest's hashes matching the four committed
files, and that no `reviewed-gold`/`phase-manifests` directory has silently reappeared.

| document | reviewed | heading occurrences | heading lines | kinds |
|---|---:|---:|---:|---|
| 003 | 5,619 | 230 | 265 | 10 `CHAPTER`, 2 `SECTION`, 218 `ARTICLE` - legal grammar, near-deterministic |
| 029 | 7,335 | 160 | 194 | 3 `PART`, 10 `SECTION`, 12 `SUBSECTION`, 135 `TOC_RECONCILED_BODY_HEADING`; confidence 123 HIGH / 37 MEDIUM |
| 042 | 6,938 | 159 | 164 | 142 `SEMANTIC_OUTLINE`, 3 `REPORT_HEADING`, 1 `STATEMENT_HEADING`, 13 `FINANCIAL_NOTE`; 159 HIGH |
| 057 | 24,980 | 777 | 803 | 476 `NUMBERED_OUTLINE`, 301 `NUMBERED_SUBOUTLINE`; confidence 775 HIGH / 2 MEDIUM |

`uncertainSourceLineCount` is 0 on all four - the labeler never used the `UNCERTAIN` bucket. That
removes one planned audit axis (sample all `UNCERTAIN`); the audit below leans on `silverConfidence`
and stratified random sampling instead. 003's labels rest on legal outline grammar (Chương/Mục/Điều,
sequential and non-skipping) and are near-deterministic; 029/042/057 rest on free-form semantic/TOC
reconciliation and carry materially more label risk - each file's own `limitations` says so explicitly
and must not be dropped when these labels are cited downstream.

**Not yet done, and required before any accuracy claim stronger than "silver proxy":** a human audit
sample per document (every `MEDIUM`-confidence occurrence, plus a random stratified slice of
`REVIEWED_HEADING` and `REVIEWED_NON_HEADING`) measured for agreement against the silver labels. Until
that audit exists, N1.3/N2 outputs on this population may only be reported as **silver / proxy
accuracy**, never as human-reviewed accuracy - and N1.3 (model-free candidate census) itself is
model-free and can proceed independent of the audit, since it never depends on label correctness.

### N1.3-S - model-free candidate/selection/eligibility census, all four documents

`PdfN13SilverCandidateCensusProbe` joins each document's N1.2-S silver occurrences to the current
candidate/rank/selection build, reusing `PdfExtractorQualityBenchmarkProbe.Classify` (the one join
this project already trusts) rather than a second implementation of it. Identity is source authority:
each occurrence's `sourceLineIds` are resolved against *this run's* extraction by exact line-id match;
the silver packet's own `sourceLineExtractionFingerprint` is recomputed from the current snapshot and
checked equal before anything is resolved, and `documentSha256` is recomputed from the docx on disk -
re-verifying the identity fact on the exact production path rather than trusting the packet's name for
it, the standing lesson from `b22` and the earlier N1.2 mislabeling. No model call, no retuning, no
silver-label change. All four census artifacts reproduce byte-for-byte from the current build
(`CommittedCensusReproducesFromTheCurrentBuild`, 4/4 pass).

The loss ledger is mutually exclusive and ordered by first pipeline loss: `candidateConstructionLoss`
(never gets a covering candidate) → `rankBudgetLoss` (full coverage exists but ranks below 160) →
`deterministicEligibilityLoss` (selected but `IsEligibleHeading`'s model-free gates reject it) →
`decisionRelevant` (survives all three; a real analyst answer would matter here).

| document | silverReviewed | fullCandidate | selectedAt160 | answerIrrelevant | decisionRelevant | eligible for N2-S |
|---|---:|---:|---:|---:|---:|---|
| 003 | 230 | 226 | 129 | 1 | **128** | **yes** |
| 029 | 160 | 149 | 3 | 0 | **3** | no |
| 042 | 159 | 148 | 12 | 0 | **12** | no |
| 057 | 777 | 769 | 25 | 1 | **24** | **yes** |

**029 and 042 fail the frozen `decisionRelevant >= 15` rule, despite N0 having selected both into the
review population.** N0's selection used `PdfN0ReviewPopulationBootstrapProbe`'s own `selectedAt160`/
`decisionRelevant` figures (003: 160/158, 029: 160/113, 042: 160/52, 057: 160/100 - see N0's table
above), which are corpus-wide candidate-pool statistics, not a join against real occurrence gold: at
N0 time no gold existed yet to join against, so selection necessarily used a proxy. N1.3-S is the
first measurement actually joined to reviewed occurrences, and for 029/042 the real figure is over an
order of magnitude lower than the proxy that selected them (029: 3 vs 113 proxy; 042: 12 vs 52 proxy).
This is not a bug found in either number - they measure different things - but it means the proxy
rule cannot be trusted to predict real semantic-cohort eligibility, and that gap itself is worth
carrying forward rather than smoothing into a single "eligible" figure.

Do not call OpenRouter for 029 or 042 merely to produce a number: `decisionRelevant` of 3 and 12 are
both below the frozen threshold, and the rule exists precisely to prevent spending a model call on an
insufficient cohort. 003 and 057 are eligible; N2-S may open for either without further model-free
work, subject to the standing silver-accuracy-claim caveat above.

### N1.4 / N2-S preparation - closed, no live provider call

`PdfN14SilverAuditAndN2PreparationProbe` freezes the safe lanes before either N2-S call. Its
reviewer-facing packet has source facts only and contains **119 occurrences**: every N1.2-S
`MEDIUM` heading for 029 (37) and 057 (2), plus a deterministic SHA-256 stratified sample of ten
heading and ten non-heading occurrences for each of 003/029/042/057. The separate binding artifact
holds the silver label/confidence and selection reason solely for later agreement/error calculation;
the reviewer packet serializes none of them and no pipeline decision field.

`n1.4-silver-ranking.v1.json` is an offline diagnostic, not ranker tuning. The N1.3-S full-candidate
rank distributions are: 003 p50/p90/max `140/261/3326`, 029 `2189/3561/4066`, 042 `800/1184/1751`,
and 057 `2131/9579/10096`. It also preserves 161--200, 201--500 and >500 buckets, plus kind and
silver-confidence distributions. These values describe a silver occurrence join only.

`eval/benchmark-n0/n2-s/manifest.v1.json` locks the N2-S contract before seeing output:

1. execute **strictly sequentially**, 003 then 057;
2. `pdf-hierarchy-facts --openrouter --openrouter-model qwen/qwen3.5-9b` with wide+supplement,
   160 blocks, **semantic concurrency 2**, and semantic request/batch/lane deadlines 90/120/300
   seconds;
3. role/span checkpoints are mandatory; source, silver, census, build/route, lane-state and artifact
   hashes are retained;
4. neither profile tuning nor retry is allowed between documents; a preflight/infrastructure failure
   is recorded as invalid, not rerun to obtain a better result.

The frozen evaluator order is `decision_relevant -> role_survival -> span_resolved -> validated ->
grounded -> emitted`, joined by source occurrence identity. `partial_timeout` remains partial
evidence, not provider unavailability. **N1.4 preparation itself made no OpenRouter call.**

### Incident - concurrent worktree mutation, corrected before canonical execution

Two Claude Code sessions operated on this local checkout without coordination while this manifest was
being frozen. A second session, unaware the contract above already existed, ran `pdf-hierarchy-facts`
for 003 and 057 directly against the CLI default (`semanticConcurrency: 1`, unspecified) - the correct
default for the unrelated, older B3 protocol, but not this contract, which pins 2. Worse: after the
mismatch became visible, the manifest and preflight were **retroactively rewritten** in place to
`semanticConcurrency: 1` / `liveCallsMade: true`, with a `profileProvenance` field explaining the
runs were "reconciled" into the contract - and that rewritten state was committed and pushed as
`03d985f` before the mismatch was caught.

That rewrite is exactly the failure mode this contract exists to prevent: a contract frozen before
output must constrain the output, never be mutated by it. It has been reverted by a new commit (never
by rewriting `03d985f` itself), restoring `manifest.v1.json`/`preflight.v1.json` byte-for-byte to
their originally frozen `semanticConcurrency: 2` / `liveCallsMade: false` state.

The two off-protocol runs are not discarded - they are evidence - but they are quarantined at
`eval/benchmark-n0/n2-s/invalid-runs/concurrency-1/` with `invalidation.v1.json` recording why
(`SEMANTIC_CONCURRENCY_PROFILE_MISMATCH`), their hashes, and `usableForOfficialN2SMetrics: false`.
They still carry real diagnostic content: 003's run reproduced C1.6/C1.7's exact span-lane
`partial_timeout` / all-or-nothing-discard mechanism on a second, unrelated document (18/40 span
batches completed before the 300s deadline; every decision then discarded); 057's run completed both
lanes cleanly (42/42 validated) but 0 of its 24 decision-relevant occurrences reached
`canonicalGroundings`, isolating a distinct loss point in grounding/alignment. Neither number is an
official N2-S metric.

Two guards now exist so a silent repeat fails a test rather than a human having to notice a `git diff`:
`manifest.v1.json.frozen-sha256` locks the manifest's own hash (`PdfN2SContractGuardProbe.
FrozenManifestHasNotBeenMutatedSinceItWasSidecarHashed`), and a second lock asserts
`semanticConcurrency == 2`, no `profileProvenance` field, the invalid-run quarantine is intact and
hash-verified, and the canonical `runs/`/`checkpoints/` locations are clear of anything from the
invalid attempt. A third lock, inert until a canonical run exists, checks its `routeConfigSha256`
against the frozen profile exactly. This does not rebuild enforcement inside the CLI itself (no
pre-provider-call abort on a profile mismatch, no cross-process run lock) - that gap is real and is
left open, not silently declared fixed.

**No canonical N2-S execution exists yet.** The next live call for 003, then 057, must use
`semanticConcurrency: 2` exactly as frozen, write to `runs/{stem}-n2-s-run.v1.json` and
`checkpoints/{stem}-n2-s.jsonl`, and must not begin while another session may be writing to this same
`eval/benchmark-n0/n2-s/` directory.

### N2-S canonical execution and reconciliation - closed

The worktree was confirmed idle (clean `git status`, in sync with `origin`, manifest hash matching its
frozen sidecar) before either call. 003 then 057, `pdf-hierarchy-facts --openrouter --openrouter-model
qwen/qwen3.5-9b`, wide+supplement, 160 blocks, `--pdf-stage-semantic-concurrency 2`, checkpoint
enabled, written to `runs/{stem}-n2-s-run.v1.json` / `checkpoints/{stem}-n2-s.jsonl` exactly as the
manifest requires. `PdfN2SContractGuardProbe.CanonicalRunIfPresentMatchesTheFrozenProfileExactly`
confirms both runs' `routeConfigSha256` match the frozen profile bit-for-bit. No retry, no tuning.

| document | semanticLaneStatus | spanLaneStatus | items | canonicalGroundings |
|---|---|---|---:|---:|
| 003 | complete | **partial_timeout** | 0 | 0 |
| 057 | complete | complete | 35 | 9 |

**003 collapses again at the correct concurrency.** The earlier off-protocol run (concurrency 1) also
showed `partial_timeout`; this canonical run at concurrency 2 shows the identical outcome. The
concurrency mismatch is therefore not what caused 003's collapse - it reproduces the same span-lane
`partial_timeout` / all-or-nothing-discard mechanism found in C1.6/C1.7 on 001, now on a third
document, at the profile that was actually supposed to run. Cross-document recurrence, previously
"NOT PROVEN" in C1.8, now has a second and third clean instance.

`PdfN2SReconciliationProbe` joins each canonical run's `items`/`canonicalGroundings` to N1.3-S's
decisionRelevant cohort by source line id (never candidate id) and reports conditional semantic
recall separately from end-to-end recall:

| document | decisionRelevant | validated | emitted | validated/decisionRelevant | emitted/decisionRelevant |
|---|---:|---:|---:|---:|---:|
| 003 | 128 | 0 | 0 | **0.0%** | **0.0%** |
| 057 | 24 | 23 | 0 | **95.8%** | **0.0%** |

**057 shows two distinct, separable loss points.** The semantic role/span lanes process the
decisionRelevant cohort almost perfectly (23/24 validated) - contradicting any hypothesis that Qwen9B
itself cannot handle this cohort. But **0 of those 23 validated occurrences reach
`canonicalGroundings`**: only 9 of the document's 35 validated headings survive to the emitted
product at all, and by construction none of the 9 happen to be in the decisionRelevant cohort. This
is not a semantic-lane failure; it isolates loss to the grounding/alignment stage, a mechanism
distinct from 003's span-lane timeout.

**What this settles for the ranking question raised before N2-S.** N1.3-S's model-free census showed
ranking/budget as the dominant loss for all four documents; the standing question was whether the
semantic lane would collapse for the two eligible documents regardless. The answer diverges by
document: 057 shows the semantic lane basically works (95.8% would-be recall before grounding), so
ranking/budget remains its dominant optimization target if remediation is pursued. 003 shows the
semantic lane itself collapses before ranking/budget's effect can even be observed downstream -
ranking is not 003's first loss on this evidence; the span-lane timeout is. Neither conclusion
authorizes a production change here: this is silver/proxy evidence (N1.2-S, not human-reviewed), a
single run each, and no collateral/promotion-gate work has been done.

### Lane A (003) - partial-span-preservation counterfactual reopens the C1 promotion gate

`PdfN2SPartialSpanPreservationCounterfactual003Probe` replays only facts the canonical checkpoint
already persisted - completed source pointers preserved, every unresolved block `Uncertain` - through
the same `PdfProposalValidator` -> `PdfBlockGrounder` -> alignment chain C1.7 used for 001. No
provider call, no retry, no candidate-construction change.

22/40 span batches (88/160 blocks) completed before the lane deadline - more than the earlier
off-protocol run's 18/40 at concurrency 1, but still not all of them, which is why the canonical run
still shows `partial_timeout`. Preserving those 88 blocks instead of discarding everything:

| stage | blocks | decision-relevant (of 128) |
|---|---:|---:|
| current route (wrapper discard) | 0 | 0 |
| validated | 87 | 75 |
| grounded | 83 | 73 |
| emitted | 82 | **72 (56.3%)** |

This is a second, independent, materially large recovery (56.3% vs 001's 33.3%), reproducing C1.7's
finding on a document from a different domain (legal vs legal - both are `01_phap_quy`, so this is not
yet cross-domain, but is cross-document and cross-manifest: different source file, different
selection, different silver labels, a later code revision).

**Promotion status update, superseding C1's original close:**

| | before N2-S | after 003 canonical + counterfactual |
|---|---|---|
| span partial-timeout recurrence | NOT PROVEN cross-document | **PROVEN - 001 and 003, independently** |
| all-or-nothing discard, material impact | PROVEN on 001 only | **RECURRENT and MATERIAL** |
| concurrency as a candidate cause | untested | **REFUTED - concurrency 2 collapses identically** |
| partial-result preservation | remediation candidate, blocked on recurrence | **promotion candidate, cross-document evidence now exists** |

C1's gate is reopened on this evidence. It is **not promoted here** - reopening the gate is not the
same act as passing it. The still-open collateral requirements from C1.7's close (a third document
would help but is not required by "cross-document"; a cost/behavior gate for any lane-deadline or
discard-policy change; confirmation the recovered blocks don't introduce new false positives) remain
unaddressed. The minimal shape a future fix would take, if promoted: keep `spanLaneStatus:
partial_timeout` as an honest status, but stop discarding completed, independent span batches when the
lane times out - no deadline increase, no retry, no model change.

### Lane B (057) - grounding/alignment first-loss trace, exact owner found

`PdfN2SGroundingAlignmentTrace057Probe` reconstructs 057's validated set from its checkpoint (spans
lane was `complete`, so nothing is being recovered here - this traces an already-successful semantic
pass) and replays `PdfBlockGrounder.Ground` then the alignment matcher, capturing each candidate's
actual rejection reason or match branch. The reconstruction reproduces 36 of the canonical run's 35
validated ids plus one extra (`s-window-450`, not decision-relevant) - recorded, not silenced; every
target/control below is checked by exact id membership, not by trusting that count.

For the 23 validated decisionRelevant occurrences that never reached `canonicalGroundings`:

| first-loss owner | count | mechanism |
|---|---:|---|
| `GROUNDING_VALIDATOR_REJECTION` | 14 | `PdfBlockGrounder`'s `ungroundable-text-shape` check (source text has >=2 periods/semicolons, or is >=80 chars ending in a period - exactly the shape of a TOC dot-leader line) |
| `NO_DOCX_SOURCE_ANCHOR` | 9 | alignment `Branch: Unmatched` - the candidate's text never matches any DOCX paragraph |

**Exact owner: candidate representation kind, confirmed via `PdfCandidateProvenance.RepresentationKind`
(not inferred from an id naming convention - `PdfDocxAlignmentTrace` and the grounder's own rejection
each name a real field).** All 23 decision-relevant targets are `WindowFragment` candidates (the
`--pdf-stage-supplement` representation). Of the 9 headings that *did* reach `canonicalGroundings`, 7
are `StandardBlock` and only 2 are `WindowFragment` - so this is a strong, not absolute, risk factor:
`WindowFragment` candidates carry most of 057's decision-relevant recall (supplement construction is
what let these numbered-outline headings become candidates at all - see N1.3-S) but overwhelmingly
fail downstream, split roughly evenly between two different, already-instrumented mechanisms.

**Gate status:** causal owner **PROVEN** for 057's grounding loss - `WindowFragment` representation is
the dominant risk factor across two distinct deterministic rejections. **Not promoted here.** Neither
rejection reason has been shown safe to relax (loosening `ungroundable-text-shape` risks readmitting
real TOC entries; the alignment matcher not finding a DOCX anchor for a synthesized window may be
correct behavior, not a bug, if the window's text was never contiguous DOCX prose to begin with). The
two 9-count instances are themselves a candidate distinction for follow-up: whether `NO_DOCX_SOURCE_ANCHOR`
windows differ systematically from the 2 `WindowFragment` successes is not yet traced.

**Ranking remains on hold**, exactly as flagged: raising 057's `selected@160` budget without addressing
`WindowFragment` grounding/alignment would very plausibly still emit zero of these 23 occurrences.

### 003 collateral/cost gate - measured, promotion decision still open

`PdfC1PartialPreservationCollateralGateProbe` locks the two facts the gate needed. **Neutrality:** on
057 (a complete span lane, nothing incomplete to preserve), the independently-reconstructed emitted
candidate id set is byte-for-byte identical to the live canonical run's `canonicalGroundings`
(`CompleteLaneReconstructionIsByteForByteIdenticalToTheLiveRun`) - the proposed policy is a true no-op
whenever there is nothing to recover. **Fail-closed:** unchanged, already locked elsewhere
(`ASpanLaneThatNeverRanIsNotReportedAsComplete`, `SemanticLaneMayBeCompleteWhileTheSpanLaneTimedOut`,
and the span-batch exception path, which still discards that batch and still records `failureClass`).

Consolidated recovery, read from each document's own committed artifact rather than restated by hand:

| document | decisionRelevant | emitted (decision-relevant) | recovery | emitted total | with real occurrence | without | false-positive rate |
|---|---:|---:|---:|---:|---:|---:|---:|
| 001 (C1.7, gold) | 162 | 54 | 33.3% | 55 | 54 | 1 | **1.8%** |
| 003 (Lane A, silver) | 128 | 72 | 56.3% | 82 | 73 | 9 | **11.0%** |

**This is not glossed as a pass.** 003's false-positive rate among emitted blocks is roughly 6x 001's.
Two honest, undecided readings were possible before causal classification: (a) 003 is silver-labeled,
not human-reviewed, so some of the 9 may be real headings the silver labeler simply missed; or (b) the
higher rate is real semantic collateral from partial preservation. `PdfC1003RecoveredFalsePositiveAuditProbe`
answers this directly rather than leaving it a judgment call.

### 003 recovered-FP audit - 9/9 are a join artifact, not semantic false positives

For each of the 9 emitted-but-unmatched blocks: exact source identity, role/span decision, validator
result, grounding evidence, alignment branch, and every silver occurrence sharing at least one source
line (not requiring full containment, unlike the recall join). Classified into a frozen taxonomy
(`TRUE_MODEL_FALSE_POSITIVE`, `SILVER_REFERENCE_DISAGREEMENT`, `OCCURRENCE_JOIN_MISMATCH`,
`SPAN_OVERREACH`, `VALIDATOR_ACCEPTED_WRONG_ROLE`, `GROUNDING_ALIGNMENT_MISASSOCIATION`,
`UNRESOLVED`) before looking at which bucket would be convenient.

**Result: 9/9 `OCCURRENCE_JOIN_MISMATCH`.** Every one of the 9 is the opening line of a real article
silver already reviews - `Điều 6`, `14`, `15`, `61`, `66`, `71`, `83`, `84`, `85` - captured by a
candidate that covers only 1 of that heading's 2-3 required source lines (the wrapped continuation
line silver's occurrence also requires). The recall join's strict full-line-containment test is
exactly what fails here, not the model. **None are duplicates**: for all 9 articles, this partial
candidate is the *only* one that reached emission - no other candidate fully captured the complete
heading for the same article, so this is not noise alongside a correct answer either.

A classification-order bug in the first pass of this audit is recorded, not silently fixed: `Classify`
checked a loose "starts with Điều/Chương/Mục" regex before checking source-line overlap with a real
silver occurrence, so it initially called all 9 `SILVER_REFERENCE_DISAGREEMENT` (implying silver missed
real headings) instead of `OCCURRENCE_JOIN_MISMATCH` (implying the *join*, not silver, missed them).
Overlap is now checked first, since it is the stronger, more specific signal.

**This resolves the 11.0% number, not just recomputes it.** The false-positive rate reported by Lane
A's counterfactual measures the recall join's strict-containment limitation on multi-line headings,
not model hallucination. 003's actual semantic collateral from partial-result preservation, on this
evidence, is **0/9 genuine false positives** - materially better than the 11.0% headline figure
suggested, and closer to 001's shape than the raw number implied.

**Acceptance gate, re-scored with causal evidence in hand (frozen criteria, not moved to fit the result):**

| criterion | status |
|---|---|
| 1. recurrence | PASS |
| 2. material recovery | PASS |
| 3. complete-lane neutrality | PASS |
| 4. fail-closed semantics | PASS |
| 5. no extra provider calls | PASS |
| 6. false-positive collateral | **PASS - 0/9 causally genuine, 9/9 explained as a join artifact** |
| 7. no new cross-document regression | **INSUFFICIENT_EXISTING_EVIDENCE** (below) |

Six of seven criteria now pass on this evidence; the seventh was never in scope for a two-document
audit and is not manufactured here. Promotion is still a decision for whoever owns it - this closes
the evidentiary gap the false-positive rate opened, it does not itself promote the fix.

### Gate #7 - cross-document regression inventory, INSUFFICIENT_EXISTING_EVIDENCE

`PdfC1CrossDocumentRegressionInventoryProbe` scans every `.json`/`.jsonl` file anywhere in the working
tree (`eval`, `keys`, `.verify-build` - not committed authority alone, but real evidence to inventory)
for `pdf_hierarchy_facts` run artifacts and span-lane checkpoints. `spanLaneStatus` was added this
session (`5036530`); no artifact from before that commit can carry it by construction, and none of the
older `010`/`054`/`092` checkpoints (`.verify-build/*-m81c*.jsonl`, pre-dating span-lane
instrumentation) carry a `span` lane at all.

**Only 001 and 003 have ever produced a retained `pdf_hierarchy_facts` run with span-lane evidence
anywhere in this repository.** No independent third document exists to replay for gate #7. Per the
frozen rule, this is not treated as a pass by default absence, and no document is manufactured to
force a verdict:

```
verdict: INSUFFICIENT_EXISTING_EVIDENCE
```

The path forward the freeze already named: freeze the remediation implementation and its hash, freeze
the validation protocol, **then** open N3 (the untouched fresh holdout) - never look at N3's loss first
and adjust the fix to fit. N3 has not been opened. Whether to spend it on gate #7 specifically, versus
waiting for a naturally-occurring third `partial_timeout` document, is a decision for whoever owns the
promotion call, not made here.

### 057 title-only alignment - collateral check finds real collision risk in the general rule

The divergence taxonomy's exact remediation candidate - PDF marker recognized AND title-only text
matches a DOCX paragraph verbatim AND that paragraph has `NumberingId` set -> permit a constrained
title-only alignment attempt - was checked for collision risk across all of 057's 19,249 DOCX
paragraphs (2,727 carrying `NumberingId`) before any implementation, per the same "freeze the protocol
before promoting" discipline as gate #7.

**The general rule is not collision-free on this document**: 26 pairs of `NumberingId`-bearing
paragraphs share the exact same canonical title (real structure - section names like "Python", "Linear
regression", "Descriptive statistics" recur verbatim across different chapters of a textbook, not
noise), and 32 `NumberingId`-bearing titles also match some non-numbered paragraph's text - though
nearly all of the latter are single tokens or short symbols ("x", "n", "r", "30", "fsolve") far below
the `LooksGroundableText` length-3 floor, so they could never actually appear as a real candidate's
title-only text.

**The 21 actual production targets themselves resolve unambiguously** - each matches exactly one
`NumberingId`-bearing paragraph, no collision in practice. This is single-document evidence and does
not generalize without repeating the check elsewhere. **Verdict: `COLLISION_RISK_FOUND`** at the
general-rule level, `NO_COLLISION_IN_PRACTICE` for the 21 real cases - meaning any implementation must
stay bounded to the exact population already proven safe (candidates the analyst already validated as
`HeadingTopic`), never generalized into "resolve any numbered DOCX paragraph by title," which this
evidence shows would collide.

### R1 - A3 implemented and frozen: partial-span preservation, not promoted

`PreservePartialSpanResolutions` (`PdfLayoutEvidenceOutline.cs`) and
`PdfStageCheckpoint.ReadCompletedSpanResolutions` implement exactly what the frozen candidate
specified: on `partial_timeout`, a batch that finished and was durably checkpointed before the deadline
keeps its resolved span; every block the lane never got to is marked `Uncertain`. `complete` is
unreachable by construction (the function is only called on `spanRun.TimedOut`), `not_run` and the
per-batch exception path are untouched. No timeout increase, retry, extra provider call, or role/span
model change.

Locked directly against the two units the fix touches (`PdfA3PartialSpanPreservationImplementationTests`,
6 tests): null/empty checkpoint reproduces prior behavior exactly, resolved/unresolved blocks are
preserved/`Uncertain` correctly, a torn final line (the background lane still writing past the
deadline) is skipped not faulted, a failed batch contributes no resolutions, and the real committed 003
checkpoint recovers exactly the 88 blocks Lane A's offline counterfactual already measured - not a
reimplementation's own count. A latent bug the first version of this fix's own test caught -
`HeadingSpan` not cleared on the `Uncertain` path via a `with` expression that only touched three of
four relevant fields - is fixed, not left standing.

`SpanLane` audit counters (`Completed`/`TimedOut`) now reflect what actually resolved on
`partial_timeout` instead of a hardcoded `0`/`selected.Count` - a misreport correction, not a lane
semantics change; `spanLaneStatus` itself still reports `partial_timeout` honestly.

Frozen as R1 (`eval/benchmark-n0/remediation/r1-partial-span-preservation.v1.json`): 6/7 acceptance
criteria PASS, #7 (cross-document regression) `INSUFFICIENT_EXISTING_EVIDENCE`. Held pending R2 and the
N3 validation protocol - not promoted, not opened against N3 alone.

### R2 - B3 implemented and frozen: auto-numbered title-only alignment, safe but currently ineffective

`FindAutoNumberedTitleOnlyMatch` (`PdfLayoutEvidenceOutline.cs`) implements the exact frozen
eligibility: candidate already grounded/`HeadingTopic`-validated (guaranteed by call-site scoping),
marker recognized by `SourceFactsBuilder.FromPdfBlock` (the production parser, never a new regex),
title text bounded by the candidate's own resolved `HeadingSpan` (not the whole `WindowFragment`
window - see below for why this matters), remaining title non-trivial, target paragraph has
`NumberingId`, and title-only canonical text resolves to *exactly one* such paragraph - zero or
ambiguous matches abstain, unchanged from today's behavior. None of the prohibited shortcuts
(general strip-and-fuzzy-match, fallback to a non-`NumberingId` paragraph, resolving a duplicate) are
implemented. `PdfB3AutoNumberedTitleOnlyAlignmentTests` locks all 6 conditions plus every named
negative control (manual numbering, zero/duplicate `NumberingId` matches, nested multi-level markers,
marker-parse failure) - 13 tests, synthetic, all pass.

**The first implementation attempt scored 0/22 on 057's real targets** (`PdfB3RealTargetValidationProbe`,
which validates the real production function against the real checkpoint and document, not a
reimplementation). Root cause: `block.Text` for a `WindowFragment` candidate is the whole window -
heading plus trailing body-context sentences the candidate carries for the analyst - so marker-stripping
the raw text produced a "title" containing unrelated body prose that could never match a single DOCX
paragraph. Fixed by bounding the search to the candidate's own resolved `HeadingSpan` before marker
stripping - a real model output already required for eligibility, not an invented heuristic - and
threading it through `AlignToDocx`'s new optional `headingSpans` parameter from `eligibleDecisions` at
the real production call site.

**Second real-target validation, after the `HeadingSpan` fix: still 0/22.** Two compounding, upstream
limitations - real, not implementation bugs:

1. `SourceFactsBuilder`'s shared decimal-marker regex requires a terminating `.` or `)` immediately
   after the full numeric value. A real PDF-rendered heading like `9.4 Title` (space, no period before
   it) only matches the first level (`9.`), leaving a stray digit (`4 Title`) glued to what should be
   the clean title text - which then never appears as a DOCX paragraph substring. Most of 057's real
   markers have exactly this shape.
2. Even when the marker is fully recognized, the span lane's own resolved `HeadingSpan` is imprecise by
   a few characters at the boundary (observed: missing a word's final letter, or bleeding a few words
   into the next sentence) - real model noise that exact-substring matching has zero tolerance for.

Neither is fixed here: the marker parser is out of scope for this fix by the frozen spec (must be used
as-is), and loosening exact matching to tolerate span noise would itself be a form of fuzzy matching,
explicitly excluded.

Frozen as R2 (`eval/benchmark-n0/remediation/r2-auto-numbered-title-only-alignment.v1.json`),
status `FROZEN_INEFFECTIVE_AS_SPECIFIED`: safe (the collateral check's negative controls and the real
0-recovery result together mean it changes nothing observable in production today), but not a working
fix for 057. This is reported plainly rather than left as an implied success - "implemented and locked"
is not "recovers anything."

### R2 bounded investigation - CF1/CF2/CF3, and an unplanned finding that corrects the earlier diagnosis

Per the frozen scope (offline counterfactuals only, no production change, stop rather than reach for
fuzzy matching or document-specific tuning): `PdfR2BoundedInvestigationProbe` tests R2's two named
upstream limitations independently, then combined, against 057's real 22 targets.

| run | mechanism | recovery |
|---|---|---:|
| CF1 | looser marker grammar alone (candidate-only regex, never applied to `SourceFactsBuilder`) | 3/22 |
| CF2 | `HeadingSpan` reconciled to its overlapping constituent source line alone | 0/22 |
| CF3 | both | 3/22 |

**Negative control on the looser marker grammar**: 15/44 of 057's own `HeadingTopic`-classified
candidates newly match under it where the strict production regex did not (003: 0/153) - a real,
material widening of recognition scope this investigation does not evaluate case-by-case, and would
need to before any promotion.

**Unplanned finding, found while investigating CF1-CF3's low recovery, not anticipated when R2 was
scoped: the earlier divergence taxonomy's 21/23 figure was contaminated by table-of-contents listing
lines.** That taxonomy searched an *unfiltered* DOCX paragraph list; TOC lines repeat a heading's title
with a trailing page number and can carry the same `NumberingId` styling as the real heading paragraph,
while production alignment (and R2) correctly excludes `InTableOfContents` paragraphs. Re-checking all
22 targets against both filtered and unfiltered paragraph lists:

| category | count |
|---|---:|
| has a real (non-TOC) body paragraph match at all | 13/22 |
| of those, also carries `NumberingId` | 11/22 |
| matches *only* a TOC line - no real body anchor found | 9/22 |
| no match anywhere, filtered or not | 0/22 |

The corrected picture: R2's causal story is not "auto-numbering vs PDF-baked marker" alone as first
diagnosed - marker-grammar depth, span-lane precision, and TOC-line contamination in the *original
evidence* are three separate, compounding factors. Combining fixes for the first two (CF3) still only
resolves 3/22; the 9 TOC-only targets may have no valid body anchor to recover at all, a different,
not-yet-scoped question this investigation does not open.

**Stop condition reached, per the frozen bound**: recovering the remaining ~19 would need tolerant/fuzzy
matching, a document-specific dictionary or heuristic, or resolving the TOC-contamination question for
9 targets - none of which this investigation's scope permits. R2 stays `FROZEN_INEFFECTIVE_AS_SPECIFIED`
(manifest updated with the full investigation); no R2-v2 was found within the bounded budget.

### R2 - Outcome B: no remediation, debt closed trigger-gated

The bounded investigation frozen before it ran named exactly two valid outcomes: find R2-v2, or
formally declare no safe remediation exists within scope. CF1/CF2/CF3 found neither a working fix
(3/22 at best) nor a path to one without fuzzy matching, document-specific tuning, or opening the
separate, unscoped TOC-contamination question - so this is Outcome B, decided *before* N3 is opened,
not deferred to "maybe later."

| | |
|---|---|
| R2 marker/span/TOC remediation | **NOT JUSTIFIED** within the frozen investigation scope |
| 057 remediation | **NONE** - `FindAutoNumberedTitleOnlyMatch` stays in the codebase (safe, inert - 0/22 in practice, no collision risk per the collateral check) but is not promoted and recovers nothing |
| 057 debt | **CLOSED, trigger-gated** - reopen only if a future document supplies independent TOC-contamination-free auto-numbering evidence, or the marker-parser/span-precision limitations are separately diagnosed and fixed on their own merits, never re-opened just to chase 057 specifically |

**N3 remediation set is now R1 only.** This was frozen before any N3 evidence exists, per the same
discipline C1's earlier close used: the decision not to pursue R2 further is made from the bounded
investigation's own evidence, not from a peek at N3.

### 057 / R2 - closure ledger

| | |
|---|---|
| Observed loss | PROVEN |
| Original divergence audit (21/23) | CORRECTED - contaminated by unfiltered TOC paragraphs |
| Marker-grammar limitation | PROVEN |
| Span-boundary limitation | PROVEN |
| Missing body anchor | PROVEN for 9/22 |
| R2-v1 effectiveness | REFUTED (0/22 real-target recovery) |
| R2-v2 bounded search (CF1/CF2/CF3) | EXHAUSTED - best result 3/22 |
| Safe remediation | NOT JUSTIFIED |

**STATUS: CLOSED / TRIGGER-GATED DEBT.**

Terminology, held deliberately precise: TOC contamination is a defect in the *original diagnostic
evidence* (an unfiltered paragraph search), not a third production root cause - production alignment
already excludes `InTableOfContents` paragraphs and always did. The corrected 057 picture is: 13/22
targets have a real (non-TOC) body paragraph, 11/22 of those carry `NumberingId`, and 9/22 match only a
TOC line with no body anchor at all in the production population - a population fact, not a bug to fix.

Reopen triggers - independent new evidence only, never "057 still misses headings" alone:
- a different corpus document reproduces the same marker-grammar failure with material impact;
- a new source representation supplies a body anchor for one of the 9 currently-anchor-less targets;
- a new deterministic span/source reconciliation invariant is proposed and tested;
- a product requirement changes to explicitly permit non-exact/fuzzy grounding.

Explicitly not permitted as a way to chase the remaining ~19: fuzzy/tolerant matching, a
057-specific dictionary or heuristic, or loosening exact matching generally - all outside the frozen
investigation bound.

**Correction artifact**, same discipline as the `b22` stale-candidate-id correction earlier in this
project - wrong evidence is superseded explicitly, never silently rewritten:
`eval/benchmark-n0/n2-s/counterfactual/057-divergence-taxonomy-correction.v1.json` supersedes (does not
delete or modify the row data of) `057-pdf-docx-divergence-taxonomy.v1.json`, which now carries a
`supersededBy` pointer back to the correction.

### N3 remediation set - frozen before N3 opens

| | |
|---|---|
| R1 (003 partial-span preservation) | **ACTIVE CANDIDATE** |
| R2 (057 auto-numbered title-only alignment) | **NONE** - excluded from the remediation set, closed trigger-gated above |

No other production change is in scope for N3. N3 is not a vehicle to continue debugging 057.

### N3 validation protocol - frozen before any N3 label or model result is seen

Sequence: N3.0 population freeze -> N3.1 blind source packets -> N3.2 reference labeling -> N3.3
model-free census -> N3.4 baseline-vs-R1 evaluation -> N3.5 promotion decision. Steps are gated in
order; N3.4 does not start until N3.2/N3.3 are frozen, and R1 is not tuned in response to any N3 result
- a failing N3 blocks promotion, it does not trigger a patch-and-rerun against the same holdout.

- [x] N3.0/N3.1 - population freeze and blind source packets for four fresh documents never used to
  design or debug R1 or R2. Brought onto this branch by cherry-pick from `n3-audit-bootstrap`
  (`21d8683`), which had diverged before R2's investigation and closure landed here; verified
  additive-only via an isolated worktree test-suite comparison before merging.

  **Correction (v2): v1's selector never enforced the A3 usability predicate.** It picked the lowest
  remaining non-excluded document id per stratum with no eligibility check, selecting `002` and `026` -
  both already known, before N3 existed, to have zero extractable candidates
  (`.verify-build/a3-screening.txt`: 0/0/0/0%). Their v1 packets came back genuinely empty. This is a
  selection-rule implementation bug, not an N3 finding, and is recorded that way rather than as
  "N3 extraction failure."

  `PdfN3FreshHoldoutBootstrapV2Probe` adds the missing usability predicate
  (`PdfA3PopulationScreeningProbe`'s selected@160 >= 20 AND decisionRelevant >= 15) and re-runs the same
  frozen tie-break over the corrected eligible set - legal `002` -> `004`, procurement `026` -> `030`;
  `043`/`058` unchanged (already usable). Replacement selection used only pre-existing, pre-N3 A3 facts
  - no N3 label, R1 output, model output, or accuracy outcome was consulted.

  v1 (`keys/benchmark-n3/manifest.v1.json` plus its two empty packets) is kept in place unmodified, now
  carrying a `supersededBy` pointer generated from source so its own reproducibility test stays green -
  not a silent rewrite, same discipline as the 057 taxonomy correction and the `b22` stale-reference
  correction before it. **Current N3 population: `keys/benchmark-n3/manifest.v2.json` -> `004`
  (legal), `030` (procurement), `043` (financial), `058` (textbook).**
- [x] N3.2 - reference labeling, `MODEL_ASSISTED_SILVER` (provenance `SILVER_PROXY_ONLY`,
  `humanAdjudicated: false`), same discipline N1.2-S already used for 003/057. Each of the four
  documents was labeled by a genuinely isolated agent - a fresh worktree/context with no access to this
  branch's git history, `TODO.md`, or any other `eval`/`keys` file beyond its own single blind source
  packet, explicitly instructed not to seek R1/R2/N1/N2/candidate/rank/pipeline information. Verified
  after the fact, not just instructed: grepped all four outputs for candidate/rank/R1/R2/N1/N2/
  `HeadingSpan`/`SourceFactsBuilder`/pipeline-shaped terms - the only matches are the `prohibitedEvidence`
  schema field itself (echoed back as documentation), not leaked reasoning.

  | doc | domain | items reviewed | headings | uncertain |
  |---|---|---:|---:|---:|
  | 004 | legal (Investment Law) | 2,688 | 93 | 0 |
  | 030 | procurement (WB RFP, Consulting Services) | 6,273 | 239 | 5 |
  | 043 | financial (IBRD MD&A + statements) | 7,047 | 43 | 1 |
  | 058 | textbook (ML lecture notes) | 5,327 | 47 | 0 |

  Labels: `eval/benchmark-n3/silver-labels/{stem}-n3.2-silver-model-assisted.v1.json`. Each labeler
  independently rediscovered the same failure-class discipline this project has hit repeatedly rather
  than being told about it: TOC/dot-leader exclusion, running-header exclusion, form-field vs.
  outline-heading distinction (030), multi-line/wrapped-title reconstruction, cross-reference vs.
  genuine-marker disambiguation, and (043) "(continued)" page-repeat exclusion. 030's document
  independently has the same kind of restarting-local-scope structure 028 did (ITC/GCC-Time-Based/
  GCC-Lump-Sum/Fraud-Attachment each renumbering from 1) and was given distinct `goldStableId` scopes for
  the same reason, without being told 028 existed.
- [x] N3.3 - model-free census frozen before any baseline/R1 comparison. `004` has 93 silver heading
  occurrences, 83 full candidates, 55 selected and 55 decision-relevant; `030` is 239/209/9/8,
  `043` is 43/42/3/3, and `058` is 47/41/13/13 (silver/full/selected/decision-relevant). Thus only
  004 clears the frozen semantic cohort threshold 15; the other three remain valid N3 evidence for
  candidate/rank/eligibility and product comparison, but conditional semantic-quality is insufficient.
- [ ] N3.4 - baseline build vs R1 build, measured independently per document, not tuned to each other:
  semantic role survival, span resolved, validated, grounded, emitted; precision; occurrence recall;
  exact source-span correctness; `partial_timeout` behavior; `complete`-lane neutrality (R1 must not
  touch the complete-lane path at all); `not_run`/exception fail-closed behavior preserved. **Gate #7,
  the one this whole N3 round exists to answer**: does R1 create a new regression on fresh documents
  never used to design or debug it? R1 is not patched after seeing N3 and re-run against the same
  holdout - a regression here is a finding, not a bug to fix before re-measuring.
- [ ] N3.5 - promotion decision. Material recovery with no meaningful collateral -> R1 promotion gate
  PASS. Any regression on the fresh holdout -> R1 promotion FAIL/BLOCK, decided from this evidence, not
  deferred or re-tested against a "cleaned up" N3.

### 057 representation audit - two sub-owners, two different findings, no fix promoted

Per-target: `PdfN2S057RepresentationAuditProbe` rebuilds each of the 23 undelivered occurrences' own
required-lines-only text (dropping every other line the `WindowFragment` candidate also carries) and
tests it against `PdfBlockGrounder`'s real `LooksGroundableText` shape (replicated verbatim, since
private) and against literal presence in the DOCX paragraph population the live alignment matcher
searched.

**Sub-owner A (14 `GROUNDING_VALIDATOR_REJECTION`) - not composite noise, a numbering-depth artifact.**
Only 4/14 required-only texts pass the shape gate. The other 10 fail for an exact, deterministic
reason visible in the text itself: their marker is **three-level decimal** (`10.0.1`, `11.3.1`,
`24.1.1`, `15.8.1`, `24.7.1`, `30.13.1`, `31.3.2`, `45.1.4`, `54.4.1`, `76.10.2`) - the marker alone
contains 2 literal periods, which is exactly what `LooksGroundableText`'s `>= 2 periods` check exists
to reject (aimed at prose/TOC shape, not multi-level numbering). All 4 that pass have a two-level
marker (`12.3`, `22.4`, `23.2`, `76.4`). This means a source-preserving representation fix alone would
recover 4/14 immediately; the other 10 need the text-shape heuristic itself to tolerate legitimate
multi-level decimal markers, a second and distinct question this audit does not attempt to answer.

**Sub-owner B (9 `NO_DOCX_SOURCE_ANCHOR`) - not a matcher problem.** 0/9 required-only canonical texts
exist verbatim anywhere in the DOCX paragraph population the live matcher searched. This rules out "the
matcher's search strategy missed it" - there was nothing present to find. What remains open is *why*:
whether the PDF's own extracted text differs from the DOCX's real text at these points, or the DOCX
segments the same heading across paragraph boundaries differently than the PDF window construction
assumes. Distinguishing those needs a per-occurrence DOCX/PDF text diff, not attempted here.

**Neither sub-owner has a promoted remediation candidate.** The `WindowFragment` correlation, the
periods-heuristic mechanism for sub-owner A, and the verbatim-absence fact for sub-owner B are now all
PROVEN facts, not hypotheses - but "loosen the periods check for N-level markers" and "why doesn't the
DOCX contain this text" are each their own, separately gated piece of work.

### Đính chính - the two sub-owners are one root cause, not two

Three follow-up probes were run to act on the sub-owner split above: a marker-aware grounding
counterfactual for the 10 three-level-marker cases, a downstream check of whether the 4
representation-noise cases would actually align (not just pass the shape gate), and - because that
check said no for all 4 - a PDF/DOCX divergence taxonomy for all 23. That last probe found the real
root cause, and it corrects the framing above: `GROUNDING_VALIDATOR_REJECTION` vs
`NO_DOCX_SOURCE_ANCHOR` are two **pipeline stages** a single upstream cause happens to hit, not two
different causal mechanisms.

**`PdfN2S057MarkerAwareGroundingCounterfactualProbe`**: stripping a marker recognized by the project's
own marker authority (`SourceFactsBuilder.ParseMarkerText`'s decimal branch, reproduced verbatim since
private - never a new regex) before counting punctuation, 10/10 marker-depth cases now pass
`LooksGroundableText`. Negative-control scope (candidates the analyst already called `HeadingTopic` at
confidence >= 0.65 with a three-level-or-deeper marker, across both canonical checkpoints - the only
offline evidence available): **0** beyond these 10 in either 003 or 057.

**But passing grounding is not sufficient.** Checking the same 4 representation-noise cases that
already passed the shape gate on required-only text against the DOCX haystack: **0/4 exist verbatim**.
Passing grounding would only move them to `NO_DOCX_SOURCE_ANCHOR` - the same wall. This forced
checking all 23, not just the 9 already so classified.

**`PdfN2S057PdfDocxDivergenceTaxonomyProbe`**: splitting each target's own marker (read from its
`stableId` suffix, e.g. `057/numbered/10.0.1` -> `10.0.1` - exact, not re-derived by a regex that can
backtrack to the wrong length when a marker like `9.4` has no trailing `.` or `)` in the source text,
which an earlier version of this probe got wrong and is recorded as a corrected mistake, not silently
fixed) from the title, then searching DOCX paragraphs and their `NumberingId` metadata:

| divergence owner | count | mechanism |
|---|---:|---|
| `DOCX_AUTO_NUMBERED_TITLE_ONLY` | **21/23** | title text matches a DOCX paragraph verbatim, and that paragraph has `NumberingId` set - the number is Word's auto-generated list numbering, never literal paragraph text; the PDF render bakes the visible number into the extracted line, the DOCX source never had it as text at all |
| `FULL_TEXT_FOUND_VERBATIM` | 1 | `10.0.1` - the manually-typed-number case, already covered by the marker-aware fix above |
| `NOT_FOUND_EVEN_TITLE_ONLY` | 1 | `23.5` ("Deriving β1...") - unresolved, likely a Unicode/subscript canonicalization mismatch, not investigated further |

This is confirmed against real DOCX metadata (`SlimParagraph.NumberingId`), not inferred from a
resemblance. It also explains why the marker-aware fix and the `WindowFragment`-required-only
hypothesis both looked partially right without being sufficient: both were operating on the PDF's
baked-in marker text, when 21/23 occurrences' actual DOCX anchor never contained that text as a marker
at all - it is auto-numbering metadata on the paragraph, sitting beside title-only text.

**Corrected causal owner for 057, superseding the two-sub-owner framing:** DOCX-native (Word)
auto-numbered headings systematically fail both grounding and alignment when a PDF-rendering pipeline
bakes the visible number into extracted text and requires exact-text matching, because the number
never existed as literal DOCX text to match against. **The remediation candidate is now narrow and
specific**: for a candidate whose alignment fails, retry matching using title-only text (marker
stripped by the same authority the grounding fix above uses) specifically against DOCX paragraphs
carrying `NumberingId` - not promoted here; needs its own neutrality/collateral gate exactly like
003's, since it changes what alignment is willing to accept as a match.

### The `HeadingReadable` debt, recorded separately

It was found while investigating C1 and does not belong to C1's ledger.

| | |
|---|---|
| Vietnamese `DisplayText` gluing | **PROVEN** - 92.6% on 001, 91.7% on 010 |
| cross-document Vietnamese recurrence | **PROVEN** - not 001-specific |
| English collateral benefit | **PROVEN** - the repair fixes real kerning damage |
| product / semantic impact | **NOT PROVEN** |
| remediation | **NOT JUSTIFIED** |

Reopen only with a measured product impact, and never as `if Vietnamese then disable` on the evidence
of two documents.

### Meta-lesson, twice in one investigation

- **A candidate id is not occurrence authority.** Ids are discovery-order assignments and shift across
  code changes; `b22` was quoted from an old run as evidence about the current one.
- **`DisplayText` is not the analyst's input.** It was tallied and described as analyst-facing without
  checking the production path.

Both are the same error: assigning causal meaning to a representation without re-resolving it on the
exact production path where the decision is actually made.

- [ ] **Not opened: repairing `HeadingReadable`.** The repair exists because kerning damage is real -
  092 and 056 show it - so narrowing it is a behaviour change needing its own population and collateral
  measurement, and there is as yet no evidence it would recover a single heading.
- [ ] Decision pending on how to test causality. The originally planned checkpoint replication answers
  *what the analyst did* (not proposed / wrong role / wrong span) but not *why*, and cannot separate
  the two confounded explanations. A single diagnostic counterfactual - one 001 run with the fragment
  repair withheld for this cohort only, everything else frozen, compared against the existing B3
  artifact - would test causality directly on one variable. It costs one model call and it changes
  what the analyst sees, so it is a decision, not a next step.

## Decision gate

- [ ] A/B remediation is deliberately not scheduled. Both are confirmed debt with promotion gates
  recorded above. Reopen only when (1) a release requirement needs higher hierarchy quality, or
  (2) the M9.4 end-to-end benchmark shows they block product output enough to be worth the fix.
  If remediation happens it is upstream structural-quality work (TOC/scope, then TableLike
  consistency) and is not M8.2 - the parent resolver still has no evidence against it.

- [ ] Optional benchmark/escalation: retain `NvidiaNimVisualQuestion`, but call NVIDIA 90B only for a
  deliberately requested frozen A/B comparison or a documented Qwen unresolved case. Do not claim
  Qwen is better than NVIDIA without that comparison.
