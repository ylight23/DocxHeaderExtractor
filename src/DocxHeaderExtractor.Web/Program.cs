using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Routing;
using DocxHeaderExtractor.HeadingAgent;
using DocxHeaderExtractor.Infrastructure.AI;
using DocxHeaderExtractor.Application.Runtime;
using DocxHeaderExtractor.Application.Tasks;
using DocxHeaderExtractor.V6Runner;
using DocxHeaderExtractor.Infrastructure.Runtime;
using DocxHeaderExtractor.Infrastructure.Sources;
using DocxHeaderExtractor.Web;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.AspNetCore.Http.Features;

// Content root phải là thư mục chứa dll, không phải thư mục làm việc: dhx-ui.cmd chạy từ gốc
// repo để ModelCatalog thấy models\, nên mặc định (Directory.GetCurrentDirectory) sẽ trỏ sai
// và wwwroot không được phục vụ.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// Trần upload phải đặt ở CẢ HAI tầng, nếu không tầng thấp hơn quyết định trong im lặng: Kestrel
// mặc định chặn ở 30 MB và ném BadHttpRequestException TRƯỚC khi handler chạy, nên trình duyệt chỉ
// thấy ERR_CONNECTION_RESET chứ không nhận được thông báo lỗi nào. Một hằng số cho cả hai để chúng
// không đi lệch nhau.
const long MaxUploadBytes = 128L * 1024 * 1024;
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = MaxUploadBytes);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = MaxUploadBytes);
builder.Services.AddSingleton<LlamaModelCache>();
builder.Services.AddSingleton<WebTransportFactoryResolver>(sp => selection =>
    new WebInferenceTransportFactory(selection, sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<LlamaModelCache>()));
builder.Services.AddSingleton<LmStudioModelDiscovery>();
builder.Services.AddSingleton<WritebackStore>();
builder.Services.AddSingleton<ITaskRunStore>(_ => new JsonFileTaskRunStore(RuntimeStatePaths.RunDirectory));
builder.Services.AddSingleton<ITaskTelemetrySink>(_ => new JsonLinesTaskTelemetrySink(RuntimeStatePaths.TelemetryPath));
builder.Services.AddSingleton<IInputResourceResolver>(_ =>
    new FileInputResourceResolver([Path.GetTempPath(), AppContext.BaseDirectory]));
builder.Services.AddHttpClient("OpenRouter", client =>
{
    client.Timeout = TimeSpan.FromMinutes(5);
});
builder.Services.AddHttpClient("LmStudio", client =>
{
    client.Timeout = TimeSpan.FromMinutes(10);
});
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("DHX_UI_URL") ?? "http://localhost:5099");

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

var json = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
};
json.Converters.Add(new JsonStringEnumConverter());

// Danh sách mô hình .gguf tìm thấy, kèm cửa sổ ngữ cảnh nên dùng cho từng họ mô hình.
app.MapGet("/api/models", () => Results.Json(ModelCatalog.List(), json));

app.MapGet("/api/lmstudio/models", async (LmStudioModelDiscovery discovery, CancellationToken ct) =>
{
    try
    {
        return Results.Json(await discovery.ListAsync(ct), json);
    }
    catch (Exception ex) when (ex is HttpRequestException or FormatException or InvalidOperationException)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Không kết nối được LM Studio",
            detail: ex.Message);
    }
});

// Mặc định lấy thẳng từ Core, để giao diện luôn khớp với lệnh CLI tương ứng.
app.MapGet("/api/defaults", () => Results.Json(Defaults.Current(), json));

// Prompt và instruction mà agent gửi đi, chỉ để hiển thị. Chúng là hằng số trong mã nguồn: giao diện không sửa được.
app.MapGet("/api/prompts", () => Results.Json(new { pdf = PromptCatalog.Pdf, docx = PromptCatalog.Docx }, json));

// Bản .docx đã gắn outline chỉ sống trong bộ nhớ tới lần tải đầu tiên. Không có đường dẫn nào do
// client chỉ định: đích ghi luôn nằm trong thư mục tạm của chính request đó.
app.MapGet("/api/outline/{runId:guid}.docx", (Guid runId, WritebackStore store) =>
{
    var entry = store.Take(runId);
    return entry is null
        ? Results.NotFound(new { message = "Bản ghi đã được tải hoặc đã hết hạn." })
        : Results.File(entry.Content,
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            entry.FileName);
});

app.MapPost("/api/extract", async (
    HttpRequest req,
    HttpResponse res,
    WritebackStore writebackStore,
    WebTransportFactoryResolver transportFactory,
    CancellationToken ct) =>
{
    if (!req.HasFormContentType)
    {
        res.StatusCode = StatusCodes.Status400BadRequest;
        await res.WriteAsync("Cần multipart/form-data.", ct);
        return;
    }

    IFormCollection form;
    try
    {
        form = await req.ReadFormAsync(ct);
    }
    catch (BadHttpRequestException ex)
    {
        // Vượt trần thì Kestrel huỷ kết nối; không bắt ở đây thì phía trình duyệt chỉ thấy
        // ERR_CONNECTION_RESET và không có cách nào biết vì sao.
        res.StatusCode = StatusCodes.Status413PayloadTooLarge;
        await res.WriteAsync($"File quá lớn (trần {MaxUploadBytes / (1024 * 1024)} MB): {ex.Message}", ct);
        return;
    }
    catch (InvalidDataException)
    {
        res.StatusCode = StatusCodes.Status400BadRequest;
        await res.WriteAsync("Multipart upload không hợp lệ.", ct);
        return;
    }

    var upload = form.Files["file"];
    if (upload is null || upload.Length == 0)
    {
        res.StatusCode = StatusCodes.Status400BadRequest;
        await res.WriteAsync("Chưa chọn file.", ct);
        return;
    }

    // NDJSON: mỗi dòng một sự kiện, để trình duyệt hiện tiến độ trong lúc mô hình còn chạy.
    res.ContentType = "application/x-ndjson; charset=utf-8";
    res.Headers.CacheControl = "no-store";

    var events = Channel.CreateUnbounded<object>();

    async Task EmitAsync(object payload)
    {
        var line = JsonSerializer.Serialize(payload, json);
        await res.WriteAsync(line + "\n", Encoding.UTF8, ct);
        await res.Body.FlushAsync(ct);
    }

    var work = Path.Combine(Path.GetTempPath(), "dhx-ui", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(work);
    var inputPath = Path.Combine(work, SafeName(upload.FileName));

    try
    {
        await using (var fs = File.Create(inputPath))
            await upload.CopyToAsync(fs, ct);

        var options = RequestOptions.Build(form, out var problem, out var provider);
        if (problem is not null)
        {
            await EmitAsync(new { type = "error", message = problem });
            return;
        }

        // Production Web does not publish free-form provider diagnostics (which may contain
        // source text, completions or credentials). Structured harness events remain visible.
        options.Log = _ => { };
        RequestOptions.ConfigureProviderDiagnostics(form, provider, _ => { });

        // Một quyết định duy nhất về định dạng, đọc từ byte, dùng chung cho mọi bước phía sau.
        var uploadedType = UploadedSourceDetector.Detect(inputPath);

        // Hai backend dùng tài nguyên máy này cần khóa. OpenRouter có thể chạy đồng thời và không
        // được giữ hàng chỉ vì GPU local/LM Studio đang bận.
        var gateHeld = false;
        var usesLocalCompute = provider.Backend is InferenceBackend.Local or InferenceBackend.LmStudio;
        if (!options.DisableLlm && usesLocalCompute && !await Gate.WaitAsync(0, ct))
        {
            await EmitAsync(new { type = "log", message = "Đang có tài liệu khác chạy — xếp hàng chờ…" });
            await Gate.WaitAsync(ct);
            gateHeld = true;
        }
        else if (!options.DisableLlm && usesLocalCompute)
            gateHeld = true;

        try
        {
            // The factory deliberately resolves DOCX and PDF separately. In particular, an
            // OpenRouter instance constructed directly here would not carry the PDF production
            // authorization marker, while local/LM Studio remain valid DOCX backends only.
            // Chạy thẳng tuyến V6 (agent PDF/DOCX), giống CLI: không qua harness. Tiến độ stream dưới dạng sự kiện
            // "agent" để giữ nhật ký trên giao diện.
            // Phản hồi của mô hình chỉ được giữ và trả về khi người dùng tự bật "Hiện phản hồi của mô hình".
            var showRaw = form["showRaw"].ToString() is "1" or "true" or "on";
            // Each reply is written to the stream as soon as the model returns it; units run in parallel, so replies interleave.
            using var runner = new V6DocumentRunner(options, transportFactory(provider),
                showRaw ? r => events.Writer.TryWrite(ReplyEvent(r)) : null);

            // Đích ghi do server đặt bên trong thư mục tạm của request, không bao giờ lấy từ form:
            // một đường dẫn do client chỉ định là đường để ghi đè file bất kỳ trên máy chủ.
            var wantsWriteback = form["writeback"].ToString() is "1" or "true" or "on";
            var writebackTarget = wantsWriteback
                ? Path.Combine(work, Path.GetFileNameWithoutExtension(SafeName(upload.FileName)) + ".outline.docx")
                : null;

            var runId = Guid.NewGuid();
            void Stage(string stage, string kind, string message) => events.Writer.TryWrite(new
            {
                type = "agent",
                runId,
                timestamp = DateTimeOffset.UtcNow,
                stage,
                kind,
                message,
            });
            var run = Task.Run(async () =>
            {
                Stage("extract", "Started", "Bắt đầu trích xuất tài liệu.");
                var execution = await runner.ExtractAsync(inputPath, ct: ct);
                var outline = execution.Outline;
                Stage("extract", "Completed", $"Tuyến {uploadedType} trả {outline.Headings.Count} heading từ {outline.ParagraphCount} đoạn.");
                OutlineWritebackReport? written = null;
                if (writebackTarget is not null)
                {
                    Stage("writeback", "Started", "Ghi outline vào bản sao.");
                    written = V6DocumentRunner.WriteOutline(inputPath, outline, writebackTarget, options.Extraction);
                    Stage("writeback", "Completed", $"Đã ghi {written.Applied} heading, bỏ qua {written.Skipped} mục.");
                }
                return (Outline: outline, Execution: execution, Writeback: written);
            }, ct);
            _ = run.ContinueWith(_ => events.Writer.TryComplete(), TaskScheduler.Default);

            await foreach (var evt in events.Reader.ReadAllAsync(ct))
                await EmitAsync(evt);

            var (outline, execution, writeback) = await run;

            // Đọc vào bộ nhớ trước khi finally xoá thư mục tạm; link chỉ tải được đúng một lần.
            string? download = null;
            if (writeback is { } written && File.Exists(written.OutputPath))
            {
                writebackStore.Put(
                    runId,
                    await File.ReadAllBytesAsync(written.OutputPath, ct),
                    Path.GetFileName(written.OutputPath));
                download = $"/api/outline/{runId}.docx";
            }

            var requiresReview = outline.Headings.Count(h =>
                h.DecisionStatus == HeadingDecisionStatus.RequiresReview || h.Disputed);
            await EmitAsync(new
            {
                type = "result",
                outline = WebPipelineProjection.SafeOutline(outline, json),
                pipeline = WebPipelineProjection.Project(runId, "Completed", outline, execution, uploadedType.ToString().ToLowerInvariant()),
                stats = Stats.From(outline),
                sourceType = uploadedType.ToString().ToLowerInvariant(),
                agent = new
                {
                    runId,
                    outcome = "Completed",
                    steps = writeback is null ? 1 : 2,
                    repairAttempts = 0,
                    requiresReview,
                    message = $"Trích xuất {outline.Headings.Count} heading từ {outline.ParagraphCount} đoạn."
                        + (requiresReview == 0 ? "" : $" Còn {requiresReview} mục cần xem lại."),
                    writebackApplied = writeback?.Applied,
                    download,
                },
            });
        }
        finally
        {
            if (gateHeld) Gate.Release();
        }
    }
    catch (OperationCanceledException)
    {
        // Người dùng đóng tab hoặc bấm huỷ — không còn gì để gửi.
    }
    catch (Exception ex)
    {
        // A refusal about the input format names only the format; it carries no provider payload, so it can be shown.
        var message = ex is NotSupportedException or FileNotFoundException
            ? ex.Message
            : $"Execution failed ({ex.GetType().Name}). Provider payload and source diagnostics are withheld.";
        try { await EmitAsync(new { type = "error",
            message,
            pipeline = new { schemaVersion = "web-pipeline-v2", availability = "unavailable", outcome = "Failed",
                stages = new[] { new WebPipelineStage("execution", "failed", "Server exception; sub-stages not-recorded") } } }); }
        catch (Exception) { /* kết nối đã đứt */ }
    }
    finally
    {
        try { Directory.Delete(work, recursive: true); } catch (IOException) { }
    }
});

// Cấu hình log của llama.cpp một lần cho cả tiến trình, trước khi có request nào chạm native lib.
// Native backend selection is process-wide, while GPU layer count remains per model load. Prefer
// the backend bundled with this web executable so a later request can legitimately offload layers.
DocxHeaderExtractor.Infrastructure.AI.LlamaInferenceTransport.ConfigureNativeLogging(verbose: false, gpuLayerCount: int.MaxValue);

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine($"dhx-ui đang chạy: {string.Join(", ", app.Urls.DefaultIfEmpty("http://localhost:5099"))}");
Console.WriteLine("Ctrl+C để dừng.");
app.Run();

// One model reply as a stream event: what the model streamed, the tool calls it made, the tool results it was given, and its content.
static object ReplyEvent(ModelReply r) => new
{
    type = "reply",
    unit = r.Unit,
    turn = r.Turn,
    reasoning = r.Reasoning,
    finishReason = r.FinishReason,
    content = r.Content,
    toolCalls = r.ToolCalls.Select(c => new { name = c.Name, arguments = c.Arguments }).ToArray(),
    toolResults = r.ToolResults.Select(x => new { toolCallId = x.ToolCallId, content = x.Content }).ToArray(),
};

// Tên file người dùng tải lên không được phép thoát khỏi thư mục làm việc.
static string SafeName(string name)
{
    var bare = Path.GetFileName(name);
    foreach (var c in Path.GetInvalidFileNameChars()) bare = bare.Replace(c, '_');
    return string.IsNullOrWhiteSpace(bare) ? "upload.docx" : bare;
}

// Public để integration test khởi động đúng host WebApplicationFactory, không cần facade riêng.
public partial class Program
{
    /// <summary>Chỉ cho một tài liệu chạy qua mô hình tại một thời điểm.</summary>
    internal static readonly SemaphoreSlim Gate = new(1, 1);
}
