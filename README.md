# DocxHeaderExtractor

Trích xuất cây tiêu đề (heading) từ `.docx`, `.doc` và `.pdf`. Parser đọc nguồn và giữ toạ độ;
mô hình ngôn ngữ chỉ quyết định **ý nghĩa** (occurrence nào là tiêu đề); harness bind lại từng
claim vào đúng vị trí trong nguồn và từ chối mọi thứ không bind được.

## Kiến trúc

Mỗi định dạng có đúng một lane, không có đường dự phòng:

| | DOCX | PDF |
|---|---|---|
| Nguồn | paragraph OpenXML (`OpenXmlDocumentSource`) | segment atom theo dòng hình ảnh `L{row}:S{segment}`, typography `PDF_SOURCE_FACTS_V3` |
| Request | `V2_ATTENTION_FREE` | `V4_SEMANTIC_FUNCTION_SINGLE_AUTHORITY` (một `semanticFunction` đóng cho mỗi claim) |
| Contract | `DocxAliasSpan` (alias + UTF-16 span) | `PdfSemanticFunctionMembershipV1` (structured source parts) |
| Chia request | `FIXED_OWNED_COUNT_120` | P05 resource-bounded (90 KB, 96 atom sở hữu, halo 8) |

Evidence gửi mô hình là dữ kiện nguồn (text, số đo typography, vị trí trang, đánh số); harness
không gắn nhãn "ứng viên" hay phán đoán cỡ chữ. Ownership không chồng lấn: halo chỉ để đọc, claim
bắt đầu ở atom không thuộc leaf bị từ chối. Quote chỉ khác atom ở khoảng trắng hai đầu được hiểu
là cả atom; mọi khác biệt khác bị từ chối. Chi tiết: [docs/architecture/production-request-architecture-v2.md](docs/architecture/production-request-architecture-v2.md).

Host: CLI (`dhx`), Web UI (`dhx-ui`), MCP server (`dhx-mcp`) — cùng đi qua `DocumentAgentHarness`
(policy, guardrail, source-grounding validator, cổng duyệt của người trước khi ghi).

## Yêu cầu

- .NET SDK 9.0
- Mô hình: GGUF local (LLamaSharp), hoặc OpenRouter, LM Studio, SGLang/vLLM qua HTTP
- Để đọc `.doc`: LibreOffice **hoặc** Microsoft Word (OpenXML SDK không đọc trực tiếp định dạng này)

## Cài đặt

```powershell
dotnet build -c Release
.\scripts\download-model.ps1          # tải mô hình GGUF vào .\models (chỉ cần cho backend local)
```

Đường dẫn mô hình local được tìm theo thứ tự: `--model` → `DHX_MODEL` → `appsettings.json` →
file `.gguf` trong `./models`.

## Dùng nhanh

```powershell
dhx extract samples\mau.docx -f md    # chạy đầy đủ
dhx extract samples\mau.docx --no-llm # chỉ dùng luật OpenXML, không cần mô hình
dhx extract bao-cao.pdf --openrouter -f json
dhx extract .\tai-lieu\ -f csv -o outline.csv
dhx info models\model.gguf            # metadata mô hình
dhx help                              # toàn bộ tuỳ chọn
```

Ghi outline vào bản sao `.docx` (file nguồn không bao giờ bị sửa):

```powershell
dhx extract bao-cao.docx --openrouter --write-docx bao-cao.outline.docx
```

### OpenRouter

Đặt API key trong biến môi trường của tiến trình/server, không ghi vào source, `appsettings.json`
hoặc trình duyệt:

```powershell
$env:OPENROUTER_API_KEY = "sk-or-v1-..."
dhx extract tai-lieu.docx --openrouter -f json
```

Đổi model bằng `--openrouter-model` hoặc `OPENROUTER_MODEL`. Request gửi provider preferences
`zdr=true`, `data_collection=deny`, `require_parameters=true`; không có endpoint đáp ứng thì
pipeline báo lỗi thay vì hạ mức riêng tư. Nội dung tài liệu vẫn được gửi ra dịch vụ bên ngoài;
không dùng cho tài liệu mật khi chưa được phép.

### LM Studio

```powershell
$env:LMSTUDIO_ENDPOINT = "http://127.0.0.1:1234/v1/chat/completions"
$env:LMSTUDIO_MODEL = "model-identifier-from-lm-studio"
$env:LMSTUDIO_API_KEY = "local-token-if-enabled"
$env:LMSTUDIO_CONTEXT_SIZE = "16384"
.\dhx-ui.cmd
```

Endpoint bắt buộc là loopback (`localhost`, `127.0.0.1`, `::1`). CLI tương đương:
`dhx extract tai-lieu.docx --lmstudio --lmstudio-model "model-identifier" -f json`.

### Web UI

`.\dhx-ui.cmd`, mở `http://localhost:5099`. Trang cho chọn backend, chạy trích xuất, duyệt từng
heading và tải bản `.docx` đã ghi outline sau khi duyệt xong. Nhãn người dùng sửa được lưu
append-only tại `%LOCALAPPDATA%\DocxHeaderExtractor\verified-corrections.jsonl` (đổi bằng
`DHX_CORRECTION_MEMORY`); pipeline không đọc ngược lại file này.

### MCP (LM Studio/Bionic gọi DocxHeaderExtractor)

```powershell
.\scripts\publish-lmstudio-mcp.ps1 -Model "model-identifier-from-v1-models"
```

Script sinh `out-mcp\dhx-mcp.dll` và `out-mcp\lmstudio-mcp.json`; chép cấu hình vào
**Program → Install → Edit mcp.json** của LM Studio (mẫu:
[docs/lmstudio-mcp.example.json](docs/lmstudio-mcp.example.json)). MCP công khai ba tool
read-only:

- `get_docx_extractor_status`: kiểm tra API/model/root được phép;
- `extract_docx_headings`: xác thực file, xếp job nền và trả `jobId` ngay;
- `get_docx_extraction_result`: trả `Queued`/`Running` hoặc outline khi hoàn tất.

`DHX_MCP_ALLOWED_ROOTS` là danh sách thư mục tuyệt đối (phân cách `;`); path traversal, file ngoài
root và file quá 50 MB bị chặn trước pipeline. MCP không có tool shell và không có tool writeback.
Thêm `"DHX_MCP_RULES_ONLY": "true"` vào `env` để thử parser mà không cần LM Studio Local Server.

## Kiểm thử

```powershell
.\scripts\Invoke-A99TestTier.ps1                     # CORE_DETERMINISTIC, không gọi provider
.\scripts\Invoke-A99TestTier.ps1 -Tier All           # toàn bộ suite
```

Tier được khai trong [docs/testing/a99-test-suite-manifest.v1.json](docs/testing/a99-test-suite-manifest.v1.json).
Gold duy nhất nằm ở `eval/a99-closed-loop/gold/`; mỗi thay đổi Gold cần người duyệt từng mục.
Provider run chỉ chạy dưới manifest thí nghiệm đã đóng băng (`PdfExperimentExecutionGate`).

## Giới hạn đã biết

- `.doc` cần LibreOffice hoặc Word để chuyển đổi; thiếu cả hai thì chương trình báo lỗi rõ ràng.
- PDF scan (không có text layer) không có nguồn để bind; kết quả rỗng kèm lý do `pdf-no-text-layer`.
- Chất lượng lane PDF hiện hành cần được đo lại: style facts đã đổi sau lần đo T3B
  (xem mục "Open" trong tài liệu kiến trúc).
