using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.Eval.ReasoningRetention;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// Small blind live probe for the HDSA parent-relation contract. The mini-tree is selected from
/// source-backed frozen proposals, never from Gold. CandidateParents is only a primary attention
/// list; the authoritative parent universe is also serialized so a valid farther parent remains
/// visible and admissible. Structural Gold is intentionally not opened by this runner.
/// </summary>
public static class HdsaParentRelationLiveRunner
{
    private const string Model = "qwen/qwen3.7-flash";
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private const string SourcePath = "eval/a99-closed-loop/source-fidelity-audit/DOC-0205/converted-docx/025_ND_47-2020_Chia_se_du_lieu_so.docx";
    private const string SeedPredictionPath = "eval/a99-closed-loop/source-fidelity-whole-alias-live/DOC-0205/r2/whole-alias/prediction.v1.json";
    private const string SeedFreezePath = "eval/a99-closed-loop/source-fidelity-whole-alias-live/DOC-0205/r2/whole-alias/freeze.v1.json";
    private const string OutputRoot = "eval/a99-closed-loop/hdsa-parent-relation-live/DOC-0205";
    private const int PrimaryWindow = 3;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string[] MiniTreeSeedAliases =
    ["S0001", "S0005", "S0014", "S0015", "S0016", "S0019", "S0021", "S0033", "S0035", "S0044", "S0052", "S0060"];

    private const string SystemPrompt = """
You are the HDSA parent-relation reasoner. Decide only the immediate parent relation for the
supplied child occurrence. Use document order, numbering, styles, semantic roles, local context,
and the full authoritative parent universe as evidence; evidence is not a deterministic rule.
Return exactly one JSON object with child, decision, and parent. Use SELECT_PARENT only for a
source-backed immediate parent. Use ROOT only when the occurrence is structurally root. Use
UNRESOLVED when evidence is insufficient; never use ROOT merely because the primary candidate
shortlist is insufficient. Do not return level, offsets, text spans, or any ID not in the supplied
authoritative parent universe. A parent may be outside the primary attention shortlist.
""";

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(output);
        var context = LoadContext(repoRoot);
        var selected = SelectMiniTree(context);
        var manifestPath = Path.Combine(output, "manifest.v1.json");
        await WriteJsonAsync(manifestPath, new
        {
            schemaVersion = "a99-hdsa-parent-relation-live-manifest-v1",
            documentId = "DOC-0205",
            model = Model,
            sourceSha256 = context.SourceSha256,
            sourcePath = SourcePath,
            seedPredictionPath = SeedPredictionPath,
            seedFreezePath = SeedFreezePath,
            selectedMiniTree = selected.Select(item => new { item.Alias, item.SourceId, item.Text, item.Role }).ToArray(),
            primaryCandidateWindow = PrimaryWindow,
            candidateParentsAreAttentionOnly = true,
            authoritativeParentUniverse = "all selected source-backed occurrences preceding child",
            structuralGoldReadBeforeFreeze = false,
            goldReadBeforeFreeze = false,
            modelFallback = "NONE",
            semanticPromptChanged = false,
        }, ct);

        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new
            {
                schemaVersion = "a99-hdsa-parent-relation-live-summary-v1",
                status = "BLOCKED",
                reason = "OPENROUTER_API_KEY_MISSING",
                modelCalls = 0,
                providerCalls = 0,
                goldRead = false,
                goldReadBeforeFreeze = false,
            }, ct);
            return 1;
        }

        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        using var lease = await A99OpenRouterLiveProviderLease.AcquireAsync(repoRoot, "hdsa-parent-relation", "DOC-0205", ct);
        var options = new RemoteInferenceOptions
        {
            Endpoint = new Uri(Endpoint),
            Model = Model,
            ApiKey = key,
            ContextSize = 1_000_000,
            MaxOutputTokens = 8_000,
            RequestTimeoutSeconds = 600,
            TransientRequestRetries = 0,
            MaxParallelRequests = 1,
            SendChatTemplateKwargs = false,
            OpenRouterAllowNonZdrPublicBenchmark = true,
        };
        var capability = (await OpenRouterModelCapabilityResolver.ResolveAsync(options, http, ct)).Capability;
        if (capability is null || !string.Equals(capability.ModelId, Model, StringComparison.Ordinal) ||
            !capability.ReasoningSupported || !capability.StructuredOutputSupported)
        {
            await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new
            {
                schemaVersion = "a99-hdsa-parent-relation-live-summary-v1",
                status = "BLOCKED",
                reason = "MODEL_CAPABILITY_MISMATCH",
                modelCalls = 0,
                providerCalls = 0,
                capability,
                goldRead = false,
                goldReadBeforeFreeze = false,
            }, ct);
            return 1;
        }

        using var model = new OpenRouterCeilingReasoningModel(options, capability, http);
        var decisions = new List<HdsaRelationReasoningDecision>();
        var observations = new List<object>();
        var parentOutsidePrimaryHints = 0;
        foreach (var child in selected)
        {
            ct.ThrowIfCancellationRequested();
            var universe = selected.Where(item => item.SourceOrdinal < child.SourceOrdinal)
                .Select(item => new HdsaParentUniverseEntry(item.Alias, item.SourceOrdinal, item.Text, item.Role))
                .ToArray();
            var primary = HdsaParentAttentionCandidates.PrimaryPreceding(selected.Select(item => item.Alias).ToArray(), child.Alias, PrimaryWindow);
            var request = new HdsaRelationReasoningRequest(
                child.Alias,
                primary,
                universe,
                new HdsaRelationEvidence(
                    $"ordinal={child.SourceOrdinal}",
                    child.Numbering,
                    child.Styles,
                    child.Role,
                    child.LocalContext,
                    "candidateParents are attention-only; parent universe is authoritative"));
            var requestJson = JsonSerializer.Serialize(request, JsonOptions);
            var requestHash = Sha256(Encoding.UTF8.GetBytes(requestJson));
            var childDir = Path.Combine(output, child.Alias);
            Directory.CreateDirectory(childDir);
            var predictionPath = Path.Combine(childDir, "prediction.v1.json");
            var freezePath = Path.Combine(childDir, "freeze.v1.json");
            if (File.Exists(predictionPath) || File.Exists(freezePath))
            {
                if (!File.Exists(predictionPath) || !File.Exists(freezePath)) throw new InvalidDataException($"PARTIAL_HDSA_FREEZE:{child.Alias}");
                using var frozen = JsonDocument.Parse(await File.ReadAllTextAsync(freezePath, ct));
                var root = frozen.RootElement;
                if (root.GetProperty("goldReadBeforeFreeze").GetBoolean() ||
                    !string.Equals(root.GetProperty("requestSha256").GetString(), requestHash, StringComparison.Ordinal))
                    throw new InvalidDataException($"HDSA_FREEZE_LINEAGE_MISMATCH:{child.Alias}");
                using var prediction = JsonDocument.Parse(await File.ReadAllTextAsync(predictionPath, ct));
                var decision = JsonSerializer.Deserialize<HdsaRelationReasoningDecision>(prediction.RootElement.GetProperty("decision").GetRawText(), JsonOptions)
                    ?? throw new InvalidDataException($"HDSA_DECISION_MISSING:{child.Alias}");
                var frozenValidation = HdsaRelationReasoningContract.Validate(
                    request, decision, selected.Select(item => item.Alias).ToHashSet(StringComparer.Ordinal));
                if (!frozenValidation.Accepted) throw new InvalidDataException($"HDSA_FROZEN_DECISION_REJECTED:{child.Alias}:{frozenValidation.RejectionReason}");
                if (frozenValidation.ParentWasOutsideAttentionHints) parentOutsidePrimaryHints++;
                decisions.Add(decision);
                observations.Add(new { child = child.Alias, reusedFrozen = true, requestSha256 = requestHash });
                continue;
            }

            var userPrompt = $"TASK=a99-hdsa-parent-relation-v1\n{requestJson}\nReturn exactly {{\"child\":\"...\",\"decision\":\"SELECT_PARENT|ROOT|UNRESOLVED\",\"parent\":\"...\" or null}}.";
            var stopwatch = Stopwatch.StartNew();
            string content;
            RequestPacketTelemetry telemetry;
            try
            {
                (content, telemetry) = await model.CompleteRawStructuredSemanticAsync(
                    "DOC-0205", "HDSA_PARENT_RELATION", $"hdsa-parent:{child.Alias}", requestJson,
                    requestJson.Length, selected.Count, universe.Length + 1, SystemPrompt, userPrompt,
                    HdsaRelationReasoningContract.Schema(), "hdsa_parent_relation_v1", ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or ReasoningCompletionException)
            {
                await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new
                {
                    schemaVersion = "a99-hdsa-parent-relation-live-summary-v1",
                    status = "BLOCKED",
                    documentId = "DOC-0205",
                    model = Model,
                    miniTreeCount = selected.Count,
                    completedChildCount = decisions.Count,
                    completedChildren = decisions.Select(item => item.Child).ToArray(),
                    failedChild = child.Alias,
                    observations,
                    modelCalls = model.ProviderCalls,
                    providerCalls = model.ProviderCalls,
                    providerFailureType = ex.GetType().FullName,
                    providerFailure = ex.Message,
                    structuralGold = "NOT_EVALUATED_NO_AUTHORITY_OPENED",
                    goldRead = false,
                    goldReadBeforeFreeze = false,
                }, ct);
                Console.WriteLine("HDSA_PARENT_RELATION_STATUS=BLOCKED");
                Console.WriteLine($"FAILED_CHILD={child.Alias}");
                Console.WriteLine($"MODEL_CALLS={model.ProviderCalls}");
                Console.WriteLine($"PROVIDER_CALLS={model.ProviderCalls}");
                Console.WriteLine("STRUCTURAL_GOLD_READ=0");
                return 1;
            }
            var decisionResult = HdsaRelationReasoningContract.Parse(content);
            var validation = HdsaRelationReasoningContract.Validate(
                request, decisionResult, selected.Select(item => item.Alias).ToHashSet(StringComparer.Ordinal));
            if (!validation.Accepted) throw new InvalidDataException($"HDSA_DECISION_REJECTED:{child.Alias}:{validation.RejectionReason}");
            if (validation.ParentWasOutsideAttentionHints) parentOutsidePrimaryHints++;
            decisions.Add(decisionResult);
            var responseHash = Sha256(Encoding.UTF8.GetBytes(content));
            await WriteJsonAsync(predictionPath, new
            {
                schemaVersion = "a99-hdsa-parent-relation-live-prediction-v1",
                documentId = "DOC-0205",
                child = child.Alias,
                requestSha256 = requestHash,
                responseSha256 = responseHash,
                decision = decisionResult,
                validation,
                modelCalls = 1,
                providerCalls = 1,
                goldReadBeforeFreeze = false,
            }, ct);
            await WriteJsonAsync(freezePath, new
            {
                schemaVersion = "a99-hdsa-parent-relation-live-freeze-v1",
                documentId = "DOC-0205",
                child = child.Alias,
                requestSha256 = requestHash,
                predictionSha256 = Sha256(predictionPath),
                responseSha256 = responseHash,
                provider = telemetry.ProviderRoute,
                finishReason = telemetry.FinishReason,
                inputTokens = telemetry.ReportedInputTokens,
                reasoningTokens = telemetry.ReportedReasoningTokens,
                outputTokens = telemetry.ReportedOutputTokens,
                elapsedMs = stopwatch.ElapsedMilliseconds,
                modelCalls = 1,
                providerCalls = 1,
                goldReadBeforeFreeze = false,
            }, ct);
            observations.Add(new { child = child.Alias, reusedFrozen = false, requestSha256 = requestHash, validation });
        }

        var relationProposals = decisions
            .Where(item => item.Decision == HdsaParentDecision.SelectParent && item.Parent is not null)
            .Select(item => new HdsaRelationProposal(item.Parent!, item.Child, HdsaRelationType.ParentOf))
            .ToArray();
        var ids = selected.Select(item => item.Alias).ToArray();
        var normalized = HdsaRelationNormalizer.Normalize(ids, relationProposals);
        var graph = HdsaGraphValidator.Validate(ids, normalized);
        var tree = HdsaTreeConstructor.Build(ids, graph);
        await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-hdsa-parent-relation-live-summary-v1",
            status = "COMPLETE",
            documentId = "DOC-0205",
            model = Model,
            miniTreeCount = selected.Count,
            decisions,
            relationProposals,
            normalizedRelations = normalized.Relations,
            graphValidation = graph,
            tree,
            unresolvedCount = decisions.Count(item => item.Decision == HdsaParentDecision.Unresolved),
            rootDecisionCount = decisions.Count(item => item.Decision == HdsaParentDecision.Root),
            parentOutsidePrimaryHints,
            modelCalls = decisions.Count,
            providerCalls = decisions.Count,
            freshModelCallsThisInvocation = model.ProviderCalls,
            freshProviderCallsThisInvocation = model.ProviderCalls,
            telemetry = model.Telemetry,
            structuralGold = "NOT_EVALUATED_NO_AUTHORITY_OPENED",
            goldRead = false,
            goldReadBeforeFreeze = false,
        }, ct);
        Console.WriteLine($"HDSA_PARENT_RELATION_STATUS=COMPLETE");
        Console.WriteLine($"MODEL_CALLS={decisions.Count}");
        Console.WriteLine($"PROVIDER_CALLS={decisions.Count}");
        Console.WriteLine("STRUCTURAL_GOLD_READ=0");
        Console.WriteLine($"HDSA_PARENT_RELATION_SUMMARY={Path.Combine(OutputRoot, "summary.v1.json")}");
        return 0;
    }

    private static Context LoadContext(string repoRoot)
    {
        var sourcePath = Path.Combine(repoRoot, SourcePath.Replace('/', Path.DirectorySeparatorChar));
        var source = new OpenXmlDocumentSource().Read(sourcePath) with { DocumentId = "DOC-0205" };
        var aliases = source.Paragraphs.Where(item => !string.IsNullOrWhiteSpace(item.Text))
            .Select((item, index) => new AliasContext(
                $"S{index + 1:0000}", item.SourceId, item.SourceOrdinal, item.Text,
                item.Numbering.NumberLabel,
                $"{item.Style.StyleName ?? item.Style.StyleId};font={item.Style.FontSizePt};bold={item.Style.Bold}",
                item.Style.StyleName ?? item.Style.StyleId,
                $"prev={Math.Max(0, index - 1)};next={index + 1}"))
            .ToArray();
        AssertFileHash(Path.Combine(repoRoot, SeedFreezePath.Replace('/', Path.DirectorySeparatorChar)), sourcePath, aliases);
        return new(Sha256(File.ReadAllBytes(sourcePath)), aliases);
    }

    private static IReadOnlyList<AliasContext> SelectMiniTree(Context context)
    {
        var byAlias = context.Aliases.ToDictionary(item => item.Alias, StringComparer.Ordinal);
        return MiniTreeSeedAliases.Select(alias => byAlias.TryGetValue(alias, out var item)
                ? item
                : throw new InvalidDataException($"MINI_TREE_ALIAS_MISSING:{alias}"))
            .OrderBy(item => item.SourceOrdinal)
            .ToArray();
    }

    private static void AssertFileHash(string freezePath, string sourcePath, IReadOnlyList<AliasContext> aliases)
    {
        if (!File.Exists(freezePath)) throw new FileNotFoundException("HDSA_SEED_FREEZE_MISSING", freezePath);
        using var freeze = JsonDocument.Parse(File.ReadAllText(freezePath));
        var root = freeze.RootElement;
        if (root.GetProperty("goldReadBeforeFreeze").GetBoolean()) throw new InvalidDataException("HDSA_SEED_GOLD_FIREWALL_FAILED");
        if (!string.Equals(root.GetProperty("sourceSha256").GetString(), Sha256(File.ReadAllBytes(sourcePath)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("HDSA_SOURCE_HASH_MISMATCH");
        var predictionPath = Path.Combine(Path.GetDirectoryName(freezePath)!, "prediction.v1.json");
        if (!string.Equals(root.GetProperty("predictionSha256").GetString(), Sha256(predictionPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("HDSA_SEED_PREDICTION_HASH_MISMATCH");
        if (aliases.Count < MiniTreeSeedAliases.Length) throw new InvalidDataException("HDSA_SOURCE_ALIAS_CATALOG_TOO_SMALL");
    }

    private static async Task WriteJsonAsync(string path, object value, CancellationToken ct) =>
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), ct);

    private static string Sha256(string path) => Sha256(File.ReadAllBytes(path));
    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record Context(string SourceSha256, IReadOnlyList<AliasContext> Aliases);
    private sealed record AliasContext(
        string Alias, string SourceId, int SourceOrdinal, string Text, string? Numbering,
        string? Styles, string? Role, string LocalContext);
}
