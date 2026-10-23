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
| Gọi model (OpenRouter, ngân sách, đọc phản hồi) và vòng hội thoại có tool | `src/DocxHeaderExtractor.Agents/` (`Transport/`, `Budget/`, `Conversation/`) |
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
2. Trả nó từ hook tương ứng của model provider mà host truyền cho `DocumentRunner`:

```csharp
sealed class MyProvider(IModelProvider inner) : IModelProvider
{
    public ModelRoute CreateRoute() => inner.CreateRoute();
    // null = dùng HeadingPlacer mặc định
    public T? Create<T>(string role) where T : class =>
        role == AgentRoles.Placer ? new MyPlacer() as T : inner.Create<T>(role);
}

var runner = new DocumentRunner(options, new MyProvider(OpenRouterProvider.FromEnvironment()));
```

Hook nào trả `null` thì `HeadingAgentSetup` dùng agent mặc định. Nếu dựng pipeline trực tiếp (test), truyền
`new HeadingAgents(Docx: ..., Placer: ...)` vào `DocxPipeline`, `PdfHandler` hoặc `PdfPipeline.RunAsync`.

## 2. Thêm một agent mới cho tuyến heading

Ví dụ: một agent rà lại cấp bậc sau khi đặt cha.

1. **Contract** (`Contracts/Agents/`): một interface và hai record, yêu cầu và đề xuất. Quyền gọi model
   (`ModelAccess`: khóa và kiểm tra lease) được truyền theo từng lần gọi, agent không giữ nó, để pipeline gắn nó
   với lease của lần chạy, như `IHeadingPlacer`:
   ```csharp
   public interface IHeadingReviewer
   {
       string Contract { get; }
       Task<ReviewProposal> ProposeAsync(ReviewRequest request, ModelAccess access, CancellationToken ct);
   }
   ```
2. **Chỗ cắm** (`Contracts/Agents/HeadingAgents.cs`): thêm một property `IHeadingReviewer? Reviewer = null`.
   Chữ ký pipeline không đổi.
3. **Tên vai trò** (`Contracts/Agents/AgentRoles.cs`): `public const string Reviewer = "heading.reviewer";`. Host lấy agent qua `IModelProvider.Create<T>(role)`; không cần thêm thành viên nào vào `IModelProvider`.
4. **Mặc định** (`App/HeadingAgentSetup.cs`): `Reviewer: provider.Create<IHeadingReviewer>(AgentRoles.Reviewer) ?? new HeadingReviewer()`.
5. **Cài đặt agent** (`HeadingAgent/Review/HeadingReviewer.cs`): prompt, gói dữ liệu, vòng hỏi. Gọi model qua
   `Agents` với khóa trong `ModelAccess`; nếu cần hội thoại có tool, cài đặt `IConversationTask` và chạy `ToolConversation`.
6. **Bước C# kiểm tra** (`DocumentProcessing/<Bước>/`): nhận đề xuất, chỉ giữ những gì hợp lệ, ghi audit.
   Agent không bao giờ tự quyết định kết quả cuối.
7. **Thiếu agent thì từ chối**: thêm kiểm tra vào `Harness/LaneHarness.Authorize` (ví dụ
   `HEADING_REVIEWER_REQUIRED`), để một host quên lắp agent nhận lỗi rõ ràng thay vì kết quả thiếu.

## 3. Thêm một bài toán mới (ngoài heading)

Ví dụ: trích thực thể, trích bảng, phản biện văn bản.

Mọi bài toán chạy qua cùng một khung, `DocumentProcessing/Problems/ProblemRunner`:
đọc nguồn → agent đề xuất (dưới lease và deadline của `LaneRun`) → C# kiểm tra → đầu ra trong phong bì chung.
Bài toán chỉ viết hai thứ, cả hai khai báo trong `Contracts/Problems/`:

| Viết | Interface | Việc |
|---|---|---|
| Agent | `IProposer<TProposal>` | Đọc `ProblemSource` (identity + các đơn vị nguồn có text, neo và span) và đề xuất. Gọi model qua `ModelAccess`. |
| Kiểm tra | `IChecker<TProposal, TOutput>` | Giữ phần đề xuất mà nguồn ủng hộ, ghi lý do cho phần bị loại. Không gọi model. |

```csharp
var run = await ProblemRunner.RunAsync(path, options, OpenRouterProvider.FromEnvironment(), "entities",
    new EntityProposer(), new EntityChecker());
// run.Envelope: tài liệu, bài toán, contract của agent, model, có gửi dữ liệu ra ngoài không, thời gian
// run.Output: đầu ra của bài toán; null khi tắt model
```

Khung tự lo: đọc DOCX/PDF thành cùng một dạng nguồn, lấy model route và từ chối khi thiếu khóa, lease và deadline,
không gọi agent khi tắt model. Bài toán không mở file và không biết định dạng. Bản scan chưa đọc được qua khung,
vì chữ của scan hiện chỉ được chép trong lúc chọn heading.

Ví dụ chạy được: `tests/DocxHeaderExtractor.Tests/ProblemRunnerTests.cs` (một bài toán trích span nhỏ, agent là script).

- Đặt agent vào một project riêng (ví dụ `EntityAgent`), dùng `Agents` để gọi model: route, ngân sách, đọc
  phản hồi đã có sẵn.
- Checker đặt trong `DocumentProcessing/<Bài toán>/` hoặc cạnh agent; nó chỉ cần `Contracts`.

Bài toán cần hội thoại có tool thì không viết lại vòng lặp: cài đặt `IConversationTask<TOutcome>`
(`Agents/Conversation/`) và gọi `ToolConversation.Run`. Vòng lặp lo lượt, retry, ngân sách, chạy tool và lượt sửa
lỗi; bài toán chỉ lo phần của nó:

| Bài toán cung cấp | Ý nghĩa |
|---|---|
| `Build(history, toolChoice)` | request của mỗi lượt (prompt, payload của bài toán) |
| `Tools` | các tool model được gọi |
| `Judge(reply, state)` | đọc câu trả lời cuối: `Verdict.Finish(kết quả)` hoặc `Verdict.Repair(mã lỗi)` |
| `RepairMessage`, `ChoicesAfterRepair` | thông điệp sửa lỗi và `tool_choice` của các lượt sau đó |
| `Stop(state, status, failure)` | kết quả khi vòng lặp dừng (hết ngân sách, lỗi gọi, vi phạm giao thức) |
| `Limits` | số lượt, số tool mỗi lượt, trần byte, các lỗi được gửi lại |

Ví dụ có sẵn: `HeadingAgent/Runtime/SelectionTask.cs` (chọn heading của một đoạn).

## Giữ hành vi khi sửa

- `tests/DocxHeaderExtractor.Tests/AgentWireGoldenTests.cs` ghim byte mà agent gửi cho model: prompt, khai báo
  tool, kết quả tool và từng request của các hội thoại mẫu. Refactor không được làm đổi golden này.
- Khi cố ý đổi điều model đọc (prompt mới, gói dữ liệu mới), ghi lại golden bằng `DHX_RECORD_WIRE_GOLDEN=1`
  và xem diff của `Golden/agent-wire.json` cùng với thay đổi đó.
- Thêm agent mới thì thêm hội thoại mẫu của nó vào golden này.
