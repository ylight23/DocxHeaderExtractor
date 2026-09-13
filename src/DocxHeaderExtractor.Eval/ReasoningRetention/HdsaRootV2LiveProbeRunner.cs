using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>One-call live probe for the ROOT-v2 parent decision contract. It intentionally runs
/// only S0005 and freezes the decision before opening Structural Gold.</summary>
public static class HdsaRootV2LiveProbeRunner
{
    private const string HistoricalCatalogFingerprint = "5948fb130cdf730660991a7a83ceb5aab461374a1eb86ec5f40f112b7f64480e";
    private static readonly string[] MiniTreeSeedAliases =
    [
        "S0001", "S0005", "S0014", "S0015", "S0016", "S0019",
        "S0021", "S0033", "S0035", "S0044", "S0052", "S0060"
    ];

    private const string Model = "qwen/qwen3.7-flash";
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private const string SourcePath = "eval/a99-closed-loop/source-fidelity-audit/DOC-0205/converted-docx/025_ND_47-2020_Chia_se_du_lieu_so.docx";
    private const string GoldPath = "eval/a99-closed-loop/hdsa-parent-relation-live/DOC-0205/structural-gold.v1.json";
    private const string OutputRoot = "eval/a99-closed-loop/hdsa-semantic-node-root-v2-live-probe/DOC-0205/S0005";
    private const string TargetAlias = "S0005";
    private const string TargetNode = "SN-aca084dc03ba6e31";
    private const int PrimaryWindow = 8;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private const string SystemPrompt = """
You are the A99 semantic-node parent reasoner. Decide only the immediate semantic parent relation
for the supplied child semantic node. Use source order, canonical source-backed text, styles,
numbering, and context as evidence; evidence is not a deterministic rule. The request's
decisionOptions explicitly renders ROOT, candidate parent nodes, and UNRESOLVED as peer outcomes.
ROOT means the child has no semantic parent in this structural scope; it is not a parent-node ID.
The preceding/nearby headings are attention candidates, not automatically semantic parents.
Return exactly one JSON object with catalogFingerprint, childSemanticNodeId, decision, and
parentSemanticNodeId. Use SELECT_PARENT only for an immediate parent from candidateParentSemanticNodeIds.
Use UNRESOLVED when evidence is insufficient; never use ROOT merely because a candidate is hard to choose.
Do not return level, depth, offsets, text spans, legacy hierarchy fields, Gold IDs, or any ID not in the supplied catalog.
""";

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(output);
        var context = LoadContext(repoRoot);
        var input = new HdsaSemanticNodeResolutionInput(
            context.SourceSha256,
            context.PreprocessingSnapshotHash,
            context.Occurrences,
            false);
        var identity = HdsaSemanticNodeResolverV3.Resolve(input, []);
        var catalog = HdsaSemanticNodeCatalogBuilder.Build(input, identity);
        if (!string.Equals(catalog.CatalogFingerprint, HistoricalCatalogFingerprint, StringComparison.Ordinal))
            throw new InvalidDataException($"ROOT_V2_FROZEN_CATALOG_MISMATCH:{catalog.CatalogFingerprint}");
        var target = catalog.Entries.Single(entry => entry.MemberOccurrenceIds.Contains(TargetAlias, StringComparer.Ordinal));
        if (!string.Equals(target.SemanticNodeId, TargetNode, StringComparison.Ordinal))
            throw new InvalidDataException($"ROOT_V2_TARGET_NODE_MISMATCH:{target.SemanticNodeId}");

        var request = HdsaSemanticNodeParentReasoningContract.CreateRequest(catalog, target.SemanticNodeId, PrimaryWindow);
        var requestJson = JsonSerializer.Serialize(request, JsonOptions);
        var requestHash = Sha256Text(requestJson);
        await WriteJsonAsync(Path.Combine(output, "manifest.v2.json"), new
        {
            schemaVersion = "a99-hdsa-root-v2-live-probe-manifest-v1",
            documentId = "DOC-0205",
            targetAlias = TargetAlias,
            targetSemanticNodeId = target.SemanticNodeId,
            startCheckpoint = "6715096",
            model = Model,
            endpoint = Endpoint,
            sourceSha256 = context.SourceSha256,
            catalogFingerprint = catalog.CatalogFingerprint,
            historicalFrozenCatalogFingerprint = HistoricalCatalogFingerprint,
            catalogMatchesFrozenHistorical = true,
            probeValidity = "AUTHORITATIVE",
            resolverVersion = catalog.ResolverVersion,
            candidateSemanticNodeIds = request.CandidateParentSemanticNodeIds,
            decisionOptions = request.DecisionOptions,
            requestSha256 = requestHash,
            goldReadBeforeFreeze = false,
            goldDerivedInput = false,
            modelFallback = "NONE",
            probeCallsExpected = 1,
            contractDelta = "ROOT_FIRST_CLASS_DECISION_AFFORDANCE_ONLY",
        }, ct);
        await WriteJsonAsync(Path.Combine(output, "request.v2.json"), new
        {
            schemaVersion = "a99-hdsa-semantic-node-parent-request-v2",
            request,
            requestSha256 = requestHash,
            goldReadBeforeFreeze = false,
            goldDerivedInput = false,
        }, ct);

        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new
            {
                schemaVersion = "a99-hdsa-root-v2-live-probe-summary-v1",
                status = "BLOCKED",
                reason = "OPENROUTER_API_KEY_MISSING",
                modelCalls = 0,
                providerCalls = 0,
                goldReadBeforeFreeze = false,
            }, ct);
            Console.WriteLine("HDSA_ROOT_V2_LIVE_PROBE_STATUS=BLOCKED");
            Console.WriteLine("REASON=OPENROUTER_API_KEY_MISSING");
            return 1;
        }

        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        using var lease = await A99OpenRouterLiveProviderLease.AcquireAsync(repoRoot, "hdsa-root-v2-live-probe", "DOC-0205-S0005", ct);
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
            throw new InvalidDataException("ROOT_V2_MODEL_CAPABILITY_MISMATCH");

        using var model = new OpenRouterCeilingReasoningModel(options, capability, http);
        var userPrompt = $"TASK=A99_ROOT_V2_PARENT_PROBE\n{requestJson}\nReturn exactly the requested JSON object.";
        var stopwatch = Stopwatch.StartNew();
        string content;
        RequestPacketTelemetry telemetry;
        try
        {
            (content, telemetry) = await model.CompleteRawStructuredSemanticAsync(
                "DOC-0205", "HDSA_ROOT_V2_PARENT", "hdsa-root-v2-parent:S0005",
                requestJson, requestJson.Length, catalog.Entries.Count, request.AuthoritativeParentUniverse.Count + 1,
                SystemPrompt, userPrompt, HdsaSemanticNodeParentReasoningContract.Schema(),
                "hdsa_root_v2_parent_v1", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or ReasoningCompletionException)
        {
            await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new
            {
                schemaVersion = "a99-hdsa-root-v2-live-probe-summary-v1",
                status = "BLOCKED",
                reason = "PROVIDER_FAILURE:" + ex.GetType().Name,
                model = Model,
                requestSha256 = requestHash,
                modelCalls = 1,
                providerCalls = model.ProviderCalls,
                goldReadBeforeFreeze = false,
            }, ct);
            Console.WriteLine("HDSA_ROOT_V2_LIVE_PROBE_STATUS=BLOCKED");
            Console.WriteLine($"REASON=PROVIDER_FAILURE:{ex.GetType().Name}");
            Console.WriteLine("MODEL_CALLS=1");
            Console.WriteLine($"PROVIDER_CALLS={model.ProviderCalls}");
            return 1;
        }

        var decision = HdsaSemanticNodeParentReasoningContract.Parse(content);
        var validation = HdsaSemanticNodeParentReasoningContract.Validate(request, decision, catalog);
        if (!validation.Accepted)
            throw new InvalidDataException("ROOT_V2_DECISION_REJECTED:" + validation.RejectionReason);
        var responseHash = Sha256Text(content);
        stopwatch.Stop();
        var predictionPath = Path.Combine(output, "prediction.v1.json");
        await WriteJsonAsync(predictionPath, new
        {
            schemaVersion = "a99-hdsa-root-v2-live-probe-prediction-v1",
            documentId = "DOC-0205",
            targetAlias = TargetAlias,
            childSemanticNodeId = target.SemanticNodeId,
            requestSha256 = requestHash,
            responseSha256 = responseHash,
            rawResponse = content,
            decision,
            validation,
            goldReadBeforeFreeze = false,
            goldDerivedInput = false,
        }, ct);
        var freezePath = Path.Combine(output, "freeze.v1.json");
        await WriteJsonAsync(freezePath, new
        {
            schemaVersion = "a99-hdsa-root-v2-live-probe-freeze-v1",
            documentId = "DOC-0205",
            targetAlias = TargetAlias,
            childSemanticNodeId = target.SemanticNodeId,
            catalogFingerprint = catalog.CatalogFingerprint,
            probeValidity = "AUTHORITATIVE",
            authoritativeBenchmarkResult = true,
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
            providerCalls = Math.Max(1, model.ProviderCalls),
            goldReadBeforeFreeze = false,
            goldDerivedInput = false,
            frozenBeforeGold = true,
        }, ct);

        using var gold = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(repoRoot, GoldPath.Replace('/', Path.DirectorySeparatorChar)), ct));
        var goldOccurrence = gold.RootElement.GetProperty("occurrences").EnumerateArray()
            .Single(item => item.GetProperty("sourceAlias").GetString() == TargetAlias);
        var goldParentKind = goldOccurrence.GetProperty("parentSemanticNodeId").ValueKind == JsonValueKind.Null ? "ROOT" : "NON_ROOT";
        var summary = new
        {
            schemaVersion = "a99-hdsa-root-v2-live-probe-summary-v1",
            status = "COMPLETE",
            documentId = "DOC-0205",
            targetAlias = TargetAlias,
            targetSemanticNodeId = target.SemanticNodeId,
            model = Model,
            catalogFingerprint = catalog.CatalogFingerprint,
            historicalFrozenCatalogFingerprint = HistoricalCatalogFingerprint,
            catalogMatchesFrozenHistorical = true,
            probeValidity = "AUTHORITATIVE",
            requestSha256 = requestHash,
            responseSha256 = responseHash,
            frozenDecision = new { decision = DecisionName(decision.Decision), parentSemanticNodeId = decision.ParentSemanticNodeId },
            gold = new { goldOpenedAfterFreeze = true, goldParentKind },
            classification = decision.Decision == HdsaParentDecision.Root ? "ROOT_CONTRACT_V2_FIXED_OBSERVED_FAILURE" :
                decision.Decision == HdsaParentDecision.SelectParent ? "FRAMING_IMPROVEMENT_INSUFFICIENT_MODEL_ROOT_REASONING_FAILURE" :
                "CONTRACT_CHANGED_BEHAVIOR_BUT_DID_NOT_RECOVER_ROOT",
            modelCalls = 1,
            providerCalls = Math.Max(1, model.ProviderCalls),
            goldReadBeforeFreeze = false,
            goldDerivedInput = false,
            authoritativeBenchmarkResult = true,
        };
        await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), summary, ct);
        Console.WriteLine("HDSA_ROOT_V2_LIVE_PROBE_STATUS=COMPLETE");
        Console.WriteLine("MODEL_CALLS=1");
        Console.WriteLine($"PROVIDER_CALLS={Math.Max(1, model.ProviderCalls)}");
        Console.WriteLine($"DECISION={DecisionName(decision.Decision)}");
        Console.WriteLine($"CLASSIFICATION={summary.classification}");
        Console.WriteLine("GOLD_READ_BEFORE_FREEZE=0");
        return 0;
    }

    private static Context LoadContext(string repoRoot)
    {
        var sourcePath = Path.Combine(repoRoot, SourcePath.Replace('/', Path.DirectorySeparatorChar));
        var source = new OpenXmlDocumentSource().Read(sourcePath);
        var occurrences = source.Paragraphs.Where(item => !string.IsNullOrWhiteSpace(item.Text))
            .Select((item, index) => new HdsaSemanticNodeSourceOccurrence(
                $"S{index + 1:0000}",
                item.SourceOrdinal,
                item.Text,
                $"{item.Style.StyleName ?? item.Style.StyleId};font={item.Style.FontSizePt};bold={item.Style.Bold}",
                $"prev={Math.Max(0, index - 1)};next={index + 1}"))
            .Where(item => MiniTreeSeedAliases.Contains(item.OccurrenceId, StringComparer.Ordinal))
            .ToArray();
        var snapshot = JsonSerializer.Serialize(occurrences, JsonOptions);
        return new(Sha256(File.ReadAllBytes(sourcePath)), Sha256Text(snapshot), occurrences);
    }

    private static async Task WriteJsonAsync(string path, object value, CancellationToken ct) =>
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), ct);

    private static string DecisionName(HdsaParentDecision decision) => decision switch
    {
        HdsaParentDecision.SelectParent => "SELECT_PARENT",
        HdsaParentDecision.Root => "ROOT",
        HdsaParentDecision.Unresolved => "UNRESOLVED",
        _ => decision.ToString().ToUpperInvariant()
    };

    private static string Sha256Text(string value) => Sha256(Encoding.UTF8.GetBytes(value));
    private static string Sha256(string path) => Sha256(File.ReadAllBytes(path));
    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record Context(string SourceSha256, string PreprocessingSnapshotHash, IReadOnlyList<HdsaSemanticNodeSourceOccurrence> Occurrences);
}
