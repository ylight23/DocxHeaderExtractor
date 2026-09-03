using DocxHeaderExtractor.AgentHarness;
using DocxHeaderExtractor.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (args.Length > 0 && args[0].Equals("--worker", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length != 3)
        throw new ArgumentException("Worker cần --worker <jobId> <inputPath>.");
    try
    {
        await McpExtractionWorker.RunAsync(args[1], args[2]);
    }
    catch (Exception ex)
    {
        try
        {
            var store = new McpJobStore();
            var current = store.Load(args[1]);
            if (current is not null)
                store.Save(current with
                {
                    State = "Failed",
                    CompletedAt = DateTimeOffset.UtcNow,
                    Error = McpExtractionJobQueue.Safe(ex.Message),
                    RecommendedPollSeconds = 0,
                });
        }
        catch { /* process is already terminating; preserve original failure */ }
        throw;
    }
    return;
}

var builder = Host.CreateApplicationBuilder(args);

// stdout chỉ dành cho JSON-RPC MCP. Mọi log phải đi qua stderr, nếu không LM Studio sẽ mất framing.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton(DhxMcpOptions.FromEnvironment());
builder.Services.AddSingleton<DocxHeaderExtractor.Application.Runtime.ITaskRunStore>(_ =>
    new DocxHeaderExtractor.Infrastructure.Runtime.JsonFileTaskRunStore(
        DocxHeaderExtractor.Infrastructure.Runtime.RuntimeStatePaths.RunDirectory));
builder.Services.AddSingleton<DocxHeaderExtractor.Application.Runtime.ITaskTelemetrySink>(_ =>
    new DocxHeaderExtractor.Infrastructure.Runtime.JsonLinesTaskTelemetrySink(
        DocxHeaderExtractor.Infrastructure.Runtime.RuntimeStatePaths.TelemetryPath));
builder.Services.AddSingleton(
    DocxHeaderExtractor.Application.Semantics.SemanticRegistryDefaults.Create());
builder.Services.AddSingleton<McpPathPolicy>();
builder.Services.AddSingleton<McpJobStore>();
builder.Services.AddSingleton<DocumentAgentHarnessFactory>();
builder.Services.AddSingleton<McpExtractionService>();
builder.Services.AddSingleton<McpExtractionJobQueue>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<McpDocumentTools>();

await builder.Build().RunAsync();
