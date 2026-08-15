using DocxHeaderExtractor.Core.Llm;
using DocxHeaderExtractor.Core.Output;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Cli;

public sealed class CommandLineOptions
{
    public string Command { get; private set; } = "extract";
    public List<string> Inputs { get; } = [];
    public string? OutputPath { get; private set; }
    public string? TrainingOutputPath { get; private set; }
    public string? CalibrationOutputPath { get; private set; }
    public OutlineFormat Format { get; private set; } = OutlineFormat.Json;
    public bool Quiet { get; private set; }

    /// <summary>Lệnh `xml`: chỉ in phần ứng viên (đúng nội dung gửi cho mô hình) thay vì mọi đoạn.</summary>
    public bool CompactXml { get; private set; }

    /// <summary>
    /// Thư mục ghi ĐÚNG các khối mà pipeline sẽ gửi cho mô hình, kèm system prompt. Có nó thì đo
    /// "một mô hình khác trả lời ra sao trên cùng đầu vào" mới là so hai mô hình, chứ không phải
    /// so hai cách dựng prompt.
    /// </summary>
    public string? DumpChunksDir { get; private set; }
    public bool ShowHelp { get; private set; }
    public PipelineOptions Pipeline { get; } = new();

    /// <summary>Đích .docx cho hành động ghi outline; null = run chỉ đọc.</summary>
    public string? WritebackPath { get; private set; }
    public bool WritebackOverwrite { get; private set; }
    public bool WritebackHeadingStyles { get; private set; }

    /// <summary>Lệnh `toc-keys`: tỉ lệ khớp tối thiểu giữa mục lục và thân bài để nhận file.</summary>
    public double TocMatchThreshold { get; private set; } = Core.Eval.TocAnswerKeyGenerator.DefaultMatchThreshold;

    /// <summary>Lệnh `toc-keys`: ghi cả key từng phần cho file dưới ngưỡng, đánh dấu partial_toc.</summary>
    public bool TocPartial { get; private set; }

    /// <summary>Lệnh `toc-keys`: in từng mục lục không khớp/mơ hồ để chẩn đoán.</summary>
    public bool Verbose { get; private set; }

    public static CommandLineOptions Parse(string[] args)
    {
        var o = new CommandLineOptions();
        var llama = o.Pipeline.Llama;
        var extraction = o.Pipeline.Extraction;

        if (args.Length == 0) { o.ShowHelp = true; return o; }

        int i = 0;
        if (!args[0].StartsWith('-') &&
            args[0] is "extract" or "xml" or "help" or "info" or "sample" or "bench" or "eval" or "review" or "review-key" or "toc-keys")
        {
            o.Command = args[0];
            i = 1;
        }
        if (o.Command == "help") { o.ShowHelp = true; return o; }

        var explicitChunkTokens = false;

        for (; i < args.Length; i++)
        {
            var a = args[i];
            string Next(string name) =>
                i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Thiếu giá trị cho {name}");

            switch (a)
            {
                case "-h" or "--help": o.ShowHelp = true; break;
                case "-m" or "--model": llama.ModelPath = Next(a); break;
                case "-o" or "--out": o.OutputPath = Next(a); break;
                case "--training-out": o.TrainingOutputPath = Next(a); break;
                case "--calibration-out": o.CalibrationOutputPath = Next(a); break;
                case "--calibration-profile": o.Pipeline.CalibrationProfilePath = Next(a); break;
                case "--target-precision":
                    o.Pipeline.TargetPrecision = double.Parse(Next(a), System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--calibration-min-samples": o.Pipeline.MinimumCalibrationSamples = int.Parse(Next(a)); break;
                // Phản biện giờ chạy theo dấu hiệu; cờ này bật lại chế độ hỏi TẤT CẢ.
                case "--no-high-precision": o.Pipeline.HighPrecisionMode = false; break;
                case "--critique-all": o.Pipeline.HighPrecisionMode = true; break;
                case "-f" or "--format": o.Format = ParseFormat(Next(a)); break;
                case "--no-llm": o.Pipeline.DisableLlm = true; break;
                case "--openrouter":
                    o.Pipeline.Backend = InferenceBackend.OpenRouter;
                    break;
                case "--openrouter-model":
                    o.Pipeline.Backend = InferenceBackend.OpenRouter;
                    o.Pipeline.OpenRouter.Model = Next(a);
                    break;
                case "--lmstudio":
                    o.Pipeline.Backend = InferenceBackend.LmStudio;
                    break;
                case "--lmstudio-model":
                    o.Pipeline.Backend = InferenceBackend.LmStudio;
                    o.Pipeline.LmStudio.Model = Next(a);
                    break;
                case "--lmstudio-endpoint":
                    o.Pipeline.Backend = InferenceBackend.LmStudio;
                    o.Pipeline.LmStudio.Endpoint = new Uri(Next(a), UriKind.Absolute);
                    break;
                case "--lmstudio-context":
                    o.Pipeline.Backend = InferenceBackend.LmStudio;
                    o.Pipeline.LmStudio.ContextSize = int.Parse(Next(a));
                    break;
                case "--candidates-only": o.Pipeline.ReviewAllParagraphs = false; break;
                case "--review-all": o.Pipeline.ReviewAllParagraphs = true; break;
                case "--no-trust-styles": o.Pipeline.TrustStyles = false; break;
                case "--skip-styled": o.Pipeline.SkipStyledCandidates = true; break;
                case "--style-auto-assign": o.Pipeline.StyleAutoAssign = true; break;
                // Cấp thô là mặc định từ khi cấu trúc quyết định cấp; cờ này giữ lại để bật chuẩn
                // hoá theo độ sâu ngăn xếp khi cần so với hành vi cũ.
                case "--raw-levels": o.Pipeline.NormalizeLevels = false; break;
                case "--normalize-levels": o.Pipeline.NormalizeLevels = true; break;
                case "--two-pass": o.Pipeline.TwoPass = true; break;
                case "--no-global-hierarchy": o.Pipeline.GlobalHierarchy = false; break;
                case "--model-levels": o.Pipeline.LevelFromOutline = false; break;
                case "--dump-xml": o.Pipeline.DumpXmlPath = Next(a); break;
                case "--show-raw": o.Pipeline.ShowRawOutput = true; break;

                case "--ctx": llama.ContextSize = uint.Parse(Next(a)); llama.AutoContextSize = false; break;
                case "--threads" or "-t": llama.Threads = int.Parse(Next(a)); break;
                case "--chunk-tokens": o.Pipeline.Chunking.SetExplicitTokenBudget(int.Parse(Next(a))); explicitChunkTokens = true; break;
                case "--chunk-candidates": o.Pipeline.Chunking.MaxCandidatesPerChunk = int.Parse(Next(a)); break;
                case "--max-out": llama.MaxOutputTokens = int.Parse(Next(a)); break;
                case "--overlap": o.Pipeline.Chunking.Overlap = int.Parse(Next(a)); break;
                case "--temp": llama.Temperature = float.Parse(Next(a), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--seed": llama.Seed = uint.Parse(Next(a)); break;
                case "--no-grammar": llama.GrammarMode = GrammarMode.None; break;
                // Thinking và GBNF loại trừ nhau: <think>…</think> đứng trước JSON thì grammar
                // chặn ngay token đầu. Bật --think là tự tắt grammar, không để người dùng tự vấp.
                case "--think": llama.EnableThinking = true; break;
                case "--free-grammar": llama.GrammarMode = GrammarMode.Free; break;
                case "--no-reuse-prefix": llama.ReusePromptPrefix = false; break;
                case "--gpu-layers" or "-ngl": llama.GpuLayerCount = int.Parse(Next(a)); break;
                case "--verbose-native": llama.VerboseNativeLog = true; break;
                case "--no-audit": o.Pipeline.AuditNumbering = false; break;
                case "--no-structural-recovery": o.Pipeline.RecoverNumberedSiblings = false; break;

                case "--max-text": extraction.MaxTextLength = int.Parse(Next(a)); break;
                case "--threshold": extraction.CandidateThreshold = double.Parse(Next(a), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--no-tables": extraction.IncludeTables = false; break;
                case "--page-headers": extraction.IncludePageHeadersFooters = true; break;
                case "--no-context": extraction.IncludeFollowingContext = false; break;
                case "--structural-only": extraction.UseLexicalRules = false; break;
                case "--no-standalone-lines": extraction.PromoteStandaloneLines = false; break;
                case "--skip-content-controls": extraction.SkipContentControls = true; break;
                case "--bare-labels": extraction.AllowBareLabelledNumbers = true; break;
                case "--split-merged": extraction.SplitMergedParagraphs = true; break;
                case "--no-auto-mode": o.Pipeline.AutoDetectDocumentMode = false; break;
                case "--auto-mode": o.Pipeline.AutoDetectDocumentMode = true; break;
                case "--admin-outline": o.Pipeline.AdministrativeDeclaredOutline = true; break;
                case "--style-outline": o.Pipeline.StyleDeclaredOutline = true; break;
                case "--numbering-outline": o.Pipeline.NumberingDeclaredOutline = true; break;
                case "--deterministic-hierarchy": o.Pipeline.DeterministicHierarchy = true; break;
                case "--no-deterministic-hierarchy": o.Pipeline.DeterministicHierarchy = false; break;
                case "--mode-only": extraction.ReportModeOnly = true; break;
                case "--flag-repeated-labels": extraction.FlagRepeatedLabels = true; break;
                case "--skip-corrupt": extraction.SkipCorruptParagraphs = true; break;
                case "--skip-data-tables": extraction.SkipDataTables = true; break;
                case "--audit-sibling-shape": extraction.AuditSiblingShape = true; break;
                case "--style-trust": extraction.UseStyleTrust = true; break;

                case "-q" or "--quiet": o.Quiet = true; break;
                case "--compact": o.CompactXml = true; break;
                case "--dump-chunks": o.DumpChunksDir = Next(a); break;

                case "--toc-match-threshold":
                    o.TocMatchThreshold = double.Parse(Next(a), System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--toc-partial": o.TocPartial = true; break;
                case "-v" or "--verbose": o.Verbose = true; break;

                case "--write-docx": o.WritebackPath = Next(a); break;
                case "--write-overwrite": o.WritebackOverwrite = true; break;
                case "--write-heading-styles": o.WritebackHeadingStyles = true; break;

                default:
                    if (a.StartsWith('-')) throw new ArgumentException($"Tham số không hợp lệ: {a}");
                    o.Inputs.Add(a);
                    break;
            }
        }

        // Áp profile RPC SAU vòng lặp, không áp ngay trong nhánh cờ backend: `--chunk-tokens 3000
        // --openrouter` từng bị chính nhánh backend ghi đè mất giá trị người dùng vừa gõ, chỉ vì
        // thứ tự hai cờ. Ở đây override tường minh luôn thắng, bất kể viết trước hay sau.
        if (o.Pipeline.Backend is InferenceBackend.OpenRouter or InferenceBackend.LmStudio)
        {
            var chunkTokens = o.Pipeline.Chunking.TokenBudget;
            o.Pipeline.Chunking.UseRemoteProfile();
            if (explicitChunkTokens) o.Pipeline.Chunking.SetExplicitTokenBudget(chunkTokens);
        }

        return o;
    }

    private static OutlineFormat ParseFormat(string s) => s.ToLowerInvariant() switch
    {
        "json" => OutlineFormat.Json,
        "md" or "markdown" => OutlineFormat.Markdown,
        "txt" or "text" => OutlineFormat.Text,
        "xml" => OutlineFormat.Xml,
        "csv" => OutlineFormat.Csv,
        _ => throw new ArgumentException($"Định dạng không hỗ trợ: {s}"),
    };

    public const string HelpText = """
        dhx – trích xuất tiêu đề (heading) từ .docx/.doc bằng OpenXML + LLamaSharp (CPU)

        Cách dùng:
          dhx extract <file.docx|file.doc> [tuỳ chọn]
          dhx xml     <file.docx> [--compact]  # in XML tinh gọn, không gọi mô hình
                                              # --compact = đúng nội dung gửi cho mô hình
          dhx info    <file.gguf>            # xem metadata mô hình
          dhx sample  <ra.docx>              # tạo file .docx mẫu để thử
          dhx bench   [thư-mục]              # sinh bộ tài liệu thử + đáp án (mặc định ./bench)
          dhx eval    [thư-mục]              # chấm trên bộ có đáp án, in precision/recall/cấp
                                             # mỗi X.docx cần một X.key đi kèm
          dhx review  <file.docx>             # chạy dự đoán, xuất .review.json để người duyệt sửa
          dhx review-key <file.review.json>   # sinh .key + .training.jsonl từ review đã duyệt
          dhx toc-keys <thư-mục|file.docx>    # suy đáp án ỨNG VIÊN từ mục lục Word, mở rộng bench
                                              # KHÔNG thay đáp án người kiểm — xem keys/README.md

        Tuỳ chọn chính:
          -m, --model <path.gguf>   Mô hình GGUF (mặc định: biến DHX_MODEL, appsettings.json
                                    hoặc file .gguf duy nhất trong thư mục ./models)
          -o, --out <path>          Ghi kết quả ra file (mặc định in ra màn hình)
              --training-out <path> Với review-key: nơi ghi JSONL nhãn vàng (mặc định cạnh .key)
          -f, --format <fmt>        json | md | txt | xml | csv   (mặc định json)
              --no-llm              Chỉ dùng luật OpenXML, bỏ qua mô hình
              --openrouter          Gọi OpenRouter RPC; đọc key từ OPENROUTER_API_KEY
              --openrouter-model m  Model slug (mặc định qwen/qwen-2.5-7b-instruct)
              --lmstudio            Gọi LM Studio OpenAI-compatible trên loopback
              --lmstudio-model m    Model identifier trả bởi GET /v1/models
              --lmstudio-endpoint u Chat endpoint (mặc định http://127.0.0.1:1234/v1/chat/completions)
              --lmstudio-context n  Context đã nạp trong LM Studio (mặc định 16384)
              --candidates-only      Chỉ gửi ứng viên heuristic cho model (mặc định production)
              --review-all           Gửi mọi paragraph cho model (audit/thu nhãn; rất chậm)
              --dump-chunks <thư mục> (lệnh xml) Ghi từng khối sẽ gửi cho mô hình + system prompt
              --dump-xml <path>     Ghi XML tinh gọn (đầy đủ đoạn + điểm số) để kiểm tra bộ lọc
              --show-raw            In nguyên văn JSON mô hình trả về cho từng khối
              --target-precision p  Mục tiêu auto-accept (mặc định 0.93)
              --calibration-profile <json>  Profile holdout dùng để calibration confidence
              --calibration-min-samples n   Mẫu tối thiểu mỗi evidence bucket (mặc định 52)
              --no-high-precision   Không critic mọi heading; chỉ phản biện mục model-only yếu
              --calibration-out <json> Với lệnh eval: ghi profile precision từ bộ holdout
          -q, --quiet               Không in tiến trình

        Ghi outline ngược vào tài liệu (chỉ lệnh extract, mỗi lần một file):
              --write-docx <path>   Ghi w:outlineLvl của các heading đã chốt vào BẢN SAO .docx
                                    tại <path>. File nguồn không bao giờ bị sửa và không một ký
                                    tự nội dung nào bị thay đổi. Policy skill yêu cầu duyệt xong:
                                    còn mục chờ người duyệt thì harness bỏ qua bước ghi.
              --write-overwrite     Cho phép đè file đích đã tồn tại
              --write-heading-styles Gán thêm style Heading N có sẵn trong tài liệu (đổi hình thức)

        Lệnh toc-keys (mở rộng bench bằng mục lục Word):
              -o, --out <thư-mục>   Nơi ghi .key (mặc định ./keys/toc-derived)
              --toc-match-threshold p  Tỉ lệ khớp tối thiểu để nhận file (mặc định 0.80)
                                    Dưới ngưỡng: in báo cáo, KHÔNG ghi .key.
              --toc-partial         Ghi cả file dưới ngưỡng khi có mục khớp chính xác; header
                                    đánh dấu partial_toc và KHÔNG coi là outline đầy đủ.

        Mô hình / hiệu năng CPU:
              --ctx <n>             Cửa sổ ngữ cảnh (mặc định 4096)
          -t, --threads <n>         Số luồng CPU (mặc định số lõi - 1)
              --gpu-layers <n>      Số lớp đẩy lên GPU (mặc định 0 = chạy hoàn toàn CPU).
                                    Cần build với -p:UseCuda=true (NVIDIA) hoặc
                                    -p:UseVulkan=true (AMD/Intel/NVIDIA). Bản CPU bỏ qua.
              --chunk-tokens <n>    Ngân sách token mỗi khối document view (mặc định 2200)
              --chunk-candidates <n> Trần số ứng viên mỗi khối (mặc định 12). Khối càng dài,
                                    mô hình càng dễ trượt theo dãy 0 — xem LlamaOptions.
              --max-out <n>         Token đầu ra tối đa mỗi khối (mặc định 900)
              --overlap <n>         Số ứng viên chồng lấn giữa hai khối (mặc định 2)
              --temp <f>            Nhiệt độ (mặc định 0 = greedy)
              --seed <n>            Seed
              --free-grammar        GBNF chỉ ép lược đồ, mô hình tự chọn liệt kê mục nào
              --no-reuse-prefix     Nạp lại toàn bộ prompt ở từng khối thay vì tái dùng phần chung.
                                    Chậm hơn ~2 lần; chỉ dùng khi cần tái lập chính xác từng bước.
              --no-grammar          Tắt GBNF hoàn toàn

        Bộ lọc OpenXML:
              --max-text <n>        Độ dài text tối đa đưa vào XML (mặc định 160)
              --threshold <d>       Ngưỡng điểm ứng viên 0..1 (mặc định 0.45)
              --no-tables           Bỏ qua đoạn trong bảng
              --page-headers        Đọc thêm w:hdr/w:ftr
              --no-context          Không kèm đoạn văn ngữ cảnh
              --structural-only     Chỉ dùng tín hiệu cấu trúc OOXML, tắt luật theo từ ngữ
                                    (không phụ thuộc ngôn ngữ tài liệu)
              --no-trust-styles     Không tự động giữ heading theo style khi mô hình bỏ sót
              --skip-styled         Không hỏi mô hình về đoạn đã có style/outlineLvl. Nhanh hơn
                                    ~24% nhưng ĐO ĐƯỢC là precision tụt 100% → 94%: các đoạn có
                                    style nằm xen kẽ đóng vai trò neo cho mô hình. Chỉ dùng khi
                                    ưu tiên tốc độ hơn độ chính xác.
              --no-audit            Tắt hậu kiểm theo ký hiệu đánh số. Mặc định BẬT: đối chiếu
                                    cấp giữa các mục cùng dạng đánh số và tìm lỗ hổng trong dãy
                                    anh em, đánh dấu (?) chỗ đáng ngờ. Không gọi mô hình.
              --raw-levels          Giữ nguyên cấp do mô hình trả về (không chuẩn hoá)
              --two-pass            Quét hai lượt với cách cắt khối khác nhau, đánh dấu (?)
                                    những đoạn hai lượt bất đồng để xem lại. Tốn gấp ~2 lần.
              --no-global-hierarchy Không chạy lượt gán cấp riêng trên toàn bộ heading đã chọn
              --model-levels        Lấy cấp do mô hình đoán thay vì đọc w:outlineLvl trong file

        Ví dụ:
          dhx extract bao-cao.docx -m models\Llama-3.2-3B-Instruct-Q4_K_M.gguf -f md
          dhx extract *.docx --no-llm -f csv -o outline.csv
          dhx review bao-cao.docx -m models\Qwen2.5-7B-Instruct-Q4_K_M.gguf -o bao-cao.review.json
          dhx review-key bao-cao.review.json -o bao-cao.key
          dhx xml bao-cao.docx | less
        """;
}
