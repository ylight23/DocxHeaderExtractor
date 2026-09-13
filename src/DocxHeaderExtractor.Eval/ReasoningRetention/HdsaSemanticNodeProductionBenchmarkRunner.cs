using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// DOC-0205 live benchmark for the production semantic-node parent path. Semantic identity and
/// parent evaluation are deliberately separated: the catalog is frozen before Structural Gold
/// is opened, and parent reasoning can only select IDs already present in that catalog.
/// </summary>
public static class HdsaSemanticNodeProductionBenchmarkRunner
{
    private const string BehavioralParent = "8ab4310";
    private const string Model = "qwen/qwen3.7-flash";
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private const string SourcePath = "eval/a99-closed-loop/source-fidelity-audit/DOC-0205/converted-docx/025_ND_47-2020_Chia_se_du_lieu_so.docx";
    private const string SeedFreezePath = "eval/a99-closed-loop/source-fidelity-whole-alias-live/DOC-0205/r2/whole-alias/freeze.v1.json";
    private const string GoldPath = "eval/a99-closed-loop/hdsa-parent-relation-live/DOC-0205/structural-gold.v1.json";
    private const string OutputRoot = "eval/a99-closed-loop/hdsa-semantic-node-production-live/DOC-0205";
    private const int PrimaryWindow = 8;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string[] MiniTreeSeedAliases =
        ["S0001", "S0005", "S0014", "S0015", "S0016", "S0019", "S0021", "S0033", "S0035", "S0044", "S0052", "S0060"];

    private const string SystemPrompt = """
You are the A99 semantic-node parent reasoner. Decide only the immediate parent semantic-node
relation for the supplied child semantic node. Use source order, canonical source-backed text,
styles, numbering, and context as evidence; evidence is not a deterministic rule. Return exactly
one JSON object with catalogFingerprint, childSemanticNodeId, decision, and parentSemanticNodeId.
Use SELECT_PARENT only for an immediate parent from candidateParentSemanticNodeIds. Use ROOT only
when the node is structurally root. Use UNRESOLVED when evidence is insufficient; never use ROOT
merely because a candidate is hard to choose. Do not return level, depth, offsets, text spans,
legacy hierarchy fields, Gold IDs, or any ID not in the supplied catalog.
""";

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(output);
        var context = LoadContext(repoRoot);
        var semanticInput = BuildSemanticInput(context);
        var identity = HdsaSemanticNodeResolverV3.Resolve(semanticInput, []);
        var catalog = HdsaSemanticNodeCatalogBuilder.Build(semanticInput, identity);

        var manifestPath = Path.Combine(output, "manifest.v1.json");
        await WriteJsonAsync(manifestPath, new
        {
            schemaVersion = "a99-hdsa-semantic-node-production-live-manifest-v1",
            documentId = "DOC-0205",
            behavioralParent = BehavioralParent,
            benchmarkCode = GetGitHead(repoRoot),
            model = Model,
            endpoint = Endpoint,
            resolverVersion = identity.ResolverVersion,
            catalogAlgorithm = HdsaSemanticNodeResolverV3.Version,
            sourcePath = SourcePath,
            sourceSha256 = context.SourceSha256,
            preprocessingSnapshotHash = semanticInput.PreprocessingSnapshotHash,
            selectedSourceAliases = context.Selected.Select(item => item.Alias).ToArray(),
            primaryCandidateWindow = PrimaryWindow,
            candidateGeneration = "predicted semantic catalog + parser-owned source order",
            candidateGoldPruned = false,
            parentPromptSha256 = Sha256Text(SystemPrompt),
            parentSchemaSha256 = Sha256Text(JsonSerializer.Serialize(HdsaSemanticNodeParentReasoningContract.Schema())),
            goldReadBeforeFreeze = false,
            goldDerivedInput = false,
            modelFallback = "NONE",
        }, ct);

        var catalogPath = Path.Combine(output, "semantic-catalog.v1.json");
        await WriteJsonAsync(catalogPath, new
        {
            schemaVersion = "a99-hdsa-semantic-node-catalog-v1",
            documentId = "DOC-0205",
            sourceSha256 = catalog.SourceSha256,
            preprocessingSnapshotHash = catalog.PreprocessingSnapshotHash,
            resolverVersion = catalog.ResolverVersion,
            entries = catalog.Entries,
            acceptedIdentityRelations = catalog.AcceptedIdentityRelations,
            catalogFingerprint = catalog.CatalogFingerprint,
            goldUsed = catalog.GoldUsed,
            productionBenchmark = catalog.ProductionBenchmark,
        }, ct);
        var catalogFreezePath = Path.Combine(output, "semantic-catalog-freeze.v1.json");
        await WriteJsonAsync(catalogFreezePath, new
        {
            schemaVersion = "a99-hdsa-semantic-node-catalog-freeze-v1",
            documentId = "DOC-0205",
            catalogFingerprint = catalog.CatalogFingerprint,
            catalogSha256 = Sha256(catalogPath),
            nodeCount = catalog.Entries.Count,
            goldReadBeforeFreeze = false,
            goldDerivedInput = false,
            immutableBeforeParentReasoning = true,
        }, ct);

        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
            return await BlockAsync(output, "OPENROUTER_API_KEY_MISSING", catalog, 0, 0, ct);

        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        using var lease = await A99OpenRouterLiveProviderLease.AcquireAsync(repoRoot, "hdsa-semantic-node-production", "DOC-0205", ct);
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
            await WriteJsonAsync(Path.Combine(output, "capability.v1.json"), capability is null ? (object)new { status = "MISSING" } : capability, ct);
            return await BlockAsync(output, "MODEL_CAPABILITY_MISMATCH", catalog, 0, 0, ct);
        }

        using var model = new OpenRouterCeilingReasoningModel(options, capability, http);
        var decisions = new List<HdsaSemanticNodeParentDecision>();
        var requests = new List<HdsaSemanticNodeParentReasoningRequest>();
        var validations = new List<HdsaSemanticNodeParentDecisionValidation>();
        var observations = new List<object>();
        foreach (var entry in catalog.Entries.OrderBy(item => item.SourceOrder).ThenBy(item => item.SemanticNodeId, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var request = HdsaSemanticNodeParentReasoningContract.CreateRequest(catalog, entry.SemanticNodeId, PrimaryWindow);
            requests.Add(request);
            var requestJson = JsonSerializer.Serialize(request, JsonOptions);
            var requestHash = Sha256Text(requestJson);
            var childDir = Path.Combine(output, entry.SemanticNodeId);
            Directory.CreateDirectory(childDir);
            var predictionPath = Path.Combine(childDir, "prediction.v1.json");
            var freezePath = Path.Combine(childDir, "freeze.v1.json");

            if (File.Exists(predictionPath) || File.Exists(freezePath))
            {
                if (!File.Exists(predictionPath) || !File.Exists(freezePath))
                    throw new InvalidDataException($"PARTIAL_SEMANTIC_NODE_FREEZE:{entry.SemanticNodeId}");
                using var frozen = JsonDocument.Parse(await File.ReadAllTextAsync(freezePath, ct));
                if (!string.Equals(frozen.RootElement.GetProperty("requestSha256").GetString(), requestHash, StringComparison.Ordinal) ||
                    frozen.RootElement.GetProperty("goldReadBeforeFreeze").GetBoolean())
                    throw new InvalidDataException($"SEMANTIC_NODE_REQUEST_LINEAGE_MISMATCH:{entry.SemanticNodeId}");
                using var prediction = JsonDocument.Parse(await File.ReadAllTextAsync(predictionPath, ct));
                var decision = HdsaSemanticNodeParentReasoningContract.Parse(prediction.RootElement.GetProperty("rawResponse").GetString()!);
                var validation = HdsaSemanticNodeParentReasoningContract.Validate(request, decision, catalog);
                if (!validation.Accepted) throw new InvalidDataException($"FROZEN_SEMANTIC_NODE_DECISION_REJECTED:{entry.SemanticNodeId}:{validation.RejectionReason}");
                decisions.Add(decision);
                validations.Add(validation);
                observations.Add(new { child = entry.SemanticNodeId, reusedFrozen = true, requestSha256 = requestHash, validation });
                continue;
            }

            await WriteJsonAsync(Path.Combine(childDir, "request.v1.json"), new
            {
                schemaVersion = "a99-hdsa-semantic-node-parent-request-v1",
                request,
                requestSha256 = requestHash,
                goldReadBeforeFreeze = false,
                goldDerivedInput = false,
            }, ct);
            var userPrompt = $"TASK=A99_SEMANTIC_NODE_PARENT_V1\n{requestJson}\nReturn exactly the requested JSON object.";
            var stopwatch = Stopwatch.StartNew();
            string content;
            RequestPacketTelemetry telemetry;
            try
            {
                (content, telemetry) = await model.CompleteRawStructuredSemanticAsync(
                    "DOC-0205", "HDSA_SEMANTIC_NODE_PARENT", $"hdsa-semantic-node-parent:{entry.SemanticNodeId}",
                    requestJson, requestJson.Length, catalog.Entries.Count, request.AuthoritativeParentUniverse.Count + 1,
                    SystemPrompt, userPrompt, HdsaSemanticNodeParentReasoningContract.Schema(),
                    "hdsa_semantic_node_parent_v1", ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or ReasoningCompletionException)
            {
                return await BlockAsync(output, "PROVIDER_FAILURE:" + ex.GetType().Name, catalog, decisions.Count, model.ProviderCalls, ct);
            }

            var decisionResult = HdsaSemanticNodeParentReasoningContract.Parse(content);
            var validationResult = HdsaSemanticNodeParentReasoningContract.Validate(request, decisionResult, catalog);
            validations.Add(validationResult);
            if (!validationResult.Accepted)
                throw new InvalidDataException($"SEMANTIC_NODE_PARENT_DECISION_REJECTED:{entry.SemanticNodeId}:{validationResult.RejectionReason}");
            decisions.Add(decisionResult);
            var responseHash = Sha256Text(content);
            await WriteJsonAsync(predictionPath, new
            {
                schemaVersion = "a99-hdsa-semantic-node-parent-prediction-v1",
                documentId = "DOC-0205",
                childSemanticNodeId = entry.SemanticNodeId,
                requestSha256 = requestHash,
                responseSha256 = responseHash,
                rawResponse = content,
                decision = decisionResult,
                validation = validationResult,
                goldReadBeforeFreeze = false,
                goldDerivedInput = false,
            }, ct);
            await WriteJsonAsync(freezePath, new
            {
                schemaVersion = "a99-hdsa-semantic-node-parent-freeze-v1",
                documentId = "DOC-0205",
                childSemanticNodeId = entry.SemanticNodeId,
                catalogFingerprint = catalog.CatalogFingerprint,
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
                goldDerivedInput = false,
            }, ct);
            observations.Add(new { child = entry.SemanticNodeId, reusedFrozen = false, requestSha256 = requestHash, validation = validationResult });
        }

        var decisionByChild = decisions.ToDictionary(item => item.ChildSemanticNodeId, StringComparer.Ordinal);
        var hierarchy = HdsaSemanticHierarchyPipeline.Run(catalog, request => decisionByChild[request.ChildSemanticNodeId]);
        var predictionCompletePath = Path.Combine(output, "prediction-complete.v1.json");
        await WriteJsonAsync(predictionCompletePath, new
        {
            schemaVersion = "a99-hdsa-semantic-node-production-prediction-v1",
            documentId = "DOC-0205",
            catalogFingerprint = catalog.CatalogFingerprint,
            requests,
            decisions,
            validations,
            hierarchy.Tree,
            hierarchy.GraphValidation,
            observations,
            goldReadBeforeFreeze = false,
            goldDerivedInput = false,
            productionBenchmark = true,
        }, ct);
        var predictionFreezePath = Path.Combine(output, "prediction-freeze.v1.json");
        await WriteJsonAsync(predictionFreezePath, new
        {
            schemaVersion = "a99-hdsa-semantic-node-production-freeze-v1",
            documentId = "DOC-0205",
            catalogFingerprint = catalog.CatalogFingerprint,
            catalogSha256 = Sha256(catalogPath),
            predictionSha256 = Sha256(predictionCompletePath),
            requestCount = requests.Count,
            decisionCount = decisions.Count,
            goldReadBeforeFreeze = false,
            goldDerivedInput = false,
            frozenBeforeGold = true,
        }, ct);

        var evaluation = EvaluateAfterFreeze(repoRoot, context, catalog, hierarchy, decisions, requests, predictionFreezePath, predictionCompletePath);
        var summary = new
        {
            schemaVersion = "a99-hdsa-semantic-node-production-live-summary-v1",
            status = "COMPLETE",
            documentId = "DOC-0205",
            benchmarkCode = GetGitHead(repoRoot),
            behavioralParent = BehavioralParent,
            model = Model,
            source = new { context.SourceSha256, occurrenceCount = context.Selected.Count },
            semanticCatalog = new { resolverVersion = catalog.ResolverVersion, catalog.CatalogFingerprint, nodeCount = catalog.Entries.Count, goldUsed = catalog.GoldUsed },
            parentInference = new
            {
                requestCount = requests.Count,
                selectParent = decisions.Count(item => item.Decision == HdsaParentDecision.SelectParent),
                root = decisions.Count(item => item.Decision == HdsaParentDecision.Root),
                unresolved = decisions.Count(item => item.Decision == HdsaParentDecision.Unresolved),
                invalidRejected = validations.Count(item => !item.Accepted),
            },
            tree = new
            {
                valid = hierarchy.Tree.IsValid,
                rootCount = hierarchy.Tree.Nodes.Count(item => item.ParentId is null),
                maxDepth = hierarchy.Tree.Nodes.Count == 0 ? 0 : hierarchy.Tree.Nodes.Max(item => item.Level),
                cyclesRejected = hierarchy.GraphValidation.Errors.Contains("PARENT_CYCLE", StringComparer.Ordinal),
            },
            evaluation,
            calls = new { modelCalls = decisions.Count, providerCalls = Math.Max(decisions.Count, model.ProviderCalls) },
            goldReadBeforeFreeze = false,
            goldDerivedInput = false,
            productionBenchmark = true,
            legacyHierarchyConsumed = false,
            levelSource = "HDSA_TREE_DEPTH",
        };
        await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), summary, ct);
        Console.WriteLine("HDSA_SEMANTIC_NODE_PRODUCTION_STATUS=COMPLETE");
        Console.WriteLine($"MODEL_CALLS={decisions.Count}");
        Console.WriteLine($"PROVIDER_CALLS={Math.Max(decisions.Count, model.ProviderCalls)}");
        Console.WriteLine("GOLD_READ_BEFORE_FREEZE=0");
        Console.WriteLine($"SUMMARY={Path.Combine(OutputRoot, "summary.v1.json")}");
        return 0;
    }

    private static HdsaSemanticNodeResolutionInput BuildSemanticInput(Context context)
    {
        var occurrences = context.Selected.Select(item => new HdsaSemanticNodeSourceOccurrence(
            item.Alias,
            item.SourceOrdinal,
            item.Text,
            item.Styles,
            item.LocalContext)).ToArray();
        var snapshot = JsonSerializer.Serialize(occurrences, JsonOptions);
        return new(context.SourceSha256, Sha256Text(snapshot), occurrences, false);
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
                $"prev={Math.Max(0, index - 1)};next={index + 1}"))
            .ToArray();
        var byAlias = aliases.ToDictionary(item => item.Alias, StringComparer.Ordinal);
        var selected = MiniTreeSeedAliases.Select(alias => byAlias.TryGetValue(alias, out var item)
                ? item
                : throw new InvalidDataException($"MINI_TREE_ALIAS_MISSING:{alias}"))
            .OrderBy(item => item.SourceOrdinal)
            .ToArray();
        AssertSeedLineage(repoRoot, sourcePath, aliases);
        return new(Sha256(File.ReadAllBytes(sourcePath)), selected);
    }

    private static void AssertSeedLineage(string repoRoot, string sourcePath, IReadOnlyList<AliasContext> aliases)
    {
        var freezePath = Path.Combine(repoRoot, SeedFreezePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(freezePath)) throw new FileNotFoundException("HDSA_SEED_FREEZE_MISSING", freezePath);
        using var freeze = JsonDocument.Parse(File.ReadAllText(freezePath));
        var root = freeze.RootElement;
        if (root.GetProperty("goldReadBeforeFreeze").GetBoolean()) throw new InvalidDataException("HDSA_SEED_GOLD_FIREWALL_FAILED");
        var sourceSha = Sha256(File.ReadAllBytes(sourcePath));
        if (!string.Equals(root.GetProperty("sourceSha256").GetString(), sourceSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("HDSA_SOURCE_HASH_MISMATCH");
        var predictionPath = Path.Combine(Path.GetDirectoryName(freezePath)!, "prediction.v1.json");
        if (!string.Equals(root.GetProperty("predictionSha256").GetString(), Sha256(predictionPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("HDSA_SEED_PREDICTION_HASH_MISMATCH");
        if (aliases.Count < MiniTreeSeedAliases.Length) throw new InvalidDataException("HDSA_SOURCE_ALIAS_CATALOG_TOO_SMALL");
    }

    private static object EvaluateAfterFreeze(
        string repoRoot,
        Context context,
        HdsaFrozenSemanticNodeCatalog catalog,
        HdsaSemanticHierarchyRunResult hierarchy,
        IReadOnlyList<HdsaSemanticNodeParentDecision> decisions,
        IReadOnlyList<HdsaSemanticNodeParentReasoningRequest> requests,
        string predictionFreezePath,
        string predictionCompletePath)
    {
        if (!File.Exists(predictionFreezePath) || !File.Exists(predictionCompletePath))
            throw new InvalidDataException("GOLD_OPEN_BEFORE_PREDICTION_FREEZE");
        using var freeze = JsonDocument.Parse(File.ReadAllText(predictionFreezePath));
        if (freeze.RootElement.GetProperty("goldReadBeforeFreeze").GetBoolean())
            throw new InvalidDataException("PREDICTION_FREEZE_GOLD_FIREWALL_FAILED");
        var goldPath = Path.Combine(repoRoot, GoldPath.Replace('/', Path.DirectorySeparatorChar));
        using var gold = JsonDocument.Parse(File.ReadAllText(goldPath));
        var goldRoot = gold.RootElement;
        if (!string.Equals(goldRoot.GetProperty("sourceSha256").GetString(), context.SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("STRUCTURAL_GOLD_SOURCE_HASH_MISMATCH");

        var goldAliases = goldRoot.GetProperty("occurrences").EnumerateArray()
            .Where(item => item.GetProperty("reviewStatus").GetString() == "RESOLVED")
            .ToDictionary(item => item.GetProperty("sourceAlias").GetString()!, item => item.GetProperty("semanticNodeId").GetString()!, StringComparer.Ordinal);
        var goldMembers = goldAliases.GroupBy(item => item.Value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Key).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var predictedNodeToGold = catalog.Entries.ToDictionary(
            item => item.SemanticNodeId,
            item =>
            {
                var mapped = item.MemberOccurrenceIds.Where(goldAliases.ContainsKey).Select(alias => goldAliases[alias]).Distinct(StringComparer.Ordinal).ToArray();
                return mapped.Length == 1 ? mapped[0] : null;
            },
            StringComparer.Ordinal);
        var predictedEntriesByGold = predictedNodeToGold
            .Where(item => item.Value is not null)
            .GroupBy(item => item.Value!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Key).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var splitGoldNodes = goldMembers.Keys
            .Where(goldId => predictedEntriesByGold.TryGetValue(goldId, out var predictedIds) && predictedIds.Count > 1)
            .ToHashSet(StringComparer.Ordinal);
        var exactMembershipEntries = catalog.Entries
            .Where(entry =>
            {
                var mapped = entry.MemberOccurrenceIds.Where(goldAliases.ContainsKey).Select(alias => goldAliases[alias]).Distinct(StringComparer.Ordinal).ToArray();
                return mapped.Length == 1 && goldMembers[mapped[0]].SetEquals(entry.MemberOccurrenceIds.Where(goldAliases.ContainsKey));
            })
            .Select(entry => entry.SemanticNodeId)
            .ToHashSet(StringComparer.Ordinal);
        var exactMembershipMatches = 0;
        var mergeErrors = 0;
        foreach (var entry in catalog.Entries)
        {
            var mapped = entry.MemberOccurrenceIds.Where(goldAliases.ContainsKey).Select(alias => goldAliases[alias]).Distinct(StringComparer.Ordinal).ToArray();
            if (mapped.Length > 1) { mergeErrors++; continue; }
            if (mapped.Length == 1)
            {
                var predictedMembers = entry.MemberOccurrenceIds.Where(goldAliases.ContainsKey).ToHashSet(StringComparer.Ordinal);
                if (goldMembers[mapped[0]].SetEquals(predictedMembers)) exactMembershipMatches++;
            }
        }
        var goldEdges = goldRoot.GetProperty("tree").GetProperty("parentOf").EnumerateArray()
            .Select(item => $"{item.GetProperty("parent").GetString()}>{item.GetProperty("child").GetString()}")
            .ToHashSet(StringComparer.Ordinal);
        var predictedEdges = hierarchy.GraphValidation.AcceptedRelations
            .Where(item => item.Relation == HdsaRelationType.ParentOf)
            .Select(item => (Parent: predictedNodeToGold.GetValueOrDefault(item.From), Child: predictedNodeToGold.GetValueOrDefault(item.To)))
            .Where(item => item.Parent is not null && item.Child is not null)
            .Select(item => $"{item.Parent}>{item.Child}")
            .ToHashSet(StringComparer.Ordinal);
        var unmappablePredictedEdges = hierarchy.GraphValidation.AcceptedRelations
            .Where(item => item.Relation == HdsaRelationType.ParentOf)
            .Select(item => (Parent: predictedNodeToGold.GetValueOrDefault(item.From), Child: predictedNodeToGold.GetValueOrDefault(item.To)))
            .Count(item => item.Child is not null && item.Parent is null);
        var tp = predictedEdges.Intersect(goldEdges, StringComparer.Ordinal).Count();
        var fp = predictedEdges.Except(goldEdges, StringComparer.Ordinal).Count() + unmappablePredictedEdges;
        var fn = goldEdges.Except(predictedEdges, StringComparer.Ordinal).Count();
        var precision = tp + fp == 0 ? 0 : (double)tp / (tp + fp);
        var recall = tp + fn == 0 ? 0 : (double)tp / (tp + fn);
        var f1 = precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
        var goldParent = goldEdges.ToDictionary(edge => edge[(edge.IndexOf('>') + 1)..], edge => edge[..edge.IndexOf('>')], StringComparer.Ordinal);
        var diagnostics = requests.Zip(decisions).Select(pair =>
        {
            var request = pair.First;
            var decision = pair.Second;
            var predictedChildGold = predictedNodeToGold.GetValueOrDefault(decision.ChildSemanticNodeId);
            var predictedParentGold = decision.ParentSemanticNodeId is null ? null : predictedNodeToGold.GetValueOrDefault(decision.ParentSemanticNodeId);
            var goldParentId = predictedChildGold is not null && goldParent.TryGetValue(predictedChildGold, out var parent) ? parent : null;
            var isCorrectRoot = decision.Decision == HdsaParentDecision.Root && predictedChildGold is not null && goldParentId is null;
            var isCorrectParent = decision.Decision == HdsaParentDecision.SelectParent && predictedChildGold is not null &&
                predictedParentGold is not null && predictedParentGold == goldParentId;
            var kind = decision.Decision == HdsaParentDecision.Unresolved ? "UNRESOLVED" :
                predictedChildGold is null ? "SEMANTIC_RESOLUTION_ERROR" :
                isCorrectRoot || isCorrectParent ? "TP" : "MODEL_PARENT_SELECTION_ERROR";
            return new
            {
                childSemanticNodeId = decision.ChildSemanticNodeId,
                predictedParentSemanticNodeId = decision.ParentSemanticNodeId,
                goldParentSemanticNodeId = goldParentId,
                candidateSet = request.CandidateParentSemanticNodeIds,
                validated = true,
                catalogFingerprint = catalog.CatalogFingerprint,
                semanticMembershipStatus = predictedChildGold is null ? "UNMAPPABLE" :
                    exactMembershipEntries.Contains(decision.ChildSemanticNodeId) ? "EXACT_MEMBERSHIP" :
                    "SPLIT_OR_NONEXACT_EVALUATION_MAPPING",
                classification = kind,
            };
        }).ToArray();
        var predictedDepth = hierarchy.Tree.Nodes.ToDictionary(item => item.Id, item => item.Level, StringComparer.Ordinal);
        var goldDepth = DeriveGoldDepth(goldEdges);
        var levelRows = catalog.Entries.Select(entry =>
        {
            var goldId = predictedNodeToGold.GetValueOrDefault(entry.SemanticNodeId);
            var predicted = predictedDepth.GetValueOrDefault(entry.SemanticNodeId);
            var expected = goldId is not null ? goldDepth.GetValueOrDefault(goldId) : 0;
            return new { entry.SemanticNodeId, goldSemanticNodeId = goldId, predictedLevel = predicted, goldLevel = expected, exact = goldId is not null && predicted == expected };
        }).ToArray();
        return new
        {
            semanticResolution = new
            {
                predictedSemanticNodes = catalog.Entries.Count,
                goldSemanticNodes = goldMembers.Count,
                splitErrors = splitGoldNodes.Count,
                mergeErrors,
                exactMembershipMatches,
                splitGoldNodeIds = splitGoldNodes.OrderBy(item => item, StringComparer.Ordinal).ToArray(),
                evaluationAdapter = "predicted-member-occurrences-to-Gold-semantic-node; evaluation-only",
            },
            parent = new
            {
                tp,
                fp,
                fn,
                precision,
                recall,
                f1,
                predictedEdgeCount = predictedEdges.Count + unmappablePredictedEdges,
                mappedPredictedEdgeCount = predictedEdges.Count,
                unmappablePredictedEdges,
                goldEdgeCount = goldEdges.Count,
                diagnostics,
                metricsPolicy = "mapped semantic parent edges plus predicted edges with an evaluated child and unmappable parent; unresolved is not a correct edge",
            },
            level = new { exact = levelRows.Count(item => item.exact), evaluated = levelRows.Count(item => item.goldSemanticNodeId is not null), rows = levelRows },
            endToEnd = new { hierarchy.Tree.IsValid, hierarchy.CatalogWasMutated, treeErrors = hierarchy.Tree.Errors },
            gold = new { goldOpenedAfterPredictionFreeze = true, sourceSha256 = context.SourceSha256 },
        };
    }

    private static IReadOnlyDictionary<string, int> DeriveGoldDepth(IReadOnlySet<string> edges)
    {
        var parentByChild = edges.ToDictionary(edge => edge[(edge.IndexOf('>') + 1)..], edge => edge[..edge.IndexOf('>')], StringComparer.Ordinal);
        var nodes = parentByChild.Keys.Concat(parentByChild.Values).ToHashSet(StringComparer.Ordinal);
        var depth = new Dictionary<string, int>(StringComparer.Ordinal);
        int Depth(string node)
        {
            if (depth.TryGetValue(node, out var known)) return known;
            if (!parentByChild.TryGetValue(node, out var parent)) return depth[node] = 1;
            return depth[node] = Depth(parent) + 1;
        }
        foreach (var node in nodes) Depth(node);
        return depth;
    }

    private static async Task<int> BlockAsync(string output, string reason, HdsaFrozenSemanticNodeCatalog catalog, int modelCalls, int providerCalls, CancellationToken ct)
    {
        await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-hdsa-semantic-node-production-live-summary-v1",
            status = "BLOCKED",
            documentId = "DOC-0205",
            reason,
            catalogFingerprint = catalog.CatalogFingerprint,
            nodeCount = catalog.Entries.Count,
            modelCalls,
            providerCalls,
            goldReadBeforeFreeze = false,
            goldDerivedInput = false,
            productionBenchmark = true,
        }, ct);
        Console.WriteLine("HDSA_SEMANTIC_NODE_PRODUCTION_STATUS=BLOCKED");
        Console.WriteLine($"REASON={reason}");
        Console.WriteLine($"MODEL_CALLS={modelCalls}");
        Console.WriteLine($"PROVIDER_CALLS={providerCalls}");
        Console.WriteLine("GOLD_READ_BEFORE_FREEZE=0");
        return 1;
    }

    private static async Task WriteJsonAsync(string path, object value, CancellationToken ct) =>
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), ct);

    private static string Sha256Text(string value) => Sha256(Encoding.UTF8.GetBytes(value));
    private static string Sha256(string path) => Sha256(File.ReadAllBytes(path));
    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string GetGitHead(string repoRoot)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD")
            {
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            process?.WaitForExit(5000);
            return process?.ExitCode == 0 ? process.StandardOutput.ReadToEnd().Trim() : "UNKNOWN";
        }
        catch { return "UNKNOWN"; }
    }

    private sealed record Context(string SourceSha256, IReadOnlyList<AliasContext> Selected);
    private sealed record AliasContext(string Alias, string SourceId, int SourceOrdinal, string Text, string? Numbering, string? Styles, string LocalContext);
}
