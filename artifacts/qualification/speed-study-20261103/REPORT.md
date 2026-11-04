# Nghiên cứu tốc độ: tổng hợp báo cáo tháng (2026-11-03)

**Dữ liệu:** 3 báo cáo `../monthly-qwen-snapshots-20261103/08.docx`, `09.docx`, `10.docx` (khoảng 47 KB, 109–119 đoạn mỗi file).

**Số liệu model:** lấy từ lượt trích heading đã chạy `../docx-vn-aug-sep-oct-baseline-20261103/*.json` (`routeAudit.SelectionResources`). Lần nghiên cứu này không gọi model.

**Các bước không dùng model:** đo bằng `GatherTrial bench`, bản Release, trung vị 25 lần sau một lần khởi động; số liệu thô ở `bench.json`.

## 1. Thời gian model (qwen/qwen3.7-flash)

| File | Thời gian | Lượt gọi | Lượt dài nhất | Token đầu ra | Phần là suy luận |
|---|---:|---:|---:|---:|---:|
| 08 | 104,3 s | 3 | 78,7 s (10.167 token) | 17.076 | 81% |
| 09 | 83,8 s | 4 | 61,2 s (7.847 token) | 18.787 | 81% |
| 10 | 89,1 s | 3 | 50,3 s (6.224 token) | 14.293 | 81% |

- Tốc độ sinh token khá đều, **108–129 token/giây**, nên thời gian gần như tỉ lệ thuận với số token đầu ra.
- 81% token đầu ra là suy luận, không phải câu trả lời.
- Các đoạn của một file được chọn heading **song song**. Sau đó là một lượt gom nhóm (17–28 s, đầu vào 2,3–2,7 nghìn token).
- Thời gian cả file ≈ đoạn chậm nhất + lượt gom nhóm + vài giây xử lý.

## 2. Các bước không dùng model

| Bước | Trung vị | Ghi chú |
|---|---:|---|
| Hash SHA-256 một file | 0,7 ms | khóa tra cứu theo nội dung |
| Đọc một outline đã lưu (JSON) | 1,0 ms | chi phí của một lần trúng cache |
| Đọc và so hai phiên bản của một file | 31 ms | tìm đúng 5 đoạn được thêm |
| So hai phiên bản đã đọc sẵn | 0,2 ms | |
| Ghép theo chuỗi heading (đích + 2 nguồn) | 161 ms | |
| Ghi bản tổng hợp Track Changes (124 đoạn) | 24 ms | |
| Phát hiện file vừa lưu (FileSystemWatcher) | 1,1 ms | |
| Đọc một file thành đơn vị (riêng lẻ) | 159 ms | dao động 65–231 ms; là bước không-model chậm nhất |

## 3. So sánh các cách chạy

> **Giới hạn (bổ sung sau phản biện, 2026-11-03).** Bảng dưới là phép cộng các bước đo riêng, không phải phép đo end-to-end.
> - `gather_build_chain` chạy trên các file nguồn ban đầu, không chạy trên bản đã thêm 5 đoạn (BenchTrial.cs dòng 65 và 81).
> - Ghép theo chuỗi heading (`GatherByHeading.Content`) chép mọi đoạn dưới heading bằng code. Đó là contract của trang admin, khác pipeline Qwen `GatherScopePipeline` (SELECT, repair, review).
> - Repo chưa có outline cache. Từ 04/11, DOCX lane có cache SELECT/GROUP (`DocxReuseSelection`), mặc định tắt. Cache khóa theo hash cả file nên file đã sửa luôn miss; PLACE vẫn gọi model (xem `../docx-cache-production-qwen-20261104/REPORT.md`).
> - Dùng lại outline cũ cho bản đã sửa còn cần ánh xạ vị trí heading qua diff, vì chèn đoạn làm lệch `body[1]/p[N]`. Phần này chưa làm và chưa đo.
> - SQLite chưa được so tốc độ với JSON. FileSystemWatcher mới đo thời điểm nhận sự kiện, chưa đo chờ ghi xong, gộp sự kiện trùng, SSE và giao diện.

| Kịch bản | Lượt gọi model | Thời gian | Cơ sở |
|---|---:|---:|---|
| Chạy lại từ đầu cả 3 file (song song) | 10 | ≈ 104 s | file chậm nhất, mục 1 |
| Cả 3 file đã có outline lưu sẵn | 0 | ≈ 0,2 s | 3 × 1 ms + 161 + 24 ms |
| 1 file có bản mới, chỉ thêm nội dung dưới heading cũ | 0 | ≈ 0,25 s | + 31 ms so phiên bản |
| 1 file có bản mới, có đoạn nghi là heading mới | 1 lượt nhỏ | ≈ 17–28 s (ước) | cỡ lượt gom nhóm đã đo; chưa chạy thật |
| Báo sự kiện lên màn hình | 0 | ≈ 1 ms + thời gian đẩy | phần đẩy (SSE, cùng máy) chưa đo |

## 4. Kết luận

1. **Nhanh nhất là không gọi lại model khi file không đổi.** Khóa cache phải gồm hash nội dung và các phiên bản parser, prompt, model, route. Khi đó bước trích heading được bỏ qua. Con số "dưới 1 giây" chỉ là ước lượng cho luồng ghép theo heading bằng code, chưa đo end-to-end. Với file đã sửa, chưa có bằng chứng là không cần model.
2. **Phần không-model đã đủ nhanh.** Tổng dưới 0,3 giây, nên tối ưu thêm ở đây không đáng.
3. **Cơ sở dữ liệu không quyết định tốc độ.** Đọc một outline từ file JSON đã chỉ mất 1 ms. SQLite chọn vì dễ quản lý phiên bản, sự kiện và quyết định, không phải vì nhanh hơn. Với vài nghìn heading, vector database không làm nhanh hơn.
4. **Khi buộc phải gọi model:**
   - Gửi **chỉ đoạn thay đổi kèm vài đoạn xung quanh**, không gửi cả file.
   - Mức suy luận là đòn bẩy lớn nhất còn lại (81% token đầu ra). Nhưng bước chọn heading đang cố định bật suy luận ở mức mặc định. Tắt hoặc giảm có thể nhanh hơn khoảng 3–5 lần về lý thuyết, song **chưa biết ảnh hưởng tới độ chính xác**. Muốn làm phải có thí nghiệm riêng có chấm điểm, không thể suy ra từ số liệu này.
   - Chia file thành nhiều đoạn nhỏ hơn để chạy song song sẽ rút ngắn đoạn chậm nhất, đổi lại nhiều lượt gọi hơn và nhiều ranh giới đoạn hơn. Cũng chưa đo.
