# DOCX cache với GROUP production ngày 04/11/2026

Dữ liệu 074 chuẩn, Qwen/OpenRouter thật qua DocumentRunner, từng kết quả được lưu trước khi so. Lần cold bật cache rỗng; warm cùng tiến trình và cache; control độc lập tắt cache. Thời gian toàn `ExtractAsync`, không tính build và không phải SLA tổng hợp ba tài liệu. Không có Gold toàn file đã duyệt. `Governance/Voting Rights.` và `Subsequent Note:` được người dùng xác nhận là heading.

| Lượt | Giây | SELECT/GROUP | PLACE | Input tokens | Output tokens | USD xác nhận |
|---|---:|---:|---:|---:|---:|---:|
| Cold | 66,58 | 2 | 1 | 10.487 | 6.968 | 0,001304138 |
| Warm | 1,99 | 0 | 1 | 2.074 | 150 | 0,000025992 |
| Cache tắt | 66,46 | 3 | 1 | 16.253 | 7.013 | 0,001088527 |

Các token là tổng đã ghi nhận của attempts SELECT/GROUP và PLACE, không phải ước lượng request hoặc token cho toàn bộ luồng gather. Cold/warm cùng bốn heading và quan hệ parent; cả hai thiếu `Subsequent Note:`. Lượt cache tắt độc lập cũng thiếu heading này. Cache không sửa lỗi model. Không suy ra kết luận chất lượng hay độ ổn định parent từ một lần.

Xóa heading `Present:` và di chuyển heading đó sang trước đoạn khác trên hai bản sao: cả hai có source hash khác và phải gọi SELECT/GROUP, không reuse kết quả của file gốc. Bản xóa: 90,66 giây, 4 lượt SELECT/GROUP + 1 PLACE, 21.886 input và 9.701 output tokens; GROUP gộp sai title với Opening. Bản di chuyển: 82,87 giây, 3 lượt SELECT/GROUP + 1 PLACE, 16.793 input và 9.519 output tokens; title được PLACE trả `NONE` (ngoài hierarchy), các heading khác ROOT. Đây là một mẫu mỗi trường hợp; không gán lỗi GROUP/PLACE cho cache. Input bản sửa và artifacts nằm ở `../docx-cache-delete-move-qwen-20261104/`.

Cache SELECT/GROUP trên giao diện mặc định tắt; checkbox mới là opt-in trong trang trích xuất và admin. PLACE vẫn live. File sửa không dùng lại từng đoạn; chưa có ánh xạ outline qua diff, chưa có Gold toàn file, chưa benchmark end-to-end ba báo cáo hay kiểm chứng cải thiện độ chính xác. Các thay đổi chưa commit.

Test liên quan form, UI và cache: 16/16 passed. Chưa chạy full suite lại riêng cho thay đổi UI và harness.
