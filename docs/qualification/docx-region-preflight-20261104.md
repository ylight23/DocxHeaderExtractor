# Thử nghiệm kiểm tra vùng DOCX — 04/11/2026

## Trạng thái

`ExperimentalOutlineRegion` chỉ là hàm thử nghiệm offline; không được route production gọi và không gọi model. Chưa có cờ chạy vùng, chưa nối SELECT/GROUP/PLACE, chưa có phép đo tốc độ hoặc đánh giá chất lượng. Một `REGION` là vùng ứng viên, không xác nhận các quyết định cũ hay quan hệ cha là đúng.

Hàm yêu cầu caller cung cấp hash phụ thuộc trước/sau và xác nhận đủ binding của heading. Hiện **chưa có** cơ chế tạo hoặc xác minh đầy đủ các bằng chứng này. Chỉ so hash styles.xml và numbering.xml sẽ chưa đủ: còn số liệu toàn tài liệu, kết quả tool, các phần heading nhiều đoạn và alias. Thiếu hoặc khác hash, thiếu binding, thay vùng không hỗ trợ, hoặc có đoạn lặp gây mơ hồ thì fallback FULL. Kiểm tra đoạn lặp nay xét sự thay đổi số lượng, sự trùng với heading hoặc sự hiện diện trong phạm vi ảnh hưởng thay vì chặn mọi đoạn lặp ở mọi nơi; đây mới là heuristic chưa chứng minh an toàn trên file thật. Khi có ứng viên, phạm vi mở rộng 20 đoạn; heading trong phạm vi không được đưa vào danh sách giữ nguyên. Quan hệ ngoài vùng vẫn có thể bị ảnh hưởng: cần kiểm tra lại PLACE và toàn cây trước khi sử dụng.

Test `ExperimentalOutlineRegionTests` và `OutlineRegionTests` bao gồm thiếu/thay phụ thuộc, heading chưa đủ binding, vùng chưa hỗ trợ, đoạn lặp, thêm/xóa/di chuyển và ranh giới 20 đoạn. Đây là fixture tổng hợp, chưa chứng minh tính đầy đủ trên file thật hay các nguồn cùng đổi.

## Đối chiếu ba báo cáo thật trên bản sao

`docx-region-probe` thêm một đoạn vào bản sao của mỗi báo cáo. Cả ba phải trả FULL vì chưa xác minh phụ thuộc. Khi **giả định** hai hash phụ thuộc khớp và binding đầy đủ để chỉ khảo sát heuristic đoạn lặp, 08 và 09 trả REGION; 10 trả FULL do đoạn lặp gây nhập nhằng. Giả định này không chứng minh reuse an toàn. Kết quả, hash file và vị trí lưu tại `artifacts/qualification/docx-region-real-probe-v2-20261104/region-probe.json`.

Đã bổ sung `ExperimentalHeadingBindings.Check` để kiểm tra các nguồn của heading, đúng text và span UTF-16; test gồm heading nhiều phần và offset sai. Audit outline đã lưu của 08, 09, 10 cho thấy lần lượt 20/20, 21/21, 14/14 heading không còn bằng chứng parts đầy đủ. `HeadingRecord.SourceParts` và `ValidatedSourcePartCount` có `JsonIgnore`, nên không tồn tại trong JSON lưu outline. Muốn chứng minh heading nhiều đoạn phải thiết kế snapshot binding riêng có version, xác minh được nguồn, và xử lý cache cũ thiếu thông tin; không được đặt `completeHeadingBindings=true` tùy ý. Kiểm tra binding không chứng minh model chọn đủ heading, cũng chưa xác minh style, numbering, facts toàn tài liệu và tool.

Đã thêm prototype snapshot `EXPERIMENTAL_HEADING_BINDINGS_V1`: chỉ tạo khi projection còn đủ source parts, text nguồn khớp và span hợp lệ; lưu mã nguồn, offset và hash của text từng phần kèm hash file. Snapshot có thể deserialize và được kiểm tra lại với cùng file và text. Prototype chưa được nối vào `OutlineStore`, chưa có chuyển đổi cache cũ, chưa chứng minh thay đổi phiên bản nào là an toàn và chưa bảo đảm heading được chọn đủ.

## Đối chứng revert 992ee65

Trong checkout riêng tại `artifacts/qualification/docx-cache-revert-control-20261104`, `git revert --no-commit 992ee65`, với cùng test selector/reuse, 19/19 qua; bản hiện tại trước đó 19/19 qua. Các test không đo Qwen, không chứng minh chất lượng tổng thể và không đo chuyển đổi cache.

Về tương thích, code của 992ee65 lưu entry trực tiếp dưới thư mục selection/grouping theo hash request; bản revert dùng thêm thư mục theo prefix hash nguồn. Hai cách không đọc trực tiếp entry của nhau. Rebuild có thể tiếp tục làm thay đổi khóa phiên bản assembly. Chưa revert trên nhánh chính và chưa khẳng định kế hoạch chuyển đổi cache.

Phần rào chắn file đổi chạy lại toàn bộ đã commit `4d9d625`. Full suite tại lúc kiểm tra đạt 848/849; test `SelectionConcurrencyTests.Out_of_order_scope_completions_preserve_all_aliases_requests_and_decisions` thất bại (13/15) khi suite chạy cùng build đối chứng và qua khi chạy riêng. Chưa xác định nguyên nhân.
