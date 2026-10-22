using System.Text;
using DocxHeaderExtractor.Contracts.Runtime;
using DocxHeaderExtractor.Contracts.Tasks;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.HeadingAgent.Selectors;
using DocxHeaderExtractor.Infrastructure.Runtime;
using DocxHeaderExtractor.Infrastructure.Sources;
using DocxHeaderExtractor.Web;
using DocxHeaderExtractor.Web.Endpoints;
using Microsoft.AspNetCore.Http.Features;

// Content root phải là thư mục chứa dll, không phải thư mục làm việc: dhx-ui.cmd chạy từ gốc
// repo để ModelCatalog thấy models\, nên mặc định (Directory.GetCurrentDirectory) sẽ trỏ sai
// và wwwroot không được phục vụ.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = Uploads.MaxBytes);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = Uploads.MaxBytes);
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
var configuredUiUrl = Environment.GetEnvironmentVariable("DHX_UI_URL");
if (!string.IsNullOrWhiteSpace(configuredUiUrl)) builder.WebHost.UseUrls(configuredUiUrl);
else if (string.IsNullOrWhiteSpace(builder.Configuration["urls"])) builder.WebHost.UseUrls("http://localhost:5099");

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// Read metadata from the loaded assemblies, so a stale backend can be identified without inference.
app.MapGet("/api/build", () => Results.Json(new
{
    webVersion = typeof(Program).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion,
    headingAgentVersion = typeof(AgentSelector).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion,
}, WebJson.Options));

// Danh sách mô hình .gguf tìm thấy, kèm cửa sổ ngữ cảnh nên dùng cho từng họ mô hình.
app.MapGet("/api/models", () => Results.Json(ModelCatalog.List(), WebJson.Options));

app.MapGet("/api/lmstudio/models", async (LmStudioModelDiscovery discovery, CancellationToken ct) =>
{
    try
    {
        return Results.Json(await discovery.ListAsync(ct), WebJson.Options);
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
app.MapGet("/api/defaults", () => Results.Json(Defaults.Current(), WebJson.Options));

// Prompt và instruction mà agent gửi đi, chỉ để hiển thị. Chúng là hằng số trong mã nguồn: giao diện không sửa được.
app.MapGet("/api/prompts", () => Results.Json(new { pdf = PromptCatalog.Pdf, docx = PromptCatalog.Docx, scan = PromptCatalog.Scan }, WebJson.Options));

app.MapPost("/api/inspect", InspectEndpoint.HandleAsync);

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

app.MapPost("/api/extract", ExtractEndpoint.HandleAsync);

// Cấu hình log của llama.cpp một lần cho cả tiến trình, trước khi có request nào chạm native lib.
// Native backend selection is process-wide, while GPU layer count remains per model load. Prefer
// the backend bundled with this web executable so a later request can legitimately offload layers.
DocxHeaderExtractor.Infrastructure.AI.LlamaInferenceTransport.ConfigureNativeLogging(verbose: false, gpuLayerCount: int.MaxValue);

Console.OutputEncoding = Encoding.UTF8;
app.Lifetime.ApplicationStarted.Register(() =>
{
    Console.WriteLine($"dhx-ui đang chạy: {string.Join(", ", app.Urls)}");
    Console.WriteLine($"Build: {typeof(Program).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion}");
    Console.WriteLine("Ctrl+C để dừng.");
});
app.Run();

// Public để integration test khởi động đúng host WebApplicationFactory, không cần facade riêng.
public partial class Program
{
    /// <summary>Chỉ cho một tài liệu chạy qua mô hình tại một thời điểm.</summary>
    internal static readonly SemaphoreSlim Gate = new(1, 1);
}
