# DocxHeaderExtractor

Trích xuất cây tiêu đề (heading) từ `.docx`, `.doc` và `.pdf`. Parser đọc nguồn và giữ toạ độ;
mô hình ngôn ngữ chỉ quyết định **ý nghĩa** (occurrence nào là tiêu đề); harness bind lại từng
claim vào đúng vị trí trong nguồn và từ chối mọi thứ không bind được.

## Kiến trúc

Mỗi định dạng có đúng một lane, không có đường dự phòng:

| | DOCX | PDF |
|---|---|---|
| Nguồn | paragraph OpenXML (`OpenXmlDocumentSource`, `DocxAgentSource`): style, outline level, numbering, định dạng run, vị trí trong bảng/text box | segment atom theo dòng hình ảnh `L{row}:S{segment}`, typography `PDF_SOURCE_FACTS_V3` |
| Request | V6 heading agent (Qwen, một hội thoại mỗi cửa sổ, đọc facts bằng tool trước khi trả lời), prompt và 6 tool riêng cho DOCX | V6 heading agent (cùng phương pháp; prompt và 8 tool riêng cho PDF; từ 2026-10-14) |
| Contract | các paragraph được chọn theo alias nguồn, parser nghiêm ngặt, bước nhóm do mô hình chia thành heading | các dòng được chọn theo alias nguồn, parser nghiêm ngặt, các dòng liên tiếp cùng khối layout là một heading |
| Chia request | cửa sổ cố định 48 paragraph quyết định, 20 ngữ cảnh mỗi phía | segment layout (nguyên trang, hoặc khối layout; tối đa 48 dòng quyết định, 20 dòng ngữ cảnh mỗi phía) |

Evidence gửi mô hình là dữ kiện nguồn (text, số đo typography, vị trí trang, đánh số); harness
không gắn nhãn "ứng viên" hay phán đoán cỡ chữ. Ownership không chồng lấn: halo chỉ để đọc, claim
bắt đầu ở atom không thuộc leaf bị từ chối. Quote chỉ khác atom ở khoảng trắng hai đầu được hiểu
là cả atom; mọi khác biệt khác bị từ chối. Chi tiết: [docs/architecture/production-request-architecture-v2.md](docs/architecture/production-request-architecture-v2.md).

Host: CLI (`dhx`) và Web UI (`dhx-ui`) đều chạy thẳng tuyến V6 qua `V6DocumentRunner`
(không còn harness; xem [docs/architecture/current.md](docs/architecture/current.md)).

Cấu trúc project:

| Project | Vai trò |
|---|---|
| `Sources` | Hợp đồng dùng chung (cũ là `Core`), đọc nguồn OOXML và PDF, `IHeadingSelector` |
| `DocumentProcessing` | Pipeline DOCX/PDF, router, bước ghép, projection, writeback |
| `HeadingAgent` | Agent V6 cho PDF và DOCX |
| `V6Runner` | Nối agent vào pipeline; điểm gọi của CLI và Web |
| `Application` / `Infrastructure` | Hợp đồng runtime; transport provider và factory route |
| `Cli` / `Web` | Hai host |
| `V6Qualification` | Thí nghiệm V6 đã qualify |

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

Mặc định là cấu hình đã re-baseline: `qwen/qwen3.7-flash` ghim route `Alibaba`
(`allow_fallbacks=false`), `reasoning=none`, streaming (`stream=true`, `usage.include=true`).
Đổi model bằng `--openrouter-model` hoặc `OPENROUTER_MODEL` (model khác không bị ghim route ngầm);
ghim route bằng `OPENROUTER_PROVIDER_ROUTE`. Request luôn gửi `data_collection=deny`,
`require_parameters=true` và ghi rõ `zdr` (mặc định `false`, vì endpoint Alibaba chưa có chứng nhận ZDR).
Mỗi lần gọi có deadline transport 300 s (chỉ tính truyền tải; parse và kiểm contract nằm ngoài) và
tối đa 2 lần retry cho 429/5xx/lỗi mạng/stream không hoàn tất, tôn trọng `Retry-After`; một stream đã
hoàn tất không bao giờ được gửi lại, và lỗi nội dung (JSON/contract/`finish_reason=length`) không bị
retry như lỗi mạng. Nội dung tài liệu vẫn được gửi ra dịch vụ bên ngoài;
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

## Kiểm thử

```powershell
dotnet test tests/DocxHeaderExtractor.Tests         # bộ test V6, không gọi provider
```

Từ 2026-10-14 bộ test chỉ còn thí nghiệm V6 của PDF heading agent: `PdfHeadingAgent*` (agent production, parity với code V6
đã qualify), `PdfV6SelectedLinesLaneTests` và `P7F1V6*` (code V6 trong `DocxHeaderExtractor.V6Qualification`). Các test cũ
(DOCX, web, MCP, Gold, các thí nghiệm V4-V5/P6-P7) đã xóa theo quyết định của chủ dự án; xem lịch sử git.
Gold duy nhất nằm ở `eval/a99-closed-loop/gold/`; mỗi thay đổi Gold cần người duyệt từng mục.
Provider run chỉ chạy dưới manifest thí nghiệm đã đóng băng (`PdfExperimentExecutionGate`).

## Giới hạn đã biết

- `.doc` cần LibreOffice hoặc Word để chuyển đổi; thiếu cả hai thì chương trình báo lỗi rõ ràng.
- PDF scan (không có text layer) không có nguồn để bind; kết quả rỗng kèm lý do `pdf-no-text-layer`.
- Chất lượng lane PDF hiện hành cần được đo lại: style facts đã đổi sau lần đo T3B
  (xem mục "Open" trong tài liệu kiến trúc).
