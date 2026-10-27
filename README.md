# DocxHeaderExtractor

Trích xuất cây tiêu đề (heading) từ `.docx`, `.doc` và `.pdf`. Parser đọc nguồn và giữ toạ độ;
mô hình ngôn ngữ chỉ quyết định **ý nghĩa** (occurrence nào là tiêu đề); harness bind lại từng
claim vào đúng vị trí trong nguồn và từ chối mọi thứ không bind được.

## Kiến trúc

Mỗi định dạng có đúng một lane, không có đường dự phòng:

| | DOCX | PDF |
|---|---|---|
| Nguồn | paragraph OpenXML (`OpenXmlDocumentSource`, `DocxAgentSource`): style, outline level, numbering, định dạng run, vị trí trong bảng/text box | segment atom theo dòng hình ảnh `L{row}:S{segment}`, typography `PDF_SOURCE_FACTS_V3` |
| Request | V6 heading agent (Qwen, một hội thoại mỗi cửa sổ, đọc facts bằng tool trước khi trả lời), prompt và 6 tool riêng cho DOCX | unit `PDF_STRUCTURAL_GEOMETRY_SELECTION_UNITS_V2` + instruction membership V12, kèm geometry OpenCV gọn và ảnh trang xám 960 px Q50; GROUP và placement riêng |
| Contract | các paragraph được chọn theo alias nguồn, parser nghiêm ngặt, bước nhóm do mô hình chia thành heading | `PDF_UNIT_SELECT_SOURCE_BOUND_V2`: SELECT phải khớp sourceText; mỗi cặp grouping phải khớp alias/selectedText, đầy đủ và đúng thứ tự; C# không tự quyết định ngữ nghĩa |
| Chia request | cửa sổ cố định 48 paragraph quyết định, 20 ngữ cảnh mỗi phía | theo trang sở hữu unit, trần 786.432 byte mỗi request SELECT cuối; 8 request SELECT song song |

Profile PDF mặc định được chủ dự án freeze ngày 26/10: V14 unit + V12 instruction + ảnh trang thích ứng,
ngân sách hoàn tất thích ứng, geometry và ảnh trang cache MessagePack dưới `.dhx-evidence/source`.
Crop lề ảnh, ảnh 1-bit và ShapeReview có cờ nhưng mặc định tắt. Đây là freeze cấu hình, chưa có kết quả
chất lượng hay SLA trên Qwen/OpenRouter. Xem [profile đã freeze](docs/qualification/pdf-v14-v12-frozen-profile-20261026.md)
và [các thử nghiệm tối ưu request](docs/qualification/pdf-request-optimization-20261026.md).

Evidence gửi mô hình là dữ kiện nguồn (text, số đo typography, vị trí trang, đánh số); harness
không gắn nhãn "ứng viên" hay phán đoán cỡ chữ. Ownership không chồng lấn: halo chỉ để đọc, claim
bắt đầu ở atom không thuộc leaf bị từ chối. Quote chỉ khác atom ở khoảng trắng hai đầu được hiểu
là cả atom; mọi khác biệt khác bị từ chối. Chi tiết: [docs/architecture/production-request-architecture-v2.md](docs/architecture/production-request-architecture-v2.md).

Host: CLI (`dhx`) và Web UI (`dhx-ui`) đều chạy thẳng tuyến V6 qua `DocumentRunner`
(không còn project `AgentHarness` đứng giữa; phần chạy lane nằm trong `DocumentProcessing/Harness`, xem
[docs/architecture/current.md](docs/architecture/current.md)).

Cấu trúc project:

| Project | Vai trò |
|---|---|
| `Contracts` | Kiểu dùng chung: model, `IHeadingSelector`, `IModelProvider` |
| `Sources` | Đọc nguồn OOXML, PDF và scan; snapshot nguồn, cache |
| `HeadingAgent` | Agent V6 cho PDF, DOCX và scan |
| `DocumentProcessing` | Pipeline theo bước: `Intake` → `Lanes` + `Harness` → `Selection` → `Binding` → `Placement` → `Hierarchy` → `Structure` → `Audit` → `Output` → `Writeback` |
| `App` | Nối agent vào pipeline; `OpenRouterProvider`; điểm gọi của CLI và Web |
| `Infrastructure` | Lưu lượt chạy và telemetry, đọc file đầu vào |
| `Cli` / `Web` | Hai host |

## Yêu cầu

- .NET SDK 9.0
- Mô hình: `qwen/qwen3.7-flash` qua OpenRouter, key trong `OPENROUTER_API_KEY` (không cần khi chạy `--no-llm`)
- Để đọc `.doc`: LibreOffice **hoặc** Microsoft Word (OpenXML SDK không đọc trực tiếp định dạng này)

## Cài đặt

```powershell
dotnet build -c Release
```

## Dùng nhanh

```powershell
dhx extract samples\mau.docx -f md    # chạy đầy đủ
dhx extract samples\mau.docx --no-llm # chỉ đọc nguồn, không gọi mô hình
dhx extract bao-cao.pdf -f json
dhx extract .\tai-lieu\ -f csv -o outline.csv
dhx help                              # toàn bộ tuỳ chọn
```

Ghi outline vào bản sao `.docx` (file nguồn không bao giờ bị sửa):

```powershell
dhx extract bao-cao.docx --write-docx bao-cao.outline.docx
```

### OpenRouter

Mọi lần gọi mô hình đều đi qua OpenRouter. Đặt API key trong biến môi trường của tiến trình/server,
không ghi vào source hoặc trình duyệt:

```powershell
$env:OPENROUTER_API_KEY = "sk-or-v1-..."
dhx extract tai-lieu.docx -f json
```

Mô hình cố định là mô hình các agent đã được kiểm định: `qwen/qwen3.7-flash`, ghim route `Alibaba`
(`allow_fallbacks=false`), streaming kèm `usage`. Selection/grouping bật reasoning; placement tắt reasoning
và gửi `data_collection=deny`. Agent từ chối phản hồi đến từ model hoặc route khác; request gửi
`require_parameters=true`. Transport sở hữu retry lỗi tạm thời: tối đa 3 retry mỗi lượt, tổng thời gian chờ
tối đa 30 giây, tôn trọng `Retry-After`; tầng trên không khởi động lại chuỗi retry đã hết.
Giới hạn mặc định 8 HTTP request đang chạy cho cùng API account trong một tiến trình; chỉnh bằng
`DHX_OPENROUTER_MAX_CONCURRENCY`. Xem [chính sách retry](docs/architecture/provider-retry.md).
Nội dung tài liệu vẫn được gửi ra dịch vụ bên ngoài; không dùng
cho tài liệu mật khi chưa được phép.

### Web UI

Chạy `.\dhx-ui.cmd`, rồi mở URL được in sau dòng `dhx-ui đang chạy` (mặc định
`http://localhost:5099`). Launcher build trực tiếp từ source và dùng
`dotnet watch --no-hot-reload`: lưu thay đổi C#, project tham chiếu hoặc `wwwroot` sẽ
build và restart backend. Giữ cửa sổ lệnh này mở; `Ctrl+C` dừng watcher. Dừng backend
publish cũ một lần trước khi chuyển sang launcher này để tránh trùng cổng. Chạy DLL
trong `out-vulkan-ui` trực tiếp sẽ không tự cập nhật theo source.

Lệnh .NET tương đương, chạy tại gốc repo:

```powershell
dotnet watch --project src/DocxHeaderExtractor.Web/DocxHeaderExtractor.Web.csproj --no-hot-reload run -c Release --no-launch-profile
```

Chỉ build, không khởi động server:

```powershell
dotnet build src/DocxHeaderExtractor.Web/DocxHeaderExtractor.Web.csproj -c Release
```

Backend in URL và build SHA sau khi bind thành công; `/api/build` trả version của
assembly Web và HeadingAgent thực sự đang được nạp. Có thể đổi cổng bằng
`.\dhx-ui.cmd --urls http://localhost:5101` hoặc `DHX_UI_URL` (biến môi trường ưu tiên).
Build lỗi không cho phép khởi động bản DLL cũ thay thế. Auto restart có thể ngắt
execution đang chạy, nên hoàn tất phép đo live trước khi lưu code.

Trang cho chọn backend, chạy trích xuất, duyệt từng
heading và tải bản `.docx` đã ghi outline sau khi duyệt xong. Nhãn người dùng sửa được lưu
append-only tại `%LOCALAPPDATA%\DocxHeaderExtractor\verified-corrections.jsonl` (đổi bằng
`DHX_CORRECTION_MEMORY`); pipeline không đọc ngược lại file này.

## Kiểm thử

```powershell
dotnet test tests/DocxHeaderExtractor.Tests         # bộ test V6, không gọi provider
```

Bộ test có 445 test (khoảng 1–3 phút), không gọi provider; lọc nhanh bằng `--filter "Category!=Slow"`. Hành vi
được giữ bằng hai golden: `Golden/v6-parity-src089.json.gz` (đầu ra tuyến V6, `PdfHeadingAgentParityTests`) và
`Golden/agent-wire.json` (prompt, khai báo tool và kết quả tool gửi cho model, `AgentWireGoldenTests`). Các test cũ
(web, MCP, Gold, các thí nghiệm V4-V5/P6-P7, code V6 chỉ để qualify) đã xóa theo quyết định của chủ dự án; xem lịch sử git.
Gold duy nhất nằm ở `eval/a99-closed-loop/gold/`; mỗi thay đổi Gold cần người duyệt từng mục.
Cấu trúc code hiện tại: [docs/architecture/current.md](docs/architecture/current.md).
Thay hoặc thêm agent: [docs/architecture/extending.md](docs/architecture/extending.md).

## Giới hạn đã biết

- `.doc` cần LibreOffice hoặc Word để chuyển đổi; thiếu cả hai thì chương trình báo lỗi rõ ràng.
- PDF scan (không có text layer) không có nguồn để bind; kết quả rỗng kèm lý do `pdf-no-text-layer`.
- Profile PDF V6 trước đây đã được đo trên PDF072 và PDF076; hai output PDF072 khớp Gold đã sửa ngày 25/10.
  Prompt tổng quát V7 cần qualification LLM riêng; số đo V6 không chứng minh chất lượng hoặc thời gian V7.
