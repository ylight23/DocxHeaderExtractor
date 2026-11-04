# DOCX cache — đối chứng Qwen cache tắt (2026-11-03)

Chạy lại 074/075 chuẩn qua DocumentRunner với DocxReuseSelection=false, sau các lần cold/warm. Kết quả là một mẫu Qwen độc lập; so sánh không thể tách riêng biến thiên model và ảnh hưởng cache như một phép đo định tính có Gold.

| File | Cache | Giây | SELECT/GROUP | PLACE | Heading | USD xác nhận | USD tạm giữ chưa xác nhận |
|---|---|---:|---:|---:|---:|---:|---:|
| 074 | tắt | 80,92 | 3 | 1 | 5 | 0,001585170 | 0 |
| 075 | tắt | 176,70 | 4 | 2 | 17 | 0,003302072 | 0,018454800 |

- 074: xuất hiện Governance/Voting Rights. và Subsequent Note:, hai nhãn đã được người duyệt xác định không phải heading. Lượt cache cold có 4 heading, thiếu riêng Subsequent Note: so với đối chứng tắt cache.
- 075: thiếu Attachment:Agreed Agenda so với 18 heading của cache cold/warm. Trong SELECT cuối, S0029 được NOT_SELECT; phần analysis của Qwen xếp S0029 vào cross-reference. Đây là lý do được model trả, không phải kết luận đúng về cấu trúc.
- 075 có một STREAM_ERROR với HTTP 200 và provider error 502 ở SEG-000 turn 2, accounting BILLING_UNKNOWN. 0,0184548 USD là khoản tạm giữ, không phải chi phí đã xác nhận. Lượt xử lý vẫn hoàn tất sau khi thử lại.
- Không có Gold heading được duyệt cho 074/075 chuẩn, nên không báo TP/FP/FN. Các lượt Qwen độc lập có thể biến thiên; không thể quy thay đổi vào riêng cache.

Chi phí xác nhận của hai lượt control là 0,004887242 USD. Kết quả gốc từng lượt nằm trong các file control.json cùng thư mục. Lượt cold/warm và các ca bản sửa lần lượt ở ../docx-cache-ab-qwen-20261103/REPORT.md và ../docx-cache-changes-qwen-20261103/REPORT.md.
**Đính chính 04/11/2026:** Người dùng xác nhận `Governance/Voting Rights.` và `Subsequent Note:` trong 074 là heading; các nhận xét phía trên gọi hai mục này là heading giả không còn áp dụng. Số đo cache và response thô không đổi.
