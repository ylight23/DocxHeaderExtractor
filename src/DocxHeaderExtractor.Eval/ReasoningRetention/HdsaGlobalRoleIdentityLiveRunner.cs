using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// Shadow two-stage probe: semantic role/identity normalization is frozen before a separate
/// whole-outline hierarchy call. It never invokes the global decoder and never uses Gold to build
/// either request or catalog.
/// </summary>
public static class HdsaGlobalRoleIdentityLiveRunner
{
    private const string Model = "qwen/qwen3.7-flash";
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private const string DocumentId = "DOC-0205";
    private const string CatalogPath = "eval/a99-closed-loop/hdsa-semantic-node-production-live/DOC-0205/semantic-catalog.v1.json";
    private const string OutputRoot = "eval/a99-closed-loop/hdsa-global-role-identity-live/DOC-0205";
    private const string ExpectedCatalogFingerprint = "5948fb130cdf730660991a7a83ceb5aab461374a1eb86ec5f40f112b7f64480e";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private const string NormalizationPrompt = """
You are the A99 global semantic normalization reasoner. The request contains every source-backed
semantic candidate in deterministic document order. First decide what each node is doing in the
document, whether it bears the semantic outline, and whether repeated/continued source nodes are
one logical semantic identity. Do not decide parent, hierarchy, or level.

Use exact source text, document order, local context, and parser-owned evidence as evidence, not
rigid rules. DOCUMENT_FRAMING includes issuer mastheads, banners, and document framing that are
not semantic outline roots. OUTLINE_ROOT is the root of the semantic outline. OUTLINE_HEADING and
SECTION_HEADING are outline-bearing descendants. CONTENT_LABEL and NON_OUTLINE_LABEL are labels
that do not bear the semantic outline. REPEAT and CONTINUATION are descriptive role evidence;
identityRelation must be PRIMARY, REPEAT, CONTINUATION, DISTINCT, or UNRESOLVED.

Return exactly one result for every supplied node, in supplied order. Use the same identityGroup
string for nodes that are one logical semantic identity and a unique identityGroup for nodes that
must remain separate. A group with more than one node must be a genuine repeat or continuation;
do not merge merely similar text. Do not return parent, level, offsets, Gold, legacy fields, or
invent node IDs.
""";

    private const string HierarchyPrompt = """
You are the A99 whole-outline hierarchy reasoner. The request contains the frozen output of a
separate semantic normalization stage. Decide the semantic parent of every normalized node.
DOCUMENT_FRAMING nodes are not automatically semantic parents. OUTLINE_ROOT is a semantic root
when the evidence supports it. Use structuralRole, outlineBearing, source order, text, and
context as evidence, not rigid rules.

Return exactly one JSON result for every supplied node, in supplied order. parent must be ROOT,
UNRESOLVED, or an ID from this request. proposedLevel is diagnostic only; the harness derives
levels from the validated tree. Do not merge nodes, invent IDs, return offsets, or return Gold.
""";

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        var stageA = Path.Combine(output, "stage-a");
        var stageB = Path.Combine(output, "stage-b");
        Directory.CreateDirectory(stageA);
        Directory.CreateDirectory(stageB);

        var catalog = ReadCatalog(Path.Combine(repoRoot, CatalogPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!string.Equals(catalog.CatalogFingerprint, ExpectedCatalogFingerprint, StringComparison.Ordinal))
            return await BlockAsync(output, "CATALOG_FINGERPRINT_MISMATCH", 0, 0, ct);

        var stageARequest = BuildNormalizationRequest(catalog);
        var stageAJson = JsonSerializer.Serialize(stageARequest, JsonOptions);
        var stageAHash = Sha256Text(stageAJson);
        var manifest = new
        {
            schemaVersion = "a99-hdsa-global-role-identity-live-manifest-v1",
            documentId = DocumentId,
            model = Model,
            endpoint = Endpoint,
            sourceSha256 = catalog.SourceSha256,
            catalogFingerprint = catalog.CatalogFingerprint,
            catalogNodeCount = catalog.Entries.Count,
            stageARequestSha256 = stageAHash,
            stageASchemaSha256 = Sha256Text(JsonSerializer.Serialize(HdsaGlobalSemanticNormalizationContract.Schema())),
            stageBSchemaSha256 = Sha256Text(JsonSerializer.Serialize(HdsaGlobalOutlineHierarchyContract.Schema())),
            candidateGeneration = "NONE_ALL_FROZEN_CATALOG_NODES",
            goldReadBeforeFreeze = false,
            goldDerivedInput = false,
            globalDecoderUsed = false,
            modelFallback = "NONE",
            transientRequestRetries = 0,
            causalDelta = "GLOBAL_ROLE_AND_IDENTITY_NORMALIZATION_BEFORE_GLOBAL_HIERARCHY",
        };
        await WriteJsonAsync(Path.Combine(output, "manifest.v1.json"), manifest, ct);
        await WriteJsonAsync(Path.Combine(stageA, "request.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-normalization-request-v1",
            documentId = DocumentId,
            request = stageARequest,
            requestSha256 = stageAHash,
            goldReadBeforeFreeze = false,
            goldDerivedInput = false,
        }, ct);

        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
            return await BlockAsync(output, "OPENROUTER_API_KEY_MISSING", 0, 0, ct);

        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        using var lease = await A99OpenRouterLiveProviderLease.AcquireAsync(repoRoot, "hdsa-global-role-identity", DocumentId, ct);
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
        var stageAContent = await CompleteAsync(model, stageAJson, stageARequest.Nodes.Count,
            NormalizationPrompt, HdsaGlobalSemanticNormalizationContract.Schema(),
            "hdsa_global_semantic_normalization_v1", "HDSA_GLOBAL_NORMALIZATION", ct);
        var stageAResponseHash = Sha256Text(stageAContent.Content);
        await WriteJsonAsync(Path.Combine(stageA, "response.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-normalization-response-v1",
            documentId = DocumentId,
            requestSha256 = stageAHash,
            responseSha256 = stageAResponseHash,
            rawResponse = stageAContent.Content,
            provider = stageAContent.Telemetry.ProviderRoute,
            finishReason = stageAContent.Telemetry.FinishReason,
            inputTokens = stageAContent.Telemetry.ReportedInputTokens,
            reasoningTokens = stageAContent.Telemetry.ReportedReasoningTokens,
            outputTokens = stageAContent.Telemetry.ReportedOutputTokens,
            elapsedMs = stageAContent.ElapsedMs,
            modelCalls = 1,
            providerCalls = 1,
            goldReadBeforeFreeze = false,
        }, ct);

        HdsaGlobalSemanticNormalizationProposal? normalization = null;
        string? parseError = null;
        try { normalization = HdsaGlobalSemanticNormalizationContract.Parse(stageAContent.Content); }
        catch (Exception ex) when (ex is FormatException or JsonException) { parseError = ex.Message; }
        var stageAValidation = normalization is null
            ? new HdsaGlobalSemanticNormalizationValidation(false, parseError, [], [])
            : HdsaGlobalSemanticNormalizationContract.Validate(
                catalog.Entries.Select(item => item.SemanticNodeId).ToHashSet(StringComparer.Ordinal), normalization);
        await WriteJsonAsync(Path.Combine(stageA, "prediction.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-normalization-prediction-v1",
            documentId = DocumentId,
            requestSha256 = stageAHash,
            responseSha256 = stageAResponseHash,
            proposal = normalization,
            parseError,
            validation = stageAValidation,
            goldReadBeforeFreeze = false,
            goldDerivedInput = false,
        }, ct);
        var stageAPredictionPath = Path.Combine(stageA, "prediction.v1.json");
        await WriteJsonAsync(Path.Combine(stageA, "freeze.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-normalization-freeze-v1",
            documentId = DocumentId,
            catalogFingerprint = catalog.CatalogFingerprint,
            requestSha256 = stageAHash,
            responseSha256 = stageAResponseHash,
            predictionSha256 = Sha256File(stageAPredictionPath),
            model = Model,
            provider = stageAContent.Telemetry.ProviderRoute,
            finishReason = stageAContent.Telemetry.FinishReason,
            inputTokens = stageAContent.Telemetry.ReportedInputTokens,
            reasoningTokens = stageAContent.Telemetry.ReportedReasoningTokens,
            outputTokens = stageAContent.Telemetry.ReportedOutputTokens,
            elapsedMs = stageAContent.ElapsedMs,
            modelCalls = 1,
            providerCalls = 1,
            goldReadBeforeFreeze = false,
            frozenBeforeGold = true,
        }, ct);

        if (!stageAValidation.Accepted)
            return await BlockAsync(output, "STAGE_A_INVALID:" + stageAValidation.RejectionReason, model.ProviderCalls, model.ProviderCalls, ct);

        var stageAFlags = ComputeStageAFlags(catalog, normalization!);
        if (!stageAFlags.S0001Framing || !stageAFlags.S0005OutlineRoot || !stageAFlags.S0014S0015SameGroup)
        {
            await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new
            {
                schemaVersion = "a99-hdsa-global-role-identity-live-summary-v1",
                status = "STAGE_A_HYPOTHESIS_NOT_CONFIRMED",
                documentId = DocumentId,
                model = Model,
                sourceCatalogFingerprint = catalog.CatalogFingerprint,
                stageA = new
                {
                    flags = stageAFlags,
                    validation = stageAValidation,
                },
                stageB = new
                {
                    authorized = false,
                    executed = false,
                    reason = "STAGE_A_H1_OR_H2_NOT_CONFIRMED",
                },
                modelCalls = model.ProviderCalls,
                providerCalls = model.ProviderCalls,
                goldReadBeforeFreeze = false,
                predictionFrozenBeforeGold = true,
                globalDecoderUsed = false,
            }, ct);
            Console.WriteLine("HDSA_GLOBAL_ROLE_IDENTITY_STATUS=STAGE_A_HYPOTHESIS_NOT_CONFIRMED");
            Console.WriteLine($"MODEL_CALLS={model.ProviderCalls}");
            Console.WriteLine($"PROVIDER_CALLS={model.ProviderCalls}");
            Console.WriteLine("GOLD_READ_BEFORE_FREEZE=0");
            Console.WriteLine($"STAGE_A_DOCUMENT_FRAMING_S0001={stageAFlags.S0001Framing}");
            Console.WriteLine($"STAGE_A_OUTLINE_ROOT_S0005={stageAFlags.S0005OutlineRoot}");
            Console.WriteLine($"STAGE_A_S0014_S0015_SAME_GROUP={stageAFlags.S0014S0015SameGroup}");
            return 1;
        }

        var normalizedCatalog = BuildNormalizedCatalog(catalog, normalization!);
        var stageBRequest = BuildHierarchyRequest(normalizedCatalog);
        var stageBJson = JsonSerializer.Serialize(stageBRequest, JsonOptions);
        var stageBHash = Sha256Text(stageBJson);
        await WriteJsonAsync(Path.Combine(output, "normalized-catalog.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-normalized-catalog-v1",
            documentId = DocumentId,
            sourceSha256 = catalog.SourceSha256,
            sourceCatalogFingerprint = catalog.CatalogFingerprint,
            normalizedCatalogFingerprint = normalizedCatalog.Fingerprint,
            entries = normalizedCatalog.Entries,
            stageARequestSha256 = stageAHash,
            stageAResponseSha256 = stageAResponseHash,
            goldUsed = false,
        }, ct);
        await WriteJsonAsync(Path.Combine(stageB, "request.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-hierarchy-after-normalization-request-v1",
            documentId = DocumentId,
            request = stageBRequest,
            requestSha256 = stageBHash,
            normalizedCatalogFingerprint = normalizedCatalog.Fingerprint,
            stageAFreeze = "stage-a/freeze.v1.json",
            goldReadBeforeFreeze = false,
            goldDerivedInput = false,
        }, ct);

        var stageBContent = await CompleteAsync(model, stageBJson, stageBRequest.Nodes.Count,
            HierarchyPrompt, HdsaGlobalOutlineHierarchyContract.Schema(),
            "hdsa_global_hierarchy_after_normalization_v1", "HDSA_GLOBAL_HIERARCHY_AFTER_NORMALIZATION", ct);
        var stageBResponseHash = Sha256Text(stageBContent.Content);
        await WriteJsonAsync(Path.Combine(stageB, "response.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-hierarchy-after-normalization-response-v1",
            documentId = DocumentId,
            requestSha256 = stageBHash,
            responseSha256 = stageBResponseHash,
            rawResponse = stageBContent.Content,
            provider = stageBContent.Telemetry.ProviderRoute,
            finishReason = stageBContent.Telemetry.FinishReason,
            inputTokens = stageBContent.Telemetry.ReportedInputTokens,
            reasoningTokens = stageBContent.Telemetry.ReportedReasoningTokens,
            outputTokens = stageBContent.Telemetry.ReportedOutputTokens,
            elapsedMs = stageBContent.ElapsedMs,
            modelCalls = 1,
            providerCalls = 1,
            goldReadBeforeFreeze = false,
        }, ct);

        HdsaGlobalOutlineHierarchyProposal? hierarchy = null;
        string? hierarchyParseError = null;
        try { hierarchy = HdsaGlobalOutlineHierarchyContract.Parse(stageBContent.Content); }
        catch (Exception ex) when (ex is FormatException or JsonException) { hierarchyParseError = ex.Message; }
        var hierarchyValidation = hierarchy is null
            ? null
            : HdsaGlobalOutlineHierarchyContract.Validate(stageBRequest, hierarchy);
        await WriteJsonAsync(Path.Combine(stageB, "prediction.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-hierarchy-after-normalization-prediction-v1",
            documentId = DocumentId,
            requestSha256 = stageBHash,
            responseSha256 = stageBResponseHash,
            proposal = hierarchy,
            parseError = hierarchyParseError,
            validation = hierarchyValidation,
            goldReadBeforeFreeze = false,
            goldDerivedInput = false,
            globalDecoderUsed = false,
        }, ct);
        var stageBPredictionPath = Path.Combine(stageB, "prediction.v1.json");
        await WriteJsonAsync(Path.Combine(stageB, "freeze.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-hierarchy-after-normalization-freeze-v1",
            documentId = DocumentId,
            sourceSha256 = catalog.SourceSha256,
            normalizedCatalogFingerprint = normalizedCatalog.Fingerprint,
            requestSha256 = stageBHash,
            responseSha256 = stageBResponseHash,
            predictionSha256 = Sha256File(stageBPredictionPath),
            model = Model,
            provider = stageBContent.Telemetry.ProviderRoute,
            finishReason = stageBContent.Telemetry.FinishReason,
            inputTokens = stageBContent.Telemetry.ReportedInputTokens,
            reasoningTokens = stageBContent.Telemetry.ReportedReasoningTokens,
            outputTokens = stageBContent.Telemetry.ReportedOutputTokens,
            elapsedMs = stageBContent.ElapsedMs,
            modelCalls = 1,
            providerCalls = 1,
            goldReadBeforeFreeze = false,
            frozenBeforeGold = true,
        }, ct);

        var evaluation = hierarchyValidation is null
            ? null
            : EvaluateAfterFreeze(repoRoot, normalizedCatalog, stageBRequest, hierarchy!, hierarchyValidation);
        await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-role-identity-live-summary-v1",
            status = hierarchyValidation?.Accepted == true ? "COMPLETE" : "HIERARCHY_INVALID",
            documentId = DocumentId,
            model = Model,
            provider = stageBContent.Telemetry.ProviderRoute,
            sourceCatalogFingerprint = catalog.CatalogFingerprint,
            normalizedCatalogFingerprint = normalizedCatalog.Fingerprint,
            sourceNodeCount = catalog.Entries.Count,
            normalizedNodeCount = normalizedCatalog.Entries.Count,
            stageA = new
            {
                requestSha256 = stageAHash,
                responseSha256 = stageAResponseHash,
                identityGroupCount = stageAValidation.AcceptedGroups.Count,
                flags = stageAFlags,
                validation = stageAValidation,
            },
            stageB = new
            {
                requestSha256 = stageBHash,
                responseSha256 = stageBResponseHash,
                validation = hierarchyValidation,
            },
            evaluation,
            modelCalls = model.ProviderCalls,
            providerCalls = model.ProviderCalls,
            goldReadBeforeFreeze = false,
            predictionFrozenBeforeGold = true,
            globalDecoderUsed = false,
        }, ct);
        Console.WriteLine("HDSA_GLOBAL_ROLE_IDENTITY_STATUS=" + (hierarchyValidation?.Accepted == true ? "COMPLETE" : "HIERARCHY_INVALID"));
        Console.WriteLine($"MODEL_CALLS={model.ProviderCalls}");
        Console.WriteLine($"PROVIDER_CALLS={model.ProviderCalls}");
        Console.WriteLine("GOLD_READ_BEFORE_FREEZE=0");
        Console.WriteLine($"STAGE_A_DOCUMENT_FRAMING_S0001={stageAFlags.S0001Framing}");
        Console.WriteLine($"STAGE_A_OUTLINE_ROOT_S0005={stageAFlags.S0005OutlineRoot}");
        Console.WriteLine($"STAGE_A_S0014_S0015_SAME_GROUP={stageAFlags.S0014S0015SameGroup}");
        if (evaluation is not null)
        {
            Console.WriteLine($"PARENT_F1={evaluation.Parent.F1:F4}");
            Console.WriteLine($"DERIVED_LEVEL_EXACT={evaluation.DerivedLevel.Exact}/{evaluation.DerivedLevel.Evaluated}");
        }
        return hierarchyValidation?.Accepted == true ? 0 : 1;
    }

    private static async Task<(string Content, RequestPacketTelemetry Telemetry, long ElapsedMs)> CompleteAsync(
        OpenRouterCeilingReasoningModel model, string requestJson, int itemCount, string systemPrompt,
        object schema, string schemaName, string route, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await model.CompleteRawStructuredSemanticAsync(
            DocumentId, route, route, requestJson, requestJson.Length, itemCount, itemCount,
            systemPrompt, $"TASK={route}\n{requestJson}\nReturn exactly the requested JSON object.",
            schema, schemaName, ct);
        stopwatch.Stop();
        return (result.Content, result.Telemetry, stopwatch.ElapsedMilliseconds);
    }

    private static HdsaGlobalSemanticNormalizationRequest BuildNormalizationRequest(HdsaFrozenSemanticNodeCatalog catalog) =>
        new(catalog.CatalogFingerprint, catalog.Entries.OrderBy(item => item.SourceOrder).ThenBy(item => item.SemanticNodeId, StringComparer.Ordinal)
            .Select(item => new HdsaGlobalSemanticNormalizationInputNode(
                item.SemanticNodeId, item.MemberOccurrenceIds, item.CanonicalText, item.SourceOrder,
                "source-order=" + item.SourceOrder,
                "parser-owned style/layout evidence unavailable in frozen catalog",
                "single source-backed semantic catalog")).ToArray(), false);

    private static HdsaGlobalSemanticNormalizedCatalog BuildNormalizedCatalog(
        HdsaFrozenSemanticNodeCatalog source, HdsaGlobalSemanticNormalizationProposal proposal)
    {
        var sourceById = source.Entries.ToDictionary(item => item.SemanticNodeId, StringComparer.Ordinal);
        var entries = proposal.Nodes.GroupBy(item => item.IdentityGroup, StringComparer.Ordinal)
            .OrderBy(group => group.Min(item => sourceById[item.NodeId].SourceOrder))
            .ThenBy(group => string.Join("|", group.Select(item => item.NodeId).Order(StringComparer.Ordinal)), StringComparer.Ordinal)
            .Select(group =>
            {
                var members = group.Select(item => sourceById[item.NodeId]).OrderBy(item => item.SourceOrder)
                    .ThenBy(item => item.SemanticNodeId, StringComparer.Ordinal).ToArray();
                var memberIds = members.SelectMany(item => item.MemberOccurrenceIds).ToArray();
                var id = "GN-" + Sha256Text(string.Join("|", members.Select(item => item.SemanticNodeId).Order(StringComparer.Ordinal))).Substring(0, 16);
                var first = group.OrderBy(item => sourceById[item.NodeId].SourceOrder).First();
                var role = group.Select(item => item.StructuralRole).Distinct(StringComparer.Ordinal).Count() == 1
                    ? first.StructuralRole : "UNRESOLVED";
                return new HdsaGlobalSemanticNormalizedCatalogEntry(
                    id, members.Select(item => item.SemanticNodeId).ToArray(), memberIds,
                    string.Join(" / ", members.Select(item => item.CanonicalText)),
                    members.Min(item => item.SourceOrder), role, group.All(item => item.OutlineBearing));
            }).ToArray();
        var fingerprint = Sha256Text(JsonSerializer.Serialize(new
        {
            sourceSha256 = source.SourceSha256,
            entries = entries.Select(item => new
            {
                item.NormalizedNodeId, item.MemberSourceNodeIds, item.MemberOccurrenceIds,
                item.CanonicalText, item.SourceOrder, item.StructuralRole, item.OutlineBearing,
            }),
            goldUsed = false,
        }));
        return new(source.SourceSha256, source.CatalogFingerprint, entries, fingerprint);
    }

    private static HdsaGlobalOutlineHierarchyRequest BuildHierarchyRequest(HdsaGlobalSemanticNormalizedCatalog catalog) =>
        new(catalog.Fingerprint, catalog.Entries.Select(item => new HdsaGlobalOutlineNodeContext(
            item.NormalizedNodeId, item.MemberOccurrenceIds, item.CanonicalText, item.SourceOrder,
            "structuralRole=" + item.StructuralRole + "; outlineBearing=" + item.OutlineBearing,
            "parser-owned style/layout evidence unavailable in frozen catalog",
            "members=" + string.Join(",", item.MemberSourceNodeIds))).ToArray(), [], false);

    private static StageAFlags ComputeStageAFlags(HdsaFrozenSemanticNodeCatalog catalog,
        HdsaGlobalSemanticNormalizationProposal proposal)
    {
        var bySourceId = catalog.Entries.ToDictionary(item => item.SemanticNodeId, StringComparer.Ordinal);
        var s0001 = bySourceId.FirstOrDefault(item => item.Value.MemberOccurrenceIds.Contains("S0001", StringComparer.Ordinal)).Key;
        var s0005 = bySourceId.FirstOrDefault(item => item.Value.MemberOccurrenceIds.Contains("S0005", StringComparer.Ordinal)).Key;
        var a = proposal.Nodes.ToDictionary(item => item.NodeId, StringComparer.Ordinal);
        var s0014 = bySourceId.FirstOrDefault(item => item.Value.MemberOccurrenceIds.Contains("S0014", StringComparer.Ordinal)).Key;
        var s0015 = bySourceId.FirstOrDefault(item => item.Value.MemberOccurrenceIds.Contains("S0015", StringComparer.Ordinal)).Key;
        return new(
            s0001 is not null && a.TryGetValue(s0001, out var p1) && p1.StructuralRole == "DOCUMENT_FRAMING",
            s0005 is not null && a.TryGetValue(s0005, out var p5) && p5.StructuralRole == "OUTLINE_ROOT",
            s0014 is not null && s0015 is not null && a.TryGetValue(s0014, out var p14) &&
            a.TryGetValue(s0015, out var p15) && p14.IdentityGroup == p15.IdentityGroup &&
            p14.IdentityRelation is "CONTINUATION" or "REPEAT" &&
            p15.IdentityRelation is "CONTINUATION" or "REPEAT");
    }

    private static GlobalRoleIdentityEvaluation EvaluateAfterFreeze(string repoRoot,
        HdsaGlobalSemanticNormalizedCatalog catalog, HdsaGlobalOutlineHierarchyRequest request,
        HdsaGlobalOutlineHierarchyProposal proposal, HdsaGlobalOutlineValidation validation)
    {
        var goldPath = Path.Combine(repoRoot, "eval/a99-closed-loop/hdsa-parent-relation-live/DOC-0205/structural-gold.v1.json".Replace('/', Path.DirectorySeparatorChar));
        using var gold = JsonDocument.Parse(File.ReadAllText(goldPath));
        var root = gold.RootElement;
        if (root.GetProperty("sourceSha256").GetString() != catalog.SourceSha256)
            throw new InvalidDataException("GLOBAL_ROLE_IDENTITY_GOLD_SOURCE_MISMATCH");
        var aliases = root.GetProperty("occurrences").EnumerateArray()
            .Where(item => item.GetProperty("reviewStatus").GetString() == "RESOLVED")
            .ToDictionary(item => item.GetProperty("sourceAlias").GetString()!, item => item.GetProperty("semanticNodeId").GetString()!, StringComparer.Ordinal);
        var goldEdges = root.GetProperty("tree").GetProperty("parentOf").EnumerateArray()
            .Select(item => item.GetProperty("parent").GetString() + ">" + item.GetProperty("child").GetString()).ToHashSet(StringComparer.Ordinal);
        var nodeMap = catalog.Entries.ToDictionary(item => item.NormalizedNodeId, item =>
        {
            var ids = item.MemberOccurrenceIds.Where(aliases.ContainsKey).Select(item => aliases[item]).Distinct(StringComparer.Ordinal).ToArray();
            return ids.Length == 1 ? ids[0] : null;
        }, StringComparer.Ordinal);
        var predicted = validation.ParentRelations.Where(item => item.Relation == HdsaRelationType.ParentOf)
            .Select(item => (parent: nodeMap.GetValueOrDefault(item.From), child: nodeMap.GetValueOrDefault(item.To)))
            .Where(item => item.child is not null).ToArray();
        var mapped = predicted.Where(item => item.parent is not null).Select(item => item.parent + ">" + item.child).ToHashSet(StringComparer.Ordinal);
        var unmappable = predicted.Count(item => item.parent is null);
        var tp = mapped.Intersect(goldEdges, StringComparer.Ordinal).Count();
        var fp = mapped.Except(goldEdges, StringComparer.Ordinal).Count() + unmappable;
        var fn = goldEdges.Except(mapped, StringComparer.Ordinal).Count();
        var p = tp + fp == 0 ? 0 : (double)tp / (tp + fp);
        var r = tp + fn == 0 ? 0 : (double)tp / (tp + fn);
        var f1 = p + r == 0 ? 0 : 2 * p * r / (p + r);
        var goldDepth = GoldDepth(goldEdges);
        var levelRows = catalog.Entries.Select(item =>
        {
            var goldId = nodeMap.GetValueOrDefault(item.NormalizedNodeId);
            var predictedLevel = validation.Tree.Nodes.SingleOrDefault(node => node.Id == item.NormalizedNodeId)?.Level;
            return new LevelRow(item.NormalizedNodeId, goldId, predictedLevel, goldId is not null && goldDepth.TryGetValue(goldId, out var level) ? level : 0,
                goldId is not null && predictedLevel == goldDepth[goldId]);
        }).Where(item => item.GoldSemanticNodeId is not null).ToArray();
        var s0005 = catalog.Entries.FirstOrDefault(item => item.MemberOccurrenceIds.Contains("S0005", StringComparer.Ordinal));
        var s0005Proposal = proposal.Nodes.SingleOrDefault(item => item.NodeId == s0005?.NormalizedNodeId);
        return new(new ParentMetric(tp, fp, fn, p, r, f1, goldEdges.Count, mapped.Count, unmappable),
            new LevelMetric(levelRows.Count(item => item.Exact), levelRows.Length, levelRows),
            s0005Proposal?.Parent ?? "UNPARSED", validation.Tree.IsValid,
            catalog.Entries.Count, aliases.Count);
    }

    private static IReadOnlyDictionary<string, int> GoldDepth(IReadOnlySet<string> edges)
    {
        var parent = edges.ToDictionary(edge => edge[(edge.IndexOf('>') + 1)..], edge => edge[..edge.IndexOf('>')], StringComparer.Ordinal);
        var depth = new Dictionary<string, int>(StringComparer.Ordinal);
        int D(string id) => depth.TryGetValue(id, out var value) ? value : depth[id] = parent.TryGetValue(id, out var p) ? D(p) + 1 : 1;
        foreach (var id in parent.Keys.Concat(parent.Values)) _ = D(id);
        return depth;
    }

    private static HdsaFrozenSemanticNodeCatalog ReadCatalog(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("GLOBAL_ROLE_IDENTITY_CATALOG_MISSING", path);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var entries = root.GetProperty("entries").EnumerateArray().Select(item => new
        {
            Id = item.GetProperty("semanticNodeId").GetString()!,
            Aliases = item.GetProperty("memberOccurrenceIds").EnumerateArray().Select(value => value.GetString()!).ToArray(),
            Text = item.GetProperty("canonicalText").GetString()!,
            Order = item.GetProperty("sourceOrder").GetInt32(),
        }).ToArray();
        var input = new HdsaSemanticNodeResolutionInput(
            root.GetProperty("sourceSha256").GetString()!,
            root.GetProperty("preprocessingSnapshotHash").GetString()!,
            entries.SelectMany(item => item.Aliases.Select(alias => new HdsaSemanticNodeSourceOccurrence(alias, item.Order, item.Text))).ToArray(), false);
        var predictions = entries.Select(item => new HdsaSemanticNodePrediction(item.Id, item.Aliases, item.Text, "FROZEN_SAFE_CATALOG", root.GetProperty("resolverVersion").GetString()!, false)).ToArray();
        var result = new HdsaSemanticNodeResolutionV3Result(input.SourceSha256, input.PreprocessingSnapshotHash, predictions, [], [], [], root.GetProperty("resolverVersion").GetString()!, false);
        var catalog = HdsaSemanticNodeCatalogBuilder.Build(input, result);
        if (catalog.CatalogFingerprint != root.GetProperty("catalogFingerprint").GetString())
            throw new InvalidDataException("GLOBAL_ROLE_IDENTITY_CATALOG_RECONSTRUCTION_MISMATCH");
        return catalog;
    }

    private static async Task<int> BlockAsync(string output, string reason, int modelCalls, int providerCalls, CancellationToken ct)
    {
        await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-role-identity-live-summary-v1",
            status = "BLOCKED",
            reason,
            modelCalls,
            providerCalls,
            goldReadBeforeFreeze = false,
        }, ct);
        Console.WriteLine("HDSA_GLOBAL_ROLE_IDENTITY_STATUS=BLOCKED");
        Console.WriteLine("REASON=" + reason);
        Console.WriteLine($"MODEL_CALLS={modelCalls}");
        Console.WriteLine($"PROVIDER_CALLS={providerCalls}");
        return 1;
    }

    private static string Sha256Text(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static string Sha256File(string path) => Sha256Text(File.ReadAllText(path));
    private static async Task WriteJsonAsync(string path, object value, CancellationToken ct) =>
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, ct);

    private sealed record HdsaGlobalSemanticNormalizationRequest(
        [property: JsonPropertyName("catalogFingerprint")] string CatalogFingerprint,
        [property: JsonPropertyName("nodes")] IReadOnlyList<HdsaGlobalSemanticNormalizationInputNode> Nodes,
        [property: JsonPropertyName("goldDerivedInput")] bool GoldDerivedInput);

    private sealed record HdsaGlobalSemanticNormalizationInputNode(
        [property: JsonPropertyName("nodeId")] string NodeId,
        [property: JsonPropertyName("memberOccurrenceIds")] IReadOnlyList<string> MemberOccurrenceIds,
        [property: JsonPropertyName("canonicalText")] string CanonicalText,
        [property: JsonPropertyName("documentOrder")] int DocumentOrder,
        [property: JsonPropertyName("localContext")] string LocalContext,
        [property: JsonPropertyName("styleLayoutEvidence")] string StyleLayoutEvidence,
        [property: JsonPropertyName("containerEvidence")] string ContainerEvidence);

    private sealed record HdsaGlobalSemanticNormalizedCatalog(
        string SourceSha256, string SourceCatalogFingerprint,
        IReadOnlyList<HdsaGlobalSemanticNormalizedCatalogEntry> Entries, string Fingerprint);

    private sealed record HdsaGlobalSemanticNormalizedCatalogEntry(
        string NormalizedNodeId, IReadOnlyList<string> MemberSourceNodeIds,
        IReadOnlyList<string> MemberOccurrenceIds, string CanonicalText, int SourceOrder,
        string StructuralRole, bool OutlineBearing);

    private sealed record StageAFlags(bool S0001Framing, bool S0005OutlineRoot, bool S0014S0015SameGroup);
    private sealed record GlobalRoleIdentityEvaluation(ParentMetric Parent, LevelMetric DerivedLevel, string S0005,
        bool TreeValid, int NormalizedNodeCount, int GoldOccurrenceCount);
    private sealed record ParentMetric(int TP, int FP, int FN, double Precision, double Recall, double F1,
        int GoldEdgeCount, int MappedPredictedEdgeCount, int UnmappablePredictedEdges);
    private sealed record LevelMetric(int Exact, int Evaluated, IReadOnlyList<LevelRow> Rows);
    private sealed record LevelRow(string SemanticNodeId, string? GoldSemanticNodeId, int? PredictedLevel, int GoldLevel, bool Exact);
}
