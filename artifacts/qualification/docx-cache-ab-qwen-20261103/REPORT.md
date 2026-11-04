# DOCX cache SELECT/GROUP — đối chứng Qwen (2026-11-03)

Chạy qua DocumentRunner và OpenRouter/Qwen thật. Cache bật, ban đầu rỗng; mỗi file chạy lượt cold rồi warm trong cùng chương trình. Hai input là 074 và 075 chuẩn, hash khớp docs/qualification/standard-test-documents.md. Mỗi lượt lưu kết quả ngay trước khi đối chiếu.

| File | Lượt | Giây | Request SELECT/GROUP | Request PLACE | Heading | Chi phí xác nhận USD |
|---|---|---:|---:|---:|---:|---:|
| 074 | cold | 73,24 | 3 | 1 | 4 | 0,001628742 |
| 074 | warm | 1,78 | 0 | 1 | 4 | 0,000025992 |
| 075 | cold | 518,92 | 11 | 2 | 18 | 0,012495287 |
| 075 | warm | 5,39 | 0 | 2 | 18 | 0,000201778 |

Tổng 20 lần transport được ghi nhận, 0,014351799 USD xác nhận; provisional/outstanding bằng 0 theo ResourceBudget. Thời gian là từ DocumentRunner.ExtractAsync; không tính build. Các trường selectionUnitsComplete và selectionUnitsTotal trong bốn JSON là số dòng được trả lời, không phải số segment; tên trường harness đã được sửa thành selectionLinesAnswered/selectionLinesTotal cho lần chạy sau.

## So sánh

- 074: từng quyết định SELECT/NOT_SELECT, grouping và quan hệ cha khớp hai lượt. Có heading giả Governance/Voting Rights. đã biết trong cả hai lượt; không coi đây là kết quả chính xác.
- 075: từng quyết định SELECT/NOT_SELECT và grouping khớp hai lượt. PLACE được gọi lại và thay đổi quan hệ cha của sáu mục 1–6: lượt cold đặt dưới Agenda Items (body p32, level 3), lượt warm đặt trực tiếp dưới AGENDA for FIRST MEETING (body p31, level 2). Các khác biệt được ghi trong 075-comparison.json.
- 075 trả 18 heading ở hai lượt, gồm Present, Attachment, Item 1–6 và các mục đánh số 1–6. Chưa có Gold heading được duyệt cho hai bản chuẩn nên không báo TP/FP/FN.
- Lượt cold 075 có 9 request tuần tự cho SEG-000, 0 tool call, 8 repair; RepairedFrom ghi PROTOCOL:HEADING_EXTENT_EDGE_WHITESPACE. Không có transcript từng repair để xác định mọi lượt cùng lỗi. GROUP có 2 request, 1 repair.
- Cache warm bỏ toàn bộ SELECT/GROUP trong hai lần chạy này. PLACE vẫn live; do đó cache chưa đảm bảo outline cuối ổn định.

## Giới hạn

Đây là cold và warm khi bật cache, chưa có lượt độc lập cache tắt. Chưa thử Qwen trên bản thêm nội dung, thêm heading, sửa/xóa/di chuyển heading; cache hiện ràng buộc hash cả file nên file thay đổi sẽ miss. Hai file nhỏ không đại diện toàn bộ DOCX. Chưa đo chất lượng theo Gold, chưa đủ điều kiện promote mặc định.
## Đối chiếu request PLACE

Ở 075, request HIERARCHY_PLACE-001 cold/warm có cùng SHA-256 cee34df5bccd1c5e742c2044d13e8d0492800b05e952ff4d8bcccfa2e45d74d2. Request này xử lý S0032–S0035 trong số 16 heading và đưa ra cha khác nhau giữa hai lượt: bằng chứng model trả quyết định khác với cùng request. HIERARCHY_PLACE-002 có hash khác giữa hai lượt nên không thể kết luận riêng hai quyết định cuối thay đổi dưới cùng input. SELECT/GROUP vẫn khớp và không chạy lại ở warm.
PLACE dùng batch tuần tự, batch sau nhận quan hệ đã đề xuất trước đó (HeadingPlacer.cs). Trong batch đầu cùng request, S0032–S0035 đã đổi cha; batch sau mang input đã thay đổi, vì vậy hai thay đổi S0036–S0037 không thể quy riêng cho biến thiên model trên cùng request. Bộ kiểm tra hiện bảo đảm quan hệ hợp lệ theo đồ thị, chưa xác nhận cha đúng về nghĩa.
## Lượt độc lập cache tắt

074: 80,92 giây, 3 request SELECT/GROUP + 1 PLACE, 5 heading. 075: 176,70 giây, 4 SELECT/GROUP + 2 PLACE, 17 heading; SELECT loại S0029 Attachment:Agreed Agenda và gọi đó là cross-reference. Xem ../docx-cache-control-qwen-20261103/REPORT.md để xem chi phí và một lỗi stream 502 có billing chưa xác nhận.

Tổng toàn bộ 4 lượt cold/warm, 3 lượt bản sửa và 2 lượt control: 42 transport attempt; 0,024484478 USD được xác nhận. Ngoài ra có 0,0184548 USD tạm giữ do một attempt billing unknown, không coi là chi phí thực tế.
**Đính chính 04/11/2026:** Người dùng xác nhận `Governance/Voting Rights.` và `Subsequent Note:` trong 074 là heading; các nhận xét phía trên gọi hai mục này là heading giả không còn áp dụng. Số đo cache và response thô không đổi.
