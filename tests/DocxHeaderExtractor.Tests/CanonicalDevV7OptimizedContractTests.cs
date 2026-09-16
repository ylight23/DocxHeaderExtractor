using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.Eval.ReasoningRetention;

namespace DocxHeaderExtractor.Tests;

public sealed class CanonicalDevV7OptimizedContractTests
{
    [Fact]
    public void V7_cli_and_runner_are_dedicated_and_schedule_only_phase_one_documents()
    {
        var root = FindRepositoryRoot();
        var program = File.ReadAllText(Path.Combine(root, "src", "DocxHeaderExtractor.Cli", "Program.cs"));
        var options = File.ReadAllText(Path.Combine(root, "src", "DocxHeaderExtractor.Cli", "CommandLineOptions.cs"));
        var runner = File.ReadAllText(Path.Combine(root, "src", "DocxHeaderExtractor.Eval", "ReasoningRetention", "CanonicalDevV7OptimizedRunner.cs"));

        Assert.Contains("a99-canonical-dev-v1-exec-v7-optimized", program);
        Assert.Contains("CanonicalDevV7OptimizedRunner.RunAsync", program);
        Assert.Contains("a99-canonical-dev-v1-exec-v7-optimized", options);
        Assert.Contains("CANONICAL_DEV_V1_EXEC_V7_OPTIMIZED", runner);
        Assert.Contains("DOC-0001", runner);
        Assert.Contains("DOC-0116", runner);
        Assert.DoesNotContain("DOC-0122", runner);
        Assert.Contains("canonical-dev-v1-exec-v7-optimized", runner);
    }

    [Fact]
    public void Worker_artifacts_carry_batch_telemetry_and_promoted_totals_have_a_round_trip_contract()
    {
        var root = FindRepositoryRoot();
        var worker = File.ReadAllText(Path.Combine(root, "src", "DocxHeaderExtractor.Eval", "ReasoningRetention", "CanonicalDevV1BaselineRunner.cs"));
        Assert.Contains("batchTelemetry = audit?.BatchTelemetry", worker);
        Assert.Equal(2, worker.Split("batchTelemetry = audit?.BatchTelemetry", StringSplitOptions.None).Length - 1);

        var telemetry = new PdfPipelineBatchTelemetry(
            SourceParagraphCount: 101,
            RoleInputBlockCount: 32,
            RoleBatchCount: 2,
            RoleProviderCalls: 3,
            RoleInputTokensTotal: 5001,
            RoleLargestBatchBlocks: 31,
            RoleLargestBatchTokens: 4999,
            HeadingLikeAfterRole: 17,
            SpanBatchCount: 4,
            SpanProviderCalls: 5,
            SpanInputTokensTotal: 7002,
            HierarchyInputCount: 17,
            HierarchyProviderCalls: 6,
            TotalProviderCalls: 14,
            TotalResponses: 13,
            ElapsedMs: 123456);
        var artifact = new { batchTelemetry = telemetry, providerCalls = 14, responseCount = 13 };
        var json = JsonSerializer.Serialize(artifact);
        using var reloaded = JsonDocument.Parse(json);
        var rootElement = reloaded.RootElement;
        Assert.Equal(14, rootElement.GetProperty("batchTelemetry").GetProperty("totalProviderCalls").GetInt32());
        Assert.Equal(13, rootElement.GetProperty("batchTelemetry").GetProperty("totalResponses").GetInt32());
        Assert.Equal(14, rootElement.GetProperty("providerCalls").GetInt32());
        Assert.Equal(13, rootElement.GetProperty("responseCount").GetInt32());
        Assert.Equal(32, rootElement.GetProperty("batchTelemetry").GetProperty("roleInputBlockCount").GetInt32());
        Assert.Equal(123456, rootElement.GetProperty("batchTelemetry").GetProperty("elapsedMs").GetInt64());
    }

    [Fact]
    public void Performance_audit_uses_document_local_calls_not_campaign_cumulative_calls()
    {
        var telemetryJson = "{\"roleProviderCalls\":20,\"spanProviderCalls\":30,\"hierarchyProviderCalls\":40}";
        using var telemetryDocument = JsonDocument.Parse(telemetryJson);

        // DOC-0001=7 is deliberately outside this document-local calculation.
        var metrics = CanonicalDevV7OptimizedRunner.CalculatePerformanceAuditMetrics(
            documentProviderCalls: 100,
            documentResponseCount: 100,
            telemetryDocument.RootElement);

        Assert.Equal(100, metrics.DocumentProviderCalls);
        Assert.Equal(100, metrics.DocumentResponseCount);
        Assert.Equal(90, metrics.BaseProviderCalls);
        Assert.Equal(10, metrics.AdditionalProviderCallsObserved);
        Assert.NotEqual(107, metrics.DocumentProviderCalls);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DocxHeaderExtractor.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
