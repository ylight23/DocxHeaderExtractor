# Bài toán: trích xuất thực thể (thiết kế, chưa triển khai)

Trạng thái: **thiết kế**, 2026-10-23. Chưa có code. Đây là bài toán thứ hai sau heading, dùng để kiểm tra khung
đã tách ở [problem-output.md](problem-output.md). Các quyết định sản phẩm còn mở được liệt kê ở cuối.

## Bài toán

Từ một tài liệu (DOCX, PDF, hoặc scan), tìm các thực thể thuộc các loại đã định (ví dụ: cơ quan, người, ngày, văn bản pháp luật,
số tiền) và trả về từng thực thể cùng vị trí của nó trong nguồn. Như heading, agent chỉ suy luận trong tài liệu đang được xử lý: không dùng bằng chứng hay schema từ tài liệu khác.

## Khác heading ở đâu

| | Heading | Thực thể |
|---|---|---|
| Đơn vị quyết định | dòng hoặc đoạn (cả dòng là heading hay không) | một cụm chữ trong dòng (span con) |
| Hình dạng quyết định | nhóm dòng + cấp + cha | (loại, văn bản, vị trí) |
| Cách C# kiểm tra | khớp extent với dòng; đặt cha qua `ParentResolver` | span có nằm trong text không; loại có trong danh sách không; không trùng |
| Phạm vi | toàn bộ cấu trúc | độc lập từng cụm; không có cây |

Kết luận: phần quyết định (`SelectionAuthority`, `DecisionBinder`, placement, hierarchy, structure, `OutlineProjection`, writeback)
**không dùng lại**. Phần nguồn, model, lease, và envelope **dùng lại**.

## Dùng lại được

| Thành phần | Vị trí | Dùng để làm gì |
|---|---|---|
| Nguồn có neo | `Contracts/Models/DocumentSourceCatalog.cs`: `DocumentSourceUnit`, `SourceAnchor`, `StructuralSpan` | Mỗi thực thể trỏ về đơn vị nguồn (`SourceId`) và span `[Start, End)` trong `Text`. `StructuralSpan.IsValidFor(text)` là kiểm tra có sẵn. |
| Vòng hội thoại có tool | `Agents/Conversation/ToolConversation` + `IConversationTask<T>` | Một task cho mỗi khối đơn vị nguồn. Vòng lặp, ngân sách, retry, sửa lỗi đã có. |
| Gọi model | `Agents/OpenRouterWire`, `AgentModel`, `AgentBudget` | Không đổi. |
| Khóa model | `ModelRoute`, `ModelAccess` | Không đổi; lấy qua `IModelProvider.Create<T>(role)`. |
| Lease và deadline | `Harness/LaneRun`, `LaneExecution`, `LaneLease` | Chạy lane dưới cùng cơ chế với heading. |
| Envelope đầu ra | `RunEnvelope` (đề xuất trong problem-output.md) | Identity, provenance, route, model, `SentDataExternally`, thời gian. |

## Cần viết mới

1. **Vai trò agent:** `AgentRoles.EntityExtractor = "entity.extractor"`, và interface `IEntityExtractor` trong `Contracts/Agents`.
2. **Agent:** project riêng `EntityAgent` (theo mục 3 của `extending.md`). Nó chỉ phụ thuộc `Agents` và `Sources`, như `HeadingAgent`.
   Gồm: prompt, định nghĩa các loại thực thể, tool (lấy đơn vị theo id, lấy ngữ cảnh lân cận), và `IConversationTask` cho một khối.
3. **Kiểm tra C# (`DocumentProcessing/Entities/`):**
   - span `[Start, End)` hợp lệ với `Text` của đơn vị được trỏ tới (`IsValidFor`);
   - loại thuộc danh sách đã định;
   - hai thực thể cùng loại không chồng lấn span, hoặc có quy tắc rõ về chồng lấn;
   - mọi thực thể bị loại được ghi vào audit, không lặng lẽ bỏ.
   Agent đề xuất, C# quyết định nhận hay loại. Đây là cùng nguyên tắc với heading.
4. **Lane (`DocumentProcessing/Lanes/Entities/`):** nguồn → chia khối → `LaneRun.CompleteAsync` → chạy các task → kiểm tra → output.
   Không gọi `LaneHarness.DecideAndAssembleAsync`, vì bước đó dành cho heading.
5. **Output:** `EntityOutput` (danh sách thực thể + audit), bọc trong envelope chung. Không đụng `DocumentOutline`.
6. **Nối vào host:** `HeadingAgentSetup` không đổi. Một `EntityAgentSetup` mới trong `App` lắp agent, và `DocumentRunner` cần một đường đi mới cho thực thể.

## Rủi ro và ghi chú

- **Tài liệu dài:** nhiều khối, nhiều task. Chia khối theo đơn vị nguồn và giới hạn kích thước request (`AgentBudget.MaxRequestBytes`, 128 KB mặc định). Chi phí tăng theo số khối; cần đo trước khi chạy thật.
- **Scan:** thực thể dựa trên chữ đã chép (transcription). Lỗi chép sẽ thành lỗi thực thể; cần ghi độ tin cậy của chữ vào audit.
- **Đánh giá cần Gold:** chưa có bộ đáp án thực thể. Theo quy tắc của dự án, Gold chỉ thay đổi khi chủ dự án duyệt, và model không được là đầu vào của Gold.
- **Không chạy thật** khi chưa có cho phép. Build và test offline vẫn làm được.

## Quyết định sản phẩm cần chủ dự án trả lời

1. **Danh sách loại thực thể** là gì? Mỗi loại cần định nghĩa ngắn để prompt và kiểm tra dùng chung.
2. **Định dạng một thực thể** gồm những trường nào? Đề xuất tối thiểu: `type`, `text`, `sourceId`, `start`, `end`, `page` (nếu có).
3. **Chuẩn hóa** (ví dụ "Điều 4" và "điều 4" có phải là một thực thể không) có nằm trong phạm vi không? Đề xuất: không, giai đoạn đầu.
4. **Loại tài liệu ban đầu:** chỉ DOCX có chữ, hay cả PDF và scan?
5. **Tiêu chí chấp nhận:** cần đo bằng Gold nào, và ngưỡng nào để chuyển sang dùng thật?

Sau khi có câu trả lời cho 1–2, bước đầu tiên là một `EntityAgent` offline có test kiểm tra span, không gọi model.
