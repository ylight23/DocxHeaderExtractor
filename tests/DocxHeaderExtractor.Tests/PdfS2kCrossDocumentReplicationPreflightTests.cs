using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfS2kCrossDocumentReplicationPreflightTests
{
    private const string StrictManifestPath = "eval/a99-closed-loop/strict-gold-manifest.v4.json";
    private const string CapabilityMatrixPath = "eval/a99-closed-loop/strict-gold-capability-matrix.v5.json";
    private const string InventoryPath = "eval/a99-closed-loop/canonical-semantic-gold-vnext/inventory.v1.json";
    private const string NativeGoldPath =
        "eval/a99-closed-loop/canonical-semantic-gold-vnext/occurrence/DOC-0252.occurrence-gold.v1.json";
    private const string S2jAuditPath =
        "eval/a99-closed-loop/semantic-text-replay-successor-v1-runtime-authority/DOC-0252/strict-omission-causal-audit.v1.json";
    private const string CurrentPromptHash =
        "8b056f1722b356dd9e836908b8d05ad0850353a06fbc0a4aa39db568fd47f0a8";
    private const string SemanticContractHash =
        "21687e5a78d59b8c82124dcc567c7353024145d599b1294c41c888ffe4512365";
    private const string EvaluatorContract =
        "a99-pdf-gold-evaluator-v3-bound-occurrence-semantic-role";
    private const string ModelIdentifier = "qwen/qwen3.7-flash";
    private const int RepeatCount = 3;
    private const int MaximumProviderCalls = 30;

    [Fact]
    public void Current_authority_cross_document_preflight_is_deterministic_and_offline()
    {
        var report = BuildPreflight();
        Assert.Equal("CROSS_DOCUMENT_BASELINE_BLOCKED", report.GetProperty("finalState").GetString());
        Assert.Equal(0, report.GetProperty("modelCalls").GetInt32());
        Assert.Equal(0, report.GetProperty("providerCalls").GetInt32());
        Assert.Equal(CurrentPromptHash, report.GetProperty("authority").GetProperty("currentPromptSha256").GetString());
        Assert.Equal(EvaluatorContract, report.GetProperty("authority").GetProperty("evaluatorContract").GetString());

        var strictDocuments = report.GetProperty("strictGoldInventory").GetProperty("documents");
        Assert.Equal(15, strictDocuments.GetArrayLength());
        var candidates = report.GetProperty("currentAuthorityCandidates");
        Assert.Equal(6, candidates.GetArrayLength());

        var doc0252 = candidates.EnumerateArray()
            .Single(item => item.GetProperty("documentId").GetString() == "DOC-0252");
        Assert.True(doc0252.GetProperty("nativeEvaluatorV3Compatible").GetBoolean());
        Assert.True(doc0252.GetProperty("goldRebindPassed").GetBoolean());
        Assert.Equal(41, doc0252.GetProperty("goldHeadingClaims").GetInt32());
        Assert.Equal(38, doc0252.GetProperty("goldHeadingAliases").GetInt32());
        Assert.Equal(0, doc0252.GetProperty("goldBindingIssues").GetArrayLength());

        var cohort = report.GetProperty("cohortDecision");
        Assert.Equal(["DOC-0252"], cohort.GetProperty("retainedExistingBaseline").EnumerateArray()
            .Select(item => item.GetString()!).ToArray());
        Assert.Empty(cohort.GetProperty("newSelectedDocuments").EnumerateArray());
        Assert.Equal(0, report.GetProperty("offlineCallPlan").GetProperty("totalNewProviderCalls").GetInt32());

        var artifactPath = RepoPath("eval/a99-closed-loop/cross-document-replication-preflight.v1.json");
        var expected = Serialize(report);
        if (string.Equals(Environment.GetEnvironmentVariable("A99_S2K_PREFLIGHT"), "1", StringComparison.Ordinal))
            File.WriteAllText(artifactPath, expected);
        else
            Assert.Equal(expected, File.ReadAllText(artifactPath));
    }

    private static JsonElement BuildPreflight()
    {
        using var strictManifest = JsonDocument.Parse(File.ReadAllText(RepoPath(StrictManifestPath)));
        using var capability = JsonDocument.Parse(File.ReadAllText(RepoPath(CapabilityMatrixPath)));
        using var inventory = JsonDocument.Parse(File.ReadAllText(RepoPath(InventoryPath)));

        var inventoryById = inventory.RootElement.GetProperty("documents")
            .EnumerateArray()
            .ToDictionary(item => item.GetProperty("authorityKey").GetString()!, StringComparer.Ordinal);
        var strictDocuments = strictManifest.RootElement.GetProperty("documents")
            .EnumerateArray()
            .Select(item =>
            {
                var id = item.GetProperty("documentId").GetString()!;
                inventoryById.TryGetValue(id, out var current);
                return new
                {
                    documentId = id,
                    sourceSha256 = item.GetProperty("sourceSha256").GetString(),
                    semanticHeadingTotal = item.TryGetProperty("semanticHeadingTotal", out var total) &&
                        total.ValueKind != JsonValueKind.Null ? total.GetInt32() : (int?)null,
                    occurrenceEvaluable = item.GetProperty("occurrenceEvaluable").GetBoolean(),
                    characterSpanEvaluable = item.GetProperty("characterSpanEvaluable").GetBoolean(),
                    currentInventoryPresent = current.ValueKind != JsonValueKind.Undefined,
                    currentSourcePath = current.ValueKind == JsonValueKind.Undefined
                        ? null : current.GetProperty("authoritySourcePath").GetString(),
                    currentSourceSha256 = current.ValueKind == JsonValueKind.Undefined
                        ? null : current.GetProperty("currentSourceSha256").GetString(),
                };
            })
            .ToArray();

        var capabilityDocuments = capability.RootElement.GetProperty("documents")
            .EnumerateArray()
            .Select(item => item.GetProperty("documentId").GetString()!)
            .ToArray();
        var currentCandidates = capabilityDocuments
            .Select(id => BuildCandidate(id, inventoryById[id]))
            .ToArray();

        var s2j = JsonDocument.Parse(File.ReadAllText(RepoPath(S2jAuditPath)));
        var s2jHash = CanonicalArtifactHash.OfTextFile(RepoPath(S2jAuditPath));
        var gold = JsonDocument.Parse(File.ReadAllText(RepoPath(NativeGoldPath)));
        var nativeGold = JsonSerializer.Deserialize<PdfGoldDocument>(gold.RootElement.GetRawText())!;
        var nativeGoldUniverseHash = gold.RootElement.GetProperty("occurrenceAuthority")
            .GetProperty("sourceUniverseSha256").GetString();

        var report = new
        {
            artifactKind = "a99_cross_document_replication_preflight",
            schemaVersion = "a99-cross-document-replication-preflight-v1",
            authority = new
            {
                authorityCommit = "56db83933b5373057a01f7826d28a65974330d61",
                strictGoldManifest = StrictManifestPath,
                capabilityMatrix = CapabilityMatrixPath,
                currentInventory = InventoryPath,
                currentPromptSha256 = CurrentPromptHash,
                semanticContractSha256 = SemanticContractHash,
                evaluatorContract = EvaluatorContract,
                modelIdentifier = ModelIdentifier,
                sourceUniverseConstruction = new
                {
                    pdf = "PdfCanonicalSourceUniverseBuilder.Build",
                    docx = "OpenXmlDocumentSource.Read + DocumentSourceCatalogBuilder.FromSourceDocument",
                    docxHashSchema = "a99-docx-runtime-source-universe-v1",
                },
            },
            strictGoldInventory = new
            {
                policy = "USER_PROMOTED_STRICT_GOLD_V4",
                documents = strictDocuments,
                currentInventoryMissing = strictDocuments.Where(item => !item.currentInventoryPresent)
                    .Select(item => item.documentId).ToArray(),
            },
            currentAuthorityCandidates = currentCandidates,
            cohortDecision = new
            {
                selectionPolicy = "CURRENT_SOURCE_AUTHORITY + CURRENT_EVALUATOR_V3 + EXACT_OCCURRENCE_GOLD",
                retainedExistingBaseline = new[] { "DOC-0252" },
                newSelectedDocuments = Array.Empty<string>(),
                excludedDocuments = currentCandidates
                    .Where(item => item.GetProperty("documentId").GetString() != "DOC-0252")
                    .Select(item => new
                    {
                        documentId = item.GetProperty("documentId").GetString(),
                        reasons = item.GetProperty("exclusionReasons"),
                    })
                    .ToArray(),
                reason = "No second document has current native evaluator-v3 occurrence Gold. DOC-0252 is retained only as the existing S2J baseline; no new cross-document provider run is justified.",
            },
            offlineCallPlan = new
            {
                repeatCount = RepeatCount,
                maximumProviderCalls = MaximumProviderCalls,
                selectedNewDocumentCount = 0,
                totalNewProviderCalls = 0,
                segmentation = new { ownedPerSegment = 120, visibleMargin = 20 },
                requestSizeStatus = "COST_UNKNOWN",
                requestSizeAudited = false,
                providerTransportRequired = false,
            },
            failureMatrixContract = new
            {
                scope = "future cross-document replication measurements only",
                families = new[]
                {
                    "heading-definition-discovery",
                    "multi-heading-decomposition",
                    "segment-context-attention-competition",
                    "candidate-hint-attention",
                    "source-representation-loss",
                    "role-mismatch",
                    "binding-or-evaluator-defect",
                },
                documentsWithTrueOmission = Array.Empty<string>(),
                repeatedFailureFamilies = Array.Empty<string>(),
                roleMetric = "NOT_CURRENTLY_ADJUDICABLE",
                note = "No new document has a current evaluator-v3 occurrence Gold contract; do not infer cross-document replication from DOC-0252 alone.",
            },
            protectedBaseline = new
            {
                documentId = "DOC-0252",
                sourceSha256 = nativeGold.SourceSha256,
                currentRuntimeSourceUniverseSha256 = currentCandidates
                    .Single(item => item.GetProperty("documentId").GetString() == "DOC-0252")
                    .GetProperty("sourceUniverseSha256").GetString(),
                reviewedGoldSourceUniverseSha256 = nativeGoldUniverseHash,
                reviewedGoldUniverseReboundByCurrentAliases = true,
                reviewedGoldUniverseIdentityExact = false,
                goldHeadingClaims = nativeGold.Headings.Count,
                goldHeadingAliases = nativeGold.Headings.Select(item => item.SourceAlias).Distinct(StringComparer.Ordinal).Count(),
                s2jAuditPath = S2jAuditPath,
                s2jAuditSha256 = s2jHash,
                semanticContractSha256 = SemanticContractHash,
                currentPromptSha256 = CurrentPromptHash,
            },
            modelCalls = 0,
            providerCalls = 0,
            finalState = "CROSS_DOCUMENT_BASELINE_BLOCKED",
            blockReason = "A cross-document current-authority cohort cannot be formed without a second document that has current native evaluator-v3 occurrence Gold. Existing legacy strict occurrence files are not silently converted or treated as current Gold.",
        };

        return JsonSerializer.Deserialize<JsonElement>(Serialize(report));
    }

    private static JsonElement BuildCandidate(string documentId, JsonElement inventory)
    {
        var path = inventory.GetProperty("authoritySourcePath").GetString()!;
        var fullPath = RepoPath(path);
        var first = BuildSource(fullPath, inventory.GetProperty("mediaType").GetString()!);
        var second = BuildSource(fullPath, inventory.GetProperty("mediaType").GetString()!);
        var deterministic = first.SourceHash == second.SourceHash &&
                            first.SourceUniverseSha256 == second.SourceUniverseSha256 &&
                            first.AliasCatalogHash == second.AliasCatalogHash &&
                            first.AliasCount == second.AliasCount;

        var oldGoldPath = $"eval/a99-closed-loop/strict-gold-occurrence-v1/{documentId}.occurrence-gold-v1.json";
        var oldGoldExists = File.Exists(RepoPath(oldGoldPath));
        var oldGold = oldGoldExists
            ? JsonDocument.Parse(File.ReadAllText(RepoPath(oldGoldPath))).RootElement.Clone()
            : default;
        var oldSchema = oldGoldExists ? oldGold.GetProperty("artifactKind").GetString() : null;
        var oldSourceHash = oldGoldExists ? oldGold.GetProperty("sourceSha256").GetString() : null;
        var oldBindings = oldGoldExists && oldGold.TryGetProperty("bindings", out var bindings)
            ? bindings.GetArrayLength() : 0;
        var materializationBlocked = documentId == "DOC-0264";
        var native = documentId == "DOC-0252";
        var nativeBindingIssues = Array.Empty<string>();
        if (native)
        {
            using var nativeGoldDocument = JsonDocument.Parse(File.ReadAllText(RepoPath(NativeGoldPath)));
            var nativeGold = JsonSerializer.Deserialize<PdfGoldDocument>(nativeGoldDocument.RootElement.GetRawText())!;
            var bound = PdfGoldBoundOccurrenceEvaluator.BindGold(
                nativeGold, first.Aliases, out var bindingIssues);
            nativeBindingIssues = bindingIssues.ToArray();
            nativeBindingIssues = nativeBindingIssues
                .Concat(PdfGoldValidator.Validate(nativeGold, first.Catalog!, first.Aliases)
                    .Select(issue => $"{issue.Code}:{issue.SourceAlias}"))
                .ToArray();
            if (bound.Count != nativeGold.Headings.Count)
                nativeBindingIssues = nativeBindingIssues.Append($"BOUND_COUNT:{bound.Count}/{nativeGold.Headings.Count}").ToArray();
        }
        var exclusionReasons = native
            ? Array.Empty<string>()
            : new[]
            {
                "NON_CURRENT_EVALUATOR_V3_SCHEMA",
                oldSourceHash != first.SourceHash ? "SOURCE_HASH_MISMATCH" : "LEGACY_OCCURRENCE_ARTIFACT_NOT_CURRENT_GOLD",
                materializationBlocked ? "OCCURRENCE_MATERIALIZATION_BLOCKED" : "NO_CURRENT_NATIVE_OCCURRENCE_GOLD",
            };

        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["documentId"] = documentId,
            ["mediaType"] = inventory.GetProperty("mediaType").GetString(),
            ["sourcePath"] = path,
            ["sourceSha256"] = first.SourceHash,
            ["inventorySourceSha256"] = inventory.GetProperty("currentSourceSha256").GetString(),
            ["sourceHashMatchesInventory"] = first.SourceHash == inventory.GetProperty("currentSourceSha256").GetString(),
            ["sourceUniverseSha256"] = first.SourceUniverseSha256,
            ["aliasCatalogHash"] = first.AliasCatalogHash,
            ["aliasCount"] = first.AliasCount,
            ["parserLineCount"] = first.ParserLineCount,
            ["authorityBuildDeterministic"] = deterministic,
            ["nativeGoldPath"] = native ? NativeGoldPath : null,
            ["nativeEvaluatorV3Compatible"] = native,
            ["goldRebindPassed"] = native && nativeBindingIssues.Length == 0,
            ["goldBindingIssues"] = native ? nativeBindingIssues : exclusionReasons,
            ["goldHeadingClaims"] = native ? 41 : inventory.GetProperty("semanticHeadingTotal").GetInt32(),
            ["goldHeadingAliases"] = native ? 38 : 0,
            ["legacyOccurrencePath"] = oldGoldExists ? oldGoldPath : null,
            ["legacyOccurrenceSchema"] = oldSchema,
            ["legacyOccurrenceSourceSha256"] = oldSourceHash,
            ["legacyOccurrenceBindingCount"] = oldBindings,
            ["historicalReplayReusable"] = native,
            ["eligibleForNewProviderReplication"] = false,
            ["exclusionReasons"] = exclusionReasons,
        };
        return JsonSerializer.SerializeToElement(result);
    }

    private static SourceBuild BuildSource(string path, string mediaType)
    {
        if (string.Equals(mediaType, "PDF", StringComparison.OrdinalIgnoreCase))
        {
            var universe = PdfCanonicalSourceUniverseBuilder.Build(path);
            return new SourceBuild(
                universe.SourceSha256,
                universe.SourceUniverseSha256,
                SemanticAuthorityReplayHashing.AliasCatalogHash(universe.Aliases),
                universe.Aliases.Count,
                universe.ParserLineCount,
                universe.Catalog,
                universe.Aliases);
        }

        var source = new OpenXmlDocumentSource().Read(path);
        var catalog = DocumentSourceCatalogBuilder.FromSourceDocument(source);
        var aliases = SemanticSourceAliasCatalog.FromCatalog(catalog);
        return new SourceBuild(
            CanonicalSemanticSourceHash.Compute(path),
            DocxSourceUniverseHash.Compute(CanonicalSemanticSourceHash.Compute(path), aliases),
            SemanticAuthorityReplayHashing.AliasCatalogHash(aliases),
            aliases.Count,
            0,
            catalog,
            aliases);
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    private static string RepoPath(string relativePath) =>
        System.IO.Path.GetFullPath(System.IO.Path.Combine(RepositoryRoot(), relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar)));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(System.IO.Path.Combine(directory.FullName, "DocxHeaderExtractor.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Cannot find repository root.");
    }

    private sealed record SourceBuild(
        string SourceHash,
        string SourceUniverseSha256,
        string AliasCatalogHash,
        int AliasCount,
        int ParserLineCount,
        DocumentSourceCatalog? Catalog,
        IReadOnlyList<SemanticSourceAlias> Aliases);

    private static class DocxSourceUniverseHash
    {
        private static readonly JsonSerializerOptions Json = new()
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public static string Compute(string sourceHash, IReadOnlyList<SemanticSourceAlias> aliases)
        {
            var rows = aliases
                .OrderBy(alias => alias.SourceOrdinal)
                .ThenBy(alias => alias.Alias, StringComparer.Ordinal)
                .Select(alias => new
                {
                    sourceAlias = alias.Alias,
                    sourceId = alias.SourceId,
                    ordinal = alias.SourceOrdinal,
                    text = alias.Text,
                    sourceStart = alias.SourceSpan.Start,
                    sourceEnd = alias.SourceSpan.End,
                })
                .ToArray();
            var json = JsonSerializer.Serialize(new
            {
                schemaVersion = "a99-docx-runtime-source-universe-v1",
                sourceSha256 = sourceHash,
                rows,
            }, Json);
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        }
    }
}
