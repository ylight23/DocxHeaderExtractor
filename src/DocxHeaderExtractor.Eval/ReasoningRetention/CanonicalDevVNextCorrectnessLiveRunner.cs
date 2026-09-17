using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// Transport-only closure for the DOC-0116 canonical correctness campaign. This command freezes
/// the future live-run requests and execution configuration but intentionally performs no provider
/// transport, model capability lookup, Gold read, or scoring.
/// </summary>
public static class CanonicalDevVNextCorrectnessLiveRunner
{
    private const string Baseline = "ffc1187db403fac6528e448ba61d0b5e3308e6f1";
    private const string DocumentId = "DOC-0116";
    private const string CampaignId = "CANONICAL_DEV_VNEXT_CORRECTNESS_DOC0116_LIVE";
    private const string SourcePreflightRoot = "artifacts/level-accuracy/canonical-vnext-correctness-doc0116-preflight";
    private const string OutputRoot = "artifacts/level-accuracy/canonical-vnext-correctness-doc0116-live-preflight";
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private const string Model = "qwen/qwen3.7-flash";
    private const int ContextSize = 1_000_000;
    private const int MaxOutputTokens = 48_000;
    private const int RequestTimeoutSeconds = 600;
    private const int PerAttemptHardTimeoutSeconds = 300;
    private const int DocumentSafetyCeilingSeconds = 7_200;
    private const int TransientRequestRetries = 0;
    private const int MissingIdRetries = 0;
    private const int MaxConcurrency = 1;
    private const int ContextSafetyMarginTokens = 1_024;
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
        if (aliases.Length != 1_921 || aliases.Select(item => item.SourceId).Distinct(StringComparer.Ordinal).Count() != aliases.Length)
            return await BlockedAsync(output, startHead, "SOURCE_UNIVERSE_CARDINALITY_OR_IDENTITY_MISMATCH", ct, new { aliasCount = aliases.Length });

        var packetJson = JsonSerializer.Serialize(new
        {
            sourceAliases = aliases.Select(alias => new
            {
                alias = alias.Alias,
                text = alias.Text,
                sourceOrdinal = alias.SourceOrdinal,
            }).ToArray(),
        });
        var route = ReasoningRoute.ModelCapabilityCeiling.ToString();
        var systemPrompt = SemanticTextExactBindingContract.System;
        var userPrompt = SemanticTextExactBindingContract.BuildUser(packetJson, route);
        var schemaJson = JsonSerializer.Serialize(SemanticTextExactBindingContract.Schema());
        var estimatedInputTokens = ProviderTokenEstimate(systemPrompt + "\n" + userPrompt);
        var fits = estimatedInputTokens + MaxOutputTokens + ContextSafetyMarginTokens <= ContextSize;
        var segments = fits
            ? [new SegmentPlan(1, aliases, aliases, "SINGLE_FULL_UNIVERSE_REQUEST")]
            : BuildSegments(aliases, ContextSize - MaxOutputTokens - ContextSafetyMarginTokens);

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
            modelInputSerializationVersion = "canonical-source-alias-packet-v1",
            segmentationPolicyVersion = "full-universe-owned-alias-segments-v1",
            contextPolicyVersion = "FULL_SOURCE_UNIVERSE_ATTENTION_ONLY_V1",
            candidateGating = false,
            v2aSubsetUsed = false,
            sourceUniverseSha256 = sourceUniverseSha,
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
        };
        var configurationJson = JsonSerializer.Serialize(configuration, JsonOptions);
        var runConfigurationHash = Sha256Text(configurationJson);
        await WriteJsonAsync(Path.Combine(output, "live-run-configuration.v1.json"), configuration, ct);

        var requests = segments.Select(segment =>
        {
            var segmentPacket = JsonSerializer.Serialize(new
            {
                sourceAliases = segment.Visible.Select(alias => new
                {
                    alias = alias.Alias,
                    text = alias.Text,
                    sourceOrdinal = alias.SourceOrdinal,
                }).ToArray(),
            });
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
                requestId = $"{CampaignId}:{DocumentId}:segment-{segment.Ordinal:000}",
                ownedAliasCount = segment.Owned.Count,
                visibleAliasCount = segment.Visible.Count,
            };
        }).ToArray();
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
            status = fits ? "READY_FOR_DOC0116_CORRECTNESS_PROVIDER_AUTHORIZATION" : "READY_WITH_DETERMINISTIC_SEGMENTATION_PLAN",
            campaignId = CampaignId,
            documentId = DocumentId,
            baseline = Baseline,
            startHead,
            sourceUniverseSha256 = sourceUniverseSha,
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
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), string.Join(Environment.NewLine, new[]
        {
            "# DOC-0116 correctness live-runner transport closure",
            "",
            $"Status: `{freeze.status}`",
            $"Source aliases: `{aliases.Length}`",
            $"Planned provider requests: `{requests.Length}`",
            $"Estimated input tokens: `{estimatedInputTokens}`",
            $"Context headroom after output and safety margin: `{ContextSize - estimatedInputTokens - MaxOutputTokens - ContextSafetyMarginTokens}`",
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

    private static IReadOnlyList<SegmentPlan> BuildSegments(IReadOnlyList<AliasRow> aliases, int availableTokens)
    {
        var targetChars = Math.Max(4_096, availableTokens * 4);
        var result = new List<SegmentPlan>();
        var current = new List<AliasRow>();
        var chars = 0;
        foreach (var alias in aliases)
        {
            var aliasChars = alias.Alias.Length + alias.Text.Length + 48;
            if (current.Count > 0 && chars + aliasChars > targetChars)
            {
                result.Add(new SegmentPlan(result.Count + 1, current.ToArray(), current.ToArray(), "DETERMINISTIC_SOURCE_ORDER_SEGMENT"));
                current = [];
                chars = 0;
            }
            current.Add(alias);
            chars += aliasChars;
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

    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Sha256Text(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
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
