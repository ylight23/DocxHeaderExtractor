using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using DocxHeaderExtractor.Core.Models;
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

// Mặc định lấy thẳng từ Core, để giao diện luôn khớp với lệnh CLI tương ứng.
app.MapGet("/api/defaults", () => Results.Json(Defaults.Current(), json));

app.MapPost("/api/extract", async (HttpRequest req, HttpResponse res, CancellationToken ct) =>
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

        options.Log = m => events.Writer.TryWrite(new { type = "log", message = m });

        // Một mô hình 7B chiếm ~4,5 GB; hai request song song sẽ vắt kiệt RAM máy để bàn.
        // Xếp hàng thay vì để cả hai cùng chết.
        if (!await Gate.WaitAsync(0, ct))
        {
            await EmitAsync(new { type = "log", message = "Đang có tài liệu khác chạy — xếp hàng chờ…" });
            await Gate.WaitAsync(ct);
        }

        try
        {
            var pipeline = new HeaderExtractionPipeline(options);
            var run = Task.Run(() => pipeline.RunAsync(inputPath, ct), ct);
            _ = run.ContinueWith(_ => events.Writer.TryComplete(), TaskScheduler.Default);

            await foreach (var evt in events.Reader.ReadAllAsync(ct))
                await EmitAsync(evt);

            var outline = await run;
            await EmitAsync(new
            {
                type = "result",
                outline,
                stats = Stats.From(outline),
            });
        }
        finally
        {
            Gate.Release();
        }
    }
    catch (OperationCanceledException)
    {
        // Người dùng đóng tab hoặc bấm huỷ — không còn gì để gửi.
    }
    catch (Exception ex)
    {
        try { await EmitAsync(new { type = "error", message = ex.Message }); }
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
