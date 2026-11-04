# Thử SELECT tiêu chí tổng quát trên 074 và 075

Trạng thái: thử nghiệm, **không promote**. Không dùng Gold hoặc tên case trong instruction. Nhánh thử giữ `DocxSelector` và bổ sung hướng dẫn tổng quát: đánh giá chức năng của span tách khỏi phần còn lại của paragraph; không quyết định chỉ vì đoạn chứa prose, đánh số hoặc định dạng. Instruction gốc và phần bổ sung được băm trong `run-identity.json` của mỗi run. Baseline là các artifact `*.heading.json` và `.log` trong thư mục này.

| Tài liệu | Baseline SELECT | Nhánh thử SELECT | Thời gian nhánh thử | Provider calls | Phí xác nhận |
|---|---:|---:|---:|---:|---:|
| 075 chuẩn | 4 | 12 | 301,7 s | 8 | 0,006942658 USD |
| 074 chuẩn | 5 | 4 | 54,8 s | 2 | 0,000959481 USD |

075 nhánh thử giữ bốn alias cũ, thêm S0005 `Present:`, S0011 `Item 1`, S0020 `Item 2`, S0021 `Item 3`, S0025 `Item 4`, S0026 `Item 5`, S0027 `Item 6`, S0029 `Attachment`. S0032–S0037 là sáu mục đánh số trong agenda vẫn không được chọn; cần người duyệt xác nhận từng occurrence là heading hay body theo phạm vi đầu ra.

074 nhánh thử giữ S0001, S0002, S0004, S0010. S0012 `Subsequent Note:` không còn được chọn, nhưng S0010 `Governance/Voting Rights.` vẫn được chọn dù phạm vi thử hiện coi là không phải heading. Vì thế chưa đạt tiêu chí theo phạm vi đó.

075 nhánh thử: response đầu đã chọn 12 alias nhưng sáu `headingExtent` có end vượt một ký tự so với text đã nêu; validator từ chối. Mỗi lượt repair trả lại toàn bộ response và xử lý một extent, nên cần bảy response SELECT và một response GROUP. Validator không tự sửa offset. Đây là nguyên nhân trực tiếp của nhiều lượt nối tiếp trong run này, không chứng minh mọi tài liệu đều như vậy.

So sánh có giới hạn: cùng hash nguồn và route DOCX, nhưng artifact baseline không lưu đầy đủ request để chứng minh hai packet chỉ khác instruction. Mỗi nhánh chỉ một run, không có Gold đã duyệt và không đánh giá hierarchy/placement cuối. Không suy ra độ ổn định hay chính xác toàn production.

Chi tiết: `../docx-075-span-selection-generic-20261103/selection.json`, `../docx-074-span-selection-generic-20261103/selection.json` và response JSONL cùng thư mục mỗi run.

**Đính chính ngày 2026-11-04:** Người dùng xác nhận `Governance/Voting Rights.` và `Subsequent Note:` trong 074 là heading. Những nhận xét trước đây trong báo cáo này coi hai mục đó là không phải heading không còn áp dụng.
