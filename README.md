# DocxHeaderExtractor

Trích xuất cấu trúc tiêu đề (heading) từ file **.docx / .doc** bằng **OpenXML SDK** + **LLamaSharp**
chạy mô hình **GGUF lượng tử hoá** (`Llama-3.2-3B-Instruct-Q4_K_M`) **hoàn toàn trên CPU**.

## Ý tưởng

Gửi thẳng `word/document.xml` vào LLM là không khả thi: một tài liệu vài chục trang cho ra hàng trăm
nghìn token markup, trong đó gần như toàn bộ là rác đối với bài toán nhận diện tiêu đề.

Pipeline chia làm hai tầng:

```
.doc ──(LibreOffice/Word)──► .docx
                               │
                    ┌──────────▼───────────┐
                    │ Tầng 1: OpenXML SDK  │  đọc styles.xml + document.xml
                    │  – resolve basedOn   │  giữ lại: style, outlineLvl, bold,
                    │  – gộp run format    │  caps, size, align, numbering,
                    │  – chấm điểm luật    │  keepNext, pageBreakBefore
                    └──────────┬───────────┘
                               │  XML tinh gọn: chỉ ứng viên + <n c="k"/>
                    ┌──────────▼───────────┐
                    │ Tầng 2: LLamaSharp   │  GGUF Q4_K_M, CPU, greedy
                    │  – chia khối         │  GBNF liệt kê ép đầu ra
                    │  – GBNF liệt kê      │  {"h":[{"i":0,"l":1},…]}
                    └──────────┬───────────┘
                               │
                    Outline JSON / Markdown / CSV / XML
```

### Ba cơ chế chống ảo giác

1. **Mô hình không sinh văn bản tiêu đề.** Nó chỉ trả về chỉ số đoạn `i` và cấp `l`; nội dung
   luôn được lấy lại từ OpenXML, nên kết quả không bao giờ chứa chữ bịa.
2. **GBNF liệt kê** (`GrammarMode.Enumerated`, mặc định). Với mỗi khối, chương trình sinh grammar
   riêng có **cắm sẵn danh sách chỉ số**:

   ```gbnf
   root ::= "{\"h\":[" it0 "," it1 "," it2 "]}"
   it0  ::= "{\"i\":0,\"l\":" lvl "}"
   it1  ::= "{\"i\":7,\"l\":" lvl "}"
   it2  ::= "{\"i\":15,\"l\":" lvl "}"
   lvl  ::= [0-9]
   ```

   Mô hình chỉ còn tự do chọn **một chữ số cấp cho mỗi ứng viên** — không thể bỏ sót, không thể bịa
   chỉ số, không thể trả về JSON sai cú pháp. Đây là điểm khác biệt lớn nhất: khi để mô hình 3B tự do
   liệt kê, nó chỉ trả 3/6 ứng viên; khi ép liệt kê, nó trả đủ 6/6.
3. **Lưới an toàn theo style.** Đoạn có style Heading mà mô hình gán `l=0` vẫn được khôi phục
   (tắt bằng `--no-trust-styles`).

## Kết quả đo

Chạy `dhx eval bench` để tự đo lại. Số dưới đây đo trên một tài liệu hành chính thật
(898 đoạn, 48 ứng viên, 33 tiêu đề) — xem `bench/08-plph2.key`.

| Cấu hình | Precision | Đúng cấp | Thời gian |
|---|--:|--:|--:|
| Llama-3.2-3B, 40 ứng viên/khối | 73,3 % | 75,8 % | 4,3 phút |
| Qwen2.5-7B, 40 ứng viên/khối | 97,0 % | 93,8 % | 9,6 phút |
| + cấp đọc từ `w:outlineLvl` | 97,0 % | **100 %** | 9,6 phút |
| + 12 ứng viên/khối | 97,0 % | 100 % | 13,0 phút |
| + phủ quyết gạch đầu dòng | **100 %** | **100 %** | 12,3 phút |
| **+ tái dùng prefill prompt** | **100 %** | **100 %** | **9,2 phút** |

Lệnh cho cấu hình cuối:

```powershell
dhx extract "<file.docx>" -m models\Qwen2.5-7B-Instruct-Q4_K_M.gguf --ctx 8192 -f md
```

`--ctx 8192` là bắt buộc với Qwen: bộ tách token của nó sinh nhiều token hơn Llama cho tiếng
Việt, để 4096 sẽ tràn ngữ cảnh ngay khối đầu.

### Ba yếu tố thực sự tạo ra khác biệt

1. **Kích cỡ mô hình.** 3B → 7B đưa precision từ 73 % lên 97 %. Không có mẹo prompt nào bù được.
2. **Đọc cấp từ `w:outlineLvl` thay vì để mô hình đoán** (`LevelFromOutline`). Đây là đặc tả
   OOXML do người soạn đặt, chính xác hơn mọi suy luận từ hình thức.
3. **Khối ngắn** (`MaxCandidatesPerChunk = 12`). Grammar liệt kê buộc mô hình sinh một chữ số cho
   mỗi ứng viên **trong một chuỗi tự hồi quy**, nên một dãy `0` đúng sẽ kéo chữ số kế tiếp về `0`
   sai. Ở 40 ứng viên/khối có khối cho ra 7/40; cùng tài liệu ở 12 ứng viên/khối thì các tiêu đề
   đó đều đúng.

### Tăng tốc: tái dùng prefill (`ReusePromptPrefix`, mặc định bật)

Thời gian **không** nằm ở khâu sinh token mà ở khâu nạp prompt — đo được: cắt 90 % số token phải
sinh ra tiết kiệm 0 giây, còn mỗi khối thêm vào tốn ~55 giây. Mà prompt mỗi khối gồm 1098 token
phần chung (system + luật + ví dụ) giống hệt nhau, chỉ ~600 token XML là khác.

Nên phần chung được nạp một lần vào một `Conversation` gốc; mỗi khối `Fork()` ra nhánh dùng lại
nguyên KV cache đó rồi bị huỷ sau khi xong, nên các khối vẫn độc lập.

Phần khối nhanh hơn **58 %** trên bộ test, **38 %** trên tài liệu 898 đoạn, và mọi chỉ số giữ
nguyên trên cả 8 tài liệu. Không bảo đảm đúng từng bit: `BatchedExecutor` gộp batch khác nên vài
quyết định sát ranh giới bị lật, nhưng hai lưới an toàn (cấp từ `outlineLvl`, `TrustStyles`) hấp
thụ hết. `--no-reuse-prefix` để tắt.

### Những hướng đã thử và ĐÃ BỎ

Ghi lại để khỏi thử lại. Tất cả đều đo trên cùng tài liệu, cùng mô hình.

| Hướng | Kỳ vọng | Đo được |
|---|---|---|
| Viết lại prompt theo nguyên tắc ngữ nghĩa | Lọc bớt rác | Không loại thêm dòng nào, **và phá cấp bậc** (gần hết thành cấp 1) |
| Quét hai lượt, đánh dấu chỗ bất đồng | Khoanh vùng chỗ sai | 2× thời gian; 16 cờ báo động, **0 cờ trúng lỗi thật** |
| Bỏ hỏi ứng viên đã có style | Nhanh hơn, kết quả không đổi | Nhanh 24 % nhưng precision **100 % → 94,1 %** |
| Đầu ra chỉ là dãy chữ số | Cắt 90 % token sinh ⇒ nhanh hơn nhiều | **Không nhanh hơn** (742 s vs 738 s) và precision **→ 73,3 %** |
| Backend CUDA 12 | Tăng tốc bằng GPU | Driver 528.79 (CUDA 12.0) quá cũ, native lib không nạp, rơi về CPU và **chậm hơn 15 %** |

Hai bài học rút ra từ bảng này:

- **Không tín hiệu tự động nào khoanh được chỗ mô hình sai.** Cờ bất đồng hai lượt và cờ
  `src=Style` đều trỏ vào những dòng đúng. Mô hình sai ở chỗ nó tự tin.
- **Thời gian nằm ở khâu nạp prompt, không phải khâu sinh token.** Cắt 90 % token sinh ra mà
  tổng thời gian đứng yên.

## Yêu cầu

- .NET SDK 9.0
- ~3 GB RAM trống khi chạy mô hình 3B Q4_K_M với ngữ cảnh 4096
- Để đọc `.doc` (nhị phân đời cũ): LibreOffice **hoặc** Microsoft Word.
  OpenXML SDK không đọc trực tiếp định dạng này.

## Cài đặt

```powershell
dotnet build -c Release
.\scripts\download-model.ps1          # tải ~1.9 GB vào .\models
```

## Dùng nhanh

```powershell
dhx sample samples\mau.docx           # tạo file .docx mẫu
dhx xml samples\mau.docx              # xem tầng OpenXML lọc ra gì, không gọi mô hình
dhx extract samples\mau.docx -f md    # chạy đầy đủ
dhx extract samples\mau.docx --no-llm # chỉ dùng luật, không cần mô hình
dhx extract .\tai-lieu\ -f csv -o outline.csv
dhx info models\Llama-3.2-3B-Instruct-Q4_K_M.gguf
```

Đường dẫn mô hình được tìm theo thứ tự: `--model` → biến môi trường `DHX_MODEL`
→ `appsettings.json` → file `.gguf` trong `./models`.

### Kết quả thực tế trên file mẫu

```
  OpenXML: 19 đoạn → 6 ứng viên (4 theo style, 2 theo heuristic)
  Chia thành 1 khối XML (ngân sách 2200 token/khối)
    khối 1/1: 6 ứng viên → 6 tiêu đề (56010 ms)
      ↳ {"h":[{"i":0,"l":1},{"i":2,"l":2},{"i":5,"l":2},{"i":7,"l":1},{"i":15,"l":2},{"i":17,"l":1}]}

- Chương 1. Tổng quan hệ thống      <!-- lvl=1 i=0  src=Model -->
  - 1.1. Phạm vi                    <!-- lvl=2 i=2  src=Model -->
  - 1.2. Thuật ngữ                  <!-- lvl=2 i=5  src=Model -->
- PHỤ LỤC A – BẢNG ĐỐI CHIẾU        <!-- lvl=1 i=7  src=Model -->
  - 2.1 Kết quả thử nghiệm          <!-- lvl=2 i=15 src=Model -->
- Chương 2. Kết luận                <!-- lvl=1 i=17 src=Model -->
```

Hai dòng `PHỤ LỤC A` và `2.1 Kết quả thử nghiệm` **không dùng style Heading** — chỉ in đậm/chữ hoa/canh
giữa — nhưng vẫn được nhận đúng. Sáu ô trong bảng dữ liệu bị loại ngay từ tầng OpenXML.

`src` cho biết tiêu đề đến từ mô hình (`Model`), từ lưới an toàn theo style (`Style`),
hay từ luật khi chạy `--no-llm` (`Heuristic`).

## Cấu trúc mã nguồn

| Thành phần | Vai trò |
|---|---|
| `OpenXmlLayer/StyleResolver.cs` | Làm phẳng `styles.xml`: đi hết chuỗi `w:basedOn`, lấy `docDefaults` |
| `OpenXmlLayer/DocxSlimExtractor.cs` | Duyệt body (kể cả bảng, `w:sdt`), gộp định dạng run, chuẩn hoá text, bỏ nội dung `w:del` |
| `OpenXmlLayer/HeadingHeuristics.cs` | Chấm điểm ứng viên: style heading, `outlineLvl`, đậm/hoa/cỡ chữ/canh giữa, mẫu `Chương/Điều/1.2.3` |
| `OpenXmlLayer/SlimXmlSerializer.cs` | Sinh XML tinh gọn `<p i s out lvl b caps sz al num kn pb tbl>` + `<n c="k"/>` + `<ctx>` |
| `OpenXmlLayer/LegacyDocConverter.cs` | `.doc/.rtf/.odt` → `.docx` qua LibreOffice headless, dự phòng Word COM |
| `Chunking/SlimXmlChunker.cs` | Cắt khối theo ngân sách token **và** trần số ứng viên, chồng lấn N ứng viên ở mép |
| `Llm/HeaderPrompt.cs` | System prompt + one-shot + **sinh GBNF liệt kê** + template Llama 3 dự phòng |
| `Llm/ModelHeading.cs` | Đọc JSON chịu lỗi: nhận cả `{"h":[…]}` lẫn `{"headings":[…]}`, vớt vát khi bị cắt |
| `Llm/LlamaHeaderExtractor.cs` | Nạp GGUF, `StatelessExecutor`, greedy, tính `MaxTokens` theo số ứng viên |
| `Pipeline/HeaderExtractionPipeline.cs` | Ghép các tầng, bỏ phiếu cấp giữa các khối chồng lấn, chuẩn hoá cấp |

## Tuỳ chọn đáng chú ý

| Tuỳ chọn | Ý nghĩa |
|---|---|
| `--threshold 0.45` | Ngưỡng điểm để đoạn không có style heading trở thành ứng viên. Giảm → recall cao hơn, tốn token hơn |
| `--chunk-tokens 2200` | Ngân sách token mỗi khối XML. Tăng thì ít lượt suy luận hơn nhưng cần `--ctx` lớn hơn |
| `--ctx 4096` | Cửa sổ ngữ cảnh. Phải `> chunk-tokens + max-out + 800` |
| `-t/--threads` | Số luồng CPU. Mặc định = **số nhân vật lý ước lượng** (một nửa số luồng logic), vì llama.cpp chậm đi khi vượt số nhân vật lý |
| `--show-raw` | In nguyên văn JSON mô hình trả về từng khối |
| `--free-grammar` | GBNF chỉ ép lược đồ, để mô hình tự chọn liệt kê — dùng để so sánh, kém tin cậy hơn |
| `--no-grammar` | Tắt GBNF hoàn toàn (vẫn có bộ đọc JSON chịu lỗi) |
| `--no-trust-styles` | Không khôi phục heading theo style — dùng khi cần đánh giá riêng chất lượng mô hình |
| `--dump-xml out.xml` | Ghi XML tinh gọn kèm `role`/`sc` để soi bộ lọc |
| `--structural-only` | Tắt toàn bộ luật theo từ ngữ, xem bên dưới |
| `--compact` (lệnh `xml`) | Chỉ in phần ứng viên — đúng nội dung gửi cho mô hình |

## Ranh giới tín hiệu cấu trúc và luật từ ngữ

Bộ lọc tách làm hai nhóm, và nhóm thứ hai tắt được bằng `--structural-only`:

**Luôn bật — không phụ thuộc ngôn ngữ tài liệu:**

- Style dựng sẵn OOXML (`Heading1..9`, `Title`, `Subtitle`, `TOCHeading`). Đây là định danh do
  ECMA-376 quy định, Word ghi y hệt dù giao diện chạy tiếng gì — không phải từ vựng tiếng Anh.
- `w:outlineLvl`.
- Dòng mục lục nhận qua hyperlink trỏ neo `_Toc…`/`_heading…` và nhóm style `TOC1..TOC9`.
- Định dạng thuần: đậm, chữ hoa, cỡ chữ **so với cỡ thân bài thực tế của chính tài liệu**,
  canh lề, `keepNext`, ngắt trang, độ sâu bảng.
- Ký hiệu đánh số: `1.2.3`, `IV.`, `A)`, gạch đầu dòng, dấu câu cuối dòng. `\p{Lu}` bắt chữ hoa
  Unicode nên không giới hạn ở bảng chữ cái Latin.

**Chỉ khi bật luật từ ngữ (mặc định bật, tắt bằng `--structural-only`):**

- Tên style bản địa hoá do người dùng tự đặt: `Tiêu đề 2`, `Überschrift 2`, `Заголовок 2`.
- Từ khoá mở đầu: `Chương`, `Điều`, `Phụ lục`, `Chapter`, `Article`…
- Mẫu chú thích: `Hình 2.4.`, `Bảng 1.2`, `Figure 3:`…

Ba nhóm sau là mapping cứng thật sự — chỉ đúng với vài thứ tiếng và phải bổ sung tay khi gặp tiếng
khác. Trên một khóa luận 150 trang, tắt chúng đi cho 99 ứng viên thay vì 105: phần việc nặng do tín
hiệu cấu trúc làm, không phải do bảng từ khoá. Với tài liệu ngoài các ngôn ngữ đã liệt kê, dùng
`--structural-only` rồi để mô hình lo phần ngữ nghĩa.

Mốc so sánh cỡ chữ lấy theo **mode có trọng số theo số ký tự**, không lấy `docDefaults`: rất nhiều
tài liệu đặt 14pt cho toàn bộ nội dung trong khi `docDefaults` vẫn khai 11pt, lấy `docDefaults` sẽ
khiến mọi đoạn đều bị chấm là "chữ to hơn thân bài".

## Hiệu năng đo được

Máy thử: Intel i7-8750H (6 nhân / 12 luồng), Windows 11, backend AVX2, không GPU.

| Việc | Thời gian |
|---|---|
| Nạp mô hình 3B Q4_K_M | ~6 s |
| Một khối 6 ứng viên (~1.5k token prompt) | ~40–56 s |
| Toàn bộ tầng OpenXML (file 19 đoạn) | ~0.4 s |
| `--no-llm` toàn bộ | ~0.4 s |

Chi phí gần như toàn bộ nằm ở **prompt eval**, mà system prompt (kèm ví dụ one-shot) chiếm phần lớn và
bị đánh giá lại cho từng khối vì `StatelessExecutor` tạo ngữ cảnh mới mỗi lượt. Với tài liệu dài, tăng
`--chunk-tokens` (và `--ctx` tương ứng) để giảm số khối là cách rẻ nhất để tăng tốc.

## Kiểm thử

```powershell
dotnet test
```

23 test, không cần mô hình: kế thừa style/`outlineLvl`, nhận diện heading định dạng thủ công,
loại ô bảng, tỉ lệ nén XML, cắt khối + chồng lấn + trần ứng viên, sinh GBNF liệt kê,
parse JSON hỏng/bị cắt, chuẩn hoá cấp.

## Giới hạn đã biết

- **`.doc` chưa chạy thử được trên máy phát triển** vì không có LibreOffice lẫn Word;
  đường dẫn chuyển đổi đã viết đầy đủ (LibreOffice headless → Word COM) và khi thiếu cả hai thì
  chương trình báo lỗi rõ ràng, thoát mã 1. Nhánh `.docx` đã chạy thật đầu-cuối.
- Mô hình 3B trên CPU chậm; nếu tài liệu vốn dùng style Heading chuẩn thì `--no-llm` cho kết quả
  tương đương trong chưa tới một giây.
- `w:numbering` chỉ được dùng làm tín hiệu; chương trình không dựng lại số mục đầy đủ từ `numbering.xml`.
- Ước lượng token dùng hằng số 3 ký tự/token; với tài liệu nhiều ký tự Latin không dấu, ngân sách
  thực tế sẽ dư một chút.
