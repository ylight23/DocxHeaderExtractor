# Kiến trúc hiện tại

Cập nhật 2026-10-23. Đây là trạng thái đang chạy của code; lịch sử các giai đoạn trước nằm trong git.

## Tóm tắt

- Mỗi định dạng có đúng một tuyến, không có đường dự phòng: DOCX, PDF và bản scan đều lấy heading qua
  selector, và selector mặc định là agent V6 (Qwen qua OpenRouter, một hội thoại mỗi cửa sổ).
- CLI và Web gọi thẳng tuyến V6 qua `DocumentRunner`. Không còn project `AgentHarness`, không còn MCP.
- `.doc`, `.rtf`, `.odt` được chuyển sang OOXML trước khi vào tuyến.
- AI quyết định (dòng nào là heading, cách nhóm, đề xuất cha); C# kiểm tra và dựng cấu trúc.

## Các project

| Project | Vai trò | Phụ thuộc |
|---|---|---|
| `Contracts` | Kiểu dùng chung: model nguồn và cấu trúc; `Agents/` (mọi thứ agent nhận và trả: `IHeadingSelector`, `IHeadingPlacer`, `HeadingAgents`); interface transport (`Inference/`); `ChunkingOptions` | — |
| `Sources` | Đọc nguồn OOXML, PDF và scan; snapshot nguồn, alias, cache | `Contracts` |
| `Agents` | Phần dùng chung của mọi agent, không biết tài liệu hay heading: route OpenRouter đã qualify, gửi request, đọc phản hồi, ngân sách, SSE, audit usage; vòng hội thoại có tool (`Conversation/ToolConversation`) | — |
| `HeadingAgent` | Agent V6: selector PDF, DOCX và scan, agent đặt cha (`HeadingPlacer`); prompt, tool, bài toán chọn heading của một đoạn (`SelectionTask`, chạy trên `ToolConversation`) | `Agents`, `Sources` |
| `DocumentProcessing` | Pipeline: nhận file, các lane, harness, các bước kiểm tra và dựng cấu trúc, đầu ra, writeback | `Contracts`, `Sources` |
| `Infrastructure` | Transport provider (OpenRouter, LM Studio, SGLang, Llama local), factory chọn route, store run | `Contracts`, `Sources` |
| `App` | `HeadingAgentSetup`: nơi duy nhất lắp agent mặc định; `DocumentRunner`: chạy pipeline, chuyển `.doc`, ghi outline | `HeadingAgent`, `DocumentProcessing` |
| `Cli` | Lệnh `dhx`; gọi `DocumentRunner` trực tiếp | `App`, `Infrastructure` |
| `Web` | Giao diện `dhx-ui`; gọi `DocumentRunner` trực tiếp, stream sự kiện tiến độ | `App`, `Infrastructure` |

Quy tắc phụ thuộc: `Contracts` không phụ thuộc gì; `Sources` không biết pipeline; `HeadingAgent` chỉ thấy
`Agents` và `Sources`; `DocumentProcessing` không biết agent (chỉ biết interface trong `Contracts/Agents`); `Infrastructure`
chỉ cài đặt interface của `Contracts`. `App` là chỗ duy nhất nối agent với pipeline. Cách thêm hoặc thay agent:
[extending.md](extending.md).

Quy ước: namespace = tên project + đường dẫn thư mục. Tên file = tên type. Tên type ngắn nhưng vẫn đủ nghĩa
khi đứng một mình trong code; bỏ các tiền tố mà thư mục đã nói rõ (`Output/Outline/OutlineProjection.cs`,
không phải `HeadingOutlineProjection`; `Audit/RunAudit.cs`, không phải `PipelineExecutionAudit`).

## Luồng chạy

```text
CLI (dhx) / Web (dhx-ui)
  -> DocumentRunner.ExtractAsync(file)
       -> Sources/Detect/SourceDetector (đọc byte, không đọc đuôi)
       -> .doc/.rtf/.odt: Sources/Docx/OfficeConverter.EnsureDocx
       -> Intake/Router
            DOCX -> Intake/DocxHandler -> Lanes/Docx/DocxPipeline -> DocxLane
            PDF  -> Intake/PdfHandler  -> Lanes/Pdf/PdfPipeline   -> PdfLane
            scan (DOCX chỉ có ảnh, PDF cần OCR) -> Lanes/Scan/ScanLane
         mỗi lane chạy trong Harness/LaneHarness (lease, deadline, checkpoint của PDF):
            Selection/SelectionAuthority   (IHeadingSelector = agent V6 quyết định)
            -> Binding/DecisionBinder       (gắn và kiểm tra quyết định vào nguồn)
            -> Placement/ParentResolver     (IHeadingPlacer = HeadingPlacer đề xuất cha, C# kiểm tra đồ thị)
            -> Hierarchy/HierarchyResolver  (dựng cấp bậc)
            -> Structure/Materializer       (dựng cấu trúc; Structure/StructureAssembler nối các bước)
            -> Audit/RunAudit
       -> Output/ExtractionOutput: Final, Product, Outline, Sections -> Output/RunResult
  -> (tuỳ chọn) DocumentRunner.WriteOutline -> Writeback: bản sao .docx với w:outlineLvl
```

Nếu tuyến không ở route agent đã ủy quyền, không có selector, không có khóa provider hoặc không có placer, nó từ chối
trước khi gọi mô hình, theo đúng thứ tự đó (`AGENT_ROUTE_AUTHORIZATION_REQUIRED`, `*_HEADING_SELECTOR_REQUIRED`,
`*_HEADING_SELECTOR_KEY_MISSING`, `HEADING_PLACER_REQUIRED`). Với `--no-llm` hoặc `DisableLlm`, tuyến trả nguồn không có heading
(`docx-llm-disabled`, `pdf-function-conditioned-llm-disabled`, `scan-llm-disabled`).

## Thư mục theo bước

`DocumentProcessing`, theo thứ tự chạy:

| Thư mục | Bước |
|---|---|
| `Intake/` | Nhận file, định tuyến theo định dạng (`Router`, `DocxHandler`, `PdfHandler`, `UploadedFile`) |
| `Lanes/Pdf`, `Lanes/Docx`, `Lanes/Scan` | Phần riêng của từng định dạng: dựng nguồn, khối layout, facts bổ sung cho audit |
| `Harness/` | Phần chạy chung của mọi lane: `LaneHarness`, lease, deadline, checkpoint, transport gắn lease, options |
| `Selection/` | Biến câu trả lời của selector thành heading (`SelectionAuthority`, `SelectionResult`) |
| `Binding/` | Gắn và kiểm tra quyết định vào nguồn (`DecisionBinder`) |
| `Placement/` | C# kiểm tra đề xuất cha của agent (`ParentResolver`): chỉ giữ ROOT, NONE, UNRESOLVED hoặc một heading đứng trước |
| `Hierarchy/` | Dựng cấp bậc (`HierarchyResolver`) |
| `Structure/` | Dựng cấu trúc (`Materializer`), nối các bước 4→7 (`StructureAssembler`) |
| `Audit/` | Ghi lại lần chạy (`RunAudit`, `PlacementAudit`, `HierarchyAudit`) |
| `Output/` | Đầu ra: `Final/`, `Product/`, `Outline/`, `Sections/`; `ExtractionOutput`, `RunResult` |
| `Writeback/` | Ghi outline vào bản sao `.docx` |

`Sources`, theo các bước dựng nguồn: `Detect/` (nhận dạng file, hash) · `Docx/` (adapter, cache, chuyển `.doc`;
`Docx/OpenXmlParser/` đọc OOXML) · `Pdf/` · `Scan/` · `Snapshot/` (snapshot nguồn, catalog) · `Cache/` (kho cache MessagePack)
· `Markers/` (đánh số, nhãn) · `Aliases/` (alias, ranh giới text). Các bộ kiểm tra cấu trúc (`StructureFactory`,
`ProposalValidator`, `RelationValidator`) nằm ở `DocumentProcessing/Structure/`, `IdentityResolver` ở
`DocumentProcessing/Hierarchy/`: chỉ các bước đó dùng chúng.

Host: `Cli` có `Program.cs` (đọc tham số, chọn lệnh) và `Commands/` (mỗi lệnh một file: `ExtractCommand`, `ScanCommands`,
`InfoCommand`); `Web` có `Program.cs` (dựng host, endpoint ngắn) và `Endpoints/` (`ExtractEndpoint`, `InspectEndpoint`).

`HeadingAgent`, theo vòng của agent: `Selectors/` (điểm vào) · `Formats/` (nguồn và cách trình bày theo
định dạng) · `Protocol/` (những gì model đọc) · `Runtime/` (gọi model, ngân sách, cache) · `Tools/` (tool và sổ
ghi lần gọi) · `Grouping/` (chia nhóm, kiểm tra nhóm) · `Placement/` (agent đặt cha) · `Scan/` (OCR/VLM và selector scan).

## Ranh giới trách nhiệm

- Nguồn (`Sources`) giữ sự kiện nguồn: text, alias, toạ độ, style, đánh số. Không quyết định heading.
- Agent (`HeadingAgent`) quyết định dòng nào là heading, cách nhóm và đề xuất cha. Mọi lần gọi model đều nằm
  trong agent; agent chỉ gọi provider qua khóa của tuyến đã ủy quyền.
- Harness (`Harness/`) chạy lane: ủy quyền, lease, deadline, checkpoint. Không quyết định heading, không biết
  định dạng.
- Các bước `Binding` → `Structure` chỉ kiểm tra và dựng cấu trúc từ kết quả agent. Không lấy heading từ chỗ khác.
- `Output` và `Writeback` chỉ biến cấu trúc đã kiểm tra thành output và file.

## Backend model

`Infrastructure` còn giữ mã của các backend local (Llama qua LLamaSharp, LM Studio, SGLang) và OpenRouter.
Tuyến trích xuất chỉ chấp nhận OpenRouter: `CreateAgentRouteAsync` từ chối backend khác với
`AGENT_ROUTE_PROVIDER_UNSUPPORTED`. Vì vậy các cờ CLI/Web chọn backend local vẫn được đọc, nhưng hiện không
chạy được trích xuất. Việc xóa hẳn mã local là một bước riêng, chưa làm.

## Lưu trữ và đầu ra

- Run của Web: `JsonFileTaskRunStore` trong `DHX_RUNTIME_STATE_DIR`.
- Cache nguồn: MessagePack (PDF, DOCX, evidence scan) kèm sidecar `.sha256`.
- Checkpoint của PDF: thư mục tạm, dọn khi kết thúc (`Harness/CheckpointScope`).
- Writeback: bản sao `.docx` mới; file nguồn không bao giờ bị sửa.
- Gold và artifact thí nghiệm không phải nguồn sự thật của production.

## Kiểm thử

`dotnet test tests/DocxHeaderExtractor.Tests` (444 test, khoảng 1–3 phút, không gọi provider). Lọc nhanh:
`--filter "Category!=Slow"`. Hai golden giữ hành vi khi refactor: `Golden/v6-parity-src089.json.gz` (đầu ra
tuyến V6) và `Golden/agent-wire.json` (prompt, khai báo tool, kết quả tool và byte của từng request trong các hội thoại mẫu). Ghi lại golden wire có
chủ ý bằng `DHX_RECORD_WIRE_GOLDEN=1`.
