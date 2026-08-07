# Việc cần làm

Xếp theo (giá trị đã chứng minh) / (rủi ro đo lường). Mỗi mục ghi kèm **cách nghiệm thu**, vì ở dự
án này phần khó không phải viết code mà là biết một thay đổi có thật sự tốt hơn không.

Hai kỷ luật áp cho mọi mục, cả hai đều từng bị vi phạm và làm sai kết luận:

- **Một biến mỗi vòng đo** (`handoff.md` §4.1). Gộp hai thay đổi vào một lượt là mất khả năng quy
  trách nhiệm cho từng cái.
- **Mọi con số ghi kèm cấu hình đo.** Báo cáo `dhx eval` nay in chữ ký đầy đủ (kể cả `gpuLayers`,
  `seed`); đọc dòng đó trước khi so với bất kỳ bảng nào trong `handoff.md`.

---

## 1. Luật nhận được dòng bìa / khối chữ ký — *chặn hai thứ khác*

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

## 2. Đo nhánh `LevelTrusted` của StyleTrust

Đã cài (`ResolveLevel` + khâu chọn đoạn để hỏi cấp) nhưng **CHƯA ĐO** — xem §11.3. Đây là nhánh
nhắm thẳng vào lỗi lớn nhất còn lại: đúng cấp **40,7%** trên một báo cáo gán `Heading2` cho gần như
mọi thứ, và **~28%** trên một khoá luận dùng `Heading1→Heading3→Heading4`.

Cần hai thứ:

- **Fixture cấp style thoái hoá** trong `BenchDocumentFactory`: một tài liệu ≥8 đoạn mang style mà
  mọi mục đều cùng một cấp, đáp án thì có cây thật nhiều cấp.
- **Một lượt LLM.** `--no-llm` đi qua `HeuristicOnly` chứ không qua `ResolveLevel`, nên không đo
  được nhánh này. Trên WX 5100 là ~2 h/lượt (xem §8.2); hai lượt mỗi nhánh.

## 3. `NumberingAudit` không đọc được "Chương 1."

Dạng "nhãn + số" không sinh ra `NumberToken` vì `Parse` chỉ có mẫu Ả Rập / La Mã / chữ cái. Đây là
lý do gốc của bug 87,2% ở §5, hiện đang được *vá bằng chốt* chứ chưa được *sửa*.

**Vì sao phải làm riêng một phiên.** Khảo sát: **13 điểm gọi trên 9 file**
(`CorrectionMemory`, `EvidenceConfidenceCalibrator`, `HeaderExtractionPipeline`,
`InlineHeadingSplitter`, `ModelHeadingCriticGate`, `OutlineStructureResolver`,
`PrecisionAcceptanceGate`, `StructuralHierarchyResolver`, `StructuralRecovery`). Thêm một
`NumberToken` kind mới đổi cả `Signature` → `SignatureTiers` → suy cấp, tức đổi output của tất cả
cùng lúc. Không được gộp với bất kỳ thay đổi nào khác.

## 4. Đáp án có người xác nhận — *thắt cổ chai của mọi thứ phía sau*

**Việc duy nhất không tự động hoá được.** Mọi `.key` tài liệu thật hiện có đều do agent gán (§5,
§9.7 giới hạn 2). Và calibration profile vẫn **sinh được nhưng chưa dùng được**: cần n=52 mẫu mỗi
bucket khi không có lỗi nào, nhảy lên n=80 nếu có **một** lỗi — bucket lớn nhất đang có 28.

Cần vài chục tài liệu thật đi qua bảng Review trong giao diện web. Tài liệu tổng hợp không thay
được: nó chứng minh đường code, không sinh ra phân phối đúng của tài liệu thật.

## 5. Dùng `SlimSourceSegment` để mở khoá writeback

`OutlineWriteback` từ chối bằng `inline_body_not_splittable` mỗi khi heading chỉ chiếm một phần
paragraph. Ánh xạ offset → (run, offset thô) nay đã có (`8b95302`) nhưng **chưa có caller nào dùng**.
Nối nó vào là ghi ngược được phần `InlineHeadingSplitter` đã tách ra.

**Nghiệm thu:** test round-trip mở/ghi/đọc lại, không cần bench.

## 6. Ứng viên đa block — heading trải qua nhiều paragraph

Một tiêu đề bị Enter thật cắt đôi hiện không biểu diễn được: lược đồ chỉ cho **một quyết định trên
một `i`** (§5). Cùng khe hở với việc `InlineHeadingSplitter` không tách nổi block dính hai heading.
`LineBreakOffsets` (`8b95302`) mới chỉ giải quyết ca Shift+Enter trong cùng paragraph.

Đắt nhất, đổi lược đồ, và **chưa tài liệu thật nào trong bộ đo chứng minh nó đáng**. Làm sau cùng,
hoặc sau khi gặp một tài liệu hỏng đúng kiểu đó.

---

## Đã đóng, giữ lại để không mở lại

- **Luật R1 `auto_assign` theo style OOXML** — đã đo đầy đủ, §10. Trên bench F1 tăng 90,9% → 92,0%
  nhưng lợi ích **không đến từ nó**; trên fixture style bị áp sai nó tự nhận 3 mục sai ở confidence
  1.0 trong khi nhánh kia đẩy cả 9 sang cần duyệt. Cờ `--style-auto-assign` giữ lại để đối chứng,
  **mặc định tắt, không bật lên**.
- **`SkipStyledCandidates`** — precision 100% → 94,1%, §6.3. Mặc định tắt.
- **Bốn ý tưởng bị số liệu bác** — §9.6.
