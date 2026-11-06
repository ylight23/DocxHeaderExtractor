# PLACE trên báo cáo tháng: điều tra và cách hỏi theo độ sâu — 06/11/2026

Ghi chú cố định của đợt điều tra ngày 06/11/2026, trên nền commit `53daadd`. Không trích chữ báo cáo: heading được gọi bằng alias (`S0089`...) và kiểu nhãn (La Mã, chữ cái, chấm tròn, gạch ngang). Toàn bộ request/response thô nằm trong `.dhx-evidence/qualification/` trên máy (không commit, có chữ báo cáo).

## Vấn đề

Trên báo cáo tháng 9, PLACE đặt mục chấm tròn `S0094` dưới mục La Mã `S0080` thay vì dưới mục chữ cái `S0088` đứng ngay trên nó, trong khi mục chấm tròn liền trước (`S0089`) nằm đúng dưới `S0088`. Hệ quả ở gather: chuỗi tiêu đề sai, nội dung không khớp mục đích.

## Đã loại trừ (đo thật, Qwen, có capture)

| Giả thuyết | Phép đo | Kết quả |
|---|---|---|
| Mất ngữ cảnh giữa các lô | Đọc gói thật | Sai. Gói có đủ: `S0088` trong danh sách heading, trong `decidedHeadings` và trong `sourceContext`; `S0089`/`S0094` cùng lô. |
| Chia lô (A) so với cả tài liệu (B) | 3 lượt mỗi cách × 4 bộ đầu vào | Không khác biệt ngoài nhiễu (độ lệch trung bình ~1,2 so với ~1,1 dòng/lượt). `S0094` sai ở cả hai cách. Không promote B. |
| Thiếu suy luận | 1 lượt, reasoning `low` | 2.000–2.850 token suy luận, `S0094` vẫn sai. |
| Câu prompt "không dựa vào hình thức" | 1 lượt, thay câu | `S0094` vẫn sai. |
| Trường lý do | 1 lượt | Đúng, nhưng lượt gốc cũng có lúc đúng: chưa đủ để kết luận. |
| Hỏi lại cặp cùng kiểu khác cha | 6 lượt có hỏi lại | Có hại: 4 lần kéo `S0089` đang đúng sang cha sai cho khớp `S0094`, 1 lần sửa đúng. **Đã bỏ.** |

Trên 7 lượt PLACE hiện tại, `S0094` sai 6 lượt. Kết luận: lỗi suy luận khi model phải trỏ cha riêng cho từng dòng, không phải thiếu dữ liệu.

## Cách hỏi mới: dàn ý theo độ sâu (`OutlinePlacer`, sau cờ, mặc định tắt)

Model nhận cả tài liệu như một dàn ý: mỗi heading một dòng theo thứ tự, kèm nhãn hiển thị, in đậm hay không, và dòng đầu tiên phía sau. Model trả độ sâu từng dòng (`1, 2, 3...` hoặc `NONE`). C# chỉ kiểm tra độ sâu không tăng quá 1 và quy đổi cơ học: cha là dòng gần nhất phía trên có độ sâu nhỏ hơn 1. Cấp vẫn do model quyết, không lấy từ kiểu đánh số. Bước kiểm tra (`ParentResolver`) và tính cấp (`HierarchyResolver`) giữ nguyên; phát lại offline 4 phản hồi thật qua hai bước này: 0 cặp bị giữ, 0 heading không có cấp, cấp trùng độ sâu model ghi.

Đối chiếu theo bố cục file (La Mã > chữ cái > chấm tròn > gạch ngang dưới chấm tròn liền trên), 1 lượt mỗi bộ:

| Đầu vào (SHA-256 request, 16 ký tự đầu) | Heading | Cạnh lệch |
|---|---:|---:|
| Tháng 8, bản đọc cũ (`3094987439b5813d`) | 19 | 0 |
| Tháng 10, bản đọc cũ (`37f650e3bdb8aedb`) | 17 | 0 |
| Tháng 9 bản đầu (`265db320e3be6585`) | 20 | 0 (`S0089`, `S0094` đúng dưới `S0088`) |
| Tháng 9 bản sửa (`95af2de1ff0ab736`) | 17 | 0 (2 dòng tiêu đề: `NONE`) |
| Tháng 8, bản đọc in đậm đã sửa (`a390410bae64c147`) | 20 | 0 |
| Tháng 10, bản đọc in đậm đã sửa (`51996270561b6187`) | 18 | 0 (tiêu đề: `NONE`) |

Một bảng đối chiếu ban đầu coi dòng gạch ngang ngang cấp với chấm tròn và báo 2 "lệch"; đối chiếu lại với file (ảnh chụp báo cáo tháng 8 và mô tả tháng 9), dòng gạch ngang là mục con của chấm tròn liền trên, nên model đúng. Chi phí mỗi lượt cách hỏi mới ~0,00005 USD, 1 lượt gọi/tài liệu.

**Chưa đủ để promote:** mỗi bộ 1 lượt (chưa đo độ ổn định giữa các lượt, chính là điểm yếu của cách cũ); một mẫu tài liệu; chưa chia mục cho tài liệu quá giới hạn một request (hiện báo lỗi `REQUEST_OVER_CAP`).

## Lỗi bộ đọc DOCX phát hiện trong đợt này (đã sửa)

In đậm/nghiêng/chữ hoa của đoạn được lấy từ định dạng dấu xuống dòng (`w:pPr/w:rPr`) khi chữ không đậm toàn bộ: một dòng gạch ngang chữ thường có dấu ¶ in đậm bị đọc là in đậm và SELECT chọn làm heading. 5 đoạn bị ảnh hưởng trên 3 báo cáo (08: 3, 09: 0, 10: 2). Sau sửa, dòng đó không còn được chọn trên tháng 8.

## Thay đổi code trong đợt (đều có test)

- `OpenXmlDocumentSource`: định dạng đoạn tính từ run chữ; ¶ chỉ dùng khi đoạn không có chữ.
- `AdjacentHeadingParentConsistency` + `ParentResolver`: hai heading liền nhau cùng kiểu (style, đậm, loại nhãn theo định dạng Word, cấp danh sách) mà khác cha thì cả hai được giữ lại (`UNRESOLVED`), không hỏi lại model. Lưu ý: ở gather, một heading không có cấp làm cả file được giữ lại; chưa thay đổi.
- Cờ thử nghiệm, mặc định tắt, không đổi request production (wire golden chỉ đổi hash audit): `OutlinePlacer`, `PlacementVariant` (reasoning / câu prompt / trường lý do), `countedContext` (heading có nhãn đếm được gần nhất).
- Capture: mọi bước `HIERARCHY_*` được tính là lượt PLACE; công cụ `place-variants-captured`.

## Chi phí đợt (USD, đã xác nhận)

Trích xuất có capture 6 lượt ~0,023; A/B 24 lượt PLACE ~0,005; biến thể 3 lượt ~0,003; cách hỏi mới 6 lượt ~0,0003. Tổng ~0,032.

## Promote (cùng ngày)

Chủ dự án duyệt cổng promote: trích xuất trọn bộ (SELECT, GROUP, PLACE theo độ sâu) mỗi báo cáo đúng **1 lần**, đạt thì promote. Tuỳ chọn `PipelineOptions.DocxOutlinePlacement` (mặc định `true`) cho tuyến DOCX dùng `OutlinePlacer`; PDF và scan giữ `HeadingPlacer`. Khoá outline lưu (`OutlineReuse.ConfigKey`) có thêm `placement:HEADING_OUTLINE_DEPTH_V1`, nên outline cũ không được dùng lại.

| Báo cáo (SHA-256 bản sao, 16 ký tự đầu) | Heading | Cạnh lệch theo bố cục | Cặp bị giữ | Không có cấp | Lỗi giao thức | USD |
|---|---:|---:|---:|---:|---:|---:|
| Tháng 8 (`9a24e9b790a324ae`) | 21 | 0 | 0 | 0 | 0 | 0,0033 |
| Tháng 9 (`348dd8ec25fd8afa`) | 22 | 0 | 0 | 0 | 0 | 0,0035 |
| Tháng 10 (`587141dff42b9f98`) | 16 | 0 | 0 | 0 | 0 | 0,0033 |

Tháng 9: `S0089` và `S0094` nằm dưới `S0088`; dòng tiêu đề tài liệu là `NONE` ở tháng 8 và 9. Cặp bị giữ và cấp được kiểm tra bằng cách phát lại offline phản hồi thật qua `ParentResolver`/`HierarchyResolver` (audit pipeline không ghi phần quan sát PLACE). Mỗi trích xuất có 1 lượt PLACE. Capture đủ: không thiếu phản hồi, không phản hồi chưa gắn.

Ghi nhận ngoài PLACE: SELECT tháng 9 chọn một dòng gạch ngang không in đậm (`S0091`, được đặt đúng dưới `S0089`); SELECT tháng 10 lượt này không chọn một mục chấm tròn mà lượt trước có chọn. Đây là dao động của SELECT, không phải PLACE.

**Rủi ro còn lại khi promote:** độ ổn định giữa các lượt mới thấy qua 1–2 lượt mỗi báo cáo; một mẫu tài liệu; tài liệu quá giới hạn một request sẽ không có cấp (`REQUEST_OVER_CAP`, cả file bị giữ ở gather) vì chưa chia mục. Tắt bằng `DocxOutlinePlacement = false` để về cách cũ.

## Bước tiếp theo đề xuất

1. Chia mục theo cấp 1 cho tài liệu dài.
2. Gather: giữ lại cục bộ phần nội dung bị ảnh hưởng thay vì cả file.
3. Theo dõi độ ổn định trên các lượt thật tiếp theo và thử thêm tài liệu khác mẫu.
