# DOCX 075 — điều tra SELECT repair và PLACE

Dữ liệu: file chuẩn 075, SHA-256 1e03bd6986c228f2d097877c070b648fee695db70c37f38b01ee3c7948673d46. Giữ nguyên contract production. Ba lượt PLACE bổ sung tái dùng SELECT/GROUP đã cache; một lượt SELECT mới chạy cache tắt và ghi response theo từng lượt. Không lưu reasoning ẩn.

## SELECT: nguyên nhân lượt repair được quan sát

Request đầu dựng lại có SHA-256 b4e8fba457679fa9ed0b162eb6e9b860b5f531359d7ab40eb14234db574675ff, trùng request đầu trong lượt SELECT mới. Replay response qua AgentProtocol.ParseSelect:

| Lượt | Kết quả validator | Chi tiết |
|---|---|---|
| 1 | HEADING_EXTENT_EDGE_WHITESPACE | S0011 có khoảng trắng cuối headingExtent |
| 2 | Hợp lệ | Không còn lỗi validator |

Response đầu có sáu headingExtent có khoảng trắng cuối. Cả sáu là substring chính xác tại offset Qwen trả, được kiểm tra trên text nguồn; không phải lỗi lệch UTF-16.

| Alias | Heading | Cuối chữ heading | formatChangeOffsets trong request | End Qwen lượt 1 | End lượt 2 |
|---|---|---:|---:|---:|---:|
| S0011 | Item 1: Document Approval. | 26 | 27 | 27 | 26 |
| S0020 | Item 2: Operating Context. | 26 | 27 | 27 | 26 |
| S0021 | Item 3: Strategic Directions. | 29 | 30 | 30 | 29 |
| S0025 | Item 4: Work Plan, Budget, and Fees. | 36 | 37 | 37 | 36 |
| S0026 | Item 5: Communications Strategy. | 32 | 33 | 33 | 32 |
| S0027 | Item 6: Appointment of the Chair. | 33 | 34 | 34 | 33 |

Các offset dùng UTF-16 và end exclusive. OOXML p12 có run bold kết thúc tại 26, run thường bắt đầu bằng khoảng trắng ở 26. DocxLineFacts.FormatRuns bỏ whitespace hai đầu từng span; Compact lấy start của span tiếp theo nên formatChangeOffsets là 27. Prompt mô tả đây là offset đổi định dạng, nhưng prompt ban đầu không nói headingExtent bị cấm whitespace ở biên. Sau feedback về S0011, Qwen sửa cả sáu extent trong một lượt; 38 quyết định SELECT/NOT_SELECT không thay đổi.

Đây là bằng chứng lệch giữa cách biểu diễn evidence/prompt và ràng buộc validator. Không chứng minh toàn bộ tám repair ở lượt cold trước cùng loại: lượt trước chỉ lưu RepairedFrom đầu tiên, không lưu response từng repair.

## PLACE: năm lượt với cùng SELECT/GROUP

Lấy hai lượt cold/warm trước cộng ba lượt PLACE bổ sung. Ba lượt mới không gọi SELECT/GROUP. Batch PLACE đầu ở cả năm lượt có cùng SHA-256 cee34df5bccd1c5e742c2044d13e8d0492800b05e952ff4d8bcccfa2e45d74d2.

| Lượt | Cha của các mục 1–6 |
|---|---|
| cold trước | Agenda Items (S0031) |
| warm trước | AGENDA for FIRST MEETING (S0030) |
| place-1, place-2, place-3 | Agenda Items (S0031) |

Ở bốn mục đầu S0032–S0035, cùng request batch đầu trả cha khác nhau giữa một lượt và bốn lượt còn lại. S0036–S0037 thuộc batch sau; request batch sau ở lượt warm khác SHA vì packet mang quyết định của batch trước, nên không quy riêng hai thay đổi đó cho biến thiên trên cùng input. Cả năm lượt không báo lỗi protocol PLACE; ParentResolver kiểm tra hợp lệ đồ thị, không xác nhận quan hệ cha đúng về nghĩa. Năm mẫu không đủ để suy tỉ lệ ổn định.

## Các sai khác chất lượng khác trong lượt SELECT mới

SELECT mới trả 12 alias: S0032–S0037 (sáu heading agenda đánh số) đều NOT_SELECT ở cả hai lượt, trước và sau repair. GROUP trả nguyên văn nhóm [S0020,S0021], gộp Item 2 và Item 3, dù đây là hai mục riêng. Kết quả cuối còn 11 heading. Đây là kết quả của một lần Qwen mới, không phải tác động của cache.

## Chi phí và giới hạn

Ba lượt PLACE mới cùng lượt fresh-traced: 0,003916140 USD xác nhận; provisional bằng 0. SELECT mới mất 139,76 giây, gồm 2 lượt SELECT và 1 GROUP; PLACE 1 lượt. Không có Gold heading đã duyệt cho file chuẩn; không tính TP/FP/FN tổng thể. Không sửa parser, prompt, validator hay production defaults trong điều tra này.

Artifacts: reconstructed-first-request.json; fresh-traced-responses.jsonl; validator-replay.json; fresh-traced.json; place-1.json, place-2.json, place-3.json. Source text và model output trong artifacts chỉ dùng làm dữ liệu chẩn đoán.