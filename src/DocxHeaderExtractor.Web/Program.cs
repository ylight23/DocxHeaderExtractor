using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using DocxHeaderExtractor.AgentHarness;
using DocxHeaderExtractor.Core.Eval;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Learning;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;
using DocxHeaderExtractor.Web;
using Microsoft.AspNetCore.Http.Features;

// Content root phải là thư mục chứa dll, không phải thư mục làm việc: dhx-ui.cmd chạy từ gốc
// repo để ModelCatalog thấy models\, nên mặc định (Directory.GetCurrentDirectory) sẽ trỏ sai
// và wwwroot không được phục vụ.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 128L * 1024 * 1024);
builder.Services.AddSingleton<LlamaModelCache>();
builder.Services.AddSingleton(_ => new CorrectionMemory(CorrectionMemory.DefaultPath()));
builder.Services.AddSingleton<DocumentAgentHarnessFactory>();
builder.Services.AddSingleton<LmStudioModelDiscovery>();
builder.Services.AddSingleton<WritebackStore>();
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

// Nhận lại review bundle người dùng đã sửa và chỉ cho phép sinh nhãn vàng khi mọi paragraph
// đã được xác nhận. Không lưu tài liệu hay dữ liệu review trên server.
app.MapPost("/api/review/key", async (HttpRequest req, CancellationToken ct) =>
{
    try
    {
        using var reader = new StreamReader(req.Body, Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: false);
        var bundle = ReviewBundle.Parse(await reader.ReadToEndAsync(ct));
        return Results.Json(new
        {
            key = bundle.ToAnswerKeyText(),
            trainingJsonl = bundle.ToTrainingJsonl(),
        }, json);
    }
    catch (Exception ex) when (ex is FormatException or InvalidOperationException or JsonException)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});

// Chỉ lưu những dòng người dùng đổi khác dự đoán. Correction nằm cục bộ và chỉ được retrieval
// làm ví dụ; endpoint không fine-tune hoặc tự deploy model mới.
app.MapPost("/api/corrections", async (HttpRequest req, CorrectionMemory memory, CancellationToken ct) =>
{
    try
    {
        using var reader = new StreamReader(req.Body, Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: false);
        var bundle = ReviewBundle.Parse(await reader.ReadToEndAsync(ct));
        var saved = await memory.SaveChangedAsync(bundle, ct);
        return Results.Json(new { saved, total = memory.Count }, json);
    }
    catch (Exception ex) when (ex is FormatException or InvalidOperationException or JsonException)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});

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
    LlamaModelCache modelCache,
    DocumentAgentHarnessFactory harnessFactory,
    IHttpClientFactory httpClientFactory,
    CorrectionMemory correctionMemory,
    WritebackStore writebackStore,
    CancellationToken ct) =>
{
    if (!req.HasFormContentType)
    {
        res.StatusCode = StatusCodes.Status400BadRequest;
        await res.WriteAsync("Cần multipart/form-data.", ct);
        return;
    }

    var form = await req.ReadFormAsync(ct);
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

        var options = RequestOptions.Build(form, out var problem);
        if (problem is not null)
        {
            await EmitAsync(new { type = "error", message = problem });
            return;
        }

        // Giữ log trong UI như trước, đồng thời ghi ra stdout để chạy `dhx-ui.cmd`
            // có dev log tương tự cửa sổ Developer Logs của LM Studio.
            options.Log = m =>
            {
                Console.WriteLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [DHX] {m}");
                events.Writer.TryWrite(new { type = "log", message = m });
            };
        if (options.Backend == InferenceBackend.LmStudio && options.ShowRawOutput)
            options.LmStudio.DebugLog = options.Log;
        // Mọi backend áp dụng correction khớp chính xác sau suy luận. Local GGUF và LM Studio
        // được retrieval ví dụ tương tự; pipeline không gửi lịch sử correction ra OpenRouter.
        options.CorrectionMemoryPath = correctionMemory.PathOnDisk;

        // Dùng đúng extractor/options như pipeline để bundle review có stable ID khớp tài liệu.
        var conversion = LegacyDocConverter.EnsureDocx(inputPath);
        SlimDocument slim;
        try { slim = new DocxSlimExtractor(options.Extraction).Extract(conversion.Path); }
        finally { LegacyDocConverter.Cleanup(conversion); }

        // Hai backend dùng tài nguyên máy này cần khóa. OpenRouter có thể chạy đồng thời và không
        // được giữ hàng chỉ vì GPU local/LM Studio đang bận.
        var gateHeld = false;
        var usesLocalCompute = options.Backend is InferenceBackend.Local or InferenceBackend.LmStudio;
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
            DocxHeaderExtractor.Core.Llm.IHeaderClassifier? classifier = null;
            if (!options.DisableLlm)
            {
                classifier = options.Backend switch
                {
                    InferenceBackend.OpenRouter => new DocxHeaderExtractor.Core.Llm.OpenRouterHeaderExtractor(
                        httpClientFactory.CreateClient("OpenRouter"), options.OpenRouter),
                    InferenceBackend.LmStudio => new DocxHeaderExtractor.Core.Llm.LmStudioHeaderExtractor(
                        httpClientFactory.CreateClient("LmStudio"), options.LmStudio),
                    _ => await modelCache.GetAsync(options.Llama, options.Chunking, ct),
                };
            }
            using var tool = classifier is null
                ? new PipelineDocumentExtractionTool(options)
                : new PipelineDocumentExtractionTool(
                    options,
                    classifier,
                    ownsClassifier: options.Backend is InferenceBackend.OpenRouter or InferenceBackend.LmStudio);

            // Đích ghi do server đặt bên trong thư mục tạm của request, không bao giờ lấy từ form:
            // một đường dẫn do client chỉ định là đường để ghi đè file bất kỳ trên máy chủ.
            var wantsWriteback = form["writeback"].ToString() is "1" or "true" or "on";
            var writebackTarget = wantsWriteback
                ? Path.Combine(work, Path.GetFileNameWithoutExtension(SafeName(upload.FileName)) + ".outline.docx")
                : null;
            using IDocumentActionTool? actionTool = wantsWriteback
                ? new OutlineWritebackTool(options.Extraction)
                : null;

            var sink = new DelegateAgentRunSink((evt, _) =>
            {
                events.Writer.TryWrite(new
                {
                    type = "agent",
                    runId = evt.RunId,
                    sequence = evt.Sequence,
                    stage = evt.Stage,
                    kind = evt.Kind.ToString(),
                    message = evt.Message,
                });
                return ValueTask.CompletedTask;
            });
            var harness = harnessFactory.Create(tool, sink, actionTool: actionTool);
            var request = new DocumentAgentRequest(
                inputPath,
                AllowExternalDataTransfer:
                    !options.DisableLlm && options.Backend == InferenceBackend.OpenRouter)
            {
                WritebackTargetPath = writebackTarget,
            };
            var run = Task.Run(() => harness.RunAsync(request, ct), ct);
            _ = run.ContinueWith(_ => events.Writer.TryComplete(), TaskScheduler.Default);

            await foreach (var evt in events.Reader.ReadAllAsync(ct))
                await EmitAsync(evt);

            var agentRun = await run;
            var outline = agentRun.Outline;

            // Đọc vào bộ nhớ trước khi finally xoá thư mục tạm; link chỉ tải được đúng một lần.
            string? download = null;
            if (agentRun.Writeback is { } written && File.Exists(written.OutputPath))
            {
                writebackStore.Put(
                    agentRun.RunId,
                    await File.ReadAllBytesAsync(written.OutputPath, ct),
                    Path.GetFileName(written.OutputPath));
                download = $"/api/outline/{agentRun.RunId}.docx";
            }

            await EmitAsync(new
            {
                type = "result",
                outline,
                stats = Stats.From(outline),
                review = ReviewBundle.Create(outline, slim),
                agent = new
                {
                    runId = agentRun.RunId,
                    outcome = agentRun.Outcome.ToString(),
                    steps = agentRun.Steps,
                    repairAttempts = agentRun.RepairAttempts,
                    requiresReview = agentRun.RequiresReview,
                    skill = agentRun.Skill.ToString(),
                    // Câu tường thuật do harness dựng từ chính outline đã qua validator, để UI
                    // không phải tự diễn giải lại các con số và tự ý làm nhẹ phần "cần duyệt".
                    message = AgentRunNarrator.Describe(agentRun),
                    writebackApplied = agentRun.Writeback?.Applied,
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
        try { await EmitAsync(new { type = "error", message = AgentRunNarrator.DescribeError(ex) }); }
        catch (Exception) { /* kết nối đã đứt */ }
    }
    finally
    {
        try { Directory.Delete(work, recursive: true); } catch (IOException) { }
    }
});

// Cấu hình log của llama.cpp một lần cho cả tiến trình, trước khi có request nào chạm native lib.
DocxHeaderExtractor.Core.Llm.LlamaHeaderExtractor.ConfigureNativeLogging(verbose: false);

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine($"dhx-ui đang chạy: {string.Join(", ", app.Urls.DefaultIfEmpty("http://localhost:5099"))}");
Console.WriteLine("Ctrl+C để dừng.");
app.Run();

// Tên file người dùng tải lên không được phép thoát khỏi thư mục làm việc.
static string SafeName(string name)
{
    var bare = Path.GetFileName(name);
    foreach (var c in Path.GetInvalidFileNameChars()) bare = bare.Replace(c, '_');
    return string.IsNullOrWhiteSpace(bare) ? "upload.docx" : bare;
}

// Lớp sinh ra từ top-level statements là internal partial — phải khớp accessibility.
internal partial class Program
{
    /// <summary>Chỉ cho một tài liệu chạy qua mô hình tại một thời điểm.</summary>
    internal static readonly SemaphoreSlim Gate = new(1, 1);
}
