using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.App;
using DocxHeaderExtractor.DocumentProcessing.Harness;
using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.Contracts.Provider;

if (args.Length is < 4 or > 5) throw new ArgumentException("Usage: <source> <artifact-root> <baseline|inline|hierarchy|combined|source-bound|chain-repair|source-bound-chain-repair|membership-v2|membership-v3-review|membership-v4-review|membership-v5-review|membership-boundaries|pairwise-grouping|pairwise-membership-v6|general-structural-v7|general-structural-v8|production-aligned-v12|production-block-evidence-v13|production-unit-selection-v14> <identity-json> [frozen-selection-json]");
var input = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
var mode = args[2];
if (mode is not ("baseline" or "inline" or "hierarchy" or "combined" or "source-bound" or "chain-repair" or "source-bound-chain-repair" or "membership-v2" or "membership-v3-review" or "membership-v4-review" or "membership-v5-review" or "membership-boundaries" or "pairwise-grouping" or "pairwise-membership-v6" or "general-structural-v7" or "general-structural-v8" or "production-aligned-v12" or "production-block-evidence-v13" or "production-unit-selection-v14")) throw new ArgumentException("Unknown mode");
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"))) throw new InvalidOperationException("API_KEY_NOT_CONFIGURED");
Directory.CreateDirectory(output);
if (File.Exists(Path.Combine(output, "run-result.json"))) throw new InvalidOperationException("REFUSE_OVERWRITING_EXISTING_RUN");
var identity = JsonDocument.Parse(File.ReadAllText(args[3])).RootElement.Clone();
var started = DateTimeOffset.UtcNow;
var runId = Guid.NewGuid().ToString("D");
var json = new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
var gate = new object();
var replyCount = 0;
using var replies = new StreamWriter(Path.Combine(output, "model-replies.ndjson"), append: false) { AutoFlush = true };
var options = new PipelineOptions
{
    PdfInlineGrouping = mode is "inline" or "combined",
    WholeDocumentPlacement = mode is "hierarchy" or "combined",
    PdfSourceBoundSelection = mode is "source-bound" or "source-bound-chain-repair" or "membership-v2" or "membership-v3-review" or "membership-v4-review" or "membership-v5-review" or "membership-boundaries" or "pairwise-grouping" or "pairwise-membership-v6" or "general-structural-v7" or "general-structural-v8" or "production-aligned-v12" or "production-block-evidence-v13" or "production-unit-selection-v14",
    RepairParentChains = mode switch
    {
        "chain-repair" or "source-bound-chain-repair" or "membership-v2" or "membership-v3-review" or "membership-v4-review" or "membership-v5-review" or "membership-boundaries" or "pairwise-grouping" or "pairwise-membership-v6" or "production-aligned-v12" or "production-block-evidence-v13" or "production-unit-selection-v14" => true,
        _ => false
    },
    // Each frozen qualification mode owns its repair switch, independent of evolving application defaults.
    PdfRepairParentChains = mode is "general-structural-v7" or "general-structural-v8",
    PdfClarifyMembership = mode is "membership-v2" or "membership-v3-review" or "membership-v4-review" or "membership-v5-review" or "membership-boundaries" or "pairwise-grouping" or "pairwise-membership-v6" or "general-structural-v7" or "general-structural-v8" or "production-aligned-v12" or "production-block-evidence-v13" or "production-unit-selection-v14",
    PdfMembershipInstructionVersion = mode == "production-unit-selection-v14" ? 14 : mode == "production-block-evidence-v13" ? 13 : mode == "production-aligned-v12" ? 12 : mode == "general-structural-v8" ? 8 : mode == "general-structural-v7" ? 7 : mode == "pairwise-membership-v6" ? 6 : mode is "membership-v5-review" or "membership-boundaries" or "pairwise-grouping" ? 5 : mode == "membership-v4-review" ? 4 : mode == "membership-v3-review" ? 3 : 2,
    PdfAdaptiveVisualSelection = mode == "production-unit-selection-v14",
    PdfVisualSelectionMaxRequestBytes = mode == "production-unit-selection-v14" ? 786432 : null,
    PdfBoundaryReview = mode is "membership-v3-review" or "membership-v4-review" or "membership-v5-review",
    PdfSeparateGroupingInstruction = mode is "membership-boundaries" or "pairwise-grouping" or "pairwise-membership-v6" or "general-structural-v7" or "general-structural-v8" or "production-aligned-v12" or "production-block-evidence-v13" or "production-unit-selection-v14",
    PdfPairwiseGrouping = mode is "pairwise-grouping" or "pairwise-membership-v6" or "general-structural-v7" or "general-structural-v8" or "production-aligned-v12" or "production-block-evidence-v13" or "production-unit-selection-v14",
    MaxProviderCostUsd = 1,
    MaxProviderAttempts = 300,
    SourceCacheRoot = Path.Combine(output, "source-cache")
};
using var deadline = mode == "production-unit-selection-v14"
    ? new CancellationTokenSource()
    : new CancellationTokenSource(TimeSpan.FromMinutes(20));
IModelProvider provider = OpenRouterProvider.FromEnvironment();
if (args.Length == 5) provider = new FrozenProvider(provider, args[4]);
var runner = new DocumentRunner(options, provider, reply =>
{
    lock (gate)
    {
        replyCount++;
        replies.WriteLine(JsonSerializer.Serialize(reply));
        Console.WriteLine(JsonSerializer.Serialize(new { stage = "selection-reply", count = replyCount, atUtc = DateTimeOffset.UtcNow }));
    }
});
var clock = Stopwatch.StartNew();
var result = await runner.ExtractAsync(input, deadline.Token);
clock.Stop();
var audit = result.HeadingPipeline?.Audit ?? result.Outline.RouteAudit;
var artifact = new
{
    runId, mode, frozenSelection = args.Length == 5, startedAtUtc = started, completedAtUtc = DateTimeOffset.UtcNow,
    elapsedSeconds = Math.Round(clock.Elapsed.TotalSeconds, 3),
    input = new { path = input, sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(input))) },
    buildIdentity = identity, runResourceBudget = result.ResourceBudget,
    outline = result.Outline, extractionResult = result.Result, pipelineAudit = audit,
    placementExecution = audit?.PlacementExecution, groupingObservation = audit?.Grouping,
    selectionResources = audit?.SelectionResources, rawReplyCount = replyCount
};
File.WriteAllText(Path.Combine(output, "run-result.json"), JsonSerializer.Serialize(artifact, json));
Console.WriteLine(JsonSerializer.Serialize(new { stage = "complete", mode, artifact.elapsedSeconds,
    headings = result.Outline.Headings.Count, result.ResourceBudget }));

sealed class FrozenProvider(IModelProvider provider, string fixture) : IModelProvider
{
    public ModelRoute CreateRoute() => provider.CreateRoute();
    public T? Create<T>(string role) where T : class => role == AgentRoles.PdfSelector ? new FrozenSelection(fixture) as T : null;
}

sealed class FrozenSelection : IHeadingSelector
{
    readonly HeadingSelection selection;
    readonly string sourceHash;
    public FrozenSelection(string fixture)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(fixture));
        sourceHash = doc.RootElement.GetProperty("sourceSha256").GetString()!;
        var s = doc.RootElement.GetProperty("selection");
        var insensitive = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        selection = new HeadingSelection(s.GetProperty("SelectedAliases").Deserialize<HashSet<string>>()!,
            s.GetProperty("UnitsComplete").GetInt32(), s.GetProperty("UnitsTotal").GetInt32(), [],
            s.GetProperty("ModelAnswers").Deserialize<string[]>()!, 0, 0,
            s.GetProperty("Placement").Deserialize<HeadingPlacementContext>(),
            s.GetProperty("Groups").Deserialize<List<List<string>>>()!.Select(g => (IReadOnlyList<string>)g).ToArray(),
            s.GetProperty("HeadingExtents").Deserialize<Dictionary<string, DocxHeaderExtractor.Contracts.Models.TextOffsetSpan>>(insensitive))
        {
            AliasStates = s.GetProperty("AliasStates").Deserialize<Dictionary<string, HeadingAliasState>>(),
            StageSeconds = new Dictionary<string, double> { ["selection"] = 0, ["grouping"] = 0 },
            Grouping = new GroupingObservation(),
            Resources = s.GetProperty("Resources").Deserialize<ResourceStageObservation>()
        };
    }
    public string SelectorId => "qualification-frozen-selection:" + sourceHash;
    public Task<HeadingSelection> SelectAsync(string path, string apiKey, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) != sourceHash) throw new InvalidOperationException("REPLAY_SOURCE_MISMATCH");
        return Task.FromResult(selection);
    }
}
