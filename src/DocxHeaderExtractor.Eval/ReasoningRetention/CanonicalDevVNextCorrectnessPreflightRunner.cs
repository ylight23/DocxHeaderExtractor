using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// Offline preflight for the correctness-first canonical vNext campaign.  This runner only
/// freezes the source universe and exact model contract inputs; it never constructs a provider
/// client and never reads Gold or historical evaluation artifacts.
/// </summary>
public static class CanonicalDevVNextCorrectnessPreflightRunner
{
    private const string DocumentId = "DOC-0116";
    private const string InventoryPath = "eval/a99-dataset/document-inventory.v1.json";
    private const string OutputRoot = "artifacts/level-accuracy/canonical-vnext-correctness-doc0116-preflight";
    private const string CampaignId = "CANONICAL_DEV_VNEXT_CORRECTNESS_DOC0116";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(output);
        var startHead = GitSha(repoRoot);

        var inventoryPath = Path.Combine(repoRoot, InventoryPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(inventoryPath))
            return await BlockedAsync(output, startHead, "INVENTORY_MISSING", ct);

        using var inventory = JsonDocument.Parse(await File.ReadAllTextAsync(inventoryPath, ct));
        var item = inventory.RootElement.GetProperty("documents").EnumerateArray()
            .SingleOrDefault(row => string.Equals(row.GetProperty("documentId").GetString(), DocumentId, StringComparison.Ordinal));
        if (item.ValueKind == JsonValueKind.Undefined)
            return await BlockedAsync(output, startHead, "DOC0116_NOT_IN_INVENTORY", ct);

        var sourcePath = Path.Combine(repoRoot, item.GetProperty("sourcePath").GetString()!
            .Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
        var expectedSourceSha = item.GetProperty("sourceSha256").GetString()!;
        if (!File.Exists(sourcePath))
            return await BlockedAsync(output, startHead, "SOURCE_MISSING", ct, new { sourcePath });

        var actualSourceSha = Sha256File(sourcePath);
        if (!string.Equals(actualSourceSha, expectedSourceSha, StringComparison.OrdinalIgnoreCase))
            return await BlockedAsync(output, startHead, "SOURCE_HASH_MISMATCH", ct, new { expectedSourceSha, actualSourceSha });

        var source = new OpenXmlDocumentSource().Read(sourcePath) with { DocumentId = DocumentId };
        var prepared = await VisualSourceEvidenceBuilder.BuildAsync(sourcePath, int.MaxValue, ct);
        var aliases = SemanticSourceAliasCatalog.FromCatalog(prepared.Catalog);
        var nonEmptyParagraphs = source.Paragraphs.Where(paragraph => !string.IsNullOrWhiteSpace(paragraph.Text)).ToArray();

        // The candidate policy is deliberately exercised for every owned alias. A false hint
        // must not remove an alias from the source universe.
        var hints = aliases.Select(alias =>
            new SemanticCandidateAttentionHint(alias.Alias, false, "ATTENTION_ONLY_NOT_RECALL_GATE")).ToArray();
        var attentionPolicyPass = aliases.All(alias => SemanticCandidatePolicy.CanAcceptOwnedOccurrence(alias.Alias, hints));
        var sourceIds = aliases.Select(alias => alias.SourceId).ToArray();
        var uniqueSourceIds = sourceIds.Distinct(StringComparer.Ordinal).Count() == sourceIds.Length;
        var aliasesInSourceOrder = aliases.Select(alias => alias.SourceOrdinal)
            .SequenceEqual(aliases.Select(alias => alias.SourceOrdinal).OrderBy(value => value));

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

        var sourceRows = aliases.Select(alias => new
        {
            alias = alias.Alias,
            sourceId = alias.SourceId,
            sourceOrdinal = alias.SourceOrdinal,
            text = alias.Text,
            textSha256 = Sha256Text(alias.Text),
            sourceSpan = alias.SourceSpan,
            sourceAnchor = alias.SourceAnchor,
        }).ToArray();
        var sourceUniverse = new
        {
            schemaVersion = "a99-canonical-vnext-source-universe-v1",
            campaignId = CampaignId,
            documentId = DocumentId,
            sourcePath = Path.GetRelativePath(repoRoot, sourcePath).Replace('\\', '/'),
            sourceSha256 = actualSourceSha,
            sourceParagraphCount = source.Paragraphs.Count,
            nonEmptySourceParagraphCount = nonEmptyParagraphs.Length,
            sourceCatalogUnitCount = prepared.Catalog.Units.Count,
            semanticAliasCount = aliases.Count,
            sourceIdentity = sourceRows,
            candidateHints = hints.Select(hint => new
            {
                hint.SourceAlias,
                hint.HeuristicMatch,
                hint.Reason,
            }).ToArray(),
            candidateHintPolicy = "ATTENTION_ONLY_NOT_RECALL_GATE",
            candidateGating = false,
            allOwnedAliasesRetained = true,
            goldRead = false,
            historicalParentRead = false,
            historicalLevelRead = false,
        };
        var sourceUniversePath = Path.Combine(output, "source-universe.v1.json");
        await WriteJsonAsync(sourceUniversePath, sourceUniverse, ct);

        var requestPreflight = new
        {
            schemaVersion = "a99-canonical-vnext-correctness-request-preflight-v1",
            campaignId = CampaignId,
            documentId = DocumentId,
            pipeline = "CanonicalSemanticProductionEntryPoint",
            modelAdapter = "OpenRouterCanonicalSemanticTextModel",
            semanticContractVersion = CanonicalSemanticContract.ProtocolVersion,
            bindingContractVersion = SemanticTextExactBindingContract.ProtocolVersion,
            route,
            sourceUniverseSha256 = Sha256File(sourceUniversePath),
            sourceSha256 = actualSourceSha,
            aliasCount = aliases.Count,
            targetEvidenceCount = nonEmptyParagraphs.Length,
            candidateHintCount = hints.Length,
            candidateHintsAllAttentionOnly = hints.All(hint => !hint.HeuristicMatch && hint.Reason == "ATTENTION_ONLY_NOT_RECALL_GATE"),
            candidatePolicyAcceptanceForEveryAlias = attentionPolicyPass,
            sourceIdsUnique = uniqueSourceIds,
            aliasesInSourceOrder,
            packet = new
            {
                bytesUtf8 = Encoding.UTF8.GetByteCount(packetJson),
                sha256 = Sha256Text(packetJson),
                aliasCount = aliases.Count,
                allAliasesPresent = true,
            },
            systemPrompt = new { bytesUtf8 = Encoding.UTF8.GetByteCount(systemPrompt), sha256 = Sha256Text(systemPrompt) },
            userPrompt = new { bytesUtf8 = Encoding.UTF8.GetByteCount(userPrompt), sha256 = Sha256Text(userPrompt) },
            schema = new { bytesUtf8 = Encoding.UTF8.GetByteCount(schemaJson), sha256 = Sha256Text(schemaJson) },
            exactModelInputContract = "packetJson + SemanticTextExactBindingContract.System + BuildUser(packetJson, route) + Schema",
            attentionOnlyInvariant = "Every source catalog alias remains model-visible; candidate hints do not gate recall.",
            providerCalls = 0,
            modelCalls = 0,
            goldReads = 0,
            scoring = false,
        };
        await WriteJsonAsync(Path.Combine(output, "request-preflight.v1.json"), requestPreflight, ct);

        var configMaterial = string.Join("\n", new[]
        {
            CampaignId,
            DocumentId,
            actualSourceSha,
            CanonicalSemanticContract.ProtocolVersion,
            SemanticTextExactBindingContract.ProtocolVersion,
            Sha256Text(systemPrompt),
            Sha256Text(userPrompt),
            Sha256Text(schemaJson),
            "candidateGating=false",
            "candidateHints=attention-only",
            "goldReads=0",
            "historicalParentReads=0",
            "historicalLevelReads=0",
        });
        var manifest = new
        {
            schemaVersion = "a99-canonical-vnext-correctness-preflight-manifest-v1",
            status = attentionPolicyPass && uniqueSourceIds && aliasesInSourceOrder
                ? "READY_FOR_CORRECTNESS_PROVIDER_AUTHORIZATION"
                : "BLOCKED_ON_SOURCE_UNIVERSE_INTEGRITY",
            campaignId = CampaignId,
            documentId = DocumentId,
            startHead,
            branch = Git(repoRoot, "branch --show-current"),
            sourceUniversePath = "source-universe.v1.json",
            requestPreflightPath = "request-preflight.v1.json",
            sourceUniverseSha256 = Sha256File(sourceUniversePath),
            runConfigurationHash = Sha256Text(configMaterial),
            sourceParagraphCount = source.Paragraphs.Count,
            nonEmptySourceParagraphCount = nonEmptyParagraphs.Length,
            canonicalSourceOccurrenceCount = aliases.Count,
            allSourceOccurrencesModelVisible = true,
            candidateHintsAttentionOnly = true,
            v2aSubsetUsed = false,
            providerCalls = 0,
            modelCalls = 0,
            goldReads = 0,
            scoring = false,
            predictionFrozen = false,
            predictionArtifactsCreated = false,
        };
        await WriteJsonAsync(Path.Combine(output, "preflight-manifest.v1.json"), manifest, ct);
        var report = string.Join(Environment.NewLine, new[]
        {
            $"# Canonical vNext correctness preflight — {DocumentId}",
            "",
            $"Status: `{manifest.status}`",
            "",
            $"- source paragraphs: `{source.Paragraphs.Count}`",
            $"- non-empty source paragraphs: `{nonEmptyParagraphs.Length}`",
            $"- canonical source catalog units / model-visible aliases: `{aliases.Count}`",
            $"- candidate hints: `{hints.Length}` (attention-only)",
            "- V2A subset used: `false`",
            "- provider calls: `0`",
            "- Gold reads: `0`",
            "- scoring: `false`",
            "",
            "The preflight materializes the same source alias packet used by the canonical semantic adapter.",
            "Every alias remains visible regardless of its candidate hint. Prediction and evaluation artifacts",
            "are intentionally not created; a fresh provider authorization is required for the correctness run.",
        });
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), report + Environment.NewLine, ct);
        Console.WriteLine($"STATUS={manifest.status}");
        Console.WriteLine($"DOCUMENT={DocumentId}");
        Console.WriteLine($"SOURCE_PARAGRAPHS={source.Paragraphs.Count}");
        Console.WriteLine($"SOURCE_CATALOG_UNITS={prepared.Catalog.Units.Count}");
        Console.WriteLine($"MODEL_VISIBLE_ALIASES={aliases.Count}");
        Console.WriteLine("PROVIDER_CALLS=0");
        Console.WriteLine("GOLD_READS=0");
        return manifest.status == "READY_FOR_CORRECTNESS_PROVIDER_AUTHORIZATION" ? 0 : 1;
    }

    private static async Task<int> BlockedAsync(string output, string startHead, string reason, CancellationToken ct, object? details = null)
    {
        await WriteJsonAsync(Path.Combine(output, "preflight-manifest.v1.json"), new
        {
            schemaVersion = "a99-canonical-vnext-correctness-preflight-manifest-v1",
            status = "BLOCKED_ON_SOURCE_UNIVERSE_INTEGRITY",
            reason,
            details,
            startHead,
            providerCalls = 0,
            modelCalls = 0,
            goldReads = 0,
            scoring = false,
            predictionFrozen = false,
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
}
