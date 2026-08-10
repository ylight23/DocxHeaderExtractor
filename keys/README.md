# Đáp án do người dùng xác nhận

Ba tài liệu thật, đáp án **do người kiểm**, không phải do model gán. Đây là nền của mọi con số
trong `handoff.md` từ §41 trở đi — TODO mục 4 (thắt cổ chai của cả dự án) bắt đầu đóng ở đây.

| file | tài liệu | mục | chế độ | đã chấm |
|---|---|--:|---|---|
| `kltn-style.key` | khoá luận VTV3 | 68 | style-declared | **P/R/cấp/cha = 100%** |
| `bao-cao-thuc-tap.key` | báo cáo thực tập MBBank | 29 | numbering-driven | **P/R/cấp/cha = 100%** |
| `plph1-dqp.outline` | thiết kế phân hệ ĐQP | 41 | vn-administrative | chưa — thiếu file .docx |

## Vì sao ba luật khác nhau

| | chọn mục | cấp |
|---|---|---|
| khoá luận | style Heading | độ sâu số gõ tay + 1 |
| báo cáo thực tập | cặp `(numId, ilvl)` | `ilvl + 1` |
| thiết kế phân hệ | ký hiệu gõ tay `I./1./3.1./a)` | **cha gần nhất + 1** |

Đúng nguyên tắc N1 của spec: *"Không tồn tại một luật deterministic dùng chung."* Ba tài liệu thật,
ba luật, và mỗi luật áp nhầm sang tài liệu kia đều hỏng — đã đo (§42, §43).

## Tài liệu mật

Một tài liệu thử nghiệm đóng dấu MẬT đã được **khử hoàn toàn**: không có bản sao trong repo,
không trong scratchpad, không có đáp án hay nội dung nào của nó được lưu lại. Bản gốc chỉ còn ở
thư mục Downloads của người dùng.

Nếu cần thể loại đó trong tập test lâu dài, phải dùng bản **đã khử nội dung**: giữ nguyên khung
`I./1./3.1./a)`, thay mọi số liệu và danh từ riêng bằng dữ liệu giả. Cấu trúc heading — thứ duy
nhất pipeline cần — không đổi, còn rủi ro thì biến mất, và tập test chia sẻ được.
