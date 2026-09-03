using DocxHeaderExtractor.Core.Chunking;
using DocxHeaderExtractor.Core.Llm;
using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Core.Pipeline;

public enum InferenceBackend
{
    Local,
    OpenRouter,
    LmStudio,
    Sglang,
}

public sealed class PipelineOptions
{
    public ExtractionOptions Extraction { get; set; } = new();

    /// <summary>
    /// Cách cắt khối — thuộc pipeline, không thuộc backend. Xem <see cref="ChunkingOptions"/> để
    /// biết bốn lỗi đã đo được hồi ba giá trị này còn nằm trong <see cref="LlamaOptions"/>.
    /// </summary>
    public ChunkingOptions Chunking { get; set; } = new();

    public LlamaOptions Llama { get; set; } = new();
    public OpenRouterOptions OpenRouter { get; set; } = OpenRouterOptions.FromEnvironment();
    public LmStudioOptions LmStudio { get; set; } = LmStudioOptions.FromEnvironment();
    public SglangOptions Sglang { get; set; } = SglangOptions.FromEnvironment();
    public InferenceBackend Backend { get; set; }

    /// <summary>Bỏ qua LLM, chỉ dùng luật (nhanh, để đối chiếu).</summary>
    public bool DisableLlm { get; set; }

    /// <summary>Luôn giữ đoạn có style heading kể cả khi mô hình bỏ sót.</summary>
    public bool TrustStyles { get; set; } = true;

    /// <summary>
    /// Không hỏi mô hình về đoạn đã có style heading / <c>w:outlineLvl</c> — chúng vẫn nằm trong
    /// XML làm ngữ cảnh, chỉ là không bị hỏi.
    /// <para>
    /// MẶC ĐỊNH TẮT — nghe thì có vẻ miễn phí nhưng ĐO ĐƯỢC LÀ KHÔNG. Lập luận "câu trả lời cho
    /// nhóm có style không đổi được kết quả vì TrustStyles khôi phục hết" chỉ đúng khi câu trả
    /// lời của mô hình là cố định. Thực tế bỏ 32 câu hỏi ra khỏi khối làm đổi thành phần khối,
    /// và mô hình trả lời những đoạn CÒN LẠI khác đi: trên tài liệu thật, precision tụt từ 100%
    /// xuống 94,1% (nhận nhầm hai ô tiêu đề bảng) để đổi lấy 24% thời gian.
    /// Tiêu đề có style nằm xen kẽ đóng vai trò neo cho chuỗi sinh tự hồi quy.
    /// </para>
    /// </summary>
    public bool SkipStyledCandidates { get; set; }

    /// <summary>
    /// Bật luật R1 của spec filter OOXML: đoạn mang style Heading built-in, ngoài bảng/textbox,
    /// ngắn và không kết thúc bằng dấu chấm câu thì gán thẳng heading + cấp với confidence 1.0 và
    /// KHÔNG đi qua mô hình. Xem <see cref="OoxmlStyleAutoAssign"/>.
    /// <para>MẶC ĐỊNH TẮT — cờ này tồn tại để có số cho chính nó, không phải để dùng.</para>
    /// </summary>
    public bool StyleAutoAssign { get; set; }

    /// <summary>
    /// Chuẩn hoá cấp để không nhảy cóc (1 → 3 thành 1 → 2).
    /// <para>
    /// MẶC ĐỊNH TẮT từ khi cấp do cấu trúc quyết định. Bộ chuẩn hoá gán cấp theo ĐỘ SÂU NGĂN XẾP,
    /// nên heading đầu tiên còn sống luôn bị ép về cấp 1 — mất một heading cha là mọi con của nó
    /// tụt theo. Tra tay trên 01-style-chuan (đáp án 0→1, 2→2, 4→2, 6→1, 8→2) với đoạn 0 bị đánh
    /// rơi: nó gán 2→1 và 4→1 rồi để 6, 8 đúng, khớp từng dòng với báo cáo eval. Toàn bộ lỗi cấp
    /// đo được đều một chiều "trả về 1, đáp án 2" — dấu vân tay của chính phép ép này, không phải
    /// của mô hình đoán bừa. Khi cấp đến từ w:lvl/w:pStyle, style built-in hay chuỗi đánh số đã
    /// xác thực, chuẩn hoá lại chỉ có thể làm hỏng thứ vốn đã đúng.
    /// </para>
    /// </summary>
    public bool NormalizeLevels { get; set; }

    /// <summary>
    /// Đoạn có <c>w:outlineLvl</c> thì lấy cấp từ đó, không dùng cấp mô hình đoán.
    /// outlineLvl là đặc tả OOXML do chính người soạn đặt — chính xác hơn mọi suy luận.
    /// </summary>
    public bool LevelFromOutline { get; set; } = true;

    /// <summary>
    /// Quét hai lượt với cách cắt khối khác nhau rồi đối chiếu. Grammar liệt kê buộc mô hình
    /// sinh một chữ số cho mỗi ứng viên theo thứ tự, nên một dãy 0 kéo chữ số sau nó về 0 —
    /// lỗi bám theo vị trí trong khối. Đổi mép khối thì mỗi ứng viên rơi vào lân cận khác;
    /// chỗ nào hai lượt lệch nhau là chỗ mô hình lung lay, đánh dấu để trọng tài xem lại.
    /// </summary>
    public bool TwoPass { get; set; }

    /// <summary>
    /// Mang khung outline đã dựng được sang khối sau. Khối 1 chốt "Chương 1"; khối 2 nhận lại khung
    /// đó rồi mới quyết định "1.1" đứng ở cấp nào; khối 3 nhận cả hai. Nhằm đúng cơ chế hỏng đã đo
    /// hai lần (§4.1, §21): đổi thành phần khối là lật câu trả lời cho cả mục không liên quan, vì
    /// mỗi khối tự quyết cấp trong ngữ cảnh riêng của nó mà không biết phần trước đã dựng gì.
    /// <para>
    /// Giá phải trả: lượt phân loại buộc phải TUẦN TỰ — view của khối i chỉ dựng được sau khi khối
    /// i-1 trả kết quả. Mất khả năng gửi song song, nên chỉ có nghĩa với backend RPC khi người dùng
    /// chấp nhận đánh đổi. Model local vốn đã tuần tự (<see cref="ChunkParallelism"/>) nên không mất gì.
    /// </para>
    /// </summary>
    public bool RollingOutline { get; set; }

    /// <summary>
    /// Outline = ĐÚNG các đoạn mang style Heading của Word, cấp suy từ ký hiệu đánh số. Không gọi
    /// mô hình. Đây là định nghĩa outline do người dùng xác nhận — xem
    /// <see cref="StyleDeclaredOutline"/> và §41.
    /// </summary>
    public bool StyleDeclaredOutline { get; set; }

    /// <summary>
    /// Outline theo DANH SÁCH ĐA CẤP của Word: chọn theo <c>numPr</c>, cấp = <c>ilvl + 1</c>, cộng
    /// từ khoá cấu trúc cho phần không đánh số. Chế độ <c>numpr-driven</c> của spec §4.3 — dùng khi
    /// style của tài liệu không tin được. Xem <see cref="StyleDeclaredOutline.BuildFromNumbering"/>.
    /// </summary>
    public bool NumberingDeclaredOutline { get; set; }

    /// <summary>
    /// Dựng outline tất định cho văn bản hành chính Việt Nam (<c>I.</c>/<c>1.</c>/<c>a)</c>), khi
    /// tài liệu không có style, không <c>numPr</c>, không mục lục. Xem
    /// <see cref="AdministrativeOutline"/>.
    /// </summary>
    public bool AdministrativeDeclaredOutline { get; set; }

    /// <summary>
    /// Tự đo chế độ tài liệu và chọn bộ dựng tất định tương ứng khi chưa có override thủ công.
    /// Manual flags vẫn thắng để người dùng benchmark từng đường riêng.
    /// </summary>
    /// <summary>
    /// Tự chọn bộ dựng tất định theo chế độ tài liệu đo được.
    /// <para>
    /// <b>MẶC ĐỊNH BẬT — nhưng chỉ áp cho tài liệu có ĐOẠN GỘP</b> (xem <c>CoDoanGop</c>). Chốt đó
    /// là thứ làm cho nó an toàn; không có chốt thì bật hay tắt đều sai, và §100 đã chọn sai một
    /// lần vì chỉ nhìn bench.
    /// </para>
    /// <para>
    /// Đo trên <b>cả ba</b> bộ có đáp án, auto-mode kèm chốt tốt hơn hoặc bằng ở mọi bộ:
    /// </para>
    /// <list type="table">
    /// <item><term>bench (7 tài liệu Word gốc)</term><description>F1 96% → <b>98,6%</b> · P 92,3% → <b>100%</b> · tuyệt đối 6/7 giữ nguyên</description></item>
    /// <item><term>5 đáp án người kiểm (PDF→DOCX)</term><description>đúng cấp <b>6,5% → 100%</b> · đúng cha 60,9% → 100%</description></item>
    /// <item><term>14 đáp án (gồm toc-derived WB)</term><description>tuyệt đối <b>0/14 → 8/14</b> · Nav 61,7% → 80,6%</description></item>
    /// </list>
    /// <para>
    /// Không có chốt thì bench tụt 6/7 → 2/7. Tắt hẳn thì nhóm PDF mất đúng cấp (6,5%) và WB mất
    /// toàn bộ (0/14). Ba bộ nói ngược nhau vì tiêu đề sống ở chỗ khác nhau — chốt đoạn gộp đọc
    /// đúng khác biệt đó.
    /// </para>
    /// <para>Tắt bằng <c>--no-auto-mode</c> để đối chứng.</para>
    /// </summary>
    public bool AutoDetectDocumentMode { get; set; } = true;

    /// <summary>
    /// Dùng PDF cùng stem như nguồn layout PHỤ cho nhóm typed textbook khi DOCX không có tín hiệu
    /// khai báo mạnh. PDF không thắng outlineLvl/style; nó chỉ cứu tài liệu PDF→DOCX text-layout
    /// mất ranh giới title/body. Xem handoff 2026-08-14, prototype OpenStax 056.
    /// </summary>
    public bool PdfTextbookFallback { get; set; } = true;

    /// <summary>
    /// Route PDF chung, không phụ thuộc ngôn ngữ hay thể loại: đo baseline layout, lọc header/footer
    /// và bảng, gom block theo style rồi grounding về DOCX. Chỉ nhận outline thưa; tín hiệu quá dày
    /// bị coi là content/table index và nhường cho tầng analyst hoặc route có evidence mạnh hơn.
    /// </summary>
    public bool PdfLayoutEvidenceFallback { get; set; }

    /// <summary>
    /// Slow lane for PDF layout candidates. The model sees at most 40 blocks that survived the
    /// deterministic line/table/repeat filters; <see cref="PdfBlockGrounder"/> must ground every
    /// accepted role back to extracted source text. Disabled by default until measured on keys.
    /// </summary>
    public bool PdfLayoutAnalystFallback { get; set; }

    /// <summary>
    /// Canonical authority execution following the 9B contract: source retrieval, model proposal,
    /// source-pointer validation, and canonical product output. DOCX and PDF use the same authority
    /// boundary; their adapters only differ in how source facts are built. Legacy selectors remain
    /// available to explicit diagnostic/evaluation callers, but normal extraction must not fall
    /// through to them after this route is entered.
    /// </summary>
    public bool PdfFirstValidatedFallback { get; set; } = true;

    /// <summary>
    /// Optional bounded smoke budget for the explicit PDF-first route. Zero means no candidate is
    /// dropped; a positive value is diagnostics only and must not be used for recall claims.
    /// </summary>
    public int PdfFirstAnalystBlocks { get; set; }

    /// <summary>Visual SourceFacts sent to the fallback; zero is lossless and screens every region.</summary>
    public int PdfFirstVisualRegions { get; set; }

    /// <summary>
    /// Dùng PDF cùng stem làm nguồn BOLD-RUN-ĐẦU-DÒNG cho nhóm biên bản/minutes ngắn khi DOCX rớt
    /// toàn bộ định dạng ký tự (không "b"/"br" nào còn, kể cả thân bài thật). Xem
    /// <see cref="PdfBoldLabelOutline"/>.
    /// <para>
    /// <b>MẶC ĐỊNH BẬT từ §103.</b> Trước đây tắt vì "chưa đo qua toàn corpus" — nay đã đo, trên
    /// cả bốn bộ có đáp án:
    /// </para>
    /// <list type="table">
    /// <item><term>2 đáp án người kiểm nhóm biên bản</term><description>Nav <b>0% → 100%</b> · tuyệt đối <b>0/2 → 2/2</b></description></item>
    /// <item><term>bench · 5 đáp án người · 9 đáp án mục lục</term><description>KHÔNG ĐỔI một chữ số</description></item>
    /// </list>
    /// <para>
    /// Không hồi quy ở đâu vì <see cref="PdfBoldLabelOutline.TryBuild"/> tự loại: cần một PDF cùng
    /// stem, và chỉ chạy khi DOCX đã mất sạch định dạng ký tự. Tài liệu không thoả trả về
    /// <c>no-pdf</c> hoặc bỏ qua, nên nó bất động ở mọi nhóm khác.
    /// </para>
    /// </summary>
    public bool PdfBoldLabelFallback { get; set; } = true;

    /// <summary>
    /// Đọc JSON sidecar Docling do người gọi chỉ định rồi align ngược về DOCX. Tắt mặc định: corpus
    /// không có sidecar thật để hiệu chuẩn, nên đây là adapter sandbox/explicit-input chứ không phải
    /// một PDF route production. DOCX vẫn là nguồn anchor/writeback.
    /// </summary>
    public bool DoclingSidecarFallback { get; set; }

    /// <summary>
    /// JSON Docling chỉ định tường minh cho một lượt chạy.
    /// </summary>
    public string? DoclingJsonPath { get; set; }

    /// <summary>
    /// Fallback thứ ba cho <c>FormatDriven</c>, KHÔNG cần PDF: mã phiên kiểu "D1.00 - Title" (World
    /// Bank ICP IACG minutes, nhóm 071/076-079 — <see cref="PdfBoldLabelOutline"/> không kích hoạt
    /// vì DOCX không còn bold nào để đọc, nhưng mã phiên vẫn còn nguyên là TEXT). Xem
    /// <see cref="SessionCodeOutline"/>. Mặc định TẮT — mới cài, chưa đo qua toàn corpus.
    /// </summary>
    public bool SessionCodeFallback { get; set; }

    /// <summary>
    /// Tầng cắt ranh giới title/body bằng LLM few-shot cố định theo domain — chỉ chạy cho ứng viên
    /// mà <see cref="InlineHeadingSplitter"/> KHÔNG tìm được ranh giới tất định (không phải mọi
    /// heading dính body, chỉ phần còn lại sau khi luật rẻ hơn đã thử). Xem
    /// <see cref="LlmBoundaryCutter"/> — bảng cứng đã đo 85,7%/95,0%/85,7% trên ba domain và thắng
    /// retrieval động khi so đầu đối đầu (<c>docs/llm-boundary-few-shot-retrieval.md</c> §3/§4).
    /// <para>
    /// Mặc định TẮT — kết quả đã đo là trên HARNESS RIÊNG (55 ca cô lập, không qua pipeline thật),
    /// chưa đo end-to-end qua route sản xuất này. Chỉ chạy khi mô hình đang bật (<c>--no-llm</c>
    /// tắt luôn tầng này, vì đây là tầng gọi model).
    /// </para>
    /// </summary>
    public bool LlmBoundaryCutFallback { get; set; }

    /// <summary>
    /// Hậu kiểm bằng ký hiệu đánh số của chính tài liệu: cùng dạng đánh số phải cùng cấp, và
    /// dãy anh em phải liên tục từ 1. Không tốn giây suy luận nào và bắt được cả lỗi trượt cấp
    /// của mô hình lẫn tiêu đề bị tầng lọc đánh rơi — xem <see cref="NumberingAudit"/>.
    /// </summary>
    public bool AuditNumbering { get; set; } = true;

    /// <summary>
    /// Cứu heading bị mô hình loại hẳn khi đánh số của tài liệu khẳng định nó là em kế tiếp của
    /// một heading đã nhận (3.1 → 3.2). Bộ sắp cấp chỉ sửa được cấp của heading ĐÃ chọn, không
    /// kéo lại được mục đã bị loại — xem <see cref="StructuralRecovery"/>.
    /// </summary>
    public bool RecoverNumberedSiblings { get; set; } = true;

    /// <summary>Ghi XML tinh gọn từ canonical model ra file để debug/đối chiếu source.</summary>
    public string? DumpXmlPath { get; set; }

    /// <summary>In nguyên văn đầu ra của mô hình cho từng khối (debug prompt/grammar).</summary>
    public bool ShowRawOutput { get; set; }

    /// <summary>
    /// Chỉ bật để audit/thu thập nhãn: gửi mọi paragraph không rỗng cho model. Mặc định pipeline
    /// production chỉ hỏi các ứng viên mơ hồ; style/rule và hậu kiểm cấu trúc xử lý phần chắc chắn.
    /// </summary>
    public bool ReviewAllParagraphs { get; set; }

    /// <summary>
    /// Sau khi chọn heading theo từng cửa sổ, chạy một lượt riêng để gán lại cấp trên danh sách
    /// heading theo thứ tự toàn tài liệu. Tránh lỗi chunk cắt giữa heading cha và heading con.
    /// </summary>
    public bool GlobalHierarchy { get; set; } = true;

    /// <summary>
    /// Chạy bộ suy cấp TẤT ĐỊNH (<see cref="StructuralHierarchyResolver"/> +
    /// <see cref="TableOfContentsAnchor"/>) cho kết quả deterministic, dù LLM đang bật hay tắt.
    /// <para>
    /// Hai bộ này không cần mô hình nhưng nằm trong <c>RunModelAsync</c>, nên đường không mô hình
    /// chưa bao giờ chạy chúng. Đo được trên <c>bench/02-dinh-dang-thu-cong</c>: đúng cấp 28,6%
    /// với 5/7 mục nông hơn đáp án một cấp, trong khi gọi thẳng resolver cho đúng cả 7.
    /// </para>
    /// <para>
    /// <b>MẶC ĐỊNH BẬT</b>, khác với các cờ mới khác của dự án. Lý do: §10.4 cấm lật mặc định CHỈ
    /// vì bench, nhưng đây không phải mã chưa kiểm chứng. <see cref="StructuralHierarchyResolver"/>
    /// đã có bằng chứng đáp án NGƯỜI KIỂM (§31: đúng cấp 81,1% → 91,5% trên khoá luận thật) và
    /// đường có mô hình chạy nó VÔ ĐIỀU KIỆN trong <c>RunModelAsync</c>. Nhưng route deterministic
    /// short-circuit trước <c>RunModelAsync</c>, nên nếu bỏ bước này khi LLM bật thì chỉ riêng việc
    /// dùng Qwen để bù/xác minh đã làm mất pin cấp của route tất định. Đo được trên nhóm WB: bật
    /// Qwen 27B từng làm Nav+cấp sập do bỏ bước này; chạy lại bước tất định đưa cấp về 100%.
    /// </para>
    /// <para>Tắt bằng <c>--no-deterministic-hierarchy</c> để đối chứng. Xem handoff §51.</para>
    /// </summary>
    public bool DeterministicHierarchy { get; set; } = true;

    public Action<string>? Log { get; set; }

    /// <summary>JSONL correction đã được người dùng sửa thật sự; null thì không dùng memory.</summary>
    public string? CorrectionMemoryPath { get; set; }

    /// <summary>
    /// Phản biện MỌI heading do model/style đề xuất, không cần dấu hiệu gì.
    /// <para>
    /// MẶC ĐỊNH TẮT. Bật lên là hỏi lại theo lịch chứ không theo bằng chứng, và cái giá đã đo
    /// được: trên công văn 344 đoạn, lượt critic chạy 6 khối mất khoảng 37 phút rồi kết luận
    /// "giữ 14, bác 0" — không đổi một mục nào. Khi tắt, critic chỉ nhận hai nhóm: mục bằng chứng
    /// yếu theo <see cref="ModelHeadingCriticGate"/>, và mục nằm trong khối mà mô hình có dấu hiệu
    /// trôi (bịa chỉ số, hoặc sập về một cấp duy nhất).
    /// </para>
    /// <para>Giữ lại làm công tắc cho lúc cần siết precision bằng mọi giá, ví dụ khi hiệu chuẩn.</para>
    /// </summary>
    public bool HighPrecisionMode { get; set; }

    /// <summary>Ngưỡng precision mong muốn cho selective auto-accept.</summary>
    public double TargetPrecision { get; set; } = 0.93;

    /// <summary>Số dự đoán holdout tối thiểu trong đúng evidence bucket.</summary>
    public int MinimumCalibrationSamples { get; set; } = 52;

    /// <summary>
    /// Ngưỡng điểm heuristic dưới đó model-only heading phải đi qua critic. Đây là policy có thể
    /// hiệu chuẩn, không phải chân lý cố định; đưa vào configuration signature của calibration.
    /// </summary>
    public double ModelCriticWeakEvidenceThreshold { get; set; } = 0.70;

    /// <summary>
    /// Fallback confidence theo số evidence checks đã qua khi chưa có holdout bucket đo được.
    /// Index 0..5 tương ứng 0/5..5/5. Khi có calibration profile, Wilson lower bound của bucket
    /// thắng bảng này.
    /// </summary>
    public double[] EvidenceConfidenceTiers { get; set; } = [0.50, 0.60, 0.70, 0.80, 0.85, 0.95];

    /// <summary>Profile sinh từ `dhx eval ... --calibration-out`; null = evidence chưa calibration.</summary>
    public string? CalibrationProfilePath { get; set; } =
        Environment.GetEnvironmentVariable("DHX_CALIBRATION_PROFILE");

    /// <summary>
    /// Áp profile của model GGUF lên NGÂN SÁCH THẬT mà pipeline dùng để chia khối, rồi chép sang
    /// backend cục bộ để nó tự nới context cho vừa.
    /// <para>
    /// Phải gọi trước khi chia khối. Bản đầu của refactor tách chunking để
    /// <c>LlamaHeaderExtractor.LoadAsync</c> tự áp profile lên một <see cref="ChunkingOptions"/>
    /// TẠM — cú nâng "qwen thì 2200 → 5000" rơi vào vật thể tạm rồi bị vứt, pipeline vẫn chia khối
    /// bằng 2200. Đo được ngay ở dòng log "ngân sách … token thật/khối": 5000 tụt về 2200.
    /// </para>
    /// </summary>
    public void PrepareLocalModelProfile()
    {
        if (DisableLlm || Backend != InferenceBackend.Local) return;
        if (string.IsNullOrWhiteSpace(Llama.ModelPath)) return;
        Llama.ApplyRecommendedModelProfile(Chunking);
    }
}
