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

/// <summary>
/// Transport-only closure for the DOC-0116 canonical correctness campaign. This command freezes
/// the future live-run requests and execution configuration but intentionally performs no provider
/// transport, model capability lookup, Gold read, or scoring.
/// </summary>
public static class CanonicalDevVNextCorrectnessLiveRunner
{
    private const string Baseline = "e5d29a623ea01f93a423264d6c1eb97a9fb3d68d";
    private const string DocumentId = "DOC-0116";
    private const string CampaignId = "CANONICAL_DEV_VNEXT_CORRECTNESS_DOC0116_LIVE";
    private const string SourcePreflightRoot = "artifacts/level-accuracy/canonical-vnext-correctness-doc0116-preflight";
    private const string OutputRoot = "artifacts/level-accuracy/canonical-vnext-correctness-doc0116-live-preflight";
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private const string Model = "qwen/qwen3.7-flash";
    private const int ContextSize = 1_000_000;
    private const int MaxOutputTokens = 48_000;
    private const int RequestTimeoutSeconds = 600;
    private const int PerAttemptHardTimeoutSeconds = 660;
    private const int RequestTimeoutSafetyMarginSeconds = 60;
    private const int DocumentSafetyCeilingSeconds = 7_200;
    private const int TransientRequestRetries = 0;
    private const int MissingIdRetries = 0;
    private const int MaxConcurrency = 1;
    // No exact Qwen/OpenRouter tokenizer is available in this offline harness. Keep a materially
    // conservative reserve instead of treating a few hundred estimated tokens as proof of fit.
    private const int ContextSafetyMarginTokens = 16_384;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(output);
        var startHead = GitSha(repoRoot);
        var sourceRoot = Path.Combine(repoRoot, SourcePreflightRoot.Replace('/', Path.DirectorySeparatorChar));
        var sourceManifestPath = Path.Combine(sourceRoot, "preflight-manifest.v1.json");
        var sourceUniversePath = Path.Combine(sourceRoot, "source-universe.v1.json");
        if (!File.Exists(sourceManifestPath) || !File.Exists(sourceUniversePath))
            return await BlockedAsync(output, startHead, "SOURCE_PREFLIGHT_MISSING", ct);

        using var sourceManifest = JsonDocument.Parse(await File.ReadAllTextAsync(sourceManifestPath, ct));
        using var sourceUniverse = JsonDocument.Parse(await File.ReadAllTextAsync(sourceUniversePath, ct));
        var manifestRoot = sourceManifest.RootElement;
        var universeRoot = sourceUniverse.RootElement;
        if (!string.Equals(manifestRoot.GetProperty("status").GetString(), "READY_FOR_CORRECTNESS_PROVIDER_AUTHORIZATION", StringComparison.Ordinal) ||
            !string.Equals(manifestRoot.GetProperty("documentId").GetString(), DocumentId, StringComparison.Ordinal) ||
            manifestRoot.GetProperty("v2aSubsetUsed").GetBoolean() ||
            !manifestRoot.GetProperty("candidateHintsAttentionOnly").GetBoolean())
            return await BlockedAsync(output, startHead, "SOURCE_UNIVERSE_PREFLIGHT_NOT_AUTHORITY", ct);

        var sourcePath = Path.Combine(repoRoot, universeRoot.GetProperty("sourcePath").GetString()!
            .Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
        if (!File.Exists(sourcePath))
            return await BlockedAsync(output, startHead, "SOURCE_MISSING", ct, new { sourcePath });

        var actualSourceSha = Sha256File(sourcePath);
        var expectedSourceSha = universeRoot.GetProperty("sourceSha256").GetString()!;
        if (!string.Equals(actualSourceSha, expectedSourceSha, StringComparison.OrdinalIgnoreCase))
            return await BlockedAsync(output, startHead, "SOURCE_HASH_MISMATCH", ct, new { expectedSourceSha, actualSourceSha });

        var source = new OpenXmlDocumentSource().Read(sourcePath) with { DocumentId = DocumentId };
        var prepared = await VisualSourceEvidenceBuilder.BuildAsync(sourcePath, int.MaxValue, ct);
        var features = NumberingStyleFeatures.FromSourceDocument(source);
        var derived = new DocumentFeatureDeriver().Derive(source);
        var policy = DocxPolicyStateBuilder.Build(source, features, derived,
            new PipelineOptions { DisableLlm = false }.Extraction);
        var aliases = universeRoot.GetProperty("sourceIdentity").EnumerateArray()
            .Select(item => new AliasRow(
                item.GetProperty("alias").GetString()!,
                item.GetProperty("sourceId").GetString()!,
                item.GetProperty("sourceOrdinal").GetInt32(),
                item.GetProperty("text").GetString()!))
            .OrderBy(item => item.SourceOrdinal)
            .ThenBy(item => item.Alias, StringComparer.Ordinal)
            .ToArray();
        var expectedSourceUniverseSha = manifestRoot.GetProperty("sourceUniverseSha256").GetString()!;
        var sourceUniverseSha = Sha256File(sourceUniversePath);
        if (!string.Equals(sourceUniverseSha, expectedSourceUniverseSha, StringComparison.OrdinalIgnoreCase))
            return await BlockedAsync(output, startHead, "SOURCE_UNIVERSE_HASH_MISMATCH", ct, new { expectedSourceUniverseSha, sourceUniverseSha });
        if (source.Paragraphs.Count != 3_590)
            return await BlockedAsync(output, startHead, "SOURCE_PARAGRAPH_COUNT_MISMATCH", ct,
                new { expected = 3_590, actual = source.Paragraphs.Count });
        if (aliases.Length != 1_921 || aliases.Select(item => item.SourceId).Distinct(StringComparer.Ordinal).Count() != aliases.Length)
            return await BlockedAsync(output, startHead, "SOURCE_UNIVERSE_CARDINALITY_OR_IDENTITY_MISMATCH", ct, new { aliasCount = aliases.Length });

        var catalogAliases = SemanticSourceAliasCatalog.FromCatalog(prepared.Catalog);
        var catalogByAlias = catalogAliases.ToDictionary(item => item.Alias, StringComparer.Ordinal);
        if (catalogAliases.Count != aliases.Length || aliases.Any(item => !catalogByAlias.ContainsKey(item.Alias)))
            return await BlockedAsync(output, startHead, "CANONICAL_CATALOG_ALIAS_MISMATCH", ct,
                new { sourceUniverseAliases = aliases.Length, preparedCatalogAliases = catalogAliases.Count });

        var sourceEvidence = CanonicalSemanticRichEvidenceBuilder.Build(source, prepared.Catalog, policy);
        var evidenceByAlias = sourceEvidence.ToDictionary(item => item.SourceAlias, StringComparer.Ordinal);
        if (sourceEvidence.Count != 1_921 || evidenceByAlias.Count != 1_921 ||
            aliases.Any(item => !evidenceByAlias.ContainsKey(item.Alias)))
            return await BlockedAsync(output, startHead, "EVIDENCE_PACKET_CARDINALITY_OR_REACHABILITY_MISMATCH", ct,
                new { expected = 1_921, actual = sourceEvidence.Count });

        var candidateHints = sourceEvidence.Select(item => item.CandidateAttention).ToArray();
        var targetEvidence = sourceEvidence
            .Select(item => $"{item.SourceAlias}|ordinal:{item.SourceOrdinal}|scope:{item.StructuralScope}")
            .ToArray();
        var localContext = sourceEvidence
            .SelectMany(item => item.LocalBefore.Concat(item.LocalAfter)
                .Select(context => $"{item.SourceAlias}|{context}"))
            .ToArray();
        var globalContext = new[]
        {
            $"documentId:{DocumentId}",
            $"sourceKind:{source.SourceKind}",
            $"sourceParagraphCount:{source.Paragraphs.Count}",
            $"canonicalOccurrenceCount:{sourceEvidence.Count}",
            "candidateGating:false",
            "contextPolicy:FULL_SOURCE_UNIVERSE_RICH_EVIDENCE_V1",
        };
        var input = new CanonicalSemanticProductionInput(
            prepared.Catalog, null, actualSourceSha, prepared.Pages, candidateHints,
            targetEvidence, localContext, globalContext,
            VisualPages: prepared.VisualPages,
            ExpectedSourceSha256: actualSourceSha,
            DocumentId: DocumentId,
            SourceEvidence: sourceEvidence);
        var packedContext = SemanticContextPacker.Pack(input.TargetEvidence, input.LocalContext, input.GlobalContext);
        var packetJson = CanonicalSemanticRequestMaterializer.BuildPacket(input, packedContext);
        var route = ReasoningRoute.ModelCapabilityCeiling.ToString();
        var systemPrompt = SemanticTextExactBindingContract.System;
        var userPrompt = SemanticTextExactBindingContract.BuildUser(packetJson, route);
        var schemaJson = JsonSerializer.Serialize(SemanticTextExactBindingContract.Schema());
        var estimatedInputTokens = ProviderTokenEstimate(systemPrompt + "\n" + userPrompt);
        var fits = estimatedInputTokens + MaxOutputTokens + ContextSafetyMarginTokens <= ContextSize;
        (string Packet, SemanticContextPacket Context) MaterializeSegment(IReadOnlyList<AliasRow> visible)
        {
            var segmentEvidence = visible.Select(alias => evidenceByAlias[alias.Alias]).ToArray();
            var segmentCatalogAliases = visible.Select(alias => catalogByAlias[alias.Alias]).ToArray();
            var segmentHints = segmentEvidence.Select(item => item.CandidateAttention).ToArray();
            var segmentContext = SemanticContextPacker.Pack(
                segmentEvidence.Select(item => $"{item.SourceAlias}|ordinal:{item.SourceOrdinal}|scope:{item.StructuralScope}"),
                segmentEvidence.SelectMany(item => item.LocalBefore.Concat(item.LocalAfter)
                    .Select(context => $"{item.SourceAlias}|{context}")),
                globalContext);
            return (
                CanonicalSemanticRequestMaterializer.BuildPacket(
                    segmentCatalogAliases, segmentEvidence, segmentHints, segmentContext),
                segmentContext);
        }
        var segments = fits
            ? [new SegmentPlan(1, aliases, aliases, "SINGLE_FULL_UNIVERSE_REQUEST")]
            : BuildSegments(aliases, ContextSize - MaxOutputTokens - ContextSafetyMarginTokens,
                visible => ProviderTokenEstimate(systemPrompt + "\n" +
                    SemanticTextExactBindingContract.BuildUser(MaterializeSegment(visible).Packet, route)));

        var sourceEvidenceJson = JsonSerializer.Serialize(new
        {
            schemaVersion = "a99-canonical-rich-source-evidence-v1",
            campaignId = CampaignId,
            documentId = DocumentId,
            sourceSha256 = actualSourceSha,
            sourceUniverseSha256 = sourceUniverseSha,
            occurrenceCount = sourceEvidence.Count,
            semanticallyReachableOccurrenceCount = sourceEvidence.Count,
            candidateGating = false,
            evidence = sourceEvidence,
        }, JsonOptions);
        var contextPackingJson = JsonSerializer.Serialize(new
        {
            schemaVersion = "a99-canonical-rich-context-packing-v1",
            targetEvidence = packedContext.TargetEvidence,
            localContext = packedContext.LocalContext,
            globalContext = packedContext.GlobalContext,
            canonicalOccurrenceCount = sourceEvidence.Count,
            semanticallyReachableOccurrenceCount = sourceEvidence.Count,
            candidateGating = false,
        }, JsonOptions);
        await WriteTextAsync(Path.Combine(output, "source-evidence.v1.json"), sourceEvidenceJson + Environment.NewLine, ct);
        await WriteTextAsync(Path.Combine(output, "context-packing.v1.json"), contextPackingJson + Environment.NewLine, ct);
        var sourceEvidenceArtifactSha256 = Sha256Text(sourceEvidenceJson + Environment.NewLine);
        var evidencePacketSha256 = Sha256Text(packetJson);
        var contextPackingSha256 = Sha256Text(contextPackingJson + Environment.NewLine);

        var sourceTextTokens = aliases.Sum(alias => ProviderTokenEstimate(alias.Text));
        var sourceEvidenceTokens = ProviderTokenEstimate(JsonSerializer.Serialize(sourceEvidence.Select(item => new
        {
            item.SourceAlias, item.SourceId, item.SourceOrdinal, item.StructuralScope,
            item.TableDepth, item.SectionIndex, item.InContentControl, item.InTableOfContents,
            item.ContainerFacts, item.StyleFacts, item.NumberingFacts, item.RunFormattingFacts,
            item.MarkerFacts, item.ObservedEvidence, item.CandidateAttention,
        })));
        var localContextTokens = ProviderTokenEstimate(JsonSerializer.Serialize(sourceEvidence.Select(item => new
        {
            item.SourceAlias, item.LocalBefore, item.LocalAfter,
        })));
        var globalContextTokens = ProviderTokenEstimate(JsonSerializer.Serialize(globalContext));
        var schemaSystemOverheadTokens = Math.Max(0, estimatedInputTokens - sourceTextTokens - sourceEvidenceTokens - localContextTokens - globalContextTokens);

        var configuration = new
        {
            schemaVersion = "a99-canonical-vnext-correctness-live-run-configuration-v1",
            campaignId = CampaignId,
            baseline = Baseline,
            documentId = DocumentId,
            provider = "OpenRouter",
            model = Model,
            endpoint = Endpoint,
            contextSize = ContextSize,
            maxOutputTokens = MaxOutputTokens,
            temperature = "PROVIDER_DEFAULT_UNSPECIFIED",
            reasoning = new { enabledOverride = (bool?)null, providerDefault = true, outputExcluded = true },
            providerRouting = new { route = (string?)null, mode = "OPENROUTER_AUTOMATIC_ROUTING", fallbackPolicy = "PROVIDER_DEFAULT" },
            requestTimeoutSeconds = RequestTimeoutSeconds,
            perAttemptHardTimeoutSeconds = PerAttemptHardTimeoutSeconds,
            documentSafetyCeilingSeconds = DocumentSafetyCeilingSeconds,
            transientRequestRetries = TransientRequestRetries,
            missingIdRetries = MissingIdRetries,
            maxConcurrency = MaxConcurrency,
            semanticContractVersion = CanonicalSemanticContract.ProtocolVersion,
            bindingContractVersion = SemanticTextExactBindingContract.ProtocolVersion,
            modelInputSerializationVersion = "canonical-source-alias-rich-evidence-packet-v1",
            segmentationPolicyVersion = "full-universe-owned-alias-segments-v1",
            contextPolicyVersion = "FULL_SOURCE_UNIVERSE_RICH_EVIDENCE_V1",
            candidateGating = false,
            v2aSubsetUsed = false,
            productionSemanticHash = HashFiles(repoRoot,
                "src/DocxHeaderExtractor.Core/Models/CanonicalSemanticProductionEntryPoint.cs",
                "src/DocxHeaderExtractor.Eval/ReasoningRetention/CanonicalSemanticRichEvidence.cs",
                "src/DocxHeaderExtractor.Eval/ReasoningRetention/OpenRouterCanonicalSemanticTextModel.cs"),
            executionHarnessHash = HashFiles(repoRoot,
                "src/DocxHeaderExtractor.Eval/ReasoningRetention/CanonicalDevVNextCorrectnessLiveRunner.cs",
                "src/DocxHeaderExtractor.Cli/Program.cs"),
            sourceUniverseSha256 = sourceUniverseSha,
            sourceEvidenceArtifactSha256,
            evidencePacketSha256,
            contextPackingSha256,
            evidencePacketOccurrenceCount = sourceEvidence.Count,
            semanticallyReachableOccurrenceCount = sourceEvidence.Count,
            promptHashes = new
            {
                systemPromptSha256 = Sha256Text(systemPrompt),
                userPromptSha256 = Sha256Text(userPrompt),
                schemaSha256 = Sha256Text(schemaJson),
            },
            contextFit = new
            {
                inputBytesUtf8 = Encoding.UTF8.GetByteCount(systemPrompt + "\n" + userPrompt),
                estimatedInputTokens,
                maxOutputTokens = MaxOutputTokens,
                contextSize = ContextSize,
                safetyMarginTokens = ContextSafetyMarginTokens,
                contextHeadroom = ContextSize - estimatedInputTokens - MaxOutputTokens - ContextSafetyMarginTokens,
                fits,
            },
            tokenAttribution = new
            {
                sourceTextTokens,
                sourceEvidenceTokens,
                localContextTokens,
                globalContextTokens,
                schemaSystemOverheadTokens,
                totalTokens = estimatedInputTokens,
            },
            timeoutRelationship = new
            {
                requestTimeoutSeconds = RequestTimeoutSeconds,
                outerPerAttemptHardTimeoutSeconds = PerAttemptHardTimeoutSeconds,
                safetyMarginSeconds = RequestTimeoutSafetyMarginSeconds,
                invariant = "outer per-attempt hard timeout >= provider request timeout + safety margin",
                invariantPass = PerAttemptHardTimeoutSeconds >= RequestTimeoutSeconds + RequestTimeoutSafetyMarginSeconds,
                documentSafetyCeilingSeconds = DocumentSafetyCeilingSeconds,
            },
        };
        var configurationJson = JsonSerializer.Serialize(configuration, JsonOptions);
        var runConfigurationHash = Sha256Text(configurationJson);
        await WriteJsonAsync(Path.Combine(output, "live-run-configuration.v1.json"), configuration, ct);

        var requests = segments.Select(segment =>
        {
            var materialized = MaterializeSegment(segment.Visible);
            var segmentPacket = materialized.Packet;
            var segmentContext = materialized.Context;
            var segmentEvidence = segment.Visible.Select(alias => evidenceByAlias[alias.Alias]).ToArray();
            var segmentUserPrompt = SemanticTextExactBindingContract.BuildUser(segmentPacket, route);
            return new
            {
                requestOrdinal = segment.Ordinal,
                ownedAliases = segment.Owned.Select(alias => alias.Alias).ToArray(),
                visibleAliases = segment.Visible.Select(alias => alias.Alias).ToArray(),
                overlapPolicy = "VISIBLE_OVERLAP_MAY_NOT_CREATE_DUPLICATE_OWNERSHIP",
                systemPromptSha256 = Sha256Text(systemPrompt),
                userPromptSha256 = Sha256Text(segmentUserPrompt),
                schemaSha256 = Sha256Text(schemaJson),
                serializedPayloadSha256 = Sha256Text(segmentPacket),
                serializedPayloadBytesUtf8 = Encoding.UTF8.GetByteCount(segmentPacket),
                estimatedInputTokens = ProviderTokenEstimate(systemPrompt + "\n" + segmentUserPrompt),
                maxOutputTokens = MaxOutputTokens,
                sourceEvidenceArtifactSha256 = Sha256Text(JsonSerializer.Serialize(segmentEvidence, JsonOptions) + Environment.NewLine),
                evidencePacketSha256 = Sha256Text(segmentPacket),
                contextPackingSha256 = Sha256Text(JsonSerializer.Serialize(segmentContext, JsonOptions) + Environment.NewLine),
                requestId = $"{CampaignId}:{DocumentId}:segment-{segment.Ordinal:000}",
                ownedAliasCount = segment.Owned.Count,
                visibleAliasCount = segment.Visible.Count,
            };
        }).ToArray();
        foreach (var segment in segments)
        {
            var materialized = MaterializeSegment(segment.Visible);
            var segmentUserPrompt = SemanticTextExactBindingContract.BuildUser(materialized.Packet, route);
            await WriteJsonAsync(Path.Combine(output, $"materialized-request-{segment.Ordinal:000}.v1.json"), new
            {
                schemaVersion = "a99-canonical-rich-evidence-model-request-v1",
                requestId = $"{CampaignId}:{DocumentId}:segment-{segment.Ordinal:000}",
                systemPrompt,
                userPrompt = segmentUserPrompt,
                schema = JsonSerializer.Deserialize<JsonElement>(schemaJson),
                packet = JsonSerializer.Deserialize<JsonElement>(materialized.Packet),
                schemaText = schemaJson,
                packetText = materialized.Packet,
                systemPromptSha256 = Sha256Text(systemPrompt),
                userPromptSha256 = Sha256Text(segmentUserPrompt),
                schemaSha256 = Sha256Text(schemaJson),
                packetSha256 = Sha256Text(materialized.Packet),
                runConfigurationHash,
            }, ct);
        }
        var requestPlan = new
        {
            schemaVersion = "a99-canonical-vnext-correctness-request-plan-v1",
            campaignId = CampaignId,
            documentId = DocumentId,
            sourceUniverseSha256 = sourceUniverseSha,
            runConfigurationHash,
            fullUniverseAliasCount = aliases.Length,
            segmentCount = requests.Length,
            everyAliasOwnedExactlyOnce = segments.SelectMany(segment => segment.Owned).Select(alias => alias.Alias).Distinct(StringComparer.Ordinal).Count() == aliases.Length &&
                segments.Sum(segment => segment.Owned.Count) == aliases.Length,
            visibleOverlapAliases = segments.Sum(segment => segment.Visible.Count - segment.Owned.Count),
            globalReconciliationRequired = requests.Length > 1,
            requests,
            providerCalls = 0,
            modelCalls = 0,
            goldReads = 0,
        };
        var requestPlanPath = Path.Combine(output, "request-plan.v1.json");
        await WriteJsonAsync(requestPlanPath, requestPlan, ct);

        var freeze = new
        {
            schemaVersion = "a99-canonical-vnext-correctness-request-freeze-v1",
            status = "READY_FOR_DOC0116_CANONICAL_PROVIDER_AUTHORIZATION",
            campaignId = CampaignId,
            documentId = DocumentId,
            baseline = Baseline,
            startHead,
            provider = "OpenRouter",
            model = Model,
            endpoint = Endpoint,
            requestTimeoutSeconds = RequestTimeoutSeconds,
            perAttemptHardTimeoutSeconds = PerAttemptHardTimeoutSeconds,
            documentSafetyCeilingSeconds = DocumentSafetyCeilingSeconds,
            sourceUniverseSha256 = sourceUniverseSha,
            sourceEvidenceArtifactSha256,
            productionSemanticHash = HashFiles(repoRoot,
                "src/DocxHeaderExtractor.Core/Models/CanonicalSemanticProductionEntryPoint.cs",
                "src/DocxHeaderExtractor.Eval/ReasoningRetention/CanonicalSemanticRichEvidence.cs",
                "src/DocxHeaderExtractor.Eval/ReasoningRetention/OpenRouterCanonicalSemanticTextModel.cs"),
            executionHarnessHash = HashFiles(repoRoot,
                "src/DocxHeaderExtractor.Eval/ReasoningRetention/CanonicalDevVNextCorrectnessLiveRunner.cs",
                "src/DocxHeaderExtractor.Cli/Program.cs"),
            evidencePacketSha256,
            contextPackingSha256,
            canonicalOccurrenceCount = aliases.Length,
            semanticallyReachableOccurrenceCount = aliases.Length,
            candidateGating = false,
            tokenAttribution = new
            {
                sourceTextTokens,
                sourceEvidenceTokens,
                localContextTokens,
                globalContextTokens,
                schemaSystemOverheadTokens,
                totalTokens = estimatedInputTokens,
            },
            timeoutRelationship = new
            {
                requestTimeoutSeconds = RequestTimeoutSeconds,
                outerPerAttemptHardTimeoutSeconds = PerAttemptHardTimeoutSeconds,
                safetyMarginSeconds = RequestTimeoutSafetyMarginSeconds,
                invariantPass = PerAttemptHardTimeoutSeconds >= RequestTimeoutSeconds + RequestTimeoutSafetyMarginSeconds,
            },
            runConfigurationHash,
            requestPlanSha256 = Sha256File(requestPlanPath),
            requestCount = requests.Length,
            requestHashes = requests.Select(request => new { request.requestOrdinal, request.serializedPayloadSha256, request.userPromptSha256, request.schemaSha256 }).ToArray(),
            lateResponsePolicy = "LATE_RESPONSES_AFTER_TIMEOUT_ARE_NOT_DURABLE_SEMANTIC_FACTS",
            predictionPromotion = "ATOMIC_ONLY_AFTER_VALIDATION_AND_CANONICAL_FREEZE",
            goldAccess = "FORBIDDEN_UNTIL_PREDICTION_FROZEN",
            providerCalls = 0,
            modelCalls = 0,
            goldReads = 0,
            scoring = false,
            predictionFrozen = false,
        };
        await WriteJsonAsync(Path.Combine(output, "request-freeze-manifest.v1.json"), freeze, ct);
        var executorValidation = await CanonicalDevVNextCorrectnessSegmentedExecutor
            .ValidateFrozenPlanAsync(output, ct);
        await WriteJsonAsync(Path.Combine(output, "executor-closure.v1.json"), new
        {
            schemaVersion = "a99-canonical-segmented-executor-closure-v1",
            status = "SEGMENTED_EXECUTOR_OFFLINE_VALIDATED",
            executorType = executorValidation.ExecutorType,
            segmentCount = executorValidation.SegmentCount,
            ownedOccurrenceCount = executorValidation.OwnedOccurrenceCount,
            hashesAndOwnershipValid = executorValidation.HashesAndOwnershipValid,
            mergePolicy = executorValidation.MergePolicy,
            globalPostInferenceRuns = 1,
            transportSource = "FROZEN_REQUEST_PLAN_AND_MATERIALIZED_REQUESTS",
            forbiddenPath = "CanonicalSemanticProductionEntryPoint.RunAsync(full input) before segment completion",
            providerCalls = 0,
            modelCalls = 0,
            goldReads = 0,
            scoring = false,
        }, ct);
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), string.Join(Environment.NewLine, new[]
        {
            "# DOC-0116 correctness live-runner transport closure",
            "",
            $"Status: `{freeze.status}`",
            $"Source aliases: `{aliases.Length}`",
            $"Planned provider requests: `{requests.Length}`",
            $"Estimated input tokens: `{estimatedInputTokens}`",
            $"Context headroom after output and safety margin: `{ContextSize - estimatedInputTokens - MaxOutputTokens - ContextSafetyMarginTokens}`",
            $"Source evidence packets: `{sourceEvidence.Count}`",
            $"Token attribution: source text `{sourceTextTokens}`, evidence `{sourceEvidenceTokens}`, local `{localContextTokens}`, global `{globalContextTokens}`, overhead `{schemaSystemOverheadTokens}`, total `{estimatedInputTokens}`",
            $"Timeout relation: outer `{PerAttemptHardTimeoutSeconds}s` >= request `{RequestTimeoutSeconds}s` + margin `{RequestTimeoutSafetyMarginSeconds}s`",
            $"Segmented executor closure: `{executorValidation.ValidationStatus}` ({executorValidation.SegmentCount} requests, {executorValidation.OwnedOccurrenceCount} owned occurrences)",
            "Provider calls: `0`",
            "Gold reads: `0`",
            "Scoring: `false`",
            "",
            "This phase freezes transport configuration and request lineage only. No provider client",
            "is constructed and no prediction/evaluation artifact is produced.",
        }) + Environment.NewLine, ct);
        Console.WriteLine($"STATUS={freeze.status}");
        Console.WriteLine($"REQUESTS={requests.Length}");
        Console.WriteLine($"ALIASES={aliases.Length}");
        Console.WriteLine($"ESTIMATED_INPUT_TOKENS={estimatedInputTokens}");
        Console.WriteLine($"CONTEXT_HEADROOM={ContextSize - estimatedInputTokens - MaxOutputTokens - ContextSafetyMarginTokens}");
        Console.WriteLine("PROVIDER_CALLS=0");
        Console.WriteLine("GOLD_READS=0");
        return 0;
    }

    private static IReadOnlyList<SegmentPlan> BuildSegments(
        IReadOnlyList<AliasRow> aliases,
        int availableTokens,
        Func<IReadOnlyList<AliasRow>, int> estimateTokens)
    {
        var result = new List<SegmentPlan>();
        var current = new List<AliasRow>();
        foreach (var alias in aliases)
        {
            var candidate = current.Append(alias).ToArray();
            if (current.Count > 0 && estimateTokens(candidate) > availableTokens)
            {
                result.Add(new SegmentPlan(result.Count + 1, current.ToArray(), current.ToArray(), "DETERMINISTIC_SOURCE_ORDER_SEGMENT"));
                current = [];
            }
            current.Add(alias);
        }
        if (current.Count > 0)
            result.Add(new SegmentPlan(result.Count + 1, current.ToArray(), current.ToArray(), "DETERMINISTIC_SOURCE_ORDER_SEGMENT"));
        return result;
    }

    private static int ProviderTokenEstimate(string value) => ProviderObservabilityHashing.EstimateTokens(value);

    private static async Task<int> BlockedAsync(string output, string startHead, string reason, CancellationToken ct, object? details = null)
    {
        await WriteJsonAsync(Path.Combine(output, "request-freeze-manifest.v1.json"), new
        {
            schemaVersion = "a99-canonical-vnext-correctness-request-freeze-v1",
            status = "BLOCKED_" + reason,
            campaignId = CampaignId,
            documentId = DocumentId,
            baseline = Baseline,
            startHead,
            reason,
            details,
            providerCalls = 0,
            modelCalls = 0,
            goldReads = 0,
            scoring = false,
        }, ct);
        return 1;
    }

    private static async Task WriteJsonAsync(string path, object value, CancellationToken ct) =>
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, ct);

    private static async Task WriteTextAsync(string path, string value, CancellationToken ct) =>
        await File.WriteAllTextAsync(path, value, ct);

    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Sha256Text(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static string HashFiles(string repoRoot, params string[] relativePaths) =>
        Sha256Text(string.Join("\n", relativePaths.Select(path =>
        {
            var fullPath = Path.Combine(repoRoot, path.Replace('/', Path.DirectorySeparatorChar));
            return path + ":" + (File.Exists(fullPath) ? Sha256File(fullPath) : "MISSING");
        })));
    private static string GitSha(string repoRoot) => Git(repoRoot, "rev-parse HEAD");

    private static string Git(string repoRoot, string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", args)
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("GIT_START_FAILED");
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"GIT_FAILED:{args}");
        return process.StandardOutput.ReadToEnd().Trim();
    }

    private sealed record AliasRow(string Alias, string SourceId, int SourceOrdinal, string Text);
    private sealed record SegmentPlan(int Ordinal, IReadOnlyList<AliasRow> Owned, IReadOnlyList<AliasRow> Visible, string Policy);
}
