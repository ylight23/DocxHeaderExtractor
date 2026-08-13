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
không có prose, trừ built-in Heading/numbering. Rule style tự đặt thô (lặp >=3, avgLen<90, có anchor)
cover 97/101 nhưng chọn 606 đoạn, nên chưa được cài; cần guard cụm custom-style/form-based chặt hơn.

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

---

## Đã đóng, giữ lại để không mở lại

- **Luật R1 `auto_assign` theo style OOXML** — đã đo đầy đủ, §10. Trên bench F1 tăng 90,9% → 92,0%
  nhưng lợi ích **không đến từ nó**; trên fixture style bị áp sai nó tự nhận 3 mục sai ở confidence
  1.0 trong khi nhánh kia đẩy cả 9 sang cần duyệt. Cờ `--style-auto-assign` giữ lại để đối chứng,
  **mặc định tắt, không bật lên**.
- **`SkipStyledCandidates`** — precision 100% → 94,1%, §6.3. Mặc định tắt.
- **Bốn ý tưởng bị số liệu bác** — §9.6.
