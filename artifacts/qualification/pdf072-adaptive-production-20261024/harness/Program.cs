using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.App;
using DocxHeaderExtractor.DocumentProcessing.Harness;

if (args.Length != 3) throw new ArgumentException("Usage: <pdf> <artifact-root> <build-identity.json>");
var pdf = Path.GetFullPath(args[0]);
var artifactRoot = Path.GetFullPath(args[1]);
var identityPath = Path.GetFullPath(args[2]);
if (!File.Exists(pdf)) throw new FileNotFoundException("PDF missing", pdf);
if (!Environment.GetEnvironmentVariables().Contains("OPENROUTER_API_KEY")) throw new InvalidOperationException("OPENROUTER_API_KEY_NOT_CONFIGURED");
var runId = Guid.NewGuid().ToString("D");
var started = DateTimeOffset.UtcNow;
var replies = new ConcurrentQueue<ModelReplyView>();
var options = new PipelineOptions
{
    SourceCacheRoot = Path.Combine(artifactRoot, "source-cache"),
    MaxProviderCostUsd = 5.0,
    MaxProviderAttempts = 2000
};
var provider = OpenRouterProvider.FromEnvironment();
var runner = new DocumentRunner(options, provider, replies.Enqueue);
var clock = Stopwatch.StartNew();
var result = await runner.ExtractAsync(pdf);
clock.Stop();
var completed = DateTimeOffset.UtcNow;
var audit = result.HeadingPipeline?.Audit ?? result.Outline.RouteAudit;
var jsonOptions = new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
var identity = JsonDocument.Parse(await File.ReadAllTextAsync(identityPath)).RootElement.Clone();
var artifact = new
{
    runId,
    startedAtUtc = started,
    completedAtUtc = completed,
    elapsedSeconds = Math.Round(clock.Elapsed.TotalSeconds, 3),
    input = new { path = pdf, sha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(pdf))).ToLowerInvariant() },
    buildIdentity = identity,
    configuredProfile = new { id = "openrouter-alibaba-qwen37-20261024-v1", maxRequestBytes = 262144, contextTokens = 1000000, maxPromptTokens = 983616, maxCompletionTokens = 65536, continuationReserveBytes = 65536, continuationReserveTokens = 65536, maxContinuationFraction = 0.25 },
    runResourceBudget = result.ResourceBudget,
    outline = result.Outline,
    pipelineAudit = audit,
    placementExecution = audit?.PlacementExecution,
    groupingObservation = audit?.Grouping,
    selectionResources = audit?.SelectionResources,
    rawReplyCount = replies.Count
};
await File.WriteAllTextAsync(Path.Combine(artifactRoot, "run-result.json"), JsonSerializer.Serialize(artifact, jsonOptions));
await using (var stream = new FileStream(Path.Combine(artifactRoot, "model-replies.ndjson"), FileMode.Create, FileAccess.Write, FileShare.Read))
await using (var writer = new StreamWriter(stream))
{
    foreach (var reply in replies)
        await writer.WriteLineAsync(JsonSerializer.Serialize(reply, jsonOptions));
}
Console.WriteLine(JsonSerializer.Serialize(new { runId, startedAtUtc = started, completedAtUtc = completed, elapsedSeconds = Math.Round(clock.Elapsed.TotalSeconds, 3), replyCount = replies.Count, headingCount = result.Outline.Headings.Count, route = result.Outline.DeterministicRoute, runResourceBudget = result.ResourceBudget, artifact = Path.Combine(artifactRoot, "run-result.json") }, jsonOptions));
