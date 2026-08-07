# Việc cần làm

Xếp theo (giá trị đã chứng minh) / (rủi ro đo lường). Mỗi mục ghi kèm **cách nghiệm thu**, vì ở dự
án này phần khó không phải viết code mà là biết một thay đổi có thật sự tốt hơn không.

Hai kỷ luật áp cho mọi mục, cả hai đều từng bị vi phạm và làm sai kết luận:

- **Một biến mỗi vòng đo** (`handoff.md` §4.1). Gộp hai thay đổi vào một lượt là mất khả năng quy
  trách nhiệm cho từng cái.
- **Mọi con số ghi kèm cấu hình đo.** Báo cáo `dhx eval` nay in chữ ký đầy đủ (kể cả `gpuLayers`,
  `seed`); đọc dòng đó trước khi so với bất kỳ bảng nào trong `handoff.md`.

---

## 1. Luật nhận được dòng bìa / khối chữ ký — ~~*chặn hai thứ khác*~~ **PHẦN LỚN ĐÃ XONG (§12)**

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

## 3b. Cấp theo chuỗi đánh số khi style bất nhất theo phần — *lỗi lớn nhất còn lại* (§16)

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

## 4. Đáp án có người xác nhận — *thắt cổ chai của mọi thứ phía sau*

**Việc duy nhất không tự động hoá được.** Mọi `.key` tài liệu thật hiện có đều do agent gán (§5,
§9.7 giới hạn 2). Và calibration profile vẫn **sinh được nhưng chưa dùng được**: cần n=52 mẫu mỗi
bucket khi không có lỗi nào, nhảy lên n=80 nếu có **một** lỗi — bucket lớn nhất đang có 28.

Cần vài chục tài liệu thật đi qua bảng Review trong giao diện web. Tài liệu tổng hợp không thay
được: nó chứng minh đường code, không sinh ra phân phối đúng của tài liệu thật.

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

Đắt nhất, đổi lược đồ, và **chưa tài liệu thật nào trong bộ đo chứng minh nó đáng**. Làm sau cùng,
hoặc sau khi gặp một tài liệu hỏng đúng kiểu đó.

**Bằng chứng hiện có, và nó chưa đủ.** §5 ghi đúng một ca: báo cáo thật có `i=452` chứa cả hai đề
mục vì file gốc thiếu ngắt đoạn. Một ca trên hai tài liệu thật, và nó là ca NGƯỢC (hai heading trong
một block) chứ không phải ca mục này mô tả (một heading trải qua nhiều block). Khoá luận 1498 đoạn
đo ở §9 không có ca nào thuộc cả hai dạng.

Tức chi phí thì chắc chắn (đổi lược đồ, đổi mọi điểm đọc `i`), còn lợi ích vẫn là 1 mục trên hơn 240
heading của hai tài liệu thật. **Giữ nguyên vị trí cuối hàng.** Điều kiện mở lại: gặp một tài liệu
mà dạng này chiếm từ vài mục trở lên, hoặc mục 4 cho đủ tài liệu thật để đo được tần suất thật.

---

## Đã đóng, giữ lại để không mở lại

- **Luật R1 `auto_assign` theo style OOXML** — đã đo đầy đủ, §10. Trên bench F1 tăng 90,9% → 92,0%
  nhưng lợi ích **không đến từ nó**; trên fixture style bị áp sai nó tự nhận 3 mục sai ở confidence
  1.0 trong khi nhánh kia đẩy cả 9 sang cần duyệt. Cờ `--style-auto-assign` giữ lại để đối chứng,
  **mặc định tắt, không bật lên**.
- **`SkipStyledCandidates`** — precision 100% → 94,1%, §6.3. Mặc định tắt.
- **Bốn ý tưởng bị số liệu bác** — §9.6.
