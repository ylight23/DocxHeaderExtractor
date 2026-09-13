using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// One-call shadow probe: ask the model to infer the complete outline from one frozen semantic
/// catalog. It validates the model proposal directly and never runs the global score decoder.
/// </summary>
public static class HdsaGlobalOutlineLlmLiveProbeRunner
{
    private const string Model = "qwen/qwen3.7-flash";
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private const string DocumentId = "DOC-0205";
    private const string CatalogPath = "eval/a99-closed-loop/hdsa-semantic-node-production-live/DOC-0205/semantic-catalog.v1.json";
    private const string OutputRoot = "eval/a99-closed-loop/hdsa-global-outline-llm-live/DOC-0205";
    private const string ExpectedCatalogFingerprint = "5948fb130cdf730660991a7a83ceb5aab461374a1eb86ec5f40f112b7f64480e";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private const string SystemPrompt = """
You are the A99 global semantic-outline reasoner. The request contains every semantic-node
candidate from one document in deterministic document order. Infer the logical outline globally.
Use source-backed text, order, local context, and parser-owned evidence as evidence, not as rigid
rules. An earlier or visually prominent item is not automatically a semantic parent. Mastheads,
banners, report titles, document labels, and nearby headings are parents only when they are true
semantic ancestors. Consider relationships among all nodes before deciding each parent.

Return exactly one JSON object with one result for every supplied node, in supplied order. For each
node return nodeId, parent, proposedLevel, confidence, and reason. parent must be ROOT,
UNRESOLVED, or an ID from this request. proposedLevel is diagnostic evidence only and is never
authoritative; the harness derives final levels from the validated parent tree. Do not merge nodes,
invent IDs, return offsets, return Gold, return legacy hierarchy fields, or omit a node.
""";

    /// <summary>
    /// Re-evaluates the already frozen one-call probe without contacting a provider. This is
    /// intentionally a separate command path so improving evaluation cannot accidentally rerun
    /// the live request.
    /// </summary>
    public static async Task<int> RunFrozenEvaluationAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        var catalog = ReadCatalog(Path.Combine(repoRoot, CatalogPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!string.Equals(catalog.CatalogFingerprint, ExpectedCatalogFingerprint, StringComparison.Ordinal))
            throw new InvalidDataException("GLOBAL_OUTLINE_CATALOG_RECONSTRUCTION_MISMATCH");

        var requestPath = Path.Combine(output, "request.v1.json");
        var responsePath = Path.Combine(output, "response.v1.json");
        var predictionPath = Path.Combine(output, "prediction.v1.json");
        var freezePath = Path.Combine(output, "freeze.v1.json");
        Require(requestPath);
        Require(responsePath);
        Require(predictionPath);
        Require(freezePath);

        using var requestDocument = JsonDocument.Parse(await File.ReadAllTextAsync(requestPath, ct));
        var requestRoot = requestDocument.RootElement;
        if (requestRoot.GetProperty("goldReadBeforeFreeze").GetBoolean() || requestRoot.GetProperty("goldDerivedInput").GetBoolean())
            throw new InvalidDataException("GLOBAL_OUTLINE_FROZEN_REQUEST_GOLD_CONTAMINATION");
        var requestHash = requestRoot.GetProperty("requestSha256").GetString()!;
        var request = requestRoot.GetProperty("request").Deserialize<HdsaGlobalOutlineHierarchyRequest>(JsonOptions)
            ?? throw new InvalidDataException("GLOBAL_OUTLINE_REQUEST_PARSE_FAILED");
        if (!string.Equals(requestHash, Sha256Text(JsonSerializer.Serialize(request, JsonOptions)), StringComparison.Ordinal))
            throw new InvalidDataException("GLOBAL_OUTLINE_REQUEST_HASH_MISMATCH");
        if (!string.Equals(request.CatalogFingerprint, catalog.CatalogFingerprint, StringComparison.Ordinal) ||
            request.Nodes.Count != catalog.Entries.Count)
            throw new InvalidDataException("GLOBAL_OUTLINE_FROZEN_REQUEST_CATALOG_MISMATCH");

        using var responseDocument = JsonDocument.Parse(await File.ReadAllTextAsync(responsePath, ct));
        var responseRoot = responseDocument.RootElement;
        var rawResponse = responseRoot.GetProperty("rawResponse").GetString()!;
        var responseHash = responseRoot.GetProperty("responseSha256").GetString()!;
        if (!string.Equals(responseHash, Sha256Text(rawResponse), StringComparison.Ordinal))
            throw new InvalidDataException("GLOBAL_OUTLINE_RESPONSE_HASH_MISMATCH");
        if (!string.Equals(responseRoot.GetProperty("requestSha256").GetString(), requestHash, StringComparison.Ordinal))
            throw new InvalidDataException("GLOBAL_OUTLINE_RESPONSE_REQUEST_MISMATCH");

        using var predictionDocument = JsonDocument.Parse(await File.ReadAllTextAsync(predictionPath, ct));
        using var freezeDocument = JsonDocument.Parse(await File.ReadAllTextAsync(freezePath, ct));
        var freezeRoot = freezeDocument.RootElement;
        if (freezeRoot.GetProperty("goldReadBeforeFreeze").GetBoolean() || !freezeRoot.GetProperty("frozenBeforeGold").GetBoolean())
            throw new InvalidDataException("GLOBAL_OUTLINE_FREEZE_ORDER_INVALID");
        if (!string.Equals(freezeRoot.GetProperty("predictionSha256").GetString(), Sha256File(predictionPath), StringComparison.Ordinal) ||
            !string.Equals(freezeRoot.GetProperty("requestSha256").GetString(), requestHash, StringComparison.Ordinal) ||
            !string.Equals(freezeRoot.GetProperty("responseSha256").GetString(), responseHash, StringComparison.Ordinal))
            throw new InvalidDataException("GLOBAL_OUTLINE_FREEZE_HASH_MISMATCH");

        var proposal = HdsaGlobalOutlineHierarchyContract.Parse(rawResponse);
        var validation = HdsaGlobalOutlineHierarchyContract.Validate(request, proposal);
        var evaluation = EvaluateAfterFreeze(repoRoot, catalog, request, proposal, validation);
        await WriteJsonAsync(Path.Combine(output, "evaluation.v1.json"), evaluation, ct);

        var localBaseline = new { parentF1 = .8, levelExact = "4/11", s0005 = "SELECT_PARENT(SN-d53681cb67def23d) / S0001" };
        var comparison = new
        {
            schemaVersion = "a99-hdsa-global-outline-comparison-v1",
            localIncumbent = localBaseline,
            globalOutlineLlm = new
            {
                parentF1 = evaluation.Parent.F1,
                conditionalParentF1 = evaluation.ConditionalParent.F1,
                derivedLevelExact = $@"{evaluation.DerivedTreeLevel.Exact}/{evaluation.DerivedTreeLevel.Evaluated}",
                proposedLevelExact = $@"{evaluation.ModelProposedLevel.Exact}/{evaluation.ModelProposedLevel.Evaluated}",
                s0005 = evaluation.S0005,
            },
            evaluationMode = "FROZEN_RESPONSE_REPLAY",
            freshModelCalls = 0,
            freshProviderCalls = 0,
        };
        await WriteJsonAsync(Path.Combine(output, "comparison.v1.json"), comparison, ct);
        await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-outline-llm-live-summary-v1",
            status = validation.Accepted ? "COMPLETE" : "GLOBAL_LLM_INVALID_TREE",
            documentId = DocumentId,
            model = responseRoot.TryGetProperty("model", out var modelElement) ? modelElement.GetString() : Model,
            provider = responseRoot.TryGetProperty("provider", out var providerElement) ? providerElement.GetString() : null,
            catalogFingerprint = catalog.CatalogFingerprint,
            catalogNodeCount = catalog.Entries.Count,
            requestNodeCount = request.Nodes.Count,
            requestSha256 = requestHash,
            responseSha256 = responseHash,
            historicalModelCalls = responseRoot.GetProperty("modelCalls").GetInt32(),
            historicalProviderCalls = responseRoot.GetProperty("providerCalls").GetInt32(),
            freshModelCalls = 0,
            freshProviderCalls = 0,
            goldReadBeforeFreeze = false,
            predictionFrozenBeforeGold = true,
            globalDecoderUsed = false,
            evaluation,
            comparison,
        }, ct);
        Console.WriteLine("HDSA_GLOBAL_OUTLINE_FROZEN_EVALUATION=COMPLETE");
        Console.WriteLine("FRESH_MODEL_CALLS=0");
        Console.WriteLine("FRESH_PROVIDER_CALLS=0");
        Console.WriteLine($"PARENT_F1={evaluation.Parent.F1:F4}");
        Console.WriteLine($"CONDITIONAL_PARENT_F1={evaluation.ConditionalParent.F1:F4}");
        Console.WriteLine($"S0005_GLOBAL={evaluation.S0005}");
        return 0;
    }

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(output);
        var catalog = ReadCatalog(Path.Combine(repoRoot, CatalogPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!string.Equals(catalog.CatalogFingerprint, ExpectedCatalogFingerprint, StringComparison.Ordinal))
        {
            await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new
            {
                schemaVersion = "a99-hdsa-global-outline-llm-live-summary-v1",
                status = "ABORTED_CATALOG_MISMATCH",
                expectedCatalogFingerprint = ExpectedCatalogFingerprint,
                actualCatalogFingerprint = catalog.CatalogFingerprint,
                modelCalls = 0,
                providerCalls = 0,
                goldReadBeforeFreeze = false,
            }, ct);
            Console.WriteLine("HDSA_GLOBAL_OUTLINE_STATUS=ABORTED_CATALOG_MISMATCH");
            Console.WriteLine("MODEL_CALLS=0");
            Console.WriteLine("PROVIDER_CALLS=0");
            return 1;
        }

        var request = BuildRequest(catalog);
        var requestJson = JsonSerializer.Serialize(request, JsonOptions);
        var requestHash = Sha256Text(requestJson);
        await WriteJsonAsync(Path.Combine(output, "catalog.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-outline-llm-live-catalog-v1",
            documentId = DocumentId,
            catalogFingerprint = catalog.CatalogFingerprint,
            sourceSha256 = catalog.SourceSha256,
            preprocessingSnapshotHash = catalog.PreprocessingSnapshotHash,
            resolverVersion = catalog.ResolverVersion,
            entries = catalog.Entries,
            acceptedIdentityRelations = catalog.AcceptedIdentityRelations,
            goldUsed = false,
            productionBenchmark = true,
        }, ct);
        await WriteJsonAsync(Path.Combine(output, "request.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-outline-llm-request-v1",
            request,
            requestSha256 = requestHash,
            goldReadBeforeFreeze = false,
            goldDerivedInput = false,
        }, ct);
        await WriteJsonAsync(Path.Combine(output, "manifest.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-outline-llm-live-manifest-v1",
            documentId = DocumentId,
            model = Model,
            endpoint = Endpoint,
            sourceSha256 = catalog.SourceSha256,
            catalogFingerprint = catalog.CatalogFingerprint,
            catalogNodeCount = catalog.Entries.Count,
            requestNodeCount = request.Nodes.Count,
            requestSha256 = requestHash,
            systemPromptSha256 = Sha256Text(SystemPrompt),
            responseSchemaSha256 = Sha256Text(JsonSerializer.Serialize(HdsaGlobalOutlineHierarchyContract.Schema())),
            candidateGeneration = "NONE_GLOBAL_OUTLINE_REQUEST_ALL_CATALOG_NODES",
            globalDecoderUsed = false,
            goldReadBeforeFreeze = false,
            goldDerivedInput = false,
            modelFallback = "NONE",
            expectedModelCalls = 1,
            expectedProviderCalls = 1,
            transientRequestRetries = 0,
        }, ct);

        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
            return await BlockAsync(output, "OPENROUTER_API_KEY_MISSING", 0, 0, ct);

        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        using var lease = await A99OpenRouterLiveProviderLease.AcquireAsync(repoRoot, "hdsa-global-outline-llm-live", DocumentId, ct);
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
            return await BlockAsync(output, "MODEL_CAPABILITY_MISMATCH", 0, 0, ct);

        using var model = new OpenRouterCeilingReasoningModel(options, capability, http);
        string content;
        RequestPacketTelemetry telemetry;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            (content, telemetry) = await model.CompleteRawStructuredSemanticAsync(
                DocumentId,
                "HDSA_GLOBAL_OUTLINE_LLM",
                "hdsa-global-outline:DOC-0205",
                requestJson,
                requestJson.Length,
                request.Nodes.Count,
                request.Nodes.Count,
                SystemPrompt,
                $"TASK=A99_GLOBAL_OUTLINE_HIERARCHY_V1\n{requestJson}\nReturn exactly the requested JSON object.",
                HdsaGlobalOutlineHierarchyContract.Schema(),
                "hdsa_global_outline_hierarchy_v1",
                ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or ReasoningCompletionException)
        {
            await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new
            {
                schemaVersion = "a99-hdsa-global-outline-llm-live-summary-v1",
                status = "BLOCKED",
                reason = "PROVIDER_FAILURE:" + ex.GetType().Name,
                model = Model,
                requestSha256 = requestHash,
                modelCalls = model.ProviderCalls,
                providerCalls = model.ProviderCalls,
                goldReadBeforeFreeze = false,
            }, ct);
            Console.WriteLine("HDSA_GLOBAL_OUTLINE_STATUS=BLOCKED");
            Console.WriteLine($"REASON=PROVIDER_FAILURE:{ex.GetType().Name}");
            Console.WriteLine($"MODEL_CALLS={model.ProviderCalls}");
            Console.WriteLine($"PROVIDER_CALLS={model.ProviderCalls}");
            return 1;
        }
        stopwatch.Stop();

        var responseHash = Sha256Text(content);
        await WriteJsonAsync(Path.Combine(output, "response.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-outline-llm-response-v1",
            documentId = DocumentId,
            requestSha256 = requestHash,
            responseSha256 = responseHash,
            rawResponse = content,
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

        HdsaGlobalOutlineHierarchyProposal? proposal = null;
        string? parseError = null;
        try { proposal = HdsaGlobalOutlineHierarchyContract.Parse(content); }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            parseError = ex.Message;
        }
        var validation = proposal is null
            ? null
            : HdsaGlobalOutlineHierarchyContract.Validate(request, proposal);
        await WriteJsonAsync(Path.Combine(output, "prediction.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-outline-llm-prediction-v1",
            documentId = DocumentId,
            requestSha256 = requestHash,
            responseSha256 = responseHash,
            proposal,
            parseError,
            validation,
            globalDecoderUsed = false,
            goldReadBeforeFreeze = false,
            goldDerivedInput = false,
        }, ct);
        var predictionPath = Path.Combine(output, "prediction.v1.json");
        await WriteJsonAsync(Path.Combine(output, "freeze.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-outline-llm-freeze-v1",
            documentId = DocumentId,
            catalogFingerprint = catalog.CatalogFingerprint,
            requestSha256 = requestHash,
            responseSha256 = responseHash,
            predictionSha256 = Sha256File(predictionPath),
            model = Model,
            provider = telemetry.ProviderRoute,
            finishReason = telemetry.FinishReason,
            inputTokens = telemetry.ReportedInputTokens,
            reasoningTokens = telemetry.ReportedReasoningTokens,
            outputTokens = telemetry.ReportedOutputTokens,
            elapsedMs = stopwatch.ElapsedMilliseconds,
            modelCalls = 1,
            providerCalls = 1,
            goldReadBeforeFreeze = false,
            frozenBeforeGold = true,
        }, ct);

        var evaluation = EvaluateAfterFreeze(repoRoot, catalog, request, proposal, validation);
        await WriteJsonAsync(Path.Combine(output, "evaluation.v1.json"), evaluation, ct);
        var localBaseline = new { parentF1 = .8, levelExact = "4/11", s0005 = "SELECT_PARENT(SN-d53681cb67def23d) / S0001" };
        var comparison = new
        {
            schemaVersion = "a99-hdsa-global-outline-comparison-v1",
            localIncumbent = localBaseline,
            globalOutlineLlm = new
            {
                parentF1 = evaluation.Parent.F1,
                derivedLevelExact = $"{evaluation.DerivedTreeLevel.Exact}/{evaluation.DerivedTreeLevel.Evaluated}",
                proposedLevelExact = $"{evaluation.ModelProposedLevel.Exact}/{evaluation.ModelProposedLevel.Evaluated}",
                s0005 = evaluation.S0005,
            },
        };
        await WriteJsonAsync(Path.Combine(output, "comparison.v1.json"), comparison, ct);
        await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-outline-llm-live-summary-v1",
            status = validation?.Accepted == true ? "COMPLETE" : "GLOBAL_LLM_INVALID_TREE",
            documentId = DocumentId,
            model = Model,
            provider = telemetry.ProviderRoute,
            catalogFingerprint = catalog.CatalogFingerprint,
            catalogNodeCount = catalog.Entries.Count,
            requestNodeCount = request.Nodes.Count,
            requestSha256 = requestHash,
            responseSha256 = responseHash,
            modelCalls = 1,
            providerCalls = 1,
            goldReadBeforeFreeze = false,
            predictionFrozenBeforeGold = true,
            globalDecoderUsed = false,
            evaluation,
            comparison,
        }, ct);

        Console.WriteLine("HDSA_GLOBAL_OUTLINE_STATUS=" + (validation?.Accepted == true ? "COMPLETE" : "GLOBAL_LLM_INVALID_TREE"));
        Console.WriteLine("MODEL_CALLS=1");
        Console.WriteLine("PROVIDER_CALLS=1");
        Console.WriteLine("GOLD_READ_BEFORE_FREEZE=0");
        Console.WriteLine($"S0005_GLOBAL={evaluation.S0005}");
        Console.WriteLine($"PARENT_F1={evaluation.Parent.F1:F4}");
        Console.WriteLine($"PROPOSED_LEVEL_EXACT={evaluation.ModelProposedLevel.Exact}/{evaluation.ModelProposedLevel.Evaluated}");
        Console.WriteLine($"DERIVED_LEVEL_EXACT={evaluation.DerivedTreeLevel.Exact}/{evaluation.DerivedTreeLevel.Evaluated}");
        Console.WriteLine($"SUMMARY={Path.Combine(OutputRoot, "summary.v1.json")} ");
        return validation?.Accepted == true ? 0 : 1;
    }

    private static HdsaGlobalOutlineHierarchyRequest BuildRequest(HdsaFrozenSemanticNodeCatalog catalog)
    {
        var nodes = catalog.Entries.OrderBy(item => item.SourceOrder).ThenBy(item => item.SemanticNodeId, StringComparer.Ordinal)
            .Select(item => new HdsaGlobalOutlineNodeContext(
                item.SemanticNodeId,
                item.MemberOccurrenceIds,
                item.CanonicalText,
                item.SourceOrder,
                "source-order=" + item.SourceOrder,
                "parser-owned style/layout evidence unavailable in frozen catalog",
                "single source-backed semantic catalog"))
            .ToArray();
        return new(catalog.CatalogFingerprint, nodes, catalog.AcceptedIdentityRelations, false);
    }

    private static HdsaFrozenSemanticNodeCatalog ReadCatalog(string path)
    {
        Require(path);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var sourceSha = root.GetProperty("sourceSha256").GetString()!;
        var snapshot = root.GetProperty("preprocessingSnapshotHash").GetString()!;
        var resolverVersion = root.GetProperty("resolverVersion").GetString()!;
        var entries = root.GetProperty("entries").EnumerateArray().Select(item => new
        {
            Id = item.GetProperty("semanticNodeId").GetString()!,
            Aliases = item.GetProperty("memberOccurrenceIds").EnumerateArray().Select(value => value.GetString()!).ToArray(),
            Text = item.GetProperty("canonicalText").GetString()!,
            Order = item.GetProperty("sourceOrder").GetInt32(),
        }).ToArray();
        var input = new HdsaSemanticNodeResolutionInput(sourceSha, snapshot,
            entries.SelectMany(item => item.Aliases.Select(alias => new HdsaSemanticNodeSourceOccurrence(alias, item.Order, item.Text))).ToArray(), false);
        var predictions = entries.Select(item => new HdsaSemanticNodePrediction(item.Id, item.Aliases, item.Text, "FROZEN_SAFE_CATALOG", resolverVersion, false)).ToArray();
        var relations = root.TryGetProperty("acceptedIdentityRelations", out var relationElement)
            ? relationElement.Deserialize<IReadOnlyList<HdsaSemanticIdentityRelation>>(JsonOptions) ?? []
            : [];
        var result = new HdsaSemanticNodeResolutionV3Result(sourceSha, snapshot, predictions, relations, [], [], resolverVersion, false);
        var catalog = HdsaSemanticNodeCatalogBuilder.Build(input, result);
        if (!string.Equals(catalog.CatalogFingerprint, root.GetProperty("catalogFingerprint").GetString(), StringComparison.Ordinal))
            throw new InvalidDataException("GLOBAL_OUTLINE_CATALOG_RECONSTRUCTION_MISMATCH");
        return catalog;
    }

    private static GlobalOutlineEvaluation EvaluateAfterFreeze(string repoRoot, HdsaFrozenSemanticNodeCatalog catalog,
        HdsaGlobalOutlineHierarchyRequest request, HdsaGlobalOutlineHierarchyProposal? proposal,
        HdsaGlobalOutlineValidation? validation)
    {
        var goldPath = Path.Combine(repoRoot, "eval/a99-closed-loop/hdsa-parent-relation-live/DOC-0205/structural-gold.v1.json".Replace('/', Path.DirectorySeparatorChar));
        using var gold = JsonDocument.Parse(File.ReadAllText(goldPath));
        var root = gold.RootElement;
        if (!string.Equals(root.GetProperty("sourceSha256").GetString(), catalog.SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("GLOBAL_OUTLINE_GOLD_SOURCE_MISMATCH");
        var aliases = root.GetProperty("occurrences").EnumerateArray()
            .Where(item => item.GetProperty("reviewStatus").GetString() == "RESOLVED")
            .ToDictionary(item => item.GetProperty("sourceAlias").GetString()!, item => item.GetProperty("semanticNodeId").GetString()!, StringComparer.Ordinal);
        var nodeMap = catalog.Entries.ToDictionary(entry => entry.SemanticNodeId, entry =>
        {
            var ids = entry.MemberOccurrenceIds.Where(aliases.ContainsKey).Select(alias => aliases[alias]).Distinct(StringComparer.Ordinal).ToArray();
            return ids.Length == 1 ? ids[0] : null;
        }, StringComparer.Ordinal);
        var goldEdges = root.GetProperty("tree").GetProperty("parentOf").EnumerateArray()
            .Select(item => item.GetProperty("parent").GetString() + ">" + item.GetProperty("child").GetString()).ToHashSet(StringComparer.Ordinal);
        var predictedEdges = validation?.ParentRelations
            .Where(item => item.Relation == HdsaRelationType.ParentOf)
            .Select(item => (parent: nodeMap.GetValueOrDefault(item.From), child: nodeMap.GetValueOrDefault(item.To)))
            .Where(item => item.child is not null).ToArray() ?? [];
        var mapped = predictedEdges.Where(item => item.parent is not null).Select(item => item.parent + ">" + item.child).ToHashSet(StringComparer.Ordinal);
        var unmappable = predictedEdges.Count(item => item.parent is null);
        var tp = mapped.Intersect(goldEdges, StringComparer.Ordinal).Count();
        var fp = mapped.Except(goldEdges, StringComparer.Ordinal).Count() + unmappable;
        var fn = goldEdges.Except(mapped, StringComparer.Ordinal).Count();
        var precision = tp + fp == 0 ? 0 : (double)tp / (tp + fp);
        var recall = tp + fn == 0 ? 0 : (double)tp / (tp + fn);
        var f1 = precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
        var goldDepth = GoldDepth(goldEdges);
        var predictedParentMap = validation?.ParentRelations
            .Where(item => item.Relation == HdsaRelationType.ParentOf)
            .GroupBy(item => item.To, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().From, StringComparer.Ordinal)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var goldParentMap = goldEdges.ToDictionary(
            edge => edge[(edge.IndexOf('>') + 1)..],
            edge => edge[..edge.IndexOf('>')],
            StringComparer.Ordinal);
        var derivedRows = catalog.Entries.Where(entry => nodeMap[entry.SemanticNodeId] is not null).Select(entry =>
        {
            var goldId = nodeMap[entry.SemanticNodeId]!;
            var predicted = validation?.Tree.Nodes.SingleOrDefault(node => node.Id == entry.SemanticNodeId)?.Level;
            var firstDivergentAncestor = FindFirstDivergentAncestor(entry.SemanticNodeId, nodeMap, predictedParentMap, goldParentMap);
            return new
            {
                semanticNodeId = entry.SemanticNodeId,
                goldSemanticNodeId = goldId,
                predictedLevel = predicted,
                goldLevel = goldDepth[goldId],
                exact = predicted == goldDepth[goldId],
                firstDivergentAncestor
            };
        }).ToArray();
        var proposedRows = request.Nodes.Where(node => nodeMap[node.SemanticNodeId] is not null).Select(node =>
        {
            var goldId = nodeMap[node.SemanticNodeId]!;
            var proposed = proposal?.Nodes.SingleOrDefault(item => item.NodeId == node.SemanticNodeId)?.ProposedLevel;
            return new { semanticNodeId = node.SemanticNodeId, proposedLevel = proposed, goldLevel = goldDepth[goldId], exact = proposed == goldDepth[goldId], firstDivergentAncestor = (string?)null };
        }).ToArray();
        var s0005 = catalog.Entries.FirstOrDefault(item => item.MemberOccurrenceIds.Contains("S0005", StringComparer.Ordinal));
        var s0005Proposal = proposal?.Nodes.SingleOrDefault(item => item.NodeId == s0005?.SemanticNodeId);
        var localParent = "SELECT_PARENT(SN-d53681cb67def23d) / S0001";
        var globalParent = s0005Proposal?.Parent ?? "UNPARSED";
        var exactMembershipNodeIds = catalog.Entries
            .Where(entry => IsExactGoldMembership(entry.MemberOccurrenceIds, aliases))
            .Select(entry => entry.SemanticNodeId)
            .ToHashSet(StringComparer.Ordinal);
        var conditionalPredicted = predictedEdges
            .Where(item => item.child is not null && catalog.Entries.Any(entry =>
                nodeMap.GetValueOrDefault(entry.SemanticNodeId) == item.child && exactMembershipNodeIds.Contains(entry.SemanticNodeId)))
            .ToArray();
        var conditionalMapped = conditionalPredicted
            .Where(item => item.parent is not null)
            .Select(item => item.parent + ">" + item.child)
            .ToHashSet(StringComparer.Ordinal);
        var conditionalUnmappable = conditionalPredicted.Count(item => item.parent is null);
        var conditionalGoldEdges = goldEdges.Where(edge =>
            exactMembershipNodeIds.Any(id => nodeMap.GetValueOrDefault(id) == edge[(edge.IndexOf('>') + 1)..]))
            .ToHashSet(StringComparer.Ordinal);
        var conditionalTp = conditionalMapped.Intersect(conditionalGoldEdges, StringComparer.Ordinal).Count();
        var conditionalFp = conditionalMapped.Except(conditionalGoldEdges, StringComparer.Ordinal).Count() + conditionalUnmappable;
        var conditionalFn = conditionalGoldEdges.Except(conditionalMapped, StringComparer.Ordinal).Count();
        var conditionalPrecision = conditionalTp + conditionalFp == 0 ? 0 : (double)conditionalTp / (conditionalTp + conditionalFp);
        var conditionalRecall = conditionalTp + conditionalFn == 0 ? 0 : (double)conditionalTp / (conditionalTp + conditionalFn);
        var conditionalF1 = conditionalPrecision + conditionalRecall == 0 ? 0 : 2 * conditionalPrecision * conditionalRecall / (conditionalPrecision + conditionalRecall);
        return new GlobalOutlineEvaluation(
            new ParentMetric(tp, fp, fn, precision, recall, f1, goldEdges.Count, mapped.Count, unmappable),
            new ConditionalParentMetric(conditionalTp, conditionalFp, conditionalFn, conditionalPrecision, conditionalRecall,
                conditionalF1, exactMembershipNodeIds.Count, conditionalGoldEdges.Count, conditionalMapped.Count, conditionalUnmappable),
            new LevelMetric(proposedRows.Count(item => item.exact), proposedRows.Length,
                proposedRows.Select(item => new LevelRow(item.semanticNodeId, item.proposedLevel, item.goldLevel, item.exact, item.firstDivergentAncestor)).ToArray()),
            new LevelMetric(derivedRows.Count(item => item.exact), derivedRows.Length,
                derivedRows.Select(item => new LevelRow(item.semanticNodeId, item.predictedLevel, item.goldLevel, item.exact, item.firstDivergentAncestor)).ToArray()),
            new TreeMetric(validation?.Tree.IsValid == true, validation?.Tree.Nodes.Count(item => item.ParentId is null) ?? 0,
                validation?.GraphValidation.Errors.Contains("PARENT_CYCLE") == true),
            new RootMetric(localParent, globalParent, string.Equals(globalParent, HdsaGlobalOutlineHierarchyContract.Root, StringComparison.Ordinal)),
            globalParent,
            derivedRows.Count(item => !item.exact && item.firstDivergentAncestor is not null &&
                !string.Equals(item.firstDivergentAncestor, item.semanticNodeId, StringComparison.Ordinal)));
    }

    private static string? FindFirstDivergentAncestor(
        string semanticNodeId,
        IReadOnlyDictionary<string, string?> nodeMap,
        IReadOnlyDictionary<string, string> predictedParentMap,
        IReadOnlyDictionary<string, string> goldParentMap)
    {
        var current = semanticNodeId;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (visited.Add(current))
        {
            var goldId = nodeMap.GetValueOrDefault(current);
            if (goldId is null) return current;
            var predictedParent = predictedParentMap.GetValueOrDefault(current);
            if (predictedParent is not null && !nodeMap.ContainsKey(predictedParent))
                return current;
            var predictedGoldParent = predictedParent is null ? null : nodeMap.GetValueOrDefault(predictedParent);
            var expectedGoldParent = goldParentMap.GetValueOrDefault(goldId);
            if (!string.Equals(predictedGoldParent, expectedGoldParent, StringComparison.Ordinal))
                return current;
            if (predictedParent is null) return null;
            current = predictedParent;
        }
        return current;
    }

    private static bool IsExactGoldMembership(IReadOnlyList<string> memberOccurrenceIds,
        IReadOnlyDictionary<string, string> aliases)
    {
        var goldIds = memberOccurrenceIds.Where(aliases.ContainsKey).Select(alias => aliases[alias])
            .Distinct(StringComparer.Ordinal).ToArray();
        if (goldIds.Length != 1) return false;
        var expectedMembers = aliases.Where(item => item.Value == goldIds[0]).Select(item => item.Key)
            .ToHashSet(StringComparer.Ordinal);
        return expectedMembers.SetEquals(memberOccurrenceIds);
    }

    private static IReadOnlyDictionary<string, int> GoldDepth(IReadOnlySet<string> edges)
    {
        var parent = edges.ToDictionary(edge => edge[(edge.IndexOf('>') + 1)..], edge => edge[..edge.IndexOf('>')], StringComparer.Ordinal);
        var all = parent.Keys.Concat(parent.Values).ToHashSet(StringComparer.Ordinal);
        var depth = new Dictionary<string, int>(StringComparer.Ordinal);
        int D(string id) => depth.TryGetValue(id, out var value) ? value : depth[id] = parent.TryGetValue(id, out var p) ? D(p) + 1 : 1;
        foreach (var id in all) _ = D(id);
        return depth;
    }

    private static async Task<int> BlockAsync(string output, string reason, int modelCalls, int providerCalls, CancellationToken ct)
    {
        await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-outline-llm-live-summary-v1",
            status = "BLOCKED",
            reason,
            modelCalls,
            providerCalls,
            goldReadBeforeFreeze = false,
        }, ct);
        Console.WriteLine("HDSA_GLOBAL_OUTLINE_STATUS=BLOCKED");
        Console.WriteLine("REASON=" + reason);
        Console.WriteLine($"MODEL_CALLS={modelCalls}");
        Console.WriteLine($"PROVIDER_CALLS={providerCalls}");
        return 1;
    }

    private static string Sha256Text(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static string Sha256File(string path) => Sha256Text(File.ReadAllText(path));
    private static async Task WriteJsonAsync(string path, object value, CancellationToken ct) =>
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, ct);
    private static void Require(string path) { if (!File.Exists(path)) throw new FileNotFoundException("GLOBAL_OUTLINE_INPUT_MISSING", path); }

    private sealed record GlobalOutlineEvaluation(
        ParentMetric Parent,
        ConditionalParentMetric ConditionalParent,
        LevelMetric ModelProposedLevel,
        LevelMetric DerivedTreeLevel,
        TreeMetric Tree,
        RootMetric Root,
        string S0005,
        int AncestorCascadeCount);

    private sealed record ConditionalParentMetric(
        int TP, int FP, int FN, double Precision, double Recall, double F1,
        int ExactMembershipNodeCount, int GoldEdgeCount, int MappedPredictedEdgeCount,
        int UnmappablePredictedEdges);

    private sealed record ParentMetric(
        int TP, int FP, int FN, double Precision, double Recall, double F1,
        int GoldEdgeCount, int MappedPredictedEdgeCount, int UnmappablePredictedEdges);

    private sealed record LevelMetric(int Exact, int Evaluated, IReadOnlyList<LevelRow> Rows);
    private sealed record LevelRow(string SemanticNodeId, int? PredictedLevel, int GoldLevel, bool Exact, string? FirstDivergentAncestor);
    private sealed record TreeMetric(bool Valid, int Roots, bool Cycles);
    private sealed record RootMetric(string LocalIncumbent, string GlobalOutlineLlm, bool Recovered);
}
