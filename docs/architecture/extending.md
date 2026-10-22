# Mở rộng: thay hoặc thêm agent

Tài liệu này dành cho người muốn đổi một agent hoặc thêm năng lực AI mới mà không phải hiểu hết pipeline.
Nguyên tắc chung: **agent đề xuất, C# kiểm tra**. Pipeline không bao giờ biết agent cụ thể; nó chỉ biết
interface trong `Contracts/Agents/`.

## Bản đồ: mỗi thứ nằm ở đâu

| Bạn muốn | Mở file |
|---|---|
| Xem agent nhận gì và trả gì | `src/DocxHeaderExtractor.Contracts/Agents/` |
| Xem các agent mà tuyến heading đang dùng | `Contracts/Agents/HeadingAgents.cs` |
| Xem agent mặc định được lắp thế nào | `src/DocxHeaderExtractor.App/HeadingAgentSetup.cs` |
| Đọc prompt, gói dữ liệu, vòng hỏi của agent heading | `src/DocxHeaderExtractor.HeadingAgent/` (`Selectors/`, `Placement/`, `Protocol/`) |
| Gọi model (OpenRouter, ngân sách, đọc phản hồi) | `src/DocxHeaderExtractor.Agents/` |
| Xem C# kiểm tra kết quả của agent | `src/DocxHeaderExtractor.DocumentProcessing/` (`Binding/`, `Placement/ParentResolver.cs`) |

Các agent hiện có của tuyến heading:

| Agent | Interface | Mặc định | Ai kiểm tra kết quả |
|---|---|---|---|
| Chọn heading (PDF, DOCX) | `IHeadingSelector` | `PdfSelector`, `DocxSelector` | `Selection/SelectionAuthority`, `Binding/DecisionBinder` |
| Chọn heading (scan) | `IScanHeadingSelector` | `ScanSelector` | như trên, sau khi dựng nguồn từ OCR |
| Đặt cha | `IHeadingPlacer` | `HeadingPlacer` | `Placement/ParentResolver` |

## 1. Thay một agent có sẵn (không sửa pipeline)

Ví dụ: chạy thử một agent đặt cha khác, hoặc dùng agent giả trong test.

1. Viết lớp cài đặt interface, ví dụ `IHeadingPlacer`.
2. Trả nó từ hook tương ứng của factory mà host truyền cho `DocumentRunner`:

```csharp
sealed class MyFactory(IInferenceTransportFactory inner) : IInferenceTransportFactory
{
    public bool SendsDataExternally => inner.SendsDataExternally;
    public Task<IInferenceTransport> CreateAgentRouteAsync(CancellationToken ct = default) => inner.CreateAgentRouteAsync(ct);
    public IHeadingPlacer? CreateHeadingPlacer() => new MyPlacer();   // null = dùng HeadingPlacer mặc định
}
```

Hook nào trả `null` thì `HeadingAgentSetup` dùng agent mặc định. Nếu dựng pipeline trực tiếp (test), truyền
`new HeadingAgents(Docx: ..., Placer: ...)` vào `DocxPipeline`, `PdfHandler` hoặc `PdfPipeline.RunAsync`.

## 2. Thêm một agent mới cho tuyến heading

Ví dụ: một agent rà lại cấp bậc sau khi đặt cha.

1. **Contract** (`Contracts/Agents/`): một interface và hai record, yêu cầu và đề xuất. Transport được truyền
   theo từng lần gọi, agent không giữ nó, để pipeline gắn transport với lease của lần chạy:
   ```csharp
   public interface IHeadingReviewer
   {
       string Contract { get; }
       Task<ReviewProposal> ProposeAsync(ReviewRequest request, IInferenceTransport transport, CancellationToken ct);
   }
   ```
2. **Chỗ cắm** (`Contracts/Agents/HeadingAgents.cs`): thêm một property `IHeadingReviewer? Reviewer = null`.
   Chữ ký pipeline không đổi.
3. **Hook ghi đè** (`Contracts/Inference/IInferenceTransport.cs`): `IHeadingReviewer? CreateHeadingReviewer() => null;`.
4. **Mặc định** (`App/HeadingAgentSetup.cs`): `Reviewer: factory.CreateHeadingReviewer() ?? new HeadingReviewer()`.
5. **Cài đặt agent** (`HeadingAgent/Review/HeadingReviewer.cs`): prompt, gói dữ liệu, vòng hỏi. Gọi model qua
   transport được truyền vào; nếu cần hội thoại có tool, dùng các khối trong project `Agents`.
6. **Bước C# kiểm tra** (`DocumentProcessing/<Bước>/`): nhận đề xuất, chỉ giữ những gì hợp lệ, ghi audit.
   Agent không bao giờ tự quyết định kết quả cuối.
7. **Thiếu agent thì từ chối**: thêm kiểm tra vào `Harness/LaneHarness.Authorize` (ví dụ
   `HEADING_REVIEWER_REQUIRED`), để một host quên lắp agent nhận lỗi rõ ràng thay vì kết quả thiếu.

## 3. Thêm một bài toán mới (ngoài heading)

Ví dụ: trích bảng, phản biện văn bản.

- Tạo record agent riêng, cùng mẫu với `HeadingAgents` (ví dụ `TableAgents`), và hàm lắp riêng trong `App`
  (ví dụ `TableAgentSetup`).
- Đặt agent vào một project riêng (ví dụ `TableAgent`), dùng `Agents` để gọi model: route, ngân sách, đọc
  phản hồi đã có sẵn.
- Pipeline của bài toán đi theo cùng thứ tự: nhận file → lane → harness → các bước C# kiểm tra → đầu ra.

Lưu ý: vòng lặp hội thoại có tool (`HeadingAgent/Runtime/AgentRunner.cs`, lớp `AgentUnit`) hiện còn gắn với
heading. Bài toán mới cần vòng hỏi có tool thì phải tách phần chung của nó sang `Agents` trước.

## Giữ hành vi khi sửa

- `tests/DocxHeaderExtractor.Tests/AgentWireGoldenTests.cs` ghim byte mà agent gửi cho model: prompt, khai báo
  tool, kết quả tool và từng request của các hội thoại mẫu. Refactor không được làm đổi golden này.
- Khi cố ý đổi điều model đọc (prompt mới, gói dữ liệu mới), ghi lại golden bằng `DHX_RECORD_WIRE_GOLDEN=1`
  và xem diff của `Golden/agent-wire.json` cùng với thay đổi đó.
- Thêm agent mới thì thêm hội thoại mẫu của nó vào golden này.
