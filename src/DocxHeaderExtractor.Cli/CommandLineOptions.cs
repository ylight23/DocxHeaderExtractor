using DocxHeaderExtractor.Core.Llm;
using DocxHeaderExtractor.Core.Output;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Cli;

public sealed class CommandLineOptions
{
    public string Command { get; private set; } = "extract";
    public List<string> Inputs { get; } = [];
    public string? OutputPath { get; private set; }
    public OutlineFormat Format { get; private set; } = OutlineFormat.Json;
    public bool Quiet { get; private set; }

    /// <summary>Lệnh `xml`: chỉ in phần ứng viên (đúng nội dung gửi cho mô hình) thay vì mọi đoạn.</summary>
    public bool CompactXml { get; private set; }
    public bool ShowHelp { get; private set; }
    public PipelineOptions Pipeline { get; } = new();

    public static CommandLineOptions Parse(string[] args)
    {
        var o = new CommandLineOptions();
        var llama = o.Pipeline.Llama;
        var extraction = o.Pipeline.Extraction;

        if (args.Length == 0) { o.ShowHelp = true; return o; }

        int i = 0;
        if (!args[0].StartsWith('-') &&
            args[0] is "extract" or "xml" or "help" or "info" or "sample" or "bench" or "eval")
        {
            o.Command = args[0];
            i = 1;
        }
        if (o.Command == "help") { o.ShowHelp = true; return o; }

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
                case "-f" or "--format": o.Format = ParseFormat(Next(a)); break;
                case "--no-llm": o.Pipeline.DisableLlm = true; break;
                case "--no-trust-styles": o.Pipeline.TrustStyles = false; break;
                case "--skip-styled": o.Pipeline.SkipStyledCandidates = true; break;
                case "--raw-levels": o.Pipeline.NormalizeLevels = false; break;
                case "--two-pass": o.Pipeline.TwoPass = true; break;
                case "--model-levels": o.Pipeline.LevelFromOutline = false; break;
                case "--dump-xml": o.Pipeline.DumpXmlPath = Next(a); break;
                case "--show-raw": o.Pipeline.ShowRawOutput = true; break;

                case "--ctx": llama.ContextSize = uint.Parse(Next(a)); break;
                case "--threads" or "-t": llama.Threads = int.Parse(Next(a)); break;
                case "--chunk-tokens": llama.ChunkTokenBudget = int.Parse(Next(a)); break;
                case "--chunk-candidates": llama.MaxCandidatesPerChunk = int.Parse(Next(a)); break;
                case "--max-out": llama.MaxOutputTokens = int.Parse(Next(a)); break;
                case "--overlap": llama.ChunkOverlap = int.Parse(Next(a)); break;
                case "--temp": llama.Temperature = float.Parse(Next(a), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--seed": llama.Seed = uint.Parse(Next(a)); break;
                case "--no-grammar": llama.GrammarMode = GrammarMode.None; break;
                case "--free-grammar": llama.GrammarMode = GrammarMode.Free; break;
                case "--no-reuse-prefix": llama.ReusePromptPrefix = false; break;
                case "--gpu-layers" or "-ngl": llama.GpuLayerCount = int.Parse(Next(a)); break;
                case "--verbose-native": llama.VerboseNativeLog = true; break;

                case "--max-text": extraction.MaxTextLength = int.Parse(Next(a)); break;
                case "--threshold": extraction.CandidateThreshold = double.Parse(Next(a), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--no-tables": extraction.IncludeTables = false; break;
                case "--page-headers": extraction.IncludePageHeadersFooters = true; break;
                case "--no-context": extraction.IncludeFollowingContext = false; break;
                case "--structural-only": extraction.UseLexicalRules = false; break;

                case "-q" or "--quiet": o.Quiet = true; break;
                case "--compact": o.CompactXml = true; break;

                default:
                    if (a.StartsWith('-')) throw new ArgumentException($"Tham số không hợp lệ: {a}");
                    o.Inputs.Add(a);
                    break;
            }
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

        Tuỳ chọn chính:
          -m, --model <path.gguf>   Mô hình GGUF (mặc định: biến DHX_MODEL, appsettings.json
                                    hoặc file .gguf duy nhất trong thư mục ./models)
          -o, --out <path>          Ghi kết quả ra file (mặc định in ra màn hình)
          -f, --format <fmt>        json | md | txt | xml | csv   (mặc định json)
              --no-llm              Chỉ dùng luật OpenXML, bỏ qua mô hình
              --dump-xml <path>     Ghi XML tinh gọn (đầy đủ đoạn + điểm số) để kiểm tra bộ lọc
              --show-raw            In nguyên văn JSON mô hình trả về cho từng khối
          -q, --quiet               Không in tiến trình

        Mô hình / hiệu năng CPU:
              --ctx <n>             Cửa sổ ngữ cảnh (mặc định 4096)
          -t, --threads <n>         Số luồng CPU (mặc định số lõi - 1)
              --gpu-layers <n>      Số lớp đẩy lên GPU (mặc định 0 = chạy hoàn toàn CPU).
                                    Chỉ có tác dụng khi build với -p:UseCuda=true.
              --chunk-tokens <n>    Ngân sách token mỗi khối XML (mặc định 2200)
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
              --raw-levels          Giữ nguyên cấp do mô hình trả về (không chuẩn hoá)
              --two-pass            Quét hai lượt với cách cắt khối khác nhau, đánh dấu (?)
                                    những đoạn hai lượt bất đồng để xem lại. Tốn gấp ~2 lần.
              --model-levels        Lấy cấp do mô hình đoán thay vì đọc w:outlineLvl trong file

        Ví dụ:
          dhx extract bao-cao.docx -m models\Llama-3.2-3B-Instruct-Q4_K_M.gguf -f md
          dhx extract *.docx --no-llm -f csv -o outline.csv
          dhx xml bao-cao.docx | less
        """;
}
