using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Features;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Policy;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>Gold-free preflight for a new DOC-0116 TRUE_HEADING baseline. It rebuilds the
/// source-faithful rich-evidence requests under the successor contract and never calls a provider.</summary>
public static class CanonicalDevVNextTrueHeadingPreflightRunner
{
    private const string DocumentId = "DOC-0116";
    private const string CampaignId = "CANONICAL_VNEXT_TRUE_HEADING_DOC0116_BASELINE_V1";
    private const string InventoryPath = "eval/a99-dataset/document-inventory.v1.json";
    private const string HistoricalPreflightRoot = "artifacts/level-accuracy/canonical-vnext-correctness-doc0116-preflight";
    private const string OutputRoot = "artifacts/level-accuracy/canonical-vnext-true-heading-doc0116-baseline-v1/preflight-v1";
    private const string Model = "qwen/qwen3.7-flash";
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private const int EffectiveProviderInputLimit = 983_616;
    private const int TransportReserveTokens = 16_384;
    private const int MinimumPostReserveHeadroom = 4_096;
    private const int SafeSegmentUpperBound = EffectiveProviderInputLimit - MinimumPostReserveHeadroom;
    private const int MaxOutputTokens = 48_000;
    private const int RequestTimeoutSeconds = 600;
    private const int PerAttemptHardTimeoutSeconds = 660;
    private const int DocumentSafetyCeilingSeconds = 7_200;
    private const int MaxConcurrency = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Full(repoRoot, OutputRoot);
        Directory.CreateDirectory(output);
        var inventoryPath = Full(repoRoot, InventoryPath);
        var historicalUniversePath = Full(repoRoot, HistoricalPreflightRoot + "/source-universe.v1.json");
        if (!File.Exists(inventoryPath) || !File.Exists(historicalUniversePath))
            return await BlockedAsync(output, "AUTHORITATIVE_SOURCE_INPUT_MISSING", ct);

        using var inventory = JsonDocument.Parse(await File.ReadAllTextAsync(inventoryPath, ct));
        var inventoryRow = inventory.RootElement.GetProperty("documents").EnumerateArray()
            .SingleOrDefault(row => row.GetProperty("documentId").GetString() == DocumentId);
        if (inventoryRow.ValueKind == JsonValueKind.Undefined)
            return await BlockedAsync(output, "DOC0116_NOT_IN_INVENTORY", ct);
        var sourcePath = Full(repoRoot, inventoryRow.GetProperty("sourcePath").GetString()!);
        var expectedSourceSha = inventoryRow.GetProperty("sourceSha256").GetString()!;
        var actualSourceSha = Sha256File(sourcePath);
        const string requiredSourceSha = "0a8637b1afb059e6048c9da706c1ac419ceaea18fde37965791430c5f68f66da";
        if (actualSourceSha != expectedSourceSha || actualSourceSha != requiredSourceSha)
            return await BlockedAsync(output, "SOURCE_SHA_MISMATCH", ct, new { expectedSourceSha, requiredSourceSha, actualSourceSha });

        using var oldUniverse = JsonDocument.Parse(await File.ReadAllTextAsync(historicalUniversePath, ct));
        var oldRoot = oldUniverse.RootElement;
        var expectedSourceUniverseSha = Sha256File(historicalUniversePath);
        const string requiredSourceUniverseSha = "f9c568ede3d8172dfea05cc07aeb3c8f838088b7a0c163faf461f2cd3d845d8b";
        if (!string.Equals(expectedSourceUniverseSha, requiredSourceUniverseSha, StringComparison.OrdinalIgnoreCase))
            return await BlockedAsync(output, "FROZEN_SOURCE_UNIVERSE_SHA_MISMATCH", ct, new { expectedSourceUniverseSha, requiredSourceUniverseSha });

        var source = new OpenXmlDocumentSource().Read(sourcePath) with { DocumentId = DocumentId };
        var prepared = await VisualSourceEvidenceBuilder.BuildAsync(sourcePath, int.MaxValue, ct);
        var features = NumberingStyleFeatures.FromSourceDocument(source);
        var derived = new DocumentFeatureDeriver().Derive(source);
        var policy = DocxPolicyStateBuilder.Build(source, features, derived, new PipelineOptions { DisableLlm = false }.Extraction);
        var catalog = SemanticSourceAliasCatalog.FromCatalog(prepared.Catalog);
        var frozenIdentities = oldRoot.GetProperty("sourceIdentity").EnumerateArray().ToArray();
        if (catalog.Count != 1_921 || frozenIdentities.Length != 1_921 || source.Paragraphs.Count != 3_590)
            return await BlockedAsync(output, "SOURCE_UNIVERSE_CARDINALITY_MISMATCH", ct, new { aliases = catalog.Count, frozen = frozenIdentities.Length, paragraphs = source.Paragraphs.Count });
        foreach (var item in frozenIdentities)
        {
            var alias = catalog.SingleOrDefault(row => row.Alias == item.GetProperty("alias").GetString());
            if (alias is null || alias.SourceId != item.GetProperty("sourceId").GetString() || alias.SourceOrdinal != item.GetProperty("sourceOrdinal").GetInt32() || alias.Text != item.GetProperty("text").GetString())
                return await BlockedAsync(output, "SOURCE_UNIVERSE_IDENTITY_MISMATCH", ct, new { alias = item.GetProperty("alias").GetString() });
        }

        var evidence = CanonicalSemanticRichEvidenceBuilder.Build(source, prepared.Catalog, policy);
        if (evidence.Count != 1_921)
            return await BlockedAsync(output, "RICH_EVIDENCE_CARDINALITY_MISMATCH", ct, new { actual = evidence.Count });
        var evidenceByAlias = evidence.ToDictionary(item => item.SourceAlias, StringComparer.Ordinal);
        var hints = evidence.Select(item => item.CandidateAttention).ToArray();
        var globalContext = new[]
        {
            $"documentId:{DocumentId}", $"sourceKind:{source.SourceKind}", $"sourceParagraphCount:{source.Paragraphs.Count}",
            $"runtimeSourceAliasCount:{evidence.Count}", "candidateGating:false", "contextPolicy:FULL_SOURCE_UNIVERSE_RICH_EVIDENCE_V1",
        };
        var route = ReasoningRoute.ModelCapabilityCeiling.ToString();
        var systemPrompt = SemanticTextTrueHeadingContract.System;
        var schemaJson = JsonSerializer.Serialize(SemanticTextTrueHeadingContract.Schema());
        using var schemaDoc = JsonDocument.Parse(schemaJson);
        var planningReasoning = new { enabled = true, exclude = true };

        (string Packet, SemanticContextPacket Context, string User, byte[] Wire, int UpperBound, int Estimate) Materialize(IReadOnlyList<SemanticSourceAlias> visible)
        {
            var visibleEvidence = visible.Select(alias => evidenceByAlias[alias.Alias]).ToArray();
            var context = SemanticContextPacker.Pack(
                visibleEvidence.Select(item => $"{item.SourceAlias}|ordinal:{item.SourceOrdinal}|scope:{item.StructuralScope}"),
                visibleEvidence.SelectMany(item => item.LocalBefore.Concat(item.LocalAfter).Select(value => $"{item.SourceAlias}|{value}")),
                globalContext);
            var packet = CanonicalSemanticRequestMaterializer.BuildPacket(visible, visibleEvidence, visibleEvidence.Select(item => item.CandidateAttention).ToArray(), context);
            var user = SemanticTextTrueHeadingContract.BuildUser(packet, route);
            var body = OpenRouterCeilingReasoningModel.BuildRequestBodyForAudit(Model, systemPrompt, user, MaxOutputTokens, schemaDoc.RootElement, "semantic_true_heading_v2", planningReasoning, null, true);
            var wire = OpenRouterCeilingReasoningModel.SerializeRequestBodyForAudit(body);
            return (packet, context, user, wire, checked(wire.Length + TransportReserveTokens), ProviderObservabilityHashing.EstimateTokens(systemPrompt + "\n" + user));
        }

        var full = Materialize(catalog);
        var segments = BuildSegments(catalog, visible => Materialize(visible).UpperBound);
        var evidenceJson = JsonSerializer.Serialize(new { schemaVersion = "a99-true-heading-rich-evidence-v2", campaignId = CampaignId, documentId = DocumentId, sourceSha256 = actualSourceSha, sourceUniverseSha256 = requiredSourceUniverseSha, runtimeSourceAliasCount = evidence.Count, semanticallyReachableOccurrenceCount = evidence.Count, candidateGating = false, evidence }, JsonOptions) + Environment.NewLine;
        var contextJson = JsonSerializer.Serialize(new { schemaVersion = "a99-true-heading-context-packing-v2", globalContext, runtimeSourceAliasCount = evidence.Count, semanticallyReachableOccurrenceCount = evidence.Count, candidateGating = false }, JsonOptions) + Environment.NewLine;
        await WriteTextAsync(Path.Combine(output, "source-evidence.v1.json"), evidenceJson, ct);
        await WriteTextAsync(Path.Combine(output, "context-packing.v1.json"), contextJson, ct);

        // Preserve the authoritative source-universe bytes exactly; the hash is part of the
        // identity gate and must not be changed by a text encoding round-trip.
        await File.WriteAllBytesAsync(Path.Combine(output, "source-universe.v1.json"), await File.ReadAllBytesAsync(historicalUniversePath, ct), ct);
        var sourceUniverseOutputSha = Sha256File(Path.Combine(output, "source-universe.v1.json"));
        if (!string.Equals(sourceUniverseOutputSha, requiredSourceUniverseSha, StringComparison.OrdinalIgnoreCase))
            return await BlockedAsync(output, "SOURCE_UNIVERSE_REPRODUCTION_MISMATCH", ct, new { sourceUniverseOutputSha, requiredSourceUniverseSha });

        var semanticContractSha = Sha256Text(string.Join("\n", SemanticTextTrueHeadingContract.ProtocolVersion, systemPrompt, schemaJson));
        var productionSemanticHash = HashFiles(repoRoot,
            "src/DocxHeaderExtractor.Core/Models/CanonicalSemanticProductionEntryPoint.cs",
            "src/DocxHeaderExtractor.Eval/ReasoningRetention/CanonicalSemanticRichEvidence.cs",
            "src/DocxHeaderExtractor.Eval/ReasoningRetention/OpenRouterCanonicalSemanticTextModel.cs",
            "src/DocxHeaderExtractor.Eval/ReasoningRetention/SemanticTextTrueHeadingContract.cs");
        var executionHarnessHash = HashFiles(repoRoot,
            "src/DocxHeaderExtractor.Eval/ReasoningRetention/CanonicalDevVNextTrueHeadingPreflightRunner.cs",
            "src/DocxHeaderExtractor.Eval/ReasoningRetention/CanonicalDevVNextCorrectnessSegmentedExecutor.cs",
            "src/DocxHeaderExtractor.Eval/ReasoningRetention/CanonicalDevVNextCorrectnessProviderExecutionRunner.cs",
            "src/DocxHeaderExtractor.Cli/Program.cs");
        var config = new
        {
            schemaVersion = "a99-true-heading-run-configuration-v1", campaignId = CampaignId, documentId = DocumentId,
            provider = "OpenRouter", model = Model, endpoint = Endpoint, maxOutputTokens = MaxOutputTokens,
            effectiveProviderInputLimit = EffectiveProviderInputLimit, transportReserveTokens = TransportReserveTokens,
            minimumPostReserveHeadroom = MinimumPostReserveHeadroom, safeSegmentUpperBound = SafeSegmentUpperBound,
            requestTimeoutSeconds = RequestTimeoutSeconds, perAttemptHardTimeoutSeconds = PerAttemptHardTimeoutSeconds,
            documentSafetyCeilingSeconds = DocumentSafetyCeilingSeconds, maxConcurrency = MaxConcurrency,
            retries = 0, semanticContract = new { version = SemanticTextTrueHeadingContract.ProtocolVersion, sha256 = semanticContractSha },
            productionSemanticHash, executionHarnessHash, sourceSha256 = actualSourceSha, sourceUniverseSha256 = requiredSourceUniverseSha,
            candidateGating = false, target = "ALL TRUE HEADING OCCURRENCES", goldReads = 0, scoring = false,
        };
        var configJson = JsonSerializer.Serialize(config, JsonOptions) + Environment.NewLine;
        var runConfigurationHash = Sha256Text(configJson);
        await WriteTextAsync(Path.Combine(output, "run-configuration.v1.json"), configJson, ct);

        var requestRows = new List<object>();
        foreach (var segment in segments)
        {
            var materialized = Materialize(segment.Owned);
            var wireHash = Sha256Bytes(materialized.Wire);
            var request = new
            {
                requestOrdinal = segment.Ordinal,
                requestId = $"{CampaignId}:{DocumentId}:segment-{segment.Ordinal:000}",
                ownedAliases = segment.Owned.Select(item => item.Alias).ToArray(),
                visibleAliases = segment.Owned.Select(item => item.Alias).ToArray(),
                ownedAliasCount = segment.Owned.Count,
                providerRequestBodyBytesUtf8 = materialized.Wire.Length,
                providerInputUpperBoundTokens = materialized.UpperBound,
                providerRequestBodySha256 = wireHash,
                packetSha256 = Sha256Text(materialized.Packet),
                userPromptSha256 = Sha256Text(materialized.User),
                systemPromptSha256 = Sha256Text(systemPrompt),
                schemaSha256 = Sha256Text(schemaJson),
                headroom = EffectiveProviderInputLimit - materialized.UpperBound,
            };
            requestRows.Add(request);
            await WriteJsonAsync(Path.Combine(output, $"materialized-request-{segment.Ordinal:000}.v1.json"), new
            {
                schemaVersion = "a99-true-heading-model-request-v2", requestId = request.requestId,
                systemPrompt, userPrompt = materialized.User, schema = JsonSerializer.Deserialize<JsonElement>(schemaJson),
                schemaText = schemaJson, packet = JsonSerializer.Deserialize<JsonElement>(materialized.Packet), packetText = materialized.Packet,
                providerRequestBodyBytesUtf8 = materialized.Wire.Length, providerInputUpperBoundTokens = materialized.UpperBound,
                providerRequestBodySha256 = wireHash, packetSha256 = Sha256Text(materialized.Packet),
                systemPromptSha256 = Sha256Text(systemPrompt), userPromptSha256 = Sha256Text(materialized.User), schemaSha256 = Sha256Text(schemaJson),
                runConfigurationHash, transportReserveTokens = TransportReserveTokens, minimumPostReserveHeadroom = MinimumPostReserveHeadroom,
                goldReads = 0, providerCalls = 0,
            }, ct);
        }
        var plan = new
        {
            schemaVersion = "a99-true-heading-request-plan-v1", campaignId = CampaignId, documentId = DocumentId,
            sourceSha256 = actualSourceSha, sourceUniverseSha256 = requiredSourceUniverseSha, semanticContractSha256 = semanticContractSha,
            runConfigurationHash, runtimeSourceAliasCount = catalog.Count, segmentCount = segments.Count,
            everyAliasOwnedExactlyOnce = segments.SelectMany(item => item.Owned).Select(item => item.Alias).Distinct(StringComparer.Ordinal).Count() == catalog.Count,
            requests = requestRows, providerCalls = 0, goldReads = 0, scoring = false,
            effectiveProviderInputLimit = EffectiveProviderInputLimit, minimumPostReserveHeadroom = MinimumPostReserveHeadroom,
        };
        var planPath = Path.Combine(output, "request-plan.v1.json");
        await WriteJsonAsync(planPath, plan, ct);
        var planSha = Sha256File(planPath);
        var minimumRequestHeadroom = segments.Min(segment => EffectiveProviderInputLimit - Materialize(segment.Owned).UpperBound);

        var historicalPredictionSha = "bf9b42a4ea76103805136f379b4f133e3af40d087a14743a34d811d81989bfa8";
        var promotedGoldAuthoritySha = "83ec5942171bca438f08f58341a5c4cb0f0c92dc46976d13069e784a7d3141b0";
        var bridgePath = Full(repoRoot, "artifacts/level-accuracy/canonical-vnext-correctness-doc0116-live-execution-v2-1/gold-scoring-v1/occurrence-universe-bridge-v1/bridge-audit.v1.json");
        var alignmentPath = Full(repoRoot, "artifacts/level-accuracy/canonical-vnext-correctness-doc0116-live-execution-v2-1/gold-scoring-v1/semantic-contract-alignment-v1/semantic-contract-alignment-audit.v1.json");
        var freeze = new
        {
            schemaVersion = "a99-true-heading-preflight-freeze-v1", status = "READY_FOR_TRUE_HEADING_PROVIDER_EXECUTION",
            campaignId = CampaignId, documentId = DocumentId, sourceSha256 = actualSourceSha, sourceUniverseSha256 = requiredSourceUniverseSha,
            runtimeSourceAliasCount = catalog.Count, semanticContractVersion = SemanticTextTrueHeadingContract.ProtocolVersion,
            semanticContractSha256 = semanticContractSha, systemPromptSha256 = Sha256Text(systemPrompt), schemaSha256 = Sha256Text(schemaJson),
            sourceEvidenceSha256 = Sha256File(Path.Combine(output, "source-evidence.v1.json")), contextPackingSha256 = Sha256File(Path.Combine(output, "context-packing.v1.json")),
            runConfigurationHash, requestPlanSha256 = planSha, requestCount = segments.Count,
            requestHashes = requestRows, minimumRequestHeadroom,
            lineage = new
            {
                historicalPredictionFreezeSha256 = historicalPredictionSha,
                historicalSemanticAlignmentArtifactSha256 = File.Exists(alignmentPath) ? Sha256File(alignmentPath) : "MISSING",
                promotedGoldAuthoritySha256 = promotedGoldAuthoritySha,
                bridgeAuditSha256 = File.Exists(bridgePath) ? Sha256File(bridgePath) : "MISSING",
            },
            goldReads = 0, providerCalls = 0, scoring = false, predictionFrozen = false,
            retries = 0, maxParallel = 1, target = "ALL TRUE HEADING OCCURRENCES",
        };
        await WriteJsonAsync(Path.Combine(output, "preflight-freeze.v1.json"), freeze, ct);
        await WriteTextAsync(Path.Combine(output, "report.md"), $"# DOC-0116 TRUE_HEADING new baseline preflight\n\nStatus: `READY_FOR_TRUE_HEADING_PROVIDER_EXECUTION`\n\n- Protocol: `{SemanticTextTrueHeadingContract.ProtocolVersion}`\n- Target: `ALL TRUE HEADING OCCURRENCES`\n- Heading decision: explicit and independent of role\n- Source SHA: `{actualSourceSha}`\n- Runtime source aliases: `{catalog.Count}`\n- Segments / planned provider calls: `{segments.Count}`\n- Minimum request headroom: `{minimumRequestHeadroom}` tokens\n- All wire hashes frozen: `true`\n- Gold reads: `0`\n- Provider calls: `0`\n- Scoring: `false`\n\nNo provider call is authorized by this preflight. A separate explicit approval is required. Pricing metadata is unavailable locally; input-size estimates are frozen in each materialized request and the run configuration.\n", ct);
        Console.WriteLine("STATUS=READY_FOR_TRUE_HEADING_PROVIDER_EXECUTION");
        Console.WriteLine($"SEGMENTS={segments.Count}");
        Console.WriteLine($"PLANNED_PROVIDER_CALLS={segments.Count}");
        Console.WriteLine($"MIN_HEADROOM={minimumRequestHeadroom}");
        Console.WriteLine("GOLD_READS=0");
        Console.WriteLine("PROVIDER_CALLS=0");
        return 0;
    }

    private static IReadOnlyList<Segment> BuildSegments(IReadOnlyList<SemanticSourceAlias> aliases, Func<IReadOnlyList<SemanticSourceAlias>, int> estimate)
    {
        var result = new List<Segment>();
        var current = new List<SemanticSourceAlias>();
        foreach (var alias in aliases)
        {
            var candidate = current.Append(alias).ToArray();
            if (estimate(candidate) > SafeSegmentUpperBound)
            {
                if (current.Count == 0) throw new InvalidDataException($"SINGLE_ALIAS_EXCEEDS_PROVIDER_LIMIT:{alias.Alias}");
                result.Add(new(result.Count + 1, current.ToArray()));
                current = [];
            }
            current.Add(alias);
        }
        if (current.Count > 0) result.Add(new(result.Count + 1, current.ToArray()));
        return result;
    }

    private static async Task<int> BlockedAsync(string output, string reason, CancellationToken ct, object? details = null)
    {
        await WriteJsonAsync(Path.Combine(output, "preflight-freeze.v1.json"), new { schemaVersion = "a99-true-heading-preflight-freeze-v1", status = "BLOCKED_" + reason, reason, details, goldReads = 0, providerCalls = 0, scoring = false }, ct);
        Console.WriteLine($"STATUS=BLOCKED_{reason}");
        Console.WriteLine("GOLD_READS=0");
        Console.WriteLine("PROVIDER_CALLS=0");
        return 1;
    }
    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
    private static async Task WriteJsonAsync(string path, object value, CancellationToken ct) => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, ct);
    private static async Task WriteTextAsync(string path, string value, CancellationToken ct) => await File.WriteAllTextAsync(path, value, Encoding.UTF8, ct);
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Sha256Text(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Sha256Bytes(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    private static string HashFiles(string root, params string[] files) => Sha256Text(string.Join("\n", files.Select(file => file + ":" + (File.Exists(Full(root, file)) ? Sha256File(Full(root, file)) : "MISSING"))));
    private sealed record Segment(int Ordinal, IReadOnlyList<SemanticSourceAlias> Owned);
}
