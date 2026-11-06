# Luồng người dùng cho tổng hợp văn bản

Trạng thái: đặc tả, chờ chủ dự án chốt các câu hỏi ở mục 8. Ngày 05/11/2026.

## 1. Mục tiêu

Người dùng chỉ làm bốn việc: tải văn bản lên, bấm tổng hợp, xử lý các mục chưa rõ (không bắt buộc), xuất văn bản.
Mọi việc còn lại do backend tự làm: trích outline, xếp cấp, ghép theo chuỗi heading, chạy lại khi lỗi, quyết định giữ hay ghép.
Người dùng không bao giờ thấy hay duyệt outline, cấp heading, quan hệ cha – con, alias hoặc mã kỹ thuật.

Nguyên tắc không đổi:

- Mục chưa rõ không bao giờ được ghi vào một vị trí đoán. Nó chỉ được ghi khi người dùng chọn vị trí.
- Mục chưa rõ không bao giờ bị bỏ âm thầm. Nếu người dùng không chọn, nó vào phụ lục của file xuất.
- Gợi ý của model chỉ hiển thị để tham khảo, không tự áp dụng.
- Tự động không có nghĩa là chắc chắn. SELECT có thể bỏ sót heading, GROUP có thể gộp sai, mà cây PLACE vẫn hợp lệ về cấu trúc. Bỏ bước duyệt outline không biến kết quả ghép thành đúng. Backend chỉ ghi vào thân văn bản phần mà chuỗi heading xác định được vị trí; mọi phần không xác định được vị trí phải vào nhóm mục chưa rõ hoặc phụ lục.
- Không mất nội dung. Mỗi đoạn của mỗi nguồn phải kết thúc ở đúng một trong các trạng thái ở mục 5, có nguồn và lý do.

## 2. Luồng bốn bước

| Bước | Người dùng làm | Backend làm |
| --- | --- | --- |
| 1. Tải lên | Chọn một văn bản đích và một hoặc nhiều văn bản nguồn | Kiểm định dạng, tính hash, không sửa file gốc |
| 2. Tổng hợp | Bấm "Tổng hợp", xem tiến độ | Trích outline (dùng lại outline đã lưu nếu file không đổi), ghép phần chắc chắn, gom mục chưa rõ, model gợi ý cho mục chưa rõ |
| 3. Mục chưa rõ | Với từng thẻ: chọn vị trí, tạo mục mới, hoặc bỏ qua. Có thể bỏ qua cả bước này | Lưu lựa chọn theo lượt tổng hợp |
| 4. Xuất | Bấm "Xuất", tải file Word | Ghi phần chắc chắn và các lựa chọn của người dùng; đưa mục chưa chọn vào phụ lục |

## 3. Backend tự động thay cho bước duyệt outline

- Không còn rào "outline đích phải được duyệt" trước khi xuất, và không còn giữ nguồn vì outline nguồn "chưa duyệt" (`SOURCE_OUTLINE_REVIEW_REQUIRED`).
- Nguồn chỉ bị giữ theo điều kiện cấu trúc tự kiểm được, ví dụ heading chưa có cấp (`UNCONFIRMED_HIERARCHY`). Chỉ giữ nguồn có lỗi, không giữ các nguồn khác.
- Khi một nguồn bị giữ vì cấu trúc, backend được phép chạy lại trích outline cho riêng nguồn đó, có giới hạn số lần và chi phí (xem mục 8). Nếu vẫn không được, toàn bộ nội dung nguồn đó vào phụ lục kèm một dòng thông báo.
- Công cụ sửa cha và duyệt outline trong trang admin là công cụ nội bộ. Nó không nằm trong luồng người dùng và không là điều kiện để xuất.

## 4. Màn hình mục chưa rõ

Mỗi mục là một thẻ gồm:

- Nội dung các đoạn (đầy đủ, có thể thu gọn) và nguồn (tên file).
- Lý do bằng lời thường (bảng dưới).
- Các vị trí ứng viên trong văn bản đích: tên mục, đường dẫn mục cha, vài dòng nội dung hiện có của mục đó.
- Gợi ý của model nếu có, ghi rõ là gợi ý.
- Ba nút: **Đặt vào mục đã chọn**, **Tạo mục mới**, **Bỏ qua**. Mặc định chưa chọn.

Mã kỹ thuật chuyển thành lời thường:

| Mã | Lời hiển thị | Lựa chọn cho phép |
| --- | --- | --- |
| `NEAR_NAME` | Tên mục ở văn bản nguồn hơi khác tên mục trong văn bản đích | Đặt vào, Tạo mục mới, Bỏ qua |
| `PATH_DIFFERS` | Cùng tên mục nhưng nằm dưới mục lớn khác | Đặt vào, Bỏ qua |
| `NEW_HEADING_REVIEW` | Mục này chưa có trong văn bản đích | Tạo mục mới, Đặt vào, Bỏ qua |
| `AMBIGUOUS` | Văn bản đích có nhiều mục cùng tên | Đặt vào (bắt buộc chọn một), Bỏ qua |
| `PARENT_UNCERTAIN` | Chưa xác định được mục lớn chứa mục này | Đặt vào, Bỏ qua |
| `MOVED` | Mục này có thể đã được chuyển sang chỗ khác trong văn bản đích | Đặt vào, Bỏ qua |
| `UNCONFIRMED_HIERARCHY` | Chưa đọc được cấu trúc của văn bản nguồn này | Không có lựa chọn; nội dung vào phụ lục |

Mã kỹ thuật không hiển thị cho người dùng. Có thể giữ trong thuộc tính ẩn để gỡ lỗi.

Phần đầu màn hình cho biết: bao nhiêu đoạn đã ghép tự động, bao nhiêu mục chưa rõ, bao nhiêu mục đã chọn. Nút "Xuất" luôn bấm được, kể cả khi còn mục chưa chọn.

## 5. File xuất

Mỗi đoạn của mỗi nguồn kết thúc ở đúng một trạng thái:

| Trạng thái | Ở đâu trong kết quả | Ghi kèm |
| --- | --- | --- |
| Ghép tự động | Thân văn bản, tại mục mà chuỗi heading xác định | Nguồn; được đánh dấu là ghép tự động (cách đánh dấu: mục 8) |
| Ghép theo lựa chọn của người dùng | Thân văn bản, tại mục người dùng chọn | Nguồn |
| Mục chưa rõ chưa được chọn | Phụ lục "Nội dung chưa ghép được" | Nguồn, vị trí trong nguồn, lý do bằng lời thường, nội dung nguyên văn |
| Người dùng chọn "Bỏ qua" | Phụ lục hoặc danh sách riêng (mục 8) | Như trên |
| Nguồn bị giữ vì lỗi cấu trúc | Phụ lục, toàn bộ nội dung nguồn đó | Như trên, lý do "chưa đọc được cấu trúc của văn bản nguồn" |
| Phần nguồn không đọc được (vùng không hỗ trợ, khoảng trống khi đọc) | Phụ lục | Nguồn, vị trí, lý do; ghi nguyên văn nếu đọc được chữ |
| Không ghi được vào vị trí dự kiến khi tạo file (ví dụ heading đích không tìm thấy lúc ghi) | Phụ lục | Nguồn, vị trí dự kiến, lý do |
| Đã có y hệt trong đích hoặc đã lấy từ nguồn khác | Danh sách "Không thêm" trong báo cáo, không lặp trong phụ lục | Nguồn, vị trí, lý do |
| Nội dung trong bảng của nguồn, ở bất kỳ vị trí nào (chủ dự án chốt 06/11/2026: không xử lý bảng) | Danh sách "Không thêm", không ghép, không vào phụ lục | Nguồn, vị trí, lý do "không xử lý bảng" |
| Đoạn văn trước mục đầu tiên của nguồn trùng vị trí và trùng hoàn toàn chữ (kể cả chữ số; chỉ bỏ qua hoa/thường và khoảng trắng) với đoạn tương ứng của văn bản đích | Danh sách "Không thêm", không vào phụ lục | Nguồn, vị trí, lý do |
| Đoạn văn trước mục đầu tiên của nguồn trùng vị trí và chữ, chỉ khác chữ số (có thể là kỳ báo cáo mới hoặc số liệu đổi; chương trình không phân biệt được) | Phụ lục, nhãn "chỉ khác chữ số; cần xem số" | Nguồn, vị trí, lý do |
| Đoạn văn trước mục đầu tiên của nguồn không thỏa điều kiện trên (cùng vị trí nhưng khác chữ, hoặc đích không có đoạn ở vị trí đó) | Phụ lục, ghi "phần đầu văn bản nguồn" | Nguồn, vị trí, lý do |

- Phụ lục và danh sách liệt kê từng mục, không chỉ đếm số lượng.
- Có dùng Track Changes cho phần được chèn hay không: xem mục 8.
- Không sửa file gốc. File xuất là bản mới.

## 6. Thay đổi code cần làm

1. Bỏ rào duyệt outline đích ở bước xuất và bỏ giữ nguồn theo `SOURCE_OUTLINE_REVIEW_REQUIRED` khỏi luồng người dùng.
2. Giữ nguồn chỉ theo điều kiện cấu trúc, theo từng nguồn.
3. Thêm phụ lục cho mục chưa chọn trong bước ghi file.
4. Bảng chuyển mã sang lời thường dùng chung cho giao diện và phụ lục.
5. Luồng người dùng chạy trên `admin.html`. Phần hiển thị chính chỉ còn bốn bước: tải lên, tổng hợp, mục chưa rõ (dùng lại thẻ, lựa chọn và endpoint `apply` đã có), xuất.
6. Các phần không thuộc luồng người dùng trên `admin.html` (duyệt outline, sửa cha, bằng chứng PLACE, các ô tùy chọn như không gọi model, dùng lại SELECT/GROUP, outline JSON tải lên) được bỏ khỏi luồng chính: gỡ hẳn hoặc gom vào một khu "Công cụ nội bộ" thu gọn mặc định. Không bước nào trong luồng chính phụ thuộc vào chúng.

## 7. Tiêu chí nghiệm thu

- Từ lúc tải lên đến khi có file xuất, người dùng không cần thao tác nào ngoài tải lên, tổng hợp và xuất.
- Không mục chưa rõ nào được ghi vào thân văn bản nếu người dùng chưa chọn vị trí.
- Không mất nội dung: với mỗi nguồn, mỗi đoạn có đúng một trạng thái trong bảng ở mục 5. Kiểm theo từng đoạn (vị trí trong nguồn), không chỉ đếm tổng: không đoạn nào thiếu trạng thái, không đoạn nào có hai trạng thái.
- Phần không ghi được khi tạo file (ví dụ heading đích không tìm thấy) xuất hiện trong phụ lục, không chỉ trong log.
- Một nguồn lỗi cấu trúc không làm giữ lại các nguồn khác.
- File gốc không đổi hash sau khi tổng hợp và xuất.
- Không mã kỹ thuật nào hiển thị trên giao diện người dùng hoặc trong phụ lục.

## 8. Câu hỏi chờ chủ dự án chốt

1. (Đã chốt 05/11/2026) Dùng `admin.html`, không tạo trang mới. `index.html` giữ nguyên.
2. Mục "Bỏ qua" có vào phụ lục không, hay chỉ mục chưa chọn mới vào?
3. Phần chèn vào văn bản có dùng Track Changes không? Nếu không, phần ghép tự động được đánh dấu bằng cách nào để người dùng biết đoạn nào do máy đặt (vì tự động không có nghĩa là chắc chắn)?
4. Khi nguồn lỗi cấu trúc, backend được chạy lại tối đa mấy lần và trong mức chi phí nào?
5. Có giữ bước model gợi ý cho mục chưa rõ không? Bước này tốn thêm một lần gọi model mỗi lượt tổng hợp.

## 9. Trạng thái hiện tại (05/11/2026, chưa commit)

Đã làm trên `admin.html` và backend:

- Bỏ rào duyệt outline đích khi xuất và bỏ giữ nguồn vì outline nguồn chưa duyệt. Nguồn chỉ bị giữ theo kiểm tra cấu trúc, theo từng nguồn.
- `GatherWriteback.Export`: ghi phần chuỗi heading xác định được dưới dạng Track Changes kèm nguồn; phụ lục cuối văn bản liệt kê từng mục chưa ghép, mục người dùng bỏ qua, mục không ghi được vào vị trí dự kiến, phần nguồn không đọc được, kèm nguồn, vị trí và lý do bằng lời thường.
- Lượt tổng hợp tạo file xuất ngay. Lựa chọn của người dùng tạo bản mới; mục chưa chọn vẫn ở phụ lục.
- Trang admin: luồng chính là chọn file, TỔNG HỢP, thẻ "Chưa rõ vị trí", tải bản tổng hợp. Duyệt outline, thay đổi giữa các lượt, heading đã trích và log là thẻ nội bộ, ẩn mặc định.
- Sửa hai lỗi của công cụ nội bộ: sửa cha làm đổi cha của heading khác thì bị từ chối (`OTHER_PARENTS_WOULD_CHANGE`); con trỏ phiên bản sửa hỏng thì báo lỗi, không âm thầm trích lại.

Mặc định đã chọn cho câu hỏi ở mục 8, chờ chủ dự án xác nhận: (2) mục "Bỏ qua" vẫn vào phụ lục, có ghi "Người dùng chọn bỏ qua"; (3) mọi phần chèn dùng Track Changes; (4) chưa có tự chạy lại khi nguồn lỗi cấu trúc, nguồn đó vào phụ lục; (5) giữ bước model gợi ý như trước, là tùy chọn nội bộ.

Chưa làm: kiểm tra trên trình duyệt thật; kiểm "mỗi đoạn đúng một trạng thái" trên tài liệu thật (mới có test trên tài liệu tổng hợp nhỏ).
