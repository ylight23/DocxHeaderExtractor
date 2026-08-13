# Đáp án do người dùng xác nhận

Ba tài liệu thật, đáp án **do người kiểm**, không phải do model gán. Đây là nền của mọi con số
trong `handoff.md` từ §41 trở đi — TODO mục 4 (thắt cổ chai của cả dự án) bắt đầu đóng ở đây.

| file | tài liệu | mục | chế độ | đã chấm |
|---|---|--:|---|---|
| `kltn-style.key` | khoá luận VTV3 | 68 | style-declared | **P/R/cấp/cha = 100%** |
| `bao-cao-thuc-tap.key` | báo cáo thực tập MBBank | 29 | numbering-driven | **P/R/cấp/cha = 100%** |
| `plph1-dqp.outline` | thiết kế phân hệ ĐQP | 41 | vn-administrative | chưa — thiếu file .docx |

## Vì sao ba luật khác nhau

| | chọn mục | cấp |
|---|---|---|
| khoá luận | style Heading | độ sâu số gõ tay + 1 |
| báo cáo thực tập | cặp `(numId, ilvl)` | `ilvl + 1` |
| thiết kế phân hệ | ký hiệu gõ tay `I./1./3.1./a)` | **cha gần nhất + 1** |

Đúng nguyên tắc N1 của spec: *"Không tồn tại một luật deterministic dùng chung."* Ba tài liệu thật,
ba luật, và mỗi luật áp nhầm sang tài liệu kia đều hỏng — đã đo (§42, §43).

## `toc-derived/` — đáp án ứng viên, CHƯA qua người duyệt

Sinh bằng `dhx toc-keys <thư-mục|file.docx>` (`Core/Eval/TocAnswerKeyGenerator.cs`): khớp mục lục
do Word tự sinh với TOÀN BỘ đoạn thân bài (không lọc qua ứng viên của pipeline heuristic — khớp với
đầu ra của chính pipeline đang nghi ngờ thì không đo được gì độc lập). File đạt ≥80% khớp (mặc định,
đổi bằng `--toc-match-threshold`) mới được ghi `.key`, dùng `@stableId` để không lệch khi đổi tuỳ
chọn trích xuất.

Nếu cần tận dụng phần khớp được của một mục lục lỗi thời/không đầy đủ, dùng thêm `--toc-partial`.
Các file này được đánh dấu `partial_toc` trong header và chỉ chứa những mục TOC khớp chính xác,
một-nghĩa với thân bài; không được đọc như outline đầy đủ.

**Không phải đáp án người kiểm.** Mục lục có thể lỗi thời (tác giả sửa tiêu đề mà không refresh) —
luôn đọc dòng `# Khớp n/N mục` ở đầu mỗi file trước khi dùng làm nền so sánh. Chạy trên corpus
`todo10_8/heading_corpus_95_word` (95 file) cho **0/95** đạt ngưỡng: phần lớn (86/95) là bản
PDF→DOCX không giữ mục lục Word thật, và 9 file `.docx` gốc còn lại là mẫu hợp đồng World Bank có
tiêu đề lặp lại nhiều nơi (`"mơ hồ"` — nhiều đoạn thân bài cùng chuẩn hoá về một chuỗi) nên bị loại
theo đúng thiết kế thay vì đoán đại. Cần tài liệu dạng văn xuôi có mục lục Word thật (báo cáo, khoá
luận…) để công cụ này có ích.

Đo lại với `--toc-match-threshold 0.4 --toc-partial` trên cùng corpus cho **9/95 file** có partial
key, tổng **743 cặp** exact-match. Cả 9 file đều thuộc `OutlineLevelDriven`, nên kết quả này hữu ích
để mở rộng đối chứng cho nhóm có TOC/outline Word, nhưng **chưa mở khoá được `TypedNumbering`**.
Vì Word sinh TOC field từ outline/style chứ không từ số gõ tay thuần, đây là loại trừ cơ chế chứ
không chỉ là corpus thiếu may mắn.

`dhx eval` đọc header `partial_toc` và chấm theo phạm vi từng phần: không phạt false positive ngoài
các mục đã khớp TOC, và không đưa partial key vào calibration profile. Vì vậy với partial key,
**precision/F1 chỉ là số trong vùng đã gán**, không phải precision thật của toàn tài liệu; chỉ nên
đọc recall, đúng cấp và danh sách thiếu trên các cặp TOC đã xác thực. Muốn đo precision thật vẫn cần
ít nhất một `.key` đầy đủ do người kiểm.

## `legal-human/` — đáp án pháp quy từ nguồn ngoài pipeline

`025_ND_47-2020_Chia_se_du_lieu_so.key` là full key đầu tiên cho route `LegalStructured`: 71 heading
đối chiếu từ bản HTML pháp quy ở VCCI, không lấy từ output pipeline. `010_Luat_An_ninh_mang_24-2018-QH14.key`
là key thứ hai: 50 heading từ PDF Chính phủ, dùng HTML LuatVietnam để lấy title sạch khi PDF text bị
ngắt dòng. Cả hai đều là nguồn ngoài pipeline.

Với loại key này, mỗi dòng phải ghi text heading ở comment (`# ...`). Evaluator dùng cặp
`(stable-id/index đã resolve, text comment đã chuẩn hoá)` để phân biệt nhiều heading cùng paragraph;
nếu thiếu comment text thì key duplicate-source bị từ chối. Đây là hợp đồng mới cho các tài liệu
`--split-merged` nặng.

Đo hiện tại với `--no-llm --split-merged`:

| file | truth | P/R/F1 | cấp/cha | lỗi còn lại |
|---|--:|---:|---:|---|
| `010_Luat_An_ninh_mang_24-2018-QH14` | 50 | 86,0% | 100% | 7 cặp ranh giới title/body |
| `025_ND_47-2020_Chia_se_du_lieu_so` | 71 | 80,3% | 100% | 14 cặp ranh giới title/body |
| gộp | 121 | 82,6% | 100% | 21 cặp ranh giới title/body |

5 false positive tham chiếu chéo `Chương/Mục` ở file 025 đã được chặn. Các lỗi còn lại đều là
output giữ đúng marker/title đầu nhưng nuốt thêm câu thân bài đầu tiên; luật `KHOAN` không phủ hai
file này vì tail sau title bắt đầu bằng văn xuôi, không phải `1.`/`1)`.

## `typed-human/` — đáp án số gõ tay từ nguồn ngoài pipeline

`092_RFC9111_HTTP_Caching.key` là full key đầu tiên cho `TypedNumbering`: 64 section heading thật
lấy từ RFC Editor XML (`numbered=true`), bỏ front matter, TOC, acknowledgements, index và authors.
DOCX corpus là bản PDF/text-layout theo trang nên mỗi heading xuất hiện trong TOC và body; stable ID
trong key chọn occurrence cuối, tức phần body.

Đo hiện tại với `--no-llm --split-merged` sau filter footer RFC:

| file | truth | returned | exact P/R/F1 | marker diagnostic |
|---|--:|--:|---:|---|
| `092_RFC9111_HTTP_Caching` | 64 | 293 | 1,0% / 4,7% / 1,7% | 61/64 navigation usable, level đúng 61/61 |

Kết luận: route `TypedNumbering` trên RFC không mất marker chính, nhưng exact title hỏng vì nuốt
body và bắt nhầm TOC/reference/list items. Sau khi tách builder typed riêng, cấp trong 61 heading
starts-with đã lên **61/61**; filter footer xuất bản lặp chỉ cứu 3 exact title và giữ nguyên 61/64
usable cho điều hướng. Đây là bench đầu tiên cho nhóm Typed/RFC, không dùng để làm đẹp số mà để phơi
lỗi route.

## Tài liệu mật

Một tài liệu thử nghiệm đóng dấu MẬT đã được **khử hoàn toàn**: không có bản sao trong repo,
không trong scratchpad, không có đáp án hay nội dung nào của nó được lưu lại. Bản gốc chỉ còn ở
thư mục Downloads của người dùng.

Nếu cần thể loại đó trong tập test lâu dài, phải dùng bản **đã khử nội dung**: giữ nguyên khung
`I./1./3.1./a)`, thay mọi số liệu và danh từ riêng bằng dữ liệu giả. Cấu trúc heading — thứ duy
nhất pipeline cần — không đổi, còn rủi ro thì biến mất, và tập test chia sẻ được.
