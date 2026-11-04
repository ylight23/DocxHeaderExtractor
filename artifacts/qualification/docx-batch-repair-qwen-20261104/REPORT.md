# DOCX 075: thử nghiệm báo nhiều lỗi extent trong một lượt repair

Ngày 2026-11-04. Hai lượt gọi Qwen thật sử dụng cùng đầu vào 075 và cùng SELECT instruction thử nghiệm trim-only, GROUP, PLACE; cache tắt. SHA-256 request SELECT đầu tiên của cả hai là `a6df00d43beb11210ebca1f109f02820b238b159f906f4abd78385c9b03137bb`. Nhánh mới bật option qualification báo tối đa 24 lỗi extent cùng lúc; validator vẫn từ chối phản hồi sai, không thay đổi span.

| Chỉ số | Báo lỗi đầu tiên | Báo nhiều lỗi |
|---|---:|---:|
| Thời gian toàn pipeline (giây) | 232,95 | 156,85 |
| Thời gian SELECT (giây) | 210,87 | 125,09 |
| Phản hồi SELECT | 4 | 2 |
| Lượt repair SELECT | 3 | 1 |
| Heading đầu ra | 17 | 18 |
| Số request provider gồm PLACE | 7 | 5 |
| Chi phí xác nhận (USD) | 0,005345947 | 0,003814349 |

Lượt mới: phản hồi đầu có tám extent có độ dài text và offset khác nhau: S0011, S0021, S0032–S0037. Model trả phản hồi hợp lệ sau một lượt repair, không đổi các quyết định SELECT/NOT_SELECT trong hai phản hồi. `Attachment:Agreed Agenda` xuất hiện ở lượt mới nhưng không có ở lượt cũ. 17 heading chung có parent được đề xuất giống nhau; lượt mới thêm heading thứ 18, vì vậy các request PLACE giữa hai nhánh không hoàn toàn giống nhau. Chưa có Gold được duyệt để khẳng định độ chính xác tổng thể.

Chênh lệch khoảng 76 giây (33%) là quan sát giữa hai lần chạy riêng, không chứng minh tốc độ trung bình hay quan hệ nhân quả vì kết quả và lỗi ban đầu khác nhau. Nhánh mới ban đầu có 8 lỗi, nhánh cũ ban đầu có 12 lỗi. Cần đo nhiều lần với mẫu lỗi tương đồng và thêm tài liệu. Độ ổn định PLACE trước đó vẫn là vấn đề riêng.

Test nhóm HeadingExtent, Architecture và DocxCoreFactsBudget: 44/44 pass. Không chạy full suite. Option mặc định tắt, chưa promote production; thay đổi chưa commit. Selector cache thử nghiệm được tách namespace để không dùng nhầm cache của luồng mặc định.

## Kiểm tra thêm với instruction DOCX production

Một lượt bổ sung ở `../docx-production-batch-repair-qwen-20261104/` dùng instruction production, bật option báo nhiều lỗi. Request SELECT đầu tiên có SHA-256 `b4e8fba457679fa9ed0b162eb6e9b860b5f531359d7ab40eb14234db574675ff`, giống control production 075 trong báo cáo trước.

Phản hồi đầu có sáu extent thừa khoảng trắng. Qwen sửa text trong lượt 2 nhưng còn sai offset S0020 và S0021; lượt 3 mới hợp lệ. Lần chạy mất 285,47 giây (SELECT 211,75 giây; GROUP 19,82 giây; PLACE 46,10 giây), 3 phản hồi SELECT, 11 heading đầu ra; chi phí xác nhận 0,004418907 USD. Các quyết định SELECT trong ba phản hồi đều giữ nguyên. Model không chọn sáu mục agenda S0032–S0037 và `Attachment` S0029.

Control production trước đó, cùng hash request đầu, mất 116,04 giây, 1 phản hồi SELECT và 17 heading. Đây là hai mẫu độc lập với số lỗi và quyết định ban đầu khác nhau, không chứng minh báo nhiều lỗi làm chậm hay gây bỏ sót; nhưng cũng không chứng minh nó tăng tốc với prompt production. Lượt này chứng minh model vẫn có thể tạo lỗi offset mới khi sửa, dù đã nhận nhiều lỗi cùng lúc.
