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

## 1. ~~Luật nhận được dòng bìa / khối chữ ký~~ — **ĐÓNG (§56.2): việc của tầng ngữ nghĩa, không phải luật cấu trúc**

> **Trạng thái.** Đã chữa trên đường `--style-trust`: `09-style-ap-sai` precision **57,1% → 100%**
> (hết cả ba dương tính giả), bench 9 tài liệu F1 **92,5% → 95,6%**, tuyệt đối 5/9 → 6/9. Trên khoá
> luận thật là **hoà** (đáp án đồng thuận: F1 95,1% → 95,0%); đáp án Opus đơn lẻ nói giảm 1,1 điểm
> nhưng ba trong bốn mục mất nằm đúng vùng hai người gán nhãn bất đồng — xem §12.3.
>
> Nguyên nhân hoá ra **không phải** thiếu luật hình dạng như mục này phỏng đoán, mà là hai lỗi trong
> luật đã có: hạ quyền style tự tắt luật thay thế, và "mở ra văn xuôi" bị tính bắc cầu (§12.1, §12.2).
>
> **Còn lại:** đường mặc định (không `--style-trust`) vẫn 57,1% vì ba đoạn đó mang style Heading và
> luật miễn trừ đoạn có tuyên bố cấu trúc. Chữa nốt nghĩa là đổi mặc định của `--style-trust`, và
> §10.4 cảnh báo đúng loại quyết định này — cần đo trên nhiều tài liệu thật hơn, không phải một dòng cờ.

<details><summary>Ghi chép gốc của mục này</summary>

**Vì sao đứng đầu.** Đây là chế độ hỏng mà **không tầng nào trong pipeline bác được**, đã đo hai
lần theo hai đường độc lập:

- §10.3 — trên `09-style-ap-sai`, cả nhánh có lẫn không có luật R1 đều thừa đúng `4, 12, 13`; mô
  hình được hỏi mà vẫn không cắt.
- §11.2 — `StyleTrust` nhận đúng rằng style tài liệu đó không đáng tin (*"quyền chọn HẠ"*) nhưng
  kết quả không đổi một chữ số, vì hạ quyền style là chuyển quyền cho **một chỗ trống**.

Ba dương tính giả là `Hà Nội, tháng 8 năm 2026`, `Người lập biểu`, `Nguyễn Văn A`. Không luật hình
dạng nào hiện có nói được gì về chúng: không trong bảng, không gạch đầu dòng, không kết thúc bằng
dấu câu, không phải chú thích đối tượng.

**Ràng buộc thiết kế.** Không được dùng từ khoá tiếng Việt — §9 đã bỏ `KeywordPrefixRx` lấy
`LabelledNumberPrefixRx` vì lý do đó, và §3.2 đo được prompt chứa bảng phân loại hành chính làm
critic loại nhầm 3 heading thật. Hướng theo hình dạng: dòng bìa thường là **cụm danh từ không có
động từ, đứng thành CỤM nhiều dòng liên tiếp cùng căn lề, không có đoạn thân bài xen giữa** — cùng
họ với `DemoteCoverPageBlock` đã có, nhưng nhóm này nằm ở CUỐI tài liệu chứ không ở trang bìa.

**Nghiệm thu:** `09-style-ap-sai` precision 57,1% → cao hơn, và bench 9 tài liệu không hồi quy. Đo
được bằng `--no-llm --structural-only`, vài giây, không cần lượt LLM nào.

</details>

## 2. ~~Đo nhánh `LevelTrusted`~~ — **ĐÃ ĐO, kết quả ÂM; bị chặn bởi mục 3 (§13)**

> **Trạng thái.** Đã dựng fixture `10-cap-style-thoai-hoa` và đo: nhánh này **không cải thiện gì**
> (đúng cấp 44,4% ở cả hai nhánh cờ). Nhưng cơ chế CHẠY ĐÚNG — log cho thấy mô hình được hỏi cả 9
> mục, `src=Model` — nó chỉ trả lời y hệt cấp mà style đã ghim. Hai giả thuyết "lỗi ở thứ mình gửi"
> đều bị bác bằng số (giấu metadata cấp: 0 tác dụng; bỏ chữ số khỏi tên style: 44,4% → 33,3%), cả
> hai đã hoàn nguyên.
>
> **Chỗ tắc thật:** bộ suy cấp tất định `SignatureTiers` không chạy vì `Declared()` thấy style đã
> khai cấp, và `--style-trust` không với tới chốt đó. Nhưng nới chốt cũng chưa đủ — `NumberingAudit`
> không đọc được `Chương 1.` nên không có token để suy tầng. **Mục 2 bị chặn bởi mục 3.** Thứ tự
> đúng: làm mục 3 trước, rồi mới nới `Declared` để tôn trọng `LevelTrusted`.
>
> **Thêm một tài liệu thật tái hiện đúng lớp lỗi này (2026-08-11), khi điều tra TODO mục 13.** Báo
> cáo thực tập MBBank (`keys/bao-cao-thuc-tap.key`) dùng `Heading2` cho CẢ cấp 2 lẫn cấp 3 (numbering
> phân biệt, style thì không). Đo `--no-llm --style-trust`: log tự in `"12/18 lệch so với độ sâu
> đánh số (67%) ⇒ quyền chọn HẠ, quyền gán cấp HẠ"` — đúng cơ chế phát hiện. Nhưng đúng cấp **giảm**
> so với không bật cờ (58,6% → 46,2%): tiêu đề Chương/front-matter (cấp 1, không đánh số) và mục cấp
> 2 bị đẩy lên +1 đều — khớp nguyên văn "chỗ tắc thật" ở trên (`--no-llm` không qua `ResolveLevel`
> nên "hạ quyền" không có nơi tiếp nhận). Không đo được qua LLM (máy này không có đúng Qwen3.5-9B),
> nhưng đây là tài liệu THẬT thứ hai (ngoài fixture tổng hợp `10-cap-style-thoai-hoa`) xác nhận đúng
> lớp lỗi — dùng khi bắt tay đo mục 2 bằng LLM.

<details><summary>Ghi chép gốc của mục này</summary>

Đã cài (`ResolveLevel` + khâu chọn đoạn để hỏi cấp) nhưng **CHƯA ĐO** — xem §11.3. Đây là nhánh
nhắm thẳng vào lỗi lớn nhất còn lại: đúng cấp **40,7%** trên một báo cáo gán `Heading2` cho gần như
mọi thứ, và **~28%** trên một khoá luận dùng `Heading1→Heading3→Heading4`.

Cần hai thứ:

- **Fixture cấp style thoái hoá** trong `BenchDocumentFactory`: một tài liệu ≥8 đoạn mang style mà
  mọi mục đều cùng một cấp, đáp án thì có cây thật nhiều cấp.
- **Một lượt LLM.** `--no-llm` đi qua `HeuristicOnly` chứ không qua `ResolveLevel`, nên không đo
  được nhánh này. Trên WX 5100 là ~2 h/lượt (xem §8.2); hai lượt mỗi nhánh.

</details>

## 3. ~~`NumberingAudit` không đọc được "Chương 1."~~ — **XONG (§14)**

> **Trạng thái.** Đã thêm `NumberKind.Labelled` với NHÃN nằm trong chữ ký (`Labelled(chương):1`), và
> nới `Declared` để tôn trọng `LevelTrusted`. Hai thay đổi đo riêng:
> - token `Labelled` một mình (không cờ): bench đúng cấp 88,5% → **90,4%**, 7/10 → **8/10**;
> - + nới `Declared` (có `--style-trust`): `10-cap-style-thoai-hoa` **44,4% → 100%**, bench đúng cấp
>   88,5% → **100%**, và lần đầu bench đạt **tuyệt đối toàn phần: P/R/F1 100% · đúng cấp 100% · 10/10**.
>
> Việc này cũng gỡ nốt chỗ tắc của mục 2 (§13.4). 305/305 test xanh; ba lần test suýt xanh giả đều
> bị kiểm đột biến bắt — xem §14.4.
>
> **Còn lại:** cải thiện nằm sau cờ `--style-trust`. Đổi mặc định là quyết định riêng, chung số phận
> với phần còn lại của mục 1.

<details><summary>Ghi chép gốc của mục này</summary>

Dạng "nhãn + số" không sinh ra `NumberToken` vì `Parse` chỉ có mẫu Ả Rập / La Mã / chữ cái. Đây là
lý do gốc của bug 87,2% ở §5, hiện đang được *vá bằng chốt* chứ chưa được *sửa*.

**Vì sao phải làm riêng một phiên.** Khảo sát: **13 điểm gọi trên 9 file**
(`CorrectionMemory`, `EvidenceConfidenceCalibrator`, `HeaderExtractionPipeline`,
`InlineHeadingSplitter`, `ModelHeadingCriticGate`, `OutlineStructureResolver`,
`PrecisionAcceptanceGate`, `StructuralHierarchyResolver`, `StructuralRecovery`). Thêm một
`NumberToken` kind mới đổi cả `Signature` → `SignatureTiers` → suy cấp, tức đổi output của tất cả
cùng lúc. Không được gộp với bất kỳ thay đổi nào khác.

</details>

## 3b. ~~Cấp theo chuỗi đánh số khi style bất nhất theo phần~~ — **XONG một phần (§17)**

> **Trạng thái.** Đã thêm vế thứ ba cho `StyleTrust`: đối chiếu cấp style với ĐỘ SÂU của chuỗi đánh
> số gõ tay. Khoá luận thật **đúng cấp 26,5% → 37,2%**; P/R/F1 không đổi; bench 10 tài liệu giữ
> 10/10 · cấp 100%. 309/309 test xanh, đã kiểm đột biến.
>
> **Còn lại:** 37,2% vẫn thấp vì luật chỉ chạm được đoạn CÓ chuỗi đánh số. `MỞ ĐẦU`, `KẾT LUẬN`,
> `Tiểu kết chương 1` không có số để bám nên cấp vẫn theo style. Hướng tiếp: suy cấp cho mục không
> đánh số theo vị trí giữa hai mục có đánh số (kẹp giữa), chưa đo.

<details><summary>Ghi chép gốc của mục này</summary>

Trên khoá luận thật, đúng cấp **26,5%** và chuỗi mục 2 + mục 3 **không chạm tới nó**: `StyleTrust`
chấm tài liệu này là "5 cấp riêng biệt ⇒ quyền gán cấp GIỮ", và chấm ĐÚNG theo định nghĩa hiện có.

Vấn đề nằm ở mức PHẦN chứ không mức tài liệu: cùng `Heading3` mang cấp 2 ở 9 mục và cấp 3 ở 8 mục,
tuỳ chương tác giả có dùng Heading2 hay không. Mọi thống kê gộp toàn tài liệu đều mù với nó, nên
**thêm ngưỡng cho `StyleTrust` không phải hướng đi**.

**Hướng:** chuỗi đánh số gõ tay (`1.1.`, `2.2.3.2.`) nhất quán suốt tài liệu trong khi style thì
không. Khi ĐỘ SÂU của nó mâu thuẫn có hệ thống với cấp style, ưu tiên nó. Đây là tín hiệu mức đoạn
nên nhìn được thứ thống kê mức tài liệu bỏ sót.

**Nghiệm thu:** khoá luận thật đúng cấp 26,5% → cao hơn; bench 10 tài liệu giữ đúng cấp 100% với
`--style-trust`. Cần lượt LLM (~6 phút/lượt trên RTX 3060).

**Rủi ro đã biết:** §5 ghi thiếu chốt "cấu trúc đã khai thì không suy lại" từng kéo đúng cấp
100% → 87,2%. Nới nguồn quyết định cấp là đụng đúng chỗ đó.

</details>

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

## 5. ~~Dùng `SlimSourceSegment` để mở khoá writeback~~ — **XONG, phạm vi hẹp (§15)**

> **Trạng thái.** Đã nối. Tách được khi ranh giới rơi đúng đầu một run VÀ mọi run là con trực tiếp
> của `w:p`; mọi ca khác giữ nguyên `inline_body_not_splittable`. `Verify` nay có bản đồ chỉ số —
> ràng buộc mà mô tả gốc bỏ sót. Round-trip test + ca fail-closed, ba đột biến đều bị bắt (§15.4).
> 307/307 test xanh.
>
> **Còn lại:** ca ranh giới nằm giữa run (phải cắt đôi text trong run) và ca run lồng trong
> `w:hyperlink`. Cả hai vẫn từ chối, có chủ đích.

<details><summary>Ghi chép gốc của mục này</summary>

`OutlineWriteback` từ chối bằng `inline_body_not_splittable` mỗi khi heading chỉ chiếm một phần
paragraph. Ánh xạ offset → (run, offset thô) nay đã có (`8b95302`) nhưng **chưa có caller nào dùng**.
Nối nó vào là ghi ngược được phần `InlineHeadingSplitter` đã tách ra.

**Nghiệm thu:** test round-trip mở/ghi/đọc lại, không cần bench.

### Khảo sát trước khi làm — LỚN HƠN mô tả trên

Mô tả "nối nó vào là ghi ngược được" bỏ sót ba ràng buộc, cả ba đều nằm trong chính hợp đồng của
`OutlineWriteback`:

1. **Tách đoạn làm DỊCH CHỈ SỐ.** Bất biến 3 của `OutlineWriteback` là đọc lại bản đích rồi đối
   chiếu `heading.Index` → đoạn. Chèn một `w:p` mới làm mọi chỉ số phía sau lệch +1, nên khâu xác
   minh sẽ đổ ngay ở mục kế tiếp. Cần một BẢN ĐỒ chỉ số (gốc → sau tách) dùng chung cho cả xác minh
   lẫn caller.
2. **`StableId` cũng dịch.** Nó là địa chỉ theo vị trí (`body[1]/p[N]`), nên vế `stable_id_mismatch`
   phải được tính lại chứ không so thẳng được.
3. **Run có thể nằm trong `w:hyperlink`.** `SourceSegments.RunIndex` đếm theo
   `paragraph.Descendants<Run>()`, tức tính cả run lồng trong hyperlink. Tách ở một run như vậy đòi
   tách cả hyperlink bao ngoài — phải fail-closed khi run ranh giới không phải con trực tiếp của
   `w:p`.

**Phạm vi hẹp nên làm trước:** chỉ tách khi ranh giới rơi ĐÚNG đầu một run VÀ run đó là con trực
tiếp của `w:p`; mọi ca khác giữ nguyên `inline_body_not_splittable`. Như vậy không phải cắt đôi text
trong run — phần fiddly nhất — mà vẫn mở được ca phổ biến.

**Chưa làm.** Đây là thành phần DUY NHẤT ghi ra tài liệu người dùng, và bất biến 2 của nó là "không
chạm vào một ký tự nội dung nào". Tách đoạn không đổi ký tự nào nhưng đổi CẤU TRÚC, nên bất biến đó
phải được phát biểu lại cho chính xác trước khi có dòng code đầu tiên.

</details>

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

## 9b. ~~Bộ suy cấp tất định không chạy trên `--no-llm`~~ — **XONG (§51)**

> `StructuralHierarchyResolver` và `TableOfContentsAnchor` đều tất định nhưng nằm trong
> `RunModelAsync`, nên đường `--no-llm` chưa bao giờ chạy chúng. Sửa: bench đúng cấp
> **86,1% → 100%**, đúng cha **91,7% → 100%**, tuyệt đối 5/7 → 6/7, precision không đổi.
> 95 file: số mục không đổi (6.357), cấp 9 sập 221 → 20.
>
> **Mặc định BẬT** — ngoại lệ có lý do: bộ suy cấp đã có bằng chứng đáp án người kiểm (§31) và
> đường có mô hình chạy nó vô điều kiện; bật cho `--no-llm` là sửa bất đối xứng chứ không thêm
> suy đoán. Tắt bằng `--no-deterministic-hierarchy`.
>
> **Hệ quả phải nhớ:** mọi con số `--no-llm` ghi TRƯỚC §51 đều thiếu bước này, kể cả bảng phân bố
> cấp ở §45.3.

---

## 9c. ~~Bộ dựng `vn-administrative`~~ — **XONG (§60)**

> `AdministrativeOutline` + cờ `--admin-outline`, **mặc định tắt**. Bộ dựng tất định thứ ba, cùng
> khuôn với hai bộ đã đạt 100%: cấp neo theo cha gần nhất (thứ tự lồng nhau đọc từ chính tài liệu,
> không gán cứng theo loại ký hiệu), thân bài tách tại dấu ngắt đầu tiên mở ra số liệu,
> **không một ngưỡng nào**. Trả rỗng khi chỉ có một chữ ký thay vì đoán.
>
> Đã phơi trong Web UI cùng hai bộ kia, kèm test ghim rằng mọi ô điều khiển đều được JS gửi đi
> (mutation "quên nối dây một ô" → đỏ).
>
> **Chưa chấm được** vì chưa có đáp án cho tài liệu hành chính — xem mục 4.

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

## 12. Bốn ca của spec vẫn treo, ba trong đó không có dữ liệu

| ca | trạng thái |
|---|---|
| La Mã thường `i. ii. iii.` | **ĐÃ ĐO (§55.1): 19 mục trên 12/95 file, chỉ 5 mục chắc chắn** — không đáng, vì rủi ro đọc nhầm dãy chữ cái 601 mục. Và **không phải thêm regex**: `i.` vừa là La Mã 1 vừa là chữ cái thứ 9; `IgnoreCase` sẽ đọc mọi `c.`/`d.`/`i.` trong dãy chữ cái thành cấp 1. Luật đúng phải nhìn **cả dãy** tìm `ii`/`iii`/`iv` — cùng hình dạng với luật ba bảng chữ cái ở §45.1 |
| numbering restart theo chương | chưa gặp trong tài liệu nào có đáp án |
| phụ lục đánh số riêng | chưa gặp trong tài liệu nào có đáp án |
| heading bị Enter cắt đôi | mục 6; Nghị định 30/2020 **bắt buộc** dạng này (`Chương II` một dòng, tiêu đề dòng kế) nên nó phổ biến hơn dự đoán cũ |

**Cách nghiệm thu.** §10.4: cài luật cho ca chưa có dữ liệu là thêm mã không kiểm chứng được.

---

## 13. `TableOfContentsAnchor.Apply` pin sai cấp cho heading numPr-driven — ĐÃ SỬA, ĐÃ ĐO LẠI SAU §51: đúng cấp 44,8% → 96,6%

*(Chi tiết đầy đủ, kể cả công cụ `dhx toc-keys` phát hiện ra nó: `handoff.md` §52–§53.)*

**Phát hiện khi xây `dhx toc-keys`** (đối chiếu với `keys/bao-cao-thuc-tap.key` trên chính tài liệu
nguồn thật): `TableOfContentsAnchor.DepthOf` chỉ đọc SỐ trong TEXT của dòng mục lục. Heading
`numPr`-driven (numbering do Word vẽ, không gõ tay) không để số nào trong TEXT dòng mục lục — chỉ
còn ở `NumberLabel` đã resolve riêng từ `numbering.xml`. Kết quả cũ: `DepthOf` mặc định các mục này
về cấp 1.

**Test cô lập `TableOfContentsAnchorNumberLabelTests.cs` đo trực tiếp `TableOfContentsAnchor.Apply`,
không đoán:** heading được gán ĐÚNG cấp 2 từ nguồn khác (numPr) trước đó, sau khi `Apply` chạy (bản
cũ) bị ghi đè thành cấp 1 sai — đúng cơ chế "mục lục phải nói lời cuối" mà code đã tự ghi chú. Cơ chế
lỗi có thật, tái lập 100%.

**ĐÃ SỬA**: `Apply` nay ưu tiên `NumberLabel` trước khi rơi về đọc TEXT. Test hết `Skip`, xanh.

**Đo tác động lần 1 (trước §51) — MẤT HIỆU LỰC THAM CHIẾU, giữ lại để thấy vì sao.** Lượt đo đầu tiên
báo `dhx eval --no-llm` byte-identical trước/sau trên cả bench lẫn báo cáo thực tập thật, log không
có dòng "Mục lục pin lại N cấp" — kết luận lúc đó: `Apply` không chạm heading nào trên `--no-llm`.
Kết luận ấy ĐÚNG với dữ kiện lúc đó, nhưng dữ kiện sai: TODO mục 9b/§51 phát hiện `Apply` **chưa từng
được gọi trên đường `--no-llm`** trước khi sửa (nằm trong `RunModelAsync`) — nên "byte-identical" chỉ
phản ánh việc cả hai lượt đều không gọi `Apply`, không phản ánh gì về bản sửa `NumberLabel`.

**Đo lại sau §51 (2026-08-11), một biến sạch — giữ §51 cố định, chỉ bật/tắt bản sửa `NumberLabel`:**

| Cấu hình | Đúng cấp | Sai cấp |
|---|--:|--:|
| §51 (Apply chạy) + `DepthOf` cũ (không NumberLabel) | 44,8% | 16 |
| §51 (Apply chạy) + `NumberLabel` (bản sửa) | **96,6%** | **1** |

Log giờ in `"Mục lục của tài liệu pin lại 11 cấp"` và `"Hậu xử lý hierarchy (không mô hình): sửa 15
cấp"` — xác nhận cả `TableOfContentsAnchor` lẫn `StructuralHierarchyResolver` đều chạy thật. Đóng góp
riêng của bản sửa `NumberLabel`, giữ mọi thứ khác cố định: **+51,8 điểm đúng cấp**. Đo bằng cách tạm
đổi một dòng (`DepthFromNumberLabel(p.NumberLabel) ?? DepthOf(p.Text)` → `DepthOf(p.Text)`), build,
đo, rồi hoàn nguyên — không để lại trong lịch sử git.

1 lỗi "sai cấp" còn lại (`i=701`, "Chức năng nhiệm vụ của từng vị trí") khớp đúng mục TOC từng bị
`dhx toc-keys` báo "không tìm thấy" khi xác thực — nhiều khả năng cùng loại lệch nhẹ TOC-vs-thân-bài
đã thấy ở ca "CHƯƠNG 2" trước đó, chưa điều tra sâu thêm.

Bench `dhx eval bench --no-llm` (10 tài liệu tổng hợp) vẫn không đổi qua cả ba lượt đo trên — không
có fixture nào chạm đúng tổ hợp "TOC + numPr không số trong text" nên không hồi quy nhưng cũng không
đo thêm được gì từ bộ đó.

**12 lỗi "sai cấp: trả về 2, đáp án 3" ở lượt đo TRƯỚC §51 — ĐÃ GIẢI THÍCH, không phải bug mới
(2026-08-11).** Nguồn gốc: lượt đo `--no-llm` ban đầu **thiếu cờ `--style-trust`**. Bật cờ lên thì
đúng 12 lỗi này biến mất — cơ chế đối chiếu style-vs-độ-sâu-đánh-số ở §17 đã xử lý đúng. Nhưng bật
`--style-trust` lại lộ ra **14 lỗi khác** (front-matter/chương cấp 1 và mục cấp 2 bị đẩy +1 đều) —
đúng lớp lỗi "hạ quyền chuyển cho chỗ trống" đã ghi ở mục 2 phía trên, không phải lỗi mới. Xem
addendum ở mục 2 — tài liệu này giờ là bằng chứng thật thứ hai cho mục đó, dùng khi bắt tay đo bằng
LLM ở đó. (Đo lại cùng cờ `--style-trust` sau §51 chưa làm — có thể đổi kết quả 14 lỗi kia, xem việc
còn treo dưới đây.)

**Còn treo thật sự:** đo bản sửa `TableOfContentsAnchor` (và tương tác với mục 2) trên đường ĐẦY ĐỦ
(có LLM, và/hoặc có `--style-trust` sau §51) — máy này chỉ có Qwen2.5-7B/Llama-3.2-3B cục bộ, không
phải Qwen3.5-9B đã dùng để chốt "100%" trong handoff.md. Chạy được một so sánh TRƯỚC/SAU sạch (một
biến = bản sửa, giữ nguyên model+cờ) nhưng con số tuyệt đối **không so được** với "100%" đã chốt —
khác model. Muốn con số so được thì cần đúng Qwen3.5-9B.

**Cách nghiệm thu.** Chạy `dhx eval` với đúng cấu hình đã chốt trên máy có Qwen3.5-9B, so trước/sau
bản sửa — một biến, không gộp việc khác.

**Addendum 2026-08-14 (§96): audit metadata Section và thêm span cho slice đoạn gộp.**

- Đã dump/audit 6 file holdout full vào `.verify-build/wb-holdout-section-audit/`, gồm JSON outline, XML slim và `key-metadata.csv`.
- Metadata OOXML của high-level `Section` không đủ phân biệt: gần như toàn `Normal`, font nhỏ, nhiều dòng `outlineLvl=1`, và 5/6 file có `Section` lặp ít nhất 2 lần trong cùng paragraph (`page/header/TOC-ish prefix + body title`).
- `026` trong Eval14 xác nhận conflict quy ước: partial key hiện chấm nhiều mục/bảng trong vùng Section ở level 1, nên luật `PART -> Section` rộng không thể quay lại.
- Đã thêm metadata nguồn cho `MergedParagraphHeadings`: `OriginalText`, `HeadingSpan`, `BoundarySource="MergedParagraphMarker"`; helper đổi sang `internal` để test.
- Test mới `SplitMergedParagraphsTests.Lat_cat_giu_span_nguon_trong_doan_gop`; `dotnet test --no-restore` = `533/533`.
- Eval không đổi: Eval14 `Nav 99.1%`, `Nav+cấp 99.1%`; holdout full `Nav 89.6%`, `Nav+cấp 15.6%`.

Tiếp theo nếu muốn sửa World Bank: dùng `HeadingSpan/BoundarySource` để tách TOC/page-header/body slice trước; không dùng lại luật cấp chỉ nhìn text `Section ...`.

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

## Đã đóng, giữ lại để không mở lại

- **Luật R1 `auto_assign` theo style OOXML** — đã đo đầy đủ, §10. Trên bench F1 tăng 90,9% → 92,0%
  nhưng lợi ích **không đến từ nó**; trên fixture style bị áp sai nó tự nhận 3 mục sai ở confidence
  1.0 trong khi nhánh kia đẩy cả 9 sang cần duyệt. Cờ `--style-auto-assign` giữ lại để đối chứng,
  **mặc định tắt, không bật lên**.
- **`SkipStyledCandidates`** — precision 100% → 94,1%, §6.3. Mặc định tắt.
- **Bốn ý tưởng bị số liệu bác** — §9.6.
- **`VietnameseLegal` dùng builder hành chính** — đã thay bằng `LegalStructuredOutline`; không mở
  lại hướng thêm `EnglishLegal` riêng nếu chưa có bằng chứng cấu trúc khác ngôn ngữ.
