# Rà soát thiết kế A/B cache — 2026-10-27

## Kết luận

Thiết kế hiện tại chưa đủ để kết luận chắc chắn cả ba câu hỏi. Đây là thử nghiệm thăm dò hợp lý, nhưng cần phân biệt “marker có trong request gửi đi” với “OpenRouter chuyển marker tới Alibaba”, đồng thời đo toàn bộ đường găng và các kết quả giao thức.

## 1. OpenRouter có chuyển tiếp cache marker không?

`OpenRouterWire.Build` gắn `cache_control: ephemeral` vào nội dung system và user; điều này chứng minh request do ứng dụng tạo có marker. `Usage.Check` chỉ đọc `cached_tokens` và `cache_creation_input_tokens` nếu có trong chi tiết usage. Trường vắng mặt hoặc bằng 0 không chứng minh marker bị loại bỏ: cache có thể chưa hit, prefix có thể chưa khớp, hoặc usage không cung cấp trường.

Do đó, thiết kế chỉ cho bằng chứng gián tiếp về hit/cache creation, không quan sát việc OpenRouter chuyển tiếp marker. Cần coi trường usage thiếu là “không xác định”; muốn kết luận forwarding cần bằng chứng ở phía nhận hoặc trace/đầu ra có thể quy kết trực tiếp cho forwarding. Không nên xem `cached_tokens > 0` là điều kiện bắt buộc để marker được chuyển tiếp.

## 2. Warm-up có rút ngắn critical path không?

Arm B thay đổi đồng thời trạng thái cache và lịch chạy: một unit chạy riêng trước, sau đó mới chạy bảy unit song song; Arm A chạy cả tám song song. Đo “POST đầu tiên đến reply cuối” của B có tính warm-up nếu mốc đầu/cuối bao gồm thời gian chờ/lập lịch và tất cả retry. `ToolConversation` đếm POST và retry, nhưng trong mã được xem không có phép đo wall time hay đồng bộ tám cuộc gọi; cần xác nhận bộ chạy ghi nhận đúng các mốc này.

Ba lần lặp trên hai tài liệu khó tách hiệu ứng cache khỏi biến động tải/định tuyến và thứ tự chạy. Thiết kế chưa nêu thứ tự ngẫu nhiên/đối trọng, cách tạo trạng thái cache lạnh, khoảng cách giữa các lần chạy, hay đối sánh cùng tám unit. Payload user thay đổi theo unit; lượt đầu chỉ có thể làm nóng phần prefix dùng chung, không đảm bảo làm nóng toàn packet. P50/P95 `PostDurationsMs` nên tách warm-up và bảy lượt còn lại; thống kê tổng hợp không thay thế wall time trên critical path. Ngưỡng 5% cũng cần quy tắc đủ mẫu/độ bất định.

## 3. Có giữ nguyên ngữ nghĩa production?

C và A giữ nguyên text theo mô tả, nhưng C thay đổi metadata của wire request. Đây là phép thử cấu hình cache, không phải bằng chứng tự thân rằng toàn bộ production semantics được bảo toàn. TP/FP/FN và exact groups hữu ích nhưng không bao phủ lỗi/repair, số vòng tool, retry, trạng thái kết thúc hoặc thành công hội thoại. `ToolConversation` có đếm POST, tool call phát ra/thành công, repair và retry; thiết kế chỉ nêu một phần. Nên báo cáo trạng thái/kết quả theo unit và xác nhận cấu hình còn lại giống nhau qua hash request hoặc diff có kiểm soát.

Gold chỉ đọc sau khi chạy giúp tránh can thiệp trong lúc chạy, nhưng hai tài liệu và ba lần lặp chưa chứng minh tương đương tổng quát. Không nên diễn đạt kết quả là bảo toàn ngữ nghĩa trên toàn sản phẩm nếu chỉ quan sát các chỉ số đã nêu.

## Thiếu đo lường / cách diễn giải

- Tách `0`, thiếu trường và lỗi audit; ghi tỷ lệ response có đủ trường usage.
- Ghi thời gian từ đầu pha SELECT đến phản hồi cuối, thời gian từng POST, retry, trạng thái hoàn tất và kết quả từng unit.
- Ghi thứ tự arm/run, tình trạng cache trước chạy và danh sách unit đối sánh; tăng số lần lặp và báo cáo phân phối/độ bất định.
- Tổng hợp `usage.cost` và token cho cả warm-up, retries và toàn arm; không coi trường cost/cache thiếu là zero.
- Không tuyên bố forwarding chỉ từ cache hit; không tuyên bố latency tốt hơn nếu chỉ thời lượng từng POST tốt hơn mà critical path không đổi; không tuyên bố tương đương production chỉ từ Gold scores.
