# Đối chứng contract ghép theo heading: 074 → 075

Thử nghiệm trên DOCX chuẩn tái tạo từ PDF, dùng các outline đã lưu `074v2.json` và `075v3.json`; đây không phải outline được xác nhận đầy đủ. Không ghi nội dung vào tài liệu đích. `review.json` lưu SHA-256 hai file, các ứng viên, span/nguồn, kết quả review và telemetry provider.

Ghép tên cho hai mục `Opening:` và `Present:`. Khi chỉ dùng khoảng giữa các heading, `Present:` có 6 ứng viên: danh sách Governing Committee và 5 đoạn p11–p15 mô tả mục đích cuộc họp, thảo luận, xác nhận và việc tiếp theo. Ba dòng người tham dự khác trùng nguyên nội dung đã có ở đích nên được ghi trong danh sách excluded.

Qwen qua OpenRouter review 7 ứng viên trong 2 request: giữ câu khai mạc p4 và danh sách p6; loại p11–p15 khỏi `Present:` với lý do không thuộc danh sách người tham dự. Tổng phí được provider xác nhận 0,000427258 USD; thời gian hai request khoảng 5,3 và 21,8 giây (không phải wall time toàn pipeline).

Đây là một lần thử, không chứng minh recall, không chứng minh ổn định, không chứng minh toàn bộ outline đúng. Outline 075v3 bỏ sót nhiều heading; vị trí kết thúc `Present:` suy ra từ outline là sai. Hai tài liệu còn mô tả hai cuộc họp khác ngày. Verdict KEEP chỉ nói nội dung phù hợp tên mục trong thử nghiệm; không xác nhận hai sự kiện là một. Không dùng kết quả này làm căn cứ tự động ghi vào 075.
