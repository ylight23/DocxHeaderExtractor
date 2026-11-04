# 074 → 075: thử ghi sau khi đối chiếu ranh giới thủ công

Đây là **demo kiểm nghiệm**, không phải kết quả extraction production tự nhận đủ heading. Outline của hai file được lập thủ công theo nội dung XML của đúng file chuẩn tại `../gather-074-075-manual-boundaries-20261103/`; sáu mục agenda được đưa vào làm ranh giới để kiểm thử, chưa được xác nhận Gold. Văn bản là DOCX tái tạo từ PDF. Danh mục cần kiểm tra bằng hình ảnh/trong Word nếu muốn khẳng định outline đầy đủ.

Kết quả `075_reviewed_demo.docx` là bản sao có tracked insertions, tạo từ các verdict Qwen trong `../gather-074-075-manual-review-20261103/review.json`. Replay kiểm tra SHA-256 của hai file, tên mục, vị trí heading kế tiếp, số ứng viên, text và span của mỗi ứng viên trước khi ghi.

- Nguồn 074 `body[1]/p[4]` được chèn sau nội dung `Opening` của 075 và trước `Present`.
- Nguồn 074 `body[1]/p[6]` được chèn sau nội dung trực tiếp của `Present` và trước `Item 1`.
- Năm đoạn p11–p15 nguồn được Qwen loại khỏi `Present`. Ba dòng danh sách trùng nguyên câu tại đích được loại theo quy tắc trùng và có trong `result.json`.
- Hai file nói về hai cuộc họp ngày 27 và 21 tháng 11 năm 2024. Nội dung lấy từ 074 phải đọc là nguồn của cuộc họp ngày 27; không suy ra đó là sự kiện xảy ra ngày 21. Các dòng chèn có hash nguồn và vị trí; xem kết quả và nguồn trước khi Accept.

File gốc 075 giữ SHA-256 `1e03bd6986c228f2d097877c070b648fee695db70c37f38b01ee3c7948673d46`. Thứ tự đã kiểm tra bằng XML. Lượt review này không đánh giá được recall hay tính ổn định, và không sửa pipeline heading tự động.

Kiểm tra tiếp trong Word 2016: file mở được ở Compatibility Mode, All Markup hiển thị hai đoạn chèn và Reviewing Pane báo **2 revisions**. Đoạn `Opening` xuất hiện ngay trước `Present`; đoạn danh sách `Present` xuất hiện sau câu tóm tắt biên bản và trước `Item 1`, tức đúng ranh giới mục được cung cấp nhưng không nằm sát danh sách người tham dự. Dòng nguồn hiển thị SHA-256/vị trí, không ghi rõ tên file và ngày cuộc họp ngay trong đoạn. Chưa thử Accept All hoặc Reject All trên bản demo này; không khẳng định tương đương việc kiểm tra các thao tác đó.
