using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// Production-shaped DEV benchmark using identity-inference v4 before parent reasoning. It is
/// deliberately a new artifact family; the historical v3 benchmark is immutable.
/// </summary>
public static class HdsaSemanticNodeProductionV4BenchmarkRunner
{
    private const string Model = "qwen/qwen3.7-flash";
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private const string DocumentId = "DOC-0205";
    private const string SourcePath = "eval/a99-closed-loop/source-fidelity-audit/DOC-0205/converted-docx/025_ND_47-2020_Chia_se_du_lieu_so.docx";
    private const string SeedFreezePath = "eval/a99-closed-loop/source-fidelity-whole-alias-live/DOC-0205/r2/whole-alias/freeze.v1.json";
    private const string GoldPath = "eval/a99-closed-loop/hdsa-parent-relation-live/DOC-0205/structural-gold.v1.json";
    private const string OutputRoot = "eval/a99-closed-loop/hdsa-semantic-node-production-v4-live/DOC-0205";
    private const int PrimaryWindow = 8;
    private static readonly string[] SeedAliases =
        ["S0001", "S0005", "S0014", "S0015", "S0016", "S0019", "S0021", "S0033", "S0035", "S0044", "S0052", "S0060"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private const string IdentityPrompt = """
You are the A99 semantic identity relation reasoner. Decide only the relation between the two
source-owned occurrences. Use exact text, normalized text, document order, local context,
parser-owned style/layout evidence, and source boundaries. Do not decide parent, level, or a
semantic node ID and do not merge occurrences yourself. Return exactly the requested JSON.
Choose SAME_SEMANTIC_REPEAT for the same heading repeated, CONTINUATION_OF when the right
occurrence continues the left logical heading, DISTINCT for separate headings, and UNRESOLVED
when evidence is insufficient. Never invent IDs or fields.
""";

    private const string ParentPrompt = """
You are the A99 semantic-node parent reasoner. Decide only the immediate semantic parent of the
child node. ROOT, SELECT_PARENT, and UNRESOLVED are peer outcomes. ROOT means no semantic parent
in this scope; it is not a fallback for a difficult candidate. Select only a source-backed parent
from the authoritative universe. Use source order, canonical text, numbering, style/layout and
context as evidence, not as deterministic rules. Return exactly the requested JSON. Never return
level, depth, offsets, Gold, legacy fields, or an unknown ID.
""";

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(output);
        var sourcePath = Path.Combine(repoRoot, SourcePath.Replace('/', Path.DirectorySeparatorChar));
        var context = LoadContext(repoRoot, sourcePath);
        var identityInput = BuildInput(context);
        var pairs = HdsaSemanticNodeResolverV4.DiscoverPairs(identityInput);
        var identityRequests = pairs.ToDictionary(pair => pair.PairId,
            pair => new HdsaSemanticIdentityInferenceRequest(identityInput.SourceSha256, identityInput.PreprocessingSnapshotHash, pair, false),
            StringComparer.Ordinal);
        var identityBodies = identityRequests.ToDictionary(item => item.Key,
            item => HdsaSemanticNodeResolverV4.SerializeRequest(item.Value), StringComparer.Ordinal);
        var identityManifest = HdsaLiveRunManifestGuard.Prepare(
            "hdsa-production-v4-doc0205-identity",
            identityInput.SourceSha256,
            "PRE_IDENTITY_SCOPE:" + Sha256Text(JsonSerializer.Serialize(context.Selected.Select(item => item.Alias))),
            HdsaSemanticNodeResolverV4.Version,
            "hdsa_semantic_identity_v4",
            identityBodies);
        await WriteJsonAsync(Path.Combine(output, "identity-manifest.v1.json"), identityManifest, ct);
        var identityVerification = HdsaLiveRunManifestGuard.Verify(identityManifest,
            identityInput.SourceSha256, identityManifest.CatalogFingerprint,
            HdsaSemanticNodeResolverV4.Version, "hdsa_semantic_identity_v4", identityBodies);
        if (!identityVerification.Accepted) return await AbortAsync(output, "IDENTITY_MANIFEST_REJECTED:" + identityVerification.RejectionReason, 0, 0, ct);

        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) return await AbortAsync(output, "OPENROUTER_API_KEY_MISSING", 0, 0, ct);
        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = Timeout.InfiniteTimeSpan };
        using var lease = await A99OpenRouterLiveProviderLease.AcquireAsync(repoRoot, "hdsa-production-v4", DocumentId, ct);
        var options = new RemoteInferenceOptions
        {
            Endpoint = new Uri(Endpoint), Model = Model, ApiKey = key, ContextSize = 1_000_000,
            MaxOutputTokens = 8_000, RequestTimeoutSeconds = 600, TransientRequestRetries = 0,
            MaxParallelRequests = 1, SendChatTemplateKwargs = false, OpenRouterAllowNonZdrPublicBenchmark = true,
        };
        var capability = (await OpenRouterModelCapabilityResolver.ResolveAsync(options, http, ct)).Capability;
        if (capability is null || !string.Equals(capability.ModelId, Model, StringComparison.Ordinal) ||
            !capability.ReasoningSupported || !capability.StructuredOutputSupported)
            return await AbortAsync(output, "MODEL_CAPABILITY_MISMATCH", 0, 0, ct);

        using var model = new OpenRouterCeilingReasoningModel(options, capability, http);
        var observations = new List<HdsaSemanticIdentityInferenceObservation>();
        foreach (var request in identityRequests.OrderBy(item => item.Value.Pair.Left.DocumentOrder).ThenBy(item => item.Value.Pair.Right.DocumentOrder).ThenBy(item => item.Key, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var dir = Path.Combine(output, "identity", request.Key);
            Directory.CreateDirectory(dir);
            var body = identityBodies[request.Key];
            var requestHash = HdsaSemanticNodeResolverV4.RequestHash(request.Value);
            var predictionPath = Path.Combine(dir, "prediction.v1.json");
            var freezePath = Path.Combine(dir, "freeze.v1.json");
            string content;
            RequestPacketTelemetry telemetry;
            var sw = Stopwatch.StartNew();
            if (File.Exists(predictionPath) || File.Exists(freezePath))
            {
                if (!File.Exists(predictionPath) || !File.Exists(freezePath)) throw new InvalidDataException("PARTIAL_V4_IDENTITY_FREEZE:" + request.Key);
                using var prior = JsonDocument.Parse(await File.ReadAllTextAsync(predictionPath, ct));
                content = prior.RootElement.GetProperty("rawResponse").GetString()!;
                var parsed = HdsaSemanticNodeResolverV4.Parse(content);
                var priorObservation = new HdsaSemanticIdentityInferenceObservation(request.Value, parsed, requestHash,
                    Sha256Text(content), "MODEL", Model, "FROZEN", false, false);
                if (!HdsaSemanticNodeResolverV4.Validate(request.Value, priorObservation).Accepted)
                    throw new InvalidDataException("FROZEN_V4_IDENTITY_REJECTED:" + request.Key);
                observations.Add(priorObservation);
                continue;
            }
            (content, telemetry) = await model.CompleteRawStructuredSemanticAsync(
                DocumentId, "HDSA_SEMANTIC_IDENTITY_V4", "hdsa-identity-v4:" + request.Key,
                body, body.Length, 2, 2, IdentityPrompt,
                "TASK=A99_SEMANTIC_IDENTITY_INFERENCE_V4\n" + body + "\nReturn exactly the requested JSON object.",
                HdsaSemanticNodeResolverV4.Schema(), "hdsa_semantic_identity_v4", ct);
            var parsedResponse = HdsaSemanticNodeResolverV4.Parse(content);
            var observation = new HdsaSemanticIdentityInferenceObservation(request.Value, parsedResponse, requestHash,
                Sha256Text(content), "MODEL", Model, telemetry.ProviderRoute, false, false);
            var validation = HdsaSemanticNodeResolverV4.Validate(request.Value, observation);
            if (!validation.Accepted) throw new InvalidDataException("V4_IDENTITY_RESPONSE_REJECTED:" + request.Key + ":" + validation.RejectionReason);
            observations.Add(observation);
            await WriteJsonAsync(predictionPath, new { schemaVersion = "a99-hdsa-v4-identity-prediction-v1", documentId = DocumentId, request = request.Value, requestSha256 = requestHash, rawResponse = content, response = parsedResponse, validation, goldReadBeforeFreeze = false }, ct);
            await WriteJsonAsync(freezePath, new { schemaVersion = "a99-hdsa-v4-identity-freeze-v1", documentId = DocumentId, pairId = request.Key, requestSha256 = requestHash, predictionSha256 = Sha256File(predictionPath), responseSha256 = Sha256Text(content), provider = telemetry.ProviderRoute, finishReason = telemetry.FinishReason, inputTokens = telemetry.ReportedInputTokens, reasoningTokens = telemetry.ReportedReasoningTokens, outputTokens = telemetry.ReportedOutputTokens, elapsedMs = sw.ElapsedMilliseconds, goldReadBeforeFreeze = false, frozenBeforeGold = true }, ct);
        }

        var identityResult = HdsaSemanticNodeResolverV4.Resolve(identityInput, observations);
        if (!identityResult.IsValid)
        {
            await WriteJsonAsync(Path.Combine(output, "identity-result.v1.json"), identityResult, ct);
            return await AbortAsync(output, "IDENTITY_RESOLUTION_INVALID:" + string.Join(',', identityResult.Errors), observations.Count, model.ProviderCalls, ct);
        }
        var catalog = HdsaSemanticNodeCatalogBuilder.Build(identityInput, identityResult);
        var catalogPath = Path.Combine(output, "semantic-catalog.v1.json");
        await WriteJsonAsync(catalogPath, new { schemaVersion = "a99-hdsa-v4-semantic-catalog-v1", documentId = DocumentId, catalog, goldUsed = false, productionBenchmark = true }, ct);
        await WriteJsonAsync(Path.Combine(output, "semantic-catalog-freeze.v1.json"), new { schemaVersion = "a99-hdsa-v4-semantic-catalog-freeze-v1", catalogFingerprint = catalog.CatalogFingerprint, catalogSha256 = Sha256File(catalogPath), nodeCount = catalog.Entries.Count, goldReadBeforeFreeze = false, immutableBeforeParentReasoning = true }, ct);

        var parentRequests = catalog.Entries.ToDictionary(entry => entry.SemanticNodeId,
            entry => HdsaSemanticNodeParentReasoningContract.CreateRequest(catalog, entry.SemanticNodeId, PrimaryWindow), StringComparer.Ordinal);
        var parentBodies = parentRequests.ToDictionary(item => item.Key, item => JsonSerializer.Serialize(item.Value, JsonOptions), StringComparer.Ordinal);
        var parentManifest = HdsaLiveRunManifestGuard.Prepare("hdsa-production-v4-doc0205-parent", identityInput.SourceSha256,
            catalog.CatalogFingerprint, catalog.ResolverVersion, "hdsa_semantic_node_parent_v2", parentBodies);
        await WriteJsonAsync(Path.Combine(output, "parent-manifest.v1.json"), parentManifest, ct);
        var parentVerification = HdsaLiveRunManifestGuard.Verify(parentManifest, identityInput.SourceSha256,
            catalog.CatalogFingerprint, catalog.ResolverVersion, "hdsa_semantic_node_parent_v2", parentBodies);
        if (!parentVerification.Accepted) return await AbortAsync(output, "PARENT_MANIFEST_REJECTED:" + parentVerification.RejectionReason, observations.Count, model.ProviderCalls, ct);

        var decisions = new List<HdsaSemanticNodeParentDecision>();
        var validations = new List<HdsaSemanticNodeParentDecisionValidation>();
        foreach (var item in parentRequests.OrderBy(item => catalog.Entries.Single(entry => entry.SemanticNodeId == item.Key).SourceOrder))
        {
            var dir = Path.Combine(output, "parent", item.Key);
            Directory.CreateDirectory(dir);
            var body = parentBodies[item.Key];
            var requestHash = Sha256Text(body);
            string content;
            RequestPacketTelemetry telemetry;
            var sw = Stopwatch.StartNew();
            var predictionPath = Path.Combine(dir, "prediction.v1.json");
            var freezePath = Path.Combine(dir, "freeze.v1.json");
            if (File.Exists(predictionPath) || File.Exists(freezePath))
            {
                if (!File.Exists(predictionPath) || !File.Exists(freezePath)) throw new InvalidDataException("PARTIAL_V4_PARENT_FREEZE:" + item.Key);
                using var prior = JsonDocument.Parse(await File.ReadAllTextAsync(predictionPath, ct));
                content = prior.RootElement.GetProperty("rawResponse").GetString()!;
            }
            else
            {
                (content, telemetry) = await model.CompleteRawStructuredSemanticAsync(DocumentId, "HDSA_SEMANTIC_NODE_PARENT_V2", "hdsa-v4-parent:" + item.Key, body, body.Length, catalog.Entries.Count, item.Value.AuthoritativeParentUniverse.Count + 1, ParentPrompt, "TASK=A99_SEMANTIC_NODE_PARENT_V2\n" + body + "\nReturn exactly the requested JSON object.", HdsaSemanticNodeParentReasoningContract.Schema(), "hdsa_semantic_node_parent_v2", ct);
                var parsed = HdsaSemanticNodeParentReasoningContract.Parse(content);
                var validation = HdsaSemanticNodeParentReasoningContract.Validate(item.Value, parsed, catalog);
                if (!validation.Accepted) throw new InvalidDataException("V4_PARENT_RESPONSE_REJECTED:" + item.Key + ":" + validation.RejectionReason);
                await WriteJsonAsync(predictionPath, new { schemaVersion = "a99-hdsa-v4-parent-prediction-v1", documentId = DocumentId, request = item.Value, requestSha256 = requestHash, rawResponse = content, decision = parsed, validation, goldReadBeforeFreeze = false }, ct);
                await WriteJsonAsync(freezePath, new { schemaVersion = "a99-hdsa-v4-parent-freeze-v1", documentId = DocumentId, childSemanticNodeId = item.Key, catalogFingerprint = catalog.CatalogFingerprint, requestSha256 = requestHash, predictionSha256 = Sha256File(predictionPath), responseSha256 = Sha256Text(content), provider = telemetry.ProviderRoute, finishReason = telemetry.FinishReason, inputTokens = telemetry.ReportedInputTokens, reasoningTokens = telemetry.ReportedReasoningTokens, outputTokens = telemetry.ReportedOutputTokens, elapsedMs = sw.ElapsedMilliseconds, goldReadBeforeFreeze = false, frozenBeforeGold = true }, ct);
            }
            var decision = HdsaSemanticNodeParentReasoningContract.Parse(content);
            var validated = HdsaSemanticNodeParentReasoningContract.Validate(item.Value, decision, catalog);
            if (!validated.Accepted) throw new InvalidDataException("FROZEN_V4_PARENT_REJECTED:" + item.Key + ":" + validated.RejectionReason);
            decisions.Add(decision); validations.Add(validated);
        }

        var byChild = decisions.ToDictionary(item => item.ChildSemanticNodeId, StringComparer.Ordinal);
        var hierarchy = HdsaSemanticHierarchyPipeline.Run(catalog, request => byChild[request.ChildSemanticNodeId]);
        var predictionPathFinal = Path.Combine(output, "prediction-complete.v1.json");
        await WriteJsonAsync(predictionPathFinal, new { schemaVersion = "a99-hdsa-v4-production-prediction-v1", documentId = DocumentId, catalogFingerprint = catalog.CatalogFingerprint, identityResult, parentRequests = parentRequests.Values, decisions, validations, hierarchy.Tree, hierarchy.GraphValidation, goldReadBeforeFreeze = false, goldDerivedInput = false, legacyHierarchyConsumed = false }, ct);
        var freezePathFinal = Path.Combine(output, "prediction-freeze.v1.json");
        await WriteJsonAsync(freezePathFinal, new { schemaVersion = "a99-hdsa-v4-production-freeze-v1", documentId = DocumentId, catalogFingerprint = catalog.CatalogFingerprint, predictionSha256 = Sha256File(predictionPathFinal), identityPairCount = pairs.Count, parentRequestCount = parentRequests.Count, goldReadBeforeFreeze = false, frozenBeforeGold = true }, ct);
        var evaluation = EvaluateAfterFreeze(repoRoot, context, catalog, hierarchy, freezePathFinal);
        await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new { schemaVersion = "a99-hdsa-v4-production-summary-v1", status = "COMPLETE", documentId = DocumentId, sourceSha256 = context.SourceSha256, resolver = catalog.ResolverVersion, identityPairs = pairs.Count, semanticNodes = catalog.Entries.Count, decisions, treeValid = hierarchy.Tree.IsValid, maxDepth = hierarchy.Tree.Nodes.Count == 0 ? 0 : hierarchy.Tree.Nodes.Max(item => item.Level), evaluation, modelCalls = model.ProviderCalls, providerCalls = model.ProviderCalls, goldReadBeforeFreeze = false, goldDerivedInput = false, legacyHierarchyConsumed = false, levelSource = "HDSA_TREE_DEPTH" }, ct);
        Console.WriteLine("HDSA_V4_PRODUCTION_STATUS=COMPLETE");
        Console.WriteLine($"MODEL_CALLS={model.ProviderCalls}");
        Console.WriteLine($"PROVIDER_CALLS={model.ProviderCalls}");
        Console.WriteLine("GOLD_READ_BEFORE_FREEZE=0");
        Console.WriteLine($"SUMMARY={Path.Combine(OutputRoot, "summary.v1.json")}");
        return 0;
    }

    private static object EvaluateAfterFreeze(string repoRoot, Context context, HdsaFrozenSemanticNodeCatalog catalog, HdsaSemanticHierarchyRunResult hierarchy, string freezePath)
    {
        using var freeze = JsonDocument.Parse(File.ReadAllText(freezePath));
        if (freeze.RootElement.GetProperty("goldReadBeforeFreeze").GetBoolean()) throw new InvalidDataException("GOLD_OPEN_BEFORE_V4_FREEZE");
        var goldPath = Path.Combine(repoRoot, GoldPath.Replace('/', Path.DirectorySeparatorChar));
        using var gold = JsonDocument.Parse(File.ReadAllText(goldPath));
        var root = gold.RootElement;
        if (!string.Equals(root.GetProperty("sourceSha256").GetString(), context.SourceSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("V4_GOLD_SOURCE_MISMATCH");
        var aliases = root.GetProperty("occurrences").EnumerateArray().Where(item => item.GetProperty("reviewStatus").GetString() == "RESOLVED").ToDictionary(item => item.GetProperty("sourceAlias").GetString()!, item => item.GetProperty("semanticNodeId").GetString()!, StringComparer.Ordinal);
        var goldMembers = aliases.GroupBy(item => item.Value, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Select(item => item.Key).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var nodeMap = catalog.Entries.ToDictionary(entry => entry.SemanticNodeId, entry =>
        {
            var ids = entry.MemberOccurrenceIds.Where(aliases.ContainsKey).Select(alias => aliases[alias]).Distinct(StringComparer.Ordinal).ToArray();
            return ids.Length == 1 ? ids[0] : null;
        }, StringComparer.Ordinal);
        var exactMembership = catalog.Entries.Count(entry => nodeMap[entry.SemanticNodeId] is { } goldId && goldMembers[goldId].SetEquals(entry.MemberOccurrenceIds.Where(aliases.ContainsKey)));
        var goldEdges = root.GetProperty("tree").GetProperty("parentOf").EnumerateArray().Select(item => item.GetProperty("parent").GetString() + ">" + item.GetProperty("child").GetString()).ToHashSet(StringComparer.Ordinal);
        var predictedEdges = hierarchy.GraphValidation.AcceptedRelations.Where(item => item.Relation == HdsaRelationType.ParentOf).Select(item => (parent: nodeMap.GetValueOrDefault(item.From), child: nodeMap.GetValueOrDefault(item.To))).Where(item => item.parent is not null && item.child is not null).Select(item => item.parent + ">" + item.child).ToHashSet(StringComparer.Ordinal);
        var tp = predictedEdges.Intersect(goldEdges, StringComparer.Ordinal).Count();
        var fp = predictedEdges.Except(goldEdges, StringComparer.Ordinal).Count();
        var fn = goldEdges.Except(predictedEdges, StringComparer.Ordinal).Count();
        var p = tp + fp == 0 ? 0 : (double)tp / (tp + fp);
        var r = tp + fn == 0 ? 0 : (double)tp / (tp + fn);
        var f1 = p + r == 0 ? 0 : 2 * p * r / (p + r);
        var predDepth = hierarchy.Tree.Nodes.ToDictionary(item => item.Id, item => item.Level, StringComparer.Ordinal);
        var goldDepth = GoldDepth(goldEdges);
        var levelRows = catalog.Entries.Where(entry => nodeMap[entry.SemanticNodeId] is not null).Select(entry => new { semanticNodeId = entry.SemanticNodeId, predictedLevel = predDepth.GetValueOrDefault(entry.SemanticNodeId), goldLevel = goldDepth[nodeMap[entry.SemanticNodeId]!], exact = predDepth.GetValueOrDefault(entry.SemanticNodeId) == goldDepth[nodeMap[entry.SemanticNodeId]!] }).ToArray();
        return new { goldOpenedAfterPredictionFreeze = true, semanticResolution = new { predictedSemanticNodes = catalog.Entries.Count, goldSemanticNodes = goldMembers.Count, exactMembershipMatches = exactMembership }, parent = new { tp, fp, fn, precision = p, recall = r, f1, goldEdgeCount = goldEdges.Count }, level = new { exact = levelRows.Count(item => item.exact), evaluated = levelRows.Length, accuracy = levelRows.Length == 0 ? 0 : (double)levelRows.Count(item => item.exact) / levelRows.Length, rows = levelRows }, tree = new { hierarchy.Tree.IsValid, hierarchy.CatalogWasMutated } };
    }

    private static IReadOnlyDictionary<string, int> GoldDepth(IReadOnlySet<string> edges)
    {
        var parent = edges.ToDictionary(edge => edge[(edge.IndexOf('>') + 1)..], edge => edge[..edge.IndexOf('>')], StringComparer.Ordinal);
        var all = parent.Keys.Concat(parent.Values).ToHashSet(StringComparer.Ordinal);
        var depth = new Dictionary<string, int>(StringComparer.Ordinal);
        int D(string id) => depth.TryGetValue(id, out var d) ? d : depth[id] = parent.TryGetValue(id, out var p) ? D(p) + 1 : 1;
        foreach (var id in all) _ = D(id);
        return depth;
    }

    private static Context LoadContext(string repoRoot, string sourcePath)
    {
        var source = new OpenXmlDocumentSource().Read(sourcePath) with { DocumentId = DocumentId };
        var aliases = source.Paragraphs.Where(item => !string.IsNullOrWhiteSpace(item.Text)).Select((item, index) => new AliasContext($"S{index + 1:0000}", item.SourceId, item.SourceOrdinal, item.Text, item.Numbering.NumberLabel, $"{item.Style.StyleName ?? item.Style.StyleId};font={item.Style.FontSizePt};bold={item.Style.Bold}", $"prev={Math.Max(0, index - 1)};next={index + 1}", item.Layout, item.LineBreakOffsets.Count)).ToDictionary(item => item.Alias, StringComparer.Ordinal);
        var selected = SeedAliases.Select(alias => aliases.TryGetValue(alias, out var item) ? item : throw new InvalidDataException("V4_SEED_ALIAS_MISSING:" + alias)).OrderBy(item => item.SourceOrdinal).ToArray();
        var sourceSha = Sha256File(sourcePath);
        var seedPath = Path.Combine(repoRoot, SeedFreezePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(seedPath)) throw new FileNotFoundException("V4_SEED_FREEZE_MISSING", seedPath);
        using var seed = JsonDocument.Parse(File.ReadAllText(seedPath));
        if (seed.RootElement.GetProperty("goldReadBeforeFreeze").GetBoolean() || !string.Equals(seed.RootElement.GetProperty("sourceSha256").GetString(), sourceSha, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("V4_SEED_LINEAGE_MISMATCH");
        return new(sourceSha, selected);
    }

    private static HdsaSemanticNodeResolutionInput BuildInput(Context context)
    {
        var occurrences = context.Selected.Select(item => new HdsaSemanticNodeSourceOccurrence(item.Alias, item.SourceOrdinal, item.Text, item.Styles, $"section={item.Layout.SectionIndex};tableDepth={item.Layout.TableDepth};keepNext={item.Layout.KeepNext};pageBreakBefore={item.Layout.PageBreakBefore};lineBreaks={item.LineBreaks}", null)).ToArray();
        return new(context.SourceSha256, Sha256Text(JsonSerializer.Serialize(occurrences, JsonOptions)), occurrences, false);
    }

    private static async Task<int> AbortAsync(string output, string reason, int modelCalls, int providerCalls, CancellationToken ct)
    {
        await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new { schemaVersion = "a99-hdsa-v4-production-summary-v1", status = "BLOCKED", reason, modelCalls, providerCalls, goldReadBeforeFreeze = false, runAbortedBeforeInference = providerCalls == 0 }, ct);
        Console.WriteLine("HDSA_V4_PRODUCTION_STATUS=BLOCKED"); Console.WriteLine($"REASON={reason}"); Console.WriteLine($"MODEL_CALLS={modelCalls}"); Console.WriteLine($"PROVIDER_CALLS={providerCalls}"); return 1;
    }

    private static async Task WriteJsonAsync(string path, object value, CancellationToken ct) => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), ct);
    private static string Sha256Text(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private sealed record Context(string SourceSha256, IReadOnlyList<AliasContext> Selected);
    private sealed record AliasContext(string Alias, string SourceId, int SourceOrdinal, string Text, string? Numbering, string? Styles, string LocalContext, SourceLayoutFacts Layout, int LineBreaks);
}
