> **Trạng thái (2026-10-23): chỉ là ví dụ trên khung chung, không phát triển thêm.** Chủ dự án xác nhận dự án này cần
> trích heading, không cần trích thực thể. Project `Entities`, lệnh `dhx entities` và endpoint `/api/entities` được giữ như
> một bài toán mẫu chạy qua `ProblemRunner`; không có kế hoạch cải thiện chất lượng hay chạy thêm.
>
> **Điều đã học, để nhớ nếu có bài toán tương tự:**
> - Để model trả vị trí `start`/`end` đếm ký tự là không đáng tin: trên tài liệu biên bản họp (run A, qwen), 20/29
>   thực thể được giữ bắt đầu giữa từ.
> - Cách thay thế đã thử: model trả chuỗi nguyên văn, C# tìm chuỗi trong text của đơn vị. Thử với một lô bằng một
>   subagent Haiku đóng vai model: 39/39 thực thể khớp nguyên văn, không loại cái nào. Chưa đưa vào code; bản sửa nằm
>   trong `scratchpad/entity-text-locate-wip.patch` (chưa commit, đã bỏ khỏi cây làm việc).
> - Vì vậy ghi nhận: khi model phải báo vị trí trong văn bản, hãy để C# tự tìm vị trí từ chuỗi model đã trích.

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

Khung chung đã có (`Problems/ProblemRunner`, xem mục 3 của `extending.md`), nên bài toán này chỉ cần:

1. **Agent = `IProposer<EntityProposal>`:** project riêng `EntityAgent`, chỉ phụ thuộc `Agents` và `Contracts`.
   Gồm: prompt, định nghĩa các loại thực thể, tool (lấy đơn vị theo id, lấy ngữ cảnh lân cận), và `IConversationTask` cho một khối.
2. **Kiểm tra = `IChecker<EntityProposal, EntityOutput>`:**
   - span `[Start, End)` hợp lệ với `Text` của đơn vị được trỏ tới (`IsValidFor`);
   - loại thuộc danh sách đã định;
   - hai thực thể cùng loại không chồng lấn span, hoặc có quy tắc rõ về chồng lấn;
   - mọi thực thể bị loại được ghi vào audit, không lặng lẽ bỏ.
   Agent đề xuất, C# quyết định nhận hay loại. Đây là cùng nguyên tắc với heading.
3. **Output:** `EntityOutput` (thực thể giữ lại + thực thể bị loại kèm lý do). `ProblemRunner` bọc nó trong `RunEnvelope`.
4. **Nối vào host:** một lệnh CLI hoặc endpoint Web gọi `ProblemRunner.RunAsync(..., "entities", proposer, checker)`.
   Không có lane, harness hay đường đi riêng nào phải viết.

## Rủi ro và ghi chú

- **Tài liệu dài:** nhiều khối, nhiều task. Chia khối theo đơn vị nguồn và giới hạn kích thước request (`AgentBudget.MaxRequestBytes`, 128 KB mặc định). Chi phí tăng theo số khối; cần đo trước khi chạy thật.
- **Scan:** khung đọc được scan qua `IScanReader`; thực thể dựa trên chữ đã chép. Lỗi chép sẽ thành lỗi thực thể. Dòng không đọc
  được nằm trong `ProblemSource.Gaps`, nên đầu ra phải báo khi câu trả lời có thể thiếu.
- **Đánh giá cần Gold:** chưa có bộ đáp án thực thể. Theo quy tắc của dự án, Gold chỉ thay đổi khi chủ dự án duyệt, và model không được là đầu vào của Gold.
- **Không chạy thật** khi chưa có cho phép. Build và test offline vẫn làm được.

## Quyết định sản phẩm cần chủ dự án trả lời

1. **Danh sách loại thực thể** là gì? Mỗi loại cần định nghĩa ngắn để prompt và kiểm tra dùng chung.
2. **Định dạng một thực thể** gồm những trường nào? Đề xuất tối thiểu: `type`, `text`, `sourceId`, `start`, `end`, `page` (nếu có).
3. **Chuẩn hóa** (ví dụ "Điều 4" và "điều 4" có phải là một thực thể không) có nằm trong phạm vi không? Đề xuất: không, giai đoạn đầu.
4. **Loại tài liệu ban đầu:** chỉ DOCX có chữ, hay cả PDF và scan?
5. **Tiêu chí chấp nhận:** cần đo bằng Gold nào, và ngưỡng nào để chuyển sang dùng thật?

Sau khi có câu trả lời cho 1–2, bước đầu tiên là một `EntityAgent` offline có test kiểm tra span, không gọi model.
