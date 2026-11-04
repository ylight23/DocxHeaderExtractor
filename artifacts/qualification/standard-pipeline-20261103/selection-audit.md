# Kiểm tra SELECT trên hai bản chuẩn 074 và 075

Nguồn: response SELECT thực tế trong hai file `.log` và `routeAudit.sourceBlocks` trong hai file `.heading.json` cùng thư mục. Các nhãn kỳ vọng dưới đây là phạm vi thử nghiệm do người dùng mô tả; không phải Gold độc lập đã duyệt. Không dùng Gold làm đầu vào model.

## 075

Model trả `status: COMPLETE` cho 38 alias; đây là trạng thái protocol, không bảo đảm recall. Các dòng dưới đều có trong `sourceBlocks` và response SELECT đã trả `NOT_SELECT`:

| Alias | Vị trí | Text đầu | Trạng thái cần xem |
|---|---|---|---|
| S0005 | body[1]/p[6] | Present: | Bỏ sót theo yêu cầu |
| S0011 | body[1]/p[12] | Item 1: Document Approval. | Bỏ sót phần heading theo yêu cầu |
| S0020 | body[1]/p[21] | Item 2: Operating Context. | Bỏ sót phần heading theo yêu cầu |
| S0021 | body[1]/p[22] | Item 3: Strategic Directions. | Bỏ sót phần heading theo yêu cầu |
| S0025 | body[1]/p[26] | Item 4: Work Plan, Budget, and Fees. | Bỏ sót phần heading theo yêu cầu |
| S0026 | body[1]/p[27] | Item 5: Communications Strategy. | Bỏ sót phần heading theo yêu cầu |
| S0027 | body[1]/p[28] | Item 6: Appointment of the Chair. | Bỏ sót phần heading theo yêu cầu |
| S0029 | body[1]/p[30] | Attachment:Agreed Agenda | Bỏ sót theo phạm vi báo cáo; cần xác nhận chính sách |
| S0032–S0037 | body[1]/p[33]–p[38] | Các mục 1–6 trong agenda | Bị loại; cần duyệt từng occurrence, không suy từ số thứ tự |

Phần `analysis` trong response công khai của model nói các dòng `Item X` là body được đánh số; instruction thực tế chỉ loại body *chỉ vì* giống heading, đồng thời cho phép chọn một phần của đoạn làm heading. Không suy ra prompt hay model là nguyên nhân duy nhất. Cần đối chứng cùng đầu vào; không thêm luật riêng cho tên hoặc định dạng cụ thể.

## 074

SELECT chọn S0010 `Governance/Voting Rights.` và S0012 `Subsequent Note:` với heading extent. Theo phạm vi đã ghi trong `docs/qualification/standard-test-documents.md`, đây là hai mục chọn thừa; model thực sự đề xuất các extent này trong response. SELECT cũng chọn S0002 `Opening:` và S0004 `Present:`. Một luật cấm nhãn in đậm đầu đoạn sẽ có thể loại cả hai mục mong muốn.

## Ghép và vị trí

Thử nghiệm `gather-by-name` ghép đúng một tên `Opening`, không phải ghép ngữ nghĩa. Kết quả ghi phần nguồn `074 body[1]/p[4]` vào file đích. Do outline 075 thiếu các heading tiếp theo, `NextHeadingLocation` là `body[1]/p[31]` (tiêu đề AGENDA). Kiểm tra XML file kết quả cho thấy đoạn mới nằm sau `Attachment:Agreed Agenda`, ngay trước AGENDA, không nằm sát phần khai mạc.

Hai file ghi ngày hai cuộc họp khác nhau; cùng tên mục không chứng minh thông tin về sự kiện này có thể được nhập vào sự kiện kia. Cần giữ ngày/sự kiện nguồn và review trước khi chấp nhận.

## Giới hạn

- Các file chuẩn là DOCX tái tạo từ PDF bằng converter v2, không phải Word gốc của tác giả; chỉ trang đầu đã được so trực quan.
- Một lần chạy và bộ tham chiếu chưa duyệt không đủ kết luận ổn định hoặc điểm precision/recall.
- Bản chuẩn 075 tách `Opening:` và câu sau thành hai đoạn, nên không chứng minh chức năng tách heading/content trong cùng đoạn. Cần fixture cùng đoạn và span hợp lệ/không hợp lệ.
- Không có call provider mới trong audit này.
