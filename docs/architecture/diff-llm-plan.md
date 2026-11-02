# Kế hoạch: đối chiếu tài liệu Word với bản mẫu

Trạng thái: P0 đang triển khai một phần (`Sources/Docx/Changes/`). Đã có: đọc text gốc, vị trí theo `ParagraphWalker`, định dạng khai báo trực tiếp, định dạng hiệu lực từ style, và danh sách vùng chưa hỗ trợ. Chưa chốt: chính sách hiển thị khoảng trắng, xuống dòng, Unicode và cách hiển thị định dạng. P1 thử nghiệm (ghép đoạn, diff chữ) đang triển khai tại `DocumentProcessing/Changes/`. Phạm vi cập nhật: 2026-11-02.

## 1. Mục tiêu và phạm vi

Đầu vào của mỗi lần so sánh gồm hai file DOCX:

1. **Bản mẫu:** tài liệu làm mốc đối chiếu.
2. **Tài liệu cần kiểm tra:** tài liệu được so với bản mẫu.

Kết quả phải chỉ ra nội dung **thêm, thiếu hoặc thay đổi** so với bản mẫu, kể cả thay đổi chữ trong một đoạn, mục danh sách và heading. Phải so sánh **cả nội dung chữ và định dạng**, đưa ra giá trị ở hai phía và vị trí tương ứng khi xác định được. Người dùng tự đánh giá ý nghĩa của khác biệt; hệ thống không kết luận đúng/sai hoặc được phép/không được phép.

Nếu chưa ghép được hai đoạn hoặc chưa xác định được vị trí, heading liên quan, phải ghi rõ trạng thái chưa chắc chắn. Không giấu khác biệt chỉ vì không xác định được heading.

Ngoài phạm vi trước mắt: tự động sửa tài liệu; theo dõi lịch sử phiên bản; xử lý Track Changes như đường so sánh riêng; theo dõi thư mục, OneDrive hoặc SharePoint; phát sự kiện/outbox; đánh giá nội dung đặt đúng chương hay không; phân loại khác biệt thành đáng kể hoặc được phép.

## 2. Nguyên tắc

- **Code thực hiện đối chiếu cơ bản:** đọc hai file, ghép nội dung, tìm khác biệt chữ và định dạng, xác định vị trí và tạo kết quả có thể kiểm tra lại.
- **LLM là tùy chọn:** có thể hỗ trợ ghép những đoạn khó đối chiếu hoặc giải thích khác biệt. Đề xuất của LLM phải được đánh dấu là đề xuất, kèm bằng chứng; kết quả luôn giữ nguyên văn từ hai tài liệu. Không dùng LLM để quyết định khác biệt là đúng hay sai.
- **Không mặc định ghép chắc chắn:** nội dung lặp, đoạn tách/gộp hoặc thay đổi lớn có thể có nhiều cách ghép. Ghi nhận chưa chắc chắn thay vì tự xác nhận.
- **Gold chỉ dùng để đánh giá**, không đưa vào quá trình đối chiếu thực tế.
- Kế hoạch mô tả hành vi cần xây dựng, không khẳng định code hiện tại đã có tính năng diff.

## 3. Đọc và biểu diễn hai tài liệu

Parser Word hiện có đọc cấu trúc đoạn và thông tin định dạng. Mỗi phía cần một biểu diễn phục vụ so sánh, gồm:

- SHA-256 của file, thứ tự đọc và vị trí nguồn của từng đoạn; vị trí bảng/ô khi có.
- **Text gốc** và bản chuẩn hóa dùng để hỗ trợ ghép; giữ cả hai riêng biệt.
- Thông tin danh sách và định dạng đoạn/run; tham chiếu về vị trí text gốc.
- Cây heading và trạng thái trích xuất nếu đã có. Nếu không có hoặc còn chưa rõ, vẫn cho phép so sánh và hiển thị kết quả.

`SourceId` hiện có gắn với vị trí trong tài liệu; không dùng một mình để kết luận hai đoạn ở hai file là cùng đoạn. Xem `Sources/Docx/OpenXmlParser/OpenXmlDocumentSource.cs` và `Sources/Snapshot/CatalogBuilder.cs`.

Nếu có `w14:paraId`, có thể dùng làm một tín hiệu hỗ trợ ghép sau khi kiểm tra hợp lệ và trùng lặp. ID có thể thiếu hoặc thay đổi khi tài liệu được sao chép, tạo lại hoặc xử lý bằng phần mềm khác; không coi đó là bằng chứng duy nhất. Parser hiện cần được mở rộng nếu chọn sử dụng thuộc tính này.

Phạm vi ban đầu là **nội dung chữ trong thân tài liệu và bảng** mà parser đọc được. Text box phải có vị trí nguồn và báo hạn chế thứ tự đọc khi cần. Header/footer, footnote/endnote, comment, hình ảnh và các đối tượng nhúng chưa được khẳng định là đã hỗ trợ: cần xác định, kiểm tra và thể hiện rõ vùng nào được hoặc chưa được so sánh, tránh coi phần không đọc là không thay đổi.

## 4. Phát hiện khác biệt nội dung

### 4.1 Ghép đoạn

- Dùng thứ tự, text, ngữ cảnh lân cận và cấu trúc như bảng/ô, danh sách và heading làm tín hiệu. Hash text chuẩn hóa chỉ là công cụ hỗ trợ, không phải ID duy nhất; nhiều đoạn có thể giống nhau.
- Có thể dùng Myers diff trên chuỗi đoạn để xác định các vùng khớp và chưa khớp. Trong vùng chưa khớp, ghép ứng viên theo độ giống và ngữ cảnh; ngưỡng và cách xử lý trường hợp khó phải được đánh giá.
- Phân biệt đoạn **chỉ có trong tài liệu**, **chỉ có trong bản mẫu**, **đã thay đổi**; có thể bổ sung nhãn **di chuyển** khi bằng chứng đủ rõ.
- Xem xét đoạn bị tách hoặc gộp; khi chưa thể xác định quan hệ, hiển thị phần liên quan và trạng thái chưa chắc chắn. Không tự biến suy đoán thành kết luận.
- Đối với số thứ tự tự động của danh sách, phân biệt text gốc với nhãn Word hiển thị; việc số tự đổi do thêm mục cần được thể hiện theo chính sách hiển thị đã chọn, tránh nhầm với chữ do người dùng gõ.

### 4.2 So sánh trong đoạn

Với đoạn ghép được, so sánh text ở mức từ/ký tự để thể hiện phần thêm, thiếu hoặc thay đổi. Ví dụ:

> Bản mẫu: “Thanh toán trong **30 ngày**.”  
> Tài liệu: “Thanh toán trong **45 ngày**.”

Kết quả hiển thị `30` → `45`, kèm nguyên văn xung quanh ở hai phía.

Offset, nếu có, phải tính theo UTF-16 trên **text gốc của từng phía**, end exclusive, giống quy ước hiện có của `headingExtent`. Mỗi span ghi rõ thuộc bản mẫu hay tài liệu cần kiểm tra và phải trích đúng chuỗi gốc tại offset. Không dùng offset của text đã chuẩn hóa để đánh dấu trực tiếp trên text gốc. Nếu có ánh xạ chuẩn hóa, phải kiểm tra ánh xạ đó trước khi dùng.

Chuẩn hóa Unicode và khoảng trắng có thể hỗ trợ tìm cặp, nhưng không được tự động xóa khác biệt khỏi kết quả chỉ vì dễ ghép hơn. **Cần chốt chính sách hiển thị riêng cho khoảng trắng, xuống dòng và khác biệt mã hóa Unicode**, vì người dùng đã yêu cầu so sánh nội dung chữ nhưng chưa chỉ định mức báo các khác biệt này.

### 4.3 Bảng

Hiện trạng (2026-11-02): đoạn trong bảng được đọc với vị trí `body[1]/tbl[k]/tr[r]/tc[c]/p[m]`, nhưng comparer ghép chúng như một chuỗi đoạn phẳng. Khi bảng đổi nhiều (thêm/xóa hàng, đổi thứ tự cột, gộp ô), kết quả có thể rối và ô có thể bị ghép nhầm.

Cần làm trước P2, vì ghép sai ô sẽ làm so sánh định dạng sai theo:

- Ghép theo cấu trúc: bảng với bảng, rồi hàng với hàng, rồi ô với ô; chỉ diff chữ sau khi đã ghép ô.
- Báo khác biệt ở mức hàng và cột: hàng/cột chỉ có ở một phía, hàng bị đổi chỗ khi có bằng chứng; vẫn giữ vị trí và text gốc của từng ô.
- Ô gộp (`gridSpan`, `vMerge`), bảng lồng nhau và bảng bị tách/gộp: khi chưa ghép chắc chắn được, đánh dấu chưa hỗ trợ hoặc chưa chắc chắn, không đoán.
- Bổ sung vào tập đánh giá các ca: thêm/xóa hàng, đổi thứ tự hàng, thêm cột, sửa chữ trong ô, ô gộp.

### 4.4 Quy tắc bỏ qua do người dùng định nghĩa (Regex)

Chưa có. Người dùng có thể muốn bỏ qua các khác biệt thuộc một mẫu, ví dụ ngày tháng, số điện thoại hoặc email. Nguyên tắc khi làm:

- Quy tắc **không làm sạch văn bản trước khi so sánh**: làm vậy sẽ mất offset trên text gốc.
- Kết quả diff gốc được tính như khi không có quy tắc. Quy tắc chỉ **gắn nhãn** khác biệt khớp mẫu (ví dụ `ignored_by_rule:<tên quy tắc>`), không xóa; text gốc hai phía vẫn được giữ.
- Mặc định quy tắc **không** tác động vào khóa ghép đoạn: dùng regex cho khóa ghép có thể đổi cặp đoạn được ghép và che khuất khác biệt. Nếu sau này muốn dùng regex để hỗ trợ ghép, phải đo riêng tác động trên tập đánh giá.
- Mặc định hiển thị mọi khác biệt, kể cả khác biệt đã gắn nhãn. Giao diện cho phép ẩn chúng như một bộ lọc hiển thị và luôn cho biết số lượng đang bị ẩn.
- Mỗi quy tắc có tên và mẫu được ghi vào kết quả để kiểm tra lại được; mẫu lỗi hoặc chạy quá lâu thì báo lỗi, không bỏ qua im lặng.

## 5. Phát hiện khác biệt định dạng

So sánh định dạng là **phạm vi bắt buộc**, kể cả khi chữ không đổi. Ở mức đầu cần xét:

- In đậm, in nghiêng, gạch chân.
- Font, cỡ chữ, màu chữ.
- Style đoạn/chữ và căn lề đoạn khi xác định được.

Phải so sánh định dạng trên đúng phạm vi chữ tương ứng sau khi ghép, kể cả khi một đoạn có nhiều run, chữ và định dạng cùng thay đổi, hoặc ranh giới run thay đổi mà hình thức không đổi. Hiển thị giá trị của bản mẫu và tài liệu, kèm đoạn chữ/vị trí liên quan.

Ví dụ: “Điều 1” trong bản mẫu **in đậm**, còn trong tài liệu không in đậm → báo thay đổi định dạng cho “Điều 1”.

Cần xác định và thử nghiệm cách xử lý **định dạng khai báo trực tiếp và định dạng hiệu lực do style/kế thừa**. Nếu không xác định được định dạng hiển thị thực tế, ghi rõ chưa xác định; không khẳng định hai giá trị mặc định là giống nhau. Phân biệt thay đổi khai báo style với thay đổi hình thức hiển thị để tránh thông báo gây hiểu nhầm.

## 6. Vị trí và heading liên quan

Mỗi khác biệt cần có vị trí nguồn của bản mẫu và/hoặc tài liệu cần kiểm tra. Nếu một phía không có nội dung do thêm/thiếu, ghi rõ phía đó không có đối tượng tương ứng và, khi có thể, vị trí lân cận. `alias` chỉ có nghĩa trong snapshot/file tương ứng; phải kèm định danh phía và SHA file.

Heading được dùng để giúp người dùng tìm vị trí. Có thể dùng outline đã trích và thông tin thứ tự nguồn khi đáng tin cậy. Không mặc định đoạn thuộc heading gần nhất phía trước trong mọi trường hợp; khi cây heading thiếu, không chắc chắn hoặc không có, ghi `unknown` và vẫn trả khác biệt cùng vị trí nguồn. Số trang hiển thị trong Word có thể thay đổi theo bố cục và không mặc định sẵn có từ cấu trúc DOCX.

Không bắt buộc chạy lại toàn bộ pipeline heading chỉ để trả kết quả diff. Nếu cần bổ sung heading cho một vùng, việc chạy lại cục bộ, ngữ cảnh, ranh giới ảnh hưởng và quan hệ với heading ngoài vùng phải được thiết kế và đánh giá riêng; không giả định một cửa sổ cố định luôn đủ.

## 7. Vai trò LLM tùy chọn

Đường cơ bản phải sử dụng được khi không gọi LLM. Khi bật:

1. **Hỗ trợ ghép đoạn khó:** đưa đoạn bản mẫu, các ứng viên trong tài liệu và ngữ cảnh nguồn; model có thể đề xuất cặp hoặc trả chưa chắc chắn. Đối chiếu với nguồn và giữ trạng thái đề xuất; không tự xóa các khác biệt chưa giải quyết.
2. **Diễn giải khác biệt:** đưa text và/hoặc định dạng hai phía, diff và vị trí. Response nêu thay đổi cùng tham chiếu nguồn; không đánh giá hợp lệ hay không.

Kiểm tra tham chiếu của model có thuộc dữ liệu đã cung cấp. Tham chiếu hợp lệ không tự chứng minh nội dung diễn giải đúng. Lỗi, timeout hoặc kết quả model không hợp lệ không được làm mất kết quả diff cơ bản.

## 8. Định dạng kết quả dự kiến

Đây là contract **đề xuất**, chưa phải định dạng đã triển khai. Ví dụ thay đổi chữ trong hai đoạn đã ghép:

```json
{
  "templateSha256": "<sha bản mẫu>",
  "documentSha256": "<sha tài liệu>",
  "kind": "text_modified",
  "matchStatus": "matched",
  "template": {
    "sourceId": "<vị trí đoạn trong bản mẫu>",
    "text": "30 ngày",
    "span": { "start": 0, "end": 2, "text": "30" }
  },
  "document": {
    "sourceId": "<vị trí đoạn trong tài liệu>",
    "text": "45 ngày",
    "span": { "start": 0, "end": 2, "text": "45" }
  },
  "heading": { "status": "unknown", "sourceId": null },
  "interpretation": null
}
```

Với định dạng, trả thuộc tính và giá trị hai phía cùng phạm vi chữ tương ứng, ví dụ `bold: true → false`. Với phần chỉ có ở một phía, phía còn lại là `null`; vẫn giữ vị trí hoặc đoạn lân cận nếu có. Phải có trạng thái chưa chắc chắn cho ghép đoạn và vị trí, không coi `null` là một kết luận về nội dung.

## 9. Đánh giá

Tạo các cặp bản mẫu–tài liệu từ corpus DOCX và bổ sung tài liệu thực tế có đáp án được kiểm tra:

- Thêm, thiếu, sửa câu/chữ giữa đoạn; mục danh sách, heading và bảng.
- Text không đổi nhưng đổi in đậm, nghiêng, gạch chân, font, cỡ, màu, style hoặc căn lề; nhiều run trong một đoạn; thay đổi cả chữ và định dạng.
- Nhiều đoạn trùng, thay đổi lớn, đoạn di chuyển, tách/gộp và trường hợp không có hoặc trùng `paraId`.
- Unicode, dấu tiếng Việt, emoji UTF-16, khoảng trắng và xuống dòng; kiểm tra span theo text gốc hai phía.
- Thiếu heading hoặc heading chưa rõ; tài liệu có text box và các vùng chưa hỗ trợ.
- Chạy có/không LLM và khi LLM lỗi để xác nhận diff cơ bản vẫn được trả lại.

Đo độ đầy đủ và đúng của khác biệt chữ, định dạng, ghép đoạn và vị trí; đo tỷ lệ trường hợp được đánh dấu chưa chắc chắn, thời gian và chi phí. Với dữ liệu có kiểm soát, yêu cầu kết quả khớp đáp án cho các trường hợp thuộc phạm vi đã hỗ trợ; báo riêng các trường hợp chưa hỗ trợ. Không suy từ tập kiểm tra sang bảo đảm đúng tuyệt đối trên mọi tài liệu.

## 10. Lộ trình

| Giai đoạn | Nội dung | Kết quả cần kiểm tra |
|---|---|---|
| P0 | Định nghĩa phạm vi DOCX, biểu diễn hai phía và contract kết quả | Giữ text gốc, vị trí và thông tin định dạng; nhận diện vùng chưa hỗ trợ |
| P1 | Ghép đoạn và diff nội dung | Thể hiện thêm/thiếu/sửa, đúng span hai phía; báo trường hợp ghép chưa chắc |
| P1b | Ghép theo cấu trúc bảng (mục 4.3) | Ghép bảng, hàng, ô trước khi diff; báo hàng/cột thêm/thiếu; ô gộp chưa ghép chắc thì đánh dấu, không đoán; có ca bảng trong tập đánh giá |
| P2 | So sánh định dạng | Có giá trị trước/sau và phạm vi áp dụng, kể cả khi chữ không đổi |
| P3 | Hiển thị kết quả đối chiếu trên web | Chọn bản mẫu và tài liệu; xem hai phía, vị trí và trạng thái chưa rõ |
| P3b | Quy tắc bỏ qua bằng Regex (mục 4.4) | Khác biệt khớp quy tắc được gắn nhãn, không bị xóa; offset vẫn trên text gốc; ẩn/hiện được trên giao diện |
| P4 | Gắn heading khi có bằng chứng | Không làm mất kết quả khi heading chưa xác định |
| P5 | LLM tùy chọn để hỗ trợ ghép/diễn giải | Đánh giá riêng chất lượng; tắt LLM hoặc lỗi LLM vẫn có diff cơ bản |

Trước khi triển khai P0 cần chốt chính sách hiển thị cho khoảng trắng, xuống dòng và Unicode, cùng cách thể hiện định dạng trực tiếp so với định dạng hiệu lực.

Cập nhật 2026-11-02: P1 được bắt đầu ở dạng **thử nghiệm**, chỉ cho ghép đoạn và diff chữ trên text đã đọc được, trong khi chính sách hiển thị còn để ngỏ. Dữ liệu kết quả giữ mọi khác biệt chữ, kể cả khác biệt chỉ về khoảng trắng hoặc mã hóa Unicode (được gắn nhãn riêng). Định dạng hiệu lực chưa dùng làm kết luận trước P2; khi một lớp định dạng chưa được tính, kết quả ghi rõ là chưa đầy đủ.

## 11. Điểm gắn vào code hiện có

- Đọc DOCX và thông tin nguồn: `Sources/Docx/OpenXmlParser/OpenXmlDocumentSource.cs`, `Sources/Docx/DocxSourceAdapter.cs`, `Sources/Snapshot/CatalogBuilder.cs`.
- Heading khi cần: `HeadingAgent/Selectors/DocxSelector.cs`, `HeadingAgent/Placement/HeadingPlacer.cs`.
- Giao diện web hiện có: `Web/Endpoints/ExtractEndpoint.cs`, `Web/wwwroot/index.html`; đường API so sánh hai file cần được thiết kế riêng.
- Có thể đặt mã so sánh mới tại `DocumentProcessing/Changes/`; tên thư mục và contract cụ thể được quyết định khi triển khai.

Giữ nguyên hành vi trích heading hiện tại khi thêm tính năng đối chiếu; các thay đổi parser hoặc hợp đồng nguồn phải được kiểm tra tương thích riêng.
