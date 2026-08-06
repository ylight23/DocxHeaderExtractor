using DocxHeaderExtractor.AgentHarness;

namespace DocxHeaderExtractor.Mcp;

public static class McpExtractionWorker
{
    public static async Task RunAsync(string jobId, string inputPath)
    {
        var store = new McpJobStore();
        var current = store.Load(jobId);
        if (current is null) return;
        store.Save(current with { State = "Running", StartedAt = DateTimeOffset.UtcNow, RecommendedPollSeconds = 15 });

        try
        {
            var options = DhxMcpOptions.FromEnvironment();
            var paths = new McpPathPolicy(options);
            var factory = new DocumentAgentHarnessFactory();
            using var extraction = new McpExtractionService(options, paths, factory);
            var result = await extraction.ExtractAsync(inputPath);
            store.Save(current with
            {
                State = "Completed", StartedAt = current.StartedAt ?? DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow, Result = result, RecommendedPollSeconds = 0,
            });
        }
        catch (Exception ex)
        {
            store.Save(current with
            {
                State = ex is OperationCanceledException ? "Cancelled" : "Failed",
                StartedAt = current.StartedAt ?? DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow,
                Error = McpExtractionJobQueue.Safe(ex.Message), RecommendedPollSeconds = 0,
            });
        }
    }
}
