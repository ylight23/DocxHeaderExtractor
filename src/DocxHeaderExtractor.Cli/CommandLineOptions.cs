using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Projection;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Cli;

public sealed class CommandLineOptions
{
    public string Command { get; private set; } = "extract";
    public List<string> Inputs { get; } = [];
    public string? OutputPath { get; private set; }
    public OutlineFormat Format { get; private set; } = OutlineFormat.Json;
    public bool Quiet { get; private set; }

    /// <summary>Explicit opt-in: provider payload diagnostics may contain document text.</summary>
    public bool ShowRawOutput { get; private set; }

    /// <summary>In cả stack trace khi một tài liệu lỗi.</summary>
    public bool Verbose { get; private set; }
    public bool ShowHelp { get; private set; }
    public PipelineOptions Pipeline { get; } = new();
    /// <summary>Provider selection owned by the CLI composition root, not the processing pipeline.</summary>
    public InferenceProviderSelection Provider { get; } = new();

    /// <summary>Đích .docx cho hành động ghi outline; null = run chỉ đọc.</summary>
    public string? WritebackPath { get; private set; }
    public bool WritebackOverwrite { get; private set; }
    public bool WritebackHeadingStyles { get; private set; }

    /// <summary>Configure only the host-owned remote provider, never processing options.</summary>
    public void ConfigureProviderDiagnostics(Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (ShowRawOutput && Provider.Backend is InferenceBackend.LmStudio or InferenceBackend.OpenRouter or InferenceBackend.Sglang)
            Provider.Remote.DebugLog = log;
    }

    public static CommandLineOptions Parse(string[] args)
    {
        var o = new CommandLineOptions();
        var llama = o.Provider.LocalModel;
        var extraction = o.Pipeline.Extraction;

        if (args.Length == 0) { o.ShowHelp = true; return o; }

        int i = 0;
        // Only verbs the CLI actually dispatches. Anything else is treated as an input path
        // by the default extract route.
        if (!args[0].StartsWith('-') && args[0] is "extract" or "help" or "info")
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
                case "--openrouter":
                    o.Provider.Backend = InferenceBackend.OpenRouter;
                    o.Provider.Remote = RemoteInferenceOptions.FromEnvironment("openrouter");
                    break;
                case "--openrouter-model":
                    o.Provider.Backend = InferenceBackend.OpenRouter;
                    o.Provider.Remote = RemoteInferenceOptions.FromEnvironment("openrouter");
                    o.Provider.Remote.UseOpenRouterModel(Next(a));
                    break;
                case "--lmstudio":
                    o.Provider.Backend = InferenceBackend.LmStudio;
                    o.Provider.Remote = RemoteInferenceOptions.FromEnvironment("lmstudio");
                    break;
                case "--lmstudio-model":
                    o.Provider.Backend = InferenceBackend.LmStudio;
                    o.Provider.Remote = RemoteInferenceOptions.FromEnvironment("lmstudio");
                    o.Provider.Remote.Model = Next(a);
                    break;
                case "--lmstudio-endpoint":
                    o.Provider.Backend = InferenceBackend.LmStudio;
                    o.Provider.Remote = RemoteInferenceOptions.FromEnvironment("lmstudio");
                    o.Provider.Remote.Endpoint = new Uri(Next(a), UriKind.Absolute);
                    break;
                case "--lmstudio-context":
                    o.Provider.Backend = InferenceBackend.LmStudio;
                    o.Provider.Remote = RemoteInferenceOptions.FromEnvironment("lmstudio");
                    o.Provider.Remote.ContextSize = int.Parse(Next(a));
                    break;
                case "--sglang":
                    o.Provider.Backend = InferenceBackend.Sglang;
                    o.Provider.Remote = RemoteInferenceOptions.FromEnvironment("sglang");
                    break;
                case "--sglang-model":
                    o.Provider.Backend = InferenceBackend.Sglang;
                    o.Provider.Remote = RemoteInferenceOptions.FromEnvironment("sglang");
                    o.Provider.Remote.Model = Next(a);
                    break;
                case "--sglang-endpoint":
                    o.Provider.Backend = InferenceBackend.Sglang;
                    o.Provider.Remote = RemoteInferenceOptions.FromEnvironment("sglang");
                    o.Provider.Remote.Endpoint = new Uri(Next(a), UriKind.Absolute);
                    break;
                case "--sglang-api-key":
                    o.Provider.Backend = InferenceBackend.Sglang;
                    o.Provider.Remote = RemoteInferenceOptions.FromEnvironment("sglang");
                    o.Provider.Remote.ApiKey = Next(a);
                    break;
                case "--sglang-context":
                    o.Provider.Backend = InferenceBackend.Sglang;
                    o.Provider.Remote = RemoteInferenceOptions.FromEnvironment("sglang");
                    o.Provider.Remote.ContextSize = int.Parse(Next(a));
                    break;
                case "--show-raw": o.ShowRawOutput = true; break;

                case "--ctx": llama.ContextSize = uint.Parse(Next(a)); llama.AutoContextSize = false; break;
                case "--threads" or "-t": llama.Threads = int.Parse(Next(a)); break;
                case "--chunk-tokens": o.Pipeline.Chunking.TokenBudget = int.Parse(Next(a)); break;
                case "--seed": llama.Seed = uint.Parse(Next(a)); break;
                case "--gpu-layers" or "-ngl": llama.GpuLayerCount = int.Parse(Next(a)); break;
                case "--verbose-native": llama.VerboseNativeLog = true; break;
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
          dhx extract <file> [tuỳ chọn]                  # mặc định; tên lệnh có thể bỏ qua
          dhx info    <file.gguf>                        # xem metadata mô hình
          dhx help                                       # trợ giúp

        Tuỳ chọn chính:
          -m, --model <path.gguf>   Mô hình GGUF (mặc định: biến DHX_MODEL, appsettings.json
                                    hoặc file .gguf duy nhất trong thư mục ./models)
          -o, --out <path>          Ghi kết quả ra file (mặc định in ra màn hình)
          -f, --format <fmt>        json | md | txt | xml | csv   (mặc định json)
              --no-llm              Không gọi mô hình (chỉ đọc nguồn, không tự nhận tiêu đề)
              --openrouter          Gọi OpenRouter; đọc key từ OPENROUTER_API_KEY
              --openrouter-model m  Model slug
              --lmstudio            Gọi LM Studio OpenAI-compatible trên loopback
              --lmstudio-model m    Model identifier trả bởi GET /v1/models
              --lmstudio-endpoint u Chat endpoint (mặc định http://127.0.0.1:1234/v1/chat/completions)
              --lmstudio-context n  Context đã nạp trong LM Studio
              --sglang              Gọi gateway SGLang/vLLM OpenAI-compatible; đọc SGLANG_ENDPOINT,
                                    SGLANG_MODEL, SGLANG_API_KEY từ biến môi trường
              --sglang-model m      Model identifier trên gateway
              --sglang-endpoint u   Chat endpoint
              --sglang-api-key k    Bearer token cho gateway
              --sglang-context n    Context gateway khai
              --show-raw            Log request/response remote (có thể chứa nội dung tài liệu)
          -q, --quiet               Không in tiến trình
          -v, --verbose             In stack trace khi một tài liệu lỗi

        Ghi outline ngược vào tài liệu (chỉ lệnh extract, mỗi lần một file):
              --write-docx <path>   Ghi w:outlineLvl của các heading đã chốt vào BẢN SAO .docx
                                    tại <path>. File nguồn không bao giờ bị sửa.
              --write-overwrite     Cho phép đè file đích đã tồn tại
              --write-heading-styles Gán thêm style Heading N có sẵn trong tài liệu

        Mô hình local:
              --ctx <n>             Cửa sổ ngữ cảnh
          -t, --threads <n>         Số luồng CPU
              --gpu-layers <n>      Số lớp đẩy lên GPU (0 = chỉ CPU)
              --chunk-tokens <n>    Ngân sách token mỗi chunk đầu ra
              --seed <n>            Seed
              --verbose-native      Log native của llama.cpp

        Bộ lọc OpenXML:
              --no-tables           Bỏ qua đoạn trong bảng

        Ví dụ:
          dhx extract bao-cao.docx -f md
          dhx extract *.docx --no-llm -f csv -o outline.csv
          dhx bao-cao.pdf --openrouter -f md
        """;
}
