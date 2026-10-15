# Kiến trúc hiện tại

Cập nhật 2026-10-15. Đây là trạng thái đang chạy của code; lịch sử các giai đoạn trước nằm trong git.

## Tóm tắt

- Mỗi định dạng có đúng một tuyến, không có đường dự phòng: DOCX và PDF đều lấy heading qua
  `IHeadingSelector`, và selector mặc định là agent V6 (Qwen qua OpenRouter, một hội thoại mỗi cửa sổ).
- CLI và Web gọi thẳng tuyến V6 qua `V6DocumentRunner`. Không còn harness, không còn MCP.
- `.doc`, `.rtf`, `.odt` được chuyển sang OOXML trước khi vào tuyến.
- Consent gửi dữ liệu ra ngoài đã bị bỏ theo quyết định của chủ dự án (2026-10-15).

## Các project

| Project | Vai trò | Phụ thuộc |
|---|---|---|
| `Sources` | Hợp đồng dùng chung (kiểu `Core` cũ trong `Models/`, `Semantics/`), đọc nguồn OOXML và PDF (`Source/`, `OpenXmlLayer/`), snapshot nguồn, `IHeadingSelector`, `UploadedSource` | `Application` |
| `DocumentProcessing` | Pipeline DOCX/PDF, router, bước ghép (binder, parent resolver, hierarchy, materializer), projection, writeback `.docx`, interface transport | `Sources`, `Application` |
| `HeadingAgent` | Agent V6 cho PDF (`PdfHeadingAgentSelector`) và DOCX (`DocxHeadingAgentSelector`) | `Sources` |
| `V6Runner` | `V6DocumentRunner`: nối agent vào pipeline, định tuyến file, chuyển `.doc`, ghi outline | `HeadingAgent`, `DocumentProcessing` |
| `Application` | Hợp đồng không phụ thuộc provider: runtime, semantic registry, tác vụ/tài nguyên còn dùng | `Sources` |
| `Infrastructure` | Transport provider (OpenRouter, LM Studio, SGLang, Llama local), factory chọn route, store run | `Application`, `DocumentProcessing`, `Sources` |
| `Cli` | Lệnh `dhx`; gọi `V6DocumentRunner` trực tiếp | `V6Runner`, `Infrastructure` |
| `Web` | Giao diện `dhx-ui`; gọi `V6DocumentRunner` trực tiếp, stream sự kiện tiến độ | `V6Runner`, `Infrastructure` |
| `V6Qualification` | Thí nghiệm V6 đã qualify (parity, canary). Không phải đường chạy production | `HeadingAgent`, `DocumentProcessing`, `Infrastructure` |

Quy tắc phụ thuộc: `Sources` không biết pipeline; `HeadingAgent` chỉ thấy `Sources`; `DocumentProcessing`
không biết agent (chỉ biết `IHeadingSelector`). `V6Runner` là chỗ duy nhất nối hai bên.

Đã xóa: `AgentHarness` (guardrail, validator, consent, tường thuật), skill `heading-extraction`,
`Mcp`, `Core` (gộp vào `Sources`).

Lưu ý: `Application` hiện phụ thuộc `Sources` (vì `Core` đã gộp vào đó). Đây là đảo ngược tầng; nếu cần
tách lại, phải chuyển các kiểu `Application` dùng khỏi `Sources`.

## Luồng chạy

```text
CLI (dhx) / Web (dhx-ui)
  -> V6DocumentRunner.ExtractAsync(file)
       -> UploadedSourceDetector (đọc byte, không đọc đuôi)
       -> .doc/.rtf/.odt: OfficeDocumentConverter.EnsureDocx
       -> DocumentExtractionRouter
            DOCX -> DocxExtractionPipeline -> DocxHeadingPipeline
            PDF  -> PdfExtractionHandler   -> PdfHeadingPipeline
         mỗi tuyến: source adapter -> IHeadingSelector (agent V6) -> SelectedLinesHeadingAuthority
                    -> HeadingStructureAssembler (binder, placement, hierarchy, materializer)
                    -> CanonicalFinalStructure -> DocumentProductOutputProjector
       -> DocumentExtractionExecutionResult (Outline + audit)
  -> (tuỳ chọn) V6DocumentRunner.WriteOutline -> bản sao .docx với w:outlineLvl
```

Nếu tuyến không có selector hoặc không có khóa provider, nó từ chối trước khi gọi mô hình
(`DOCX_HEADING_SELECTOR_REQUIRED`, `DOCX_HEADING_SELECTOR_KEY_MISSING`, `PDF_HEADING_SELECTOR_REQUIRED`).
Với `--no-llm` hoặc `DisableLlm`, tuyến DOCX trả nguồn không có heading (`docx-llm-disabled`).

## Ranh giới trách nhiệm

- Nguồn (`Sources`) giữ sự kiện nguồn: text, alias, toạ độ, style, đánh số. Không quyết định heading.
- Agent (`HeadingAgent`) quyết định dòng nào là heading và cách nhóm chúng. Agent chỉ gọi provider qua
  khóa của tuyến đã ủy quyền.
- Bước ghép trong `DocumentProcessing` chỉ kiểm tra và dựng cấu trúc từ kết quả agent: binder, đặt vị trí
  (`HeadingParentResolver`), phân cấp, materializer. Không lấy heading từ chỗ khác.
- Projection và writeback chỉ biến cấu trúc đã kiểm tra thành output và file.

## Backend model

`Infrastructure` còn giữ mã của các backend local (Llama qua LLamaSharp, LM Studio, SGLang) và OpenRouter.
Tuyến trích xuất chỉ chấp nhận OpenRouter: `CreateAgentRouteAsync` từ chối backend khác với
`AGENT_ROUTE_PROVIDER_UNSUPPORTED`. Vì vậy các cờ CLI/Web chọn backend local vẫn được đọc, nhưng hiện không
chạy được trích xuất. Việc xóa hẳn mã local là một bước riêng, chưa làm.

## Lưu trữ và đầu ra

- Run của Web: `JsonFileTaskRunStore` trong `DHX_RUNTIME_STATE_DIR`.
- Checkpoint của PDF: thư mục tạm, dọn khi kết thúc (`ProductionCheckpointScope`).
- Writeback: bản sao `.docx` mới; file nguồn không bao giờ bị sửa.
- Gold và artifact thí nghiệm không phải nguồn sự thật của production.

## Kiểm thử

`dotnet test tests/DocxHeaderExtractor.Tests` (747 test, không gọi provider, trừ các test đánh dấu
rõ ràng). Thí nghiệm V6 nằm trong `V6Qualification` và các test `P7F1V6*`.
