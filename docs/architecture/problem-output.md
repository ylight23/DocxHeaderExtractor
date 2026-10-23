# Đầu ra cho bài toán mới (đề xuất, chưa triển khai)

Trạng thái: 2026-10-23, **một phần đã làm**. Bài toán mới đã có khung chung (`Problems/ProblemRunner`): phong bì `RunEnvelope` và
`ProblemRun<TOutput>`, đầu ra riêng cho từng bài toán (lựa chọn B dưới đây). Đầu ra của heading (`DocumentOutline`) chưa chuyển sang phong bì này. Mục đích: khi thêm một bài toán
AI không phải trích heading (bảng, phản biện văn bản), biết phần nào của đầu ra dùng lại được và phần nào phải tách.

## Hiện trạng: phần nào là chung, phần nào gắn với heading

| Kiểu | Vị trí | Chung hay heading |
|---|---|---|
| `RunResult` | `DocumentProcessing/Output/RunResult.cs` | Chung về hình thức: cặp `(DocumentExtractionResult, DocumentOutline)` |
| `DocumentExtractionResult` | `Contracts/Models` | Chung: identity, catalog nguồn, structure, sections, chunks, provenance |
| `DocumentOutline` | `DocumentProcessing/Output/Outline` | **Heading**: `Headings`, `ProductOutput`, `DeterministicRoute` |
| `RunAudit`, `LaneResult` | `DocumentProcessing/Audit`, `Output` | Chung về cấu trúc, nhưng các trường audit (`HeadingProviderCalls`, `Hierarchy*`, `Placement*`) là heading |
| `ExtractionOutput.Build` | `DocumentProcessing/Output` | **Heading**: đọc `ValidatedStructure`, `OutlineProjection`, `ProductProjector` |
| `WebPipelineProjection`, `ExtractCommand` | `Web`, `Cli` | **Heading**: đọc trực tiếp `outline.Headings` |

Kết luận: khung `RunResult` không phụ thuộc heading, nhưng nội dung của nó (outline, product, cây cấu trúc) thì có.
Một bài toán bảng sẽ không có heading, nên không có chỗ nào để đặt kết quả của nó nếu chỉ dựa vào `DocumentOutline`.

## Ba lựa chọn

**A. Thêm nhánh vào `DocumentOutline`.** Thêm trường `Tables`, `Critique`... cho từng bài toán.
- Dễ làm ngay, nhưng `DocumentOutline` phình ra theo bài toán, và mọi nơi đọc nó (CLI, Web, writeback) phải kiểm tra từng trường.
- Không khuyến nghị.

**B. Mỗi bài toán có kiểu đầu ra riêng; các phần chung nằm trong một "phong bì" chung.** Tách:
- `RunEnvelope`: file, identity, provenance, route, model, `SentDataExternally`, thời gian, audit ở mức lane. Mọi bài toán đều có.
- `HeadingOutput` (= `DocumentOutline` hiện tại, đổi tên dần): chỉ bài toán heading, bọc trong envelope.
- `TableOutput`, `CritiqueOutput`...: mỗi bài toán một kiểu, cùng envelope.
- Pipeline của bài toán trả về `Run<TOutput>(RunEnvelope, TOutput)`.

**C. Một pipeline chung cho mọi bài toán, đầu ra là tham số kiểu.** Quá tải: các bài toán khác nhau về bước kiểm tra (heading có binding và placement; bảng thì không), nên một pipeline chung sẽ có nhiều nhánh rẽ.
- Không khuyến nghị.

## Đề xuất: B, làm theo ba bước nhỏ

1. **Tách phong bì.** Thêm `RunEnvelope` vào `Contracts/Models` và để `DocumentOutline` chứa nó. Không đổi JSON: các trường cũ được giữ
   qua `[JsonPropertyName]` hoặc ghi lại golden trước rồi so sánh.
   Kiểm tra: test golden đầu ra CLI và Web không đổi (`WebSourceInspectionTests`, `ScanProductionPipelineTests`).
2. **Đưa phần heading ra khỏi `ExtractionOutput`.** `ExtractionOutput.Build` nhận `TOutput` và một hàm dựng đầu ra. Heading truyền
   `OutlineProjection` và `ProductProjector`; bài toán khác truyền hàm của nó.
3. **Chỉ khi có bài toán thứ hai:** đổi tên `DocumentOutline` thành `HeadingOutput` và thêm kiểu đầu ra của bài toán đó.
   Trước đó không đổi tên, để không làm rối code đang chạy.

## Những gì không đổi

- Harness (`LaneRun`, `LaneExecution`, `LaneLease`, checkpoint) không biết đầu ra. Nó chỉ trả về một giá trị và không quyết định hình dạng của nó.
- Quy tắc "AI đề xuất, C# kiểm tra" vẫn áp dụng: mỗi bài toán có bước kiểm tra riêng trước khi dựng đầu ra. Bước này không được lấy dữ liệu từ chỗ khác.
- Writeback Word chỉ áp dụng cho heading. Bài toán khác không có writeback trừ khi nó có kiểu đầu ra riêng và một bước ghi riêng.

## Bài toán thứ hai: trích xuất thực thể

Chọn làm bài toán thứ hai. Thiết kế chi tiết nằm trong [entity-extraction.md](entity-extraction.md). Với khung này:
- Phong bì chung và harness dùng lại được; `DocumentOutline` không cần đổi.
- Đầu ra là `EntityOutput` riêng, không đi qua `OutlineProjection`.
- Bước kiểm tra là span và loại, không phải cây heading.

## Câu hỏi cần chủ dự án quyết

1. Có chấp nhận đổi tên `DocumentOutline` thành `HeadingOutput` ở bước 3 không? Đổi tên sẽ ảnh hưởng tới các kiểu được tham chiếu từ `Web` và `Cli`.
2. Bài toán thứ hai cụ thể là gì? Quyết định giữa bảng và phản biện ảnh hưởng tới hình dạng `TableOutput` hay `CritiqueOutput`.
