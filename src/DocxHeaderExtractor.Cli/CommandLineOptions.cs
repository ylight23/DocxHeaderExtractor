using DocxHeaderExtractor.DocumentProcessing.Output.Outline;
using DocxHeaderExtractor.DocumentProcessing.Harness;

namespace DocxHeaderExtractor.Cli;

public sealed class CommandLineOptions
{
    public string Command { get; private set; } = "extract";
    public List<string> Inputs { get; } = [];
    public string? OutputPath { get; private set; }
    /// <summary>scan-text-eval: the gold transcription of the lines to read (SCAN_LINE_TEXT_GOLD_V1).</summary>
    public string? GoldPath { get; private set; }
    /// <summary>scan-extract: calls at once to the provider in the conversation and the transcription (null: the route's defaults).</summary>
    public int? Jobs { get; private set; }
    /// <summary>scan-extract: also ask the vision model about every page's typography (off: it is measured on the images).</summary>
    public bool Vision { get; private set; }
    /// <summary>Scan: ask the selection again instead of reusing the answers already paid for (to sample the model anew).</summary>
    public bool FreshSelection { get; private set; }
    /// <summary>Scan (production): units are cut at the paragraphs measured from the line gaps and packed across pages. <c>--page-units</c> returns to the
    /// earlier cut, one unit per page (kept as the experiment it was).</summary>
    public bool ParagraphUnits { get; private set; } = true;
    /// <summary>Scan: the text lines come from OpenCV (stamp rings removed, ink bands), not from an OCR engine; the vision model reads them.</summary>
    public bool SegmentedLines { get; private set; } = true;
    public OutlineFormat Format { get; private set; } = OutlineFormat.Json;
    public bool Quiet { get; private set; }

    /// <summary>On by default for every route (PDF, DOCX, scan): each model reply is printed as it arrives, and provider payload diagnostics may
    /// contain document text. <c>--no-raw</c> turns it off.</summary>
    public bool ShowRawOutput { get; private set; } = true;

    /// <summary>In cả stack trace khi một tài liệu lỗi.</summary>
    public bool Verbose { get; private set; }
    public bool ShowHelp { get; private set; }
    public PipelineOptions Pipeline { get; } = new();

    /// <summary>Đích .docx cho hành động ghi outline; null = run chỉ đọc.</summary>
    public string? WritebackPath { get; private set; }
    public bool WritebackOverwrite { get; private set; }
    public bool WritebackHeadingStyles { get; private set; }

    public static CommandLineOptions Parse(string[] args)
    {
        var o = new CommandLineOptions();
        var extraction = o.Pipeline.Extraction;

        if (args.Length == 0) { o.ShowHelp = true; return o; }

        int i = 0;
        // Only verbs the CLI actually dispatches. Anything else is treated as an input path
        // by the default extract route.
        if (!args[0].StartsWith('-') && args[0] is "extract" or "help" or "entities" or "scan-extract" or "scan-text-eval" or "scan-heading-eval" or "scan-evidence-json")
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
                case "-o" or "--out": o.OutputPath = Next(a); break;
                case "--gold": o.GoldPath = Next(a); break;
                case "--vision": o.Vision = true; break;
                case "--fresh-selection": o.FreshSelection = true; break;
                case "--paragraph-units": o.ParagraphUnits = true; break;
                case "--page-units": o.ParagraphUnits = false; break;
                case "--segmented-lines": o.SegmentedLines = true; break;
                case "--tesseract-lines": o.SegmentedLines = false; break;
                case "--jobs": o.Jobs = int.TryParse(Next(a), out var jobs) && jobs is >= 1 and <= 64 ? jobs : throw new ArgumentException("--jobs cần số từ 1 đến 64"); break;
                case "-f" or "--format": o.Format = ParseFormat(Next(a)); break;
                case "--no-llm": o.Pipeline.DisableLlm = true; break;
                // OpenRouter is the only provider; the flag is still accepted so existing scripts keep working.
                case "--openrouter": break;
                case "--show-raw": o.ShowRawOutput = true; break;
                case "--no-raw": o.ShowRawOutput = false; break;
                case "--chunk-tokens": o.Pipeline.Chunking.TokenBudget = int.Parse(Next(a)); break;
                case "--no-tables": extraction.IncludeTables = false; break;

                case "-q" or "--quiet": o.Quiet = true; break;
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
        dhx – trích xuất tiêu đề (heading) từ .docx/.doc/.pdf

        Cách dùng:
          dhx extract <file> [tuỳ chọn]                  # mặc định; tự nhận DOCX scan để chạy OCR/VLM rồi dựng outline
          dhx scan-extract <file.docx> [-o <thư-mục>]    # chẩn đoán scan: OCR + VLM chép ảnh cắt dòng
                                                         # (qwen/qwen3.7-flash) + hội thoại V6; Qwen
                                                         # quyết định; key từ OPENROUTER_API_KEY; in JSON
                                                         # kiểu chữ (đậm, cỡ) đo trên ảnh; --vision: hỏi thêm
                                                         # VLM từng trang
                                                         # --jobs N: số lần gọi API cùng lúc cho xem trang,
                                                         # chép chữ và hội thoại (mặc định 32); giảm nếu bị
                                                         # giới hạn tốc độ
                                                         # chạy lại cùng file: selection đã trả tiền được
                                                         # dùng lại (.dhx-evidence/selection); --fresh-selection:
                                                         # hỏi lại model (đo độ dao động)
          dhx scan-text-eval <file.docx> --gold <gold.json> [-o <thư-mục>]
                                                         # đo độ chính xác chữ: 2 view OCR và VLM chép mù
                                                         # (không xem OCR) so với gold; gửi ảnh cắt dòng
          dhx scan-heading-eval <ket-qua.json> --gold <gold.json>
                                                         # chấm heading của một kết quả scan-extract theo
                                                         # bộ đáp án: đủ (recall), đúng (precision), tách đôi
          dhx entities <file> [-o <out.json>]            # trích thực thể (bài toán thử nghiệm, các loại mặc định); JSON gồm envelope và output
          dhx scan-evidence-json <evidence.msgpack>      # in evidence OCR của một scan ra JSON (stdout) để đọc khi debug;
                                                         # ghi ra file: dhx scan-evidence-json <file> > evidence.json
          dhx help                                       # trợ giúp

        Mô hình: qwen/qwen3.7-flash qua OpenRouter (route Alibaba); key đọc từ OPENROUTER_API_KEY.

        Tuỳ chọn chính:
          -o, --out <path>          Ghi kết quả ra file (mặc định in ra màn hình)
          -f, --format <fmt>        json | md | txt | xml | csv   (mặc định json)
              --no-llm              Không gọi mô hình (chỉ đọc nguồn, không tự nhận tiêu đề)
              --show-raw            Mặc định bật: in từng phản hồi của model và log request/response remote (có thể chứa nội dung tài liệu)
              --no-raw              Tắt in phản hồi của model
              --chunk-tokens <n>    Ngân sách token mỗi chunk đầu ra
          -q, --quiet               Không in tiến trình
          -v, --verbose             In stack trace khi một tài liệu lỗi

        Ghi outline ngược vào tài liệu (chỉ lệnh extract, mỗi lần một file):
              --write-docx <path>   Ghi w:outlineLvl của các heading đã chốt vào BẢN SAO .docx
                                    tại <path>. File nguồn không bao giờ bị sửa.
              --write-overwrite     Cho phép đè file đích đã tồn tại
              --write-heading-styles Gán thêm style Heading N có sẵn trong tài liệu

        Bộ lọc OpenXML:
              --no-tables           Bỏ qua đoạn trong bảng

        Ví dụ:
          dhx extract bao-cao.docx -f md
          dhx extract *.docx --no-llm -f csv -o outline.csv
          dhx bao-cao.pdf -f md
        """;
}
