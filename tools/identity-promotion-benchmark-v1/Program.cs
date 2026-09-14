using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Eval;

namespace IdentityPromotionBenchmarkPreparation;

internal static class Program
{
    private const string BenchmarkVersion = "a99-identity-promotion-benchmark-v1";
    private const string BindingPath = "artifacts/identity-gold/semantic-identity-bindings.user-reviewed.v4.json";
    private const string GoldPath = "artifacts/identity-gold/semantic-identity-gold.user-reviewed.v2.json";
    private const string OutputRoot = "artifacts/identity-benchmark/v1";
    private const string Doc0123Packet = "eval/harness-lift/review-packets/DOC-0123.v1.json";
    private const string Doc0133Packet = "eval/a99-closed-loop/research-r2/p3-isolated-review/sources/DOC-0133.review-source.v1.json";
    private const string Doc0133Pdf = "todo10_8/heading_corpus_100/03_tai_chinh_ke_toan/048_IBRD_Financial_Statements_March_2025.pdf";
    private const string Ir018VisualMatch = "artifacts/identity-gold/ir018-im1-tesseract-match.v1.json";
    private const string Doc0252Pdf = "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static int Main(string[] args)
    {
        try
        {
            var root = Path.GetFullPath(args.Length == 1 ? args[0] : Directory.GetCurrentDirectory());
            Prepare(root);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"PREPARATION_ERROR={ex}");
            return 2;
        }
    }

    private static void Prepare(string root)
    {
        var sources = new[]
        {
            BuildDocxNodes(root, "DOC-0123", Doc0123Packet, "body"),
            BuildPdfNodes(root, "DOC-0133", Doc0133Packet, Doc0133Pdf),
            BuildPdfNodes(root, "DOC-0252", null, Doc0252Pdf),
        };
        var allSourceFacts = sources.SelectMany(x => x.Nodes).ToArray();
        var catalogBytes = JsonSerializer.SerializeToUtf8Bytes(allSourceFacts, JsonOptions);
        var catalogFingerprint = Sha256(catalogBytes);
        var output = Full(root, OutputRoot);
        Directory.CreateDirectory(output);
        WriteJson(Path.Combine(output, "source-catalog.json"), new
        {
            artifactKind = "a99_identity_promotion_source_catalog",
            schemaVersion = "a99-identity-promotion-source-catalog-v1",
            benchmarkVersion = BenchmarkVersion,
            sourceDocuments = sources.Select(x => new { x.DocumentId, x.SourceSha256, x.SourcePath, occurrenceCount = x.Nodes.Count }).ToArray(),
            sourceOccurrences = allSourceFacts,
            catalogFingerprint,
            goldDerivedInput = false,
        });

        var candidates = sources.SelectMany(source =>
        {
            var generated = HdsaDeterministicIdentityCandidateGenerator.Generate(source.Nodes.Select(node =>
                new HdsaCandidateSourceNode(node.NodeId, [node.NodeId], node.Text, node.DocumentOrder)));
            return generated.Candidates.Select(candidate => new CandidateRecord(
                $"{source.DocumentId}:{candidate.PairId}", source.DocumentId, candidate.Left, candidate.Right,
                candidate.Reasons));
        }).OrderBy(x => x.DocumentId, StringComparer.Ordinal).ThenBy(x => x.PairId, StringComparer.Ordinal).ToArray();
        var candidateArtifact = new
        {
            artifactKind = "a99_identity_promotion_candidate_set",
            schemaVersion = "a99-identity-promotion-candidate-set-v1",
            benchmarkVersion = BenchmarkVersion,
            generator = new
            {
                implementation = "HdsaDeterministicIdentityCandidateGenerator",
                version = HdsaDeterministicIdentityCandidateGenerator.Version,
                source = "src/DocxHeaderExtractor.Core/Models/HdsaDeterministicIdentityCandidateGenerator.cs",
                goldUsed = false,
                candidateGenerationIsRecallGate = false,
            },
            sourceCatalogFingerprint = catalogFingerprint,
            sourceDocuments = sources.Select(x => new { x.DocumentId, x.SourceSha256, occurrenceCount = x.Nodes.Count }).ToArray(),
            candidateCount = candidates.Length,
            candidates,
            goldLabelsIncluded = false,
            goldDerivedInput = false,
        };
        var candidateJson = Serialize(candidateArtifact);
        var candidateHash = Sha256(Encoding.UTF8.GetBytes(candidateJson));

        var requestEntries = new List<RequestEntry>();
        foreach (var source in sources)
        {
            var requestNodes = source.Nodes.OrderBy(x => x.DocumentOrder).ThenBy(x => x.NodeId, StringComparer.Ordinal)
                .Select(x => new HdsaIdentityRoleNodeInput(x.NodeId, [x.NodeId], x.Text, x.DocumentOrder, "UNAVAILABLE", false)).ToArray();
            var requestNodesJson = JsonSerializer.SerializeToUtf8Bytes(requestNodes, JsonOptions);
            foreach (var candidate in candidates.Where(x => x.DocumentId == source.DocumentId))
            {
                var target = new HdsaIdentityCandidatePair(candidate.PairId, candidate.Left, candidate.Right);
                var requestBytes = SerializeRequestBytes(catalogFingerprint, requestNodesJson, target);
                requestEntries.Add(new(candidate.PairId, candidate.DocumentId, requestBytes.Sha256, requestBytes.Length,
                    candidate.Left, candidate.Right));
            }
        }
        requestEntries = requestEntries.OrderBy(x => x.RequestId, StringComparer.Ordinal).ToList();
        var requestManifest = new
        {
            artifactKind = "a99_identity_promotion_request_manifest",
            schemaVersion = "a99-identity-promotion-request-manifest-v1",
            benchmarkVersion = BenchmarkVersion,
            sourceCatalogFingerprint = catalogFingerprint,
            candidateSetSha256 = candidateHash,
            requestContract = "HdsaIdentityPairVerificationRequest / hdsa-global-identity-retrieve-verify-v1",
            modelRoleEvidence = "UNAVAILABLE_SOURCE_FIELD; placeholder is harness-owned and not Gold",
            goldDerivedInput = false,
            requestCount = requestEntries.Count,
            requests = requestEntries,
            exactBytesPersisted = false,
            exactBytesArchive = (string?)null,
            exactBytesArchiveSha256 = (string?)null,
            exactBytesArchiveFormat = (string?)null,
            sourceCatalogPath = OutputRoot + "/source-catalog.json",
            exactBytesReconstructibleFromFrozenSourceCatalog = true,
            goldReadBeforeFreeze = false,
        };
        var requestJson = Serialize(requestManifest);
        var requestHash = Sha256(Encoding.UTF8.GetBytes(requestJson));

        var sourceBindingManifest = new
        {
            artifactKind = "a99_identity_promotion_source_binding_manifest",
            schemaVersion = "a99-identity-promotion-source-binding-manifest-v1",
            benchmarkVersion = BenchmarkVersion,
            bindingArtifact = BindingPath,
            bindingArtifactSha256 = (string?)null,
            bindingArtifactHashDeferredUntilGoldRelease = true,
            machineEvaluableGoldCount = 5,
            sourceFacts = sources.Select(x => new
            {
                documentId = x.DocumentId,
                sourceSha256 = x.SourceSha256,
                sourcePath = x.SourcePath,
                sourceOccurrenceCount = x.Nodes.Count,
                sourceOccurrenceIds = x.Nodes.Select(n => n.NodeId).ToArray(),
            }).ToArray(),
            relationLabelsIncluded = false,
            goldConfidenceIncluded = false,
            modelOutputsIncluded = false,
            goldDerivedInput = false,
        };

        var manifest = new
        {
            artifactKind = "a99_identity_promotion_benchmark_manifest",
            schemaVersion = BenchmarkVersion,
            status = "READY_FOR_PROVIDER_EXECUTION",
            branch = "accuracy99/autonomous-closed-loop",
            head = Git(root, "rev-parse HEAD"),
            semanticGoldPath = GoldPath,
            semanticGoldSha256 = (string?)null,
            semanticGoldHashDeferredUntilGoldRelease = true,
            bindingArtifactPath = BindingPath,
            bindingArtifactSha256 = (string?)null,
            bindingArtifactHashDeferredUntilGoldRelease = true,
            sourceCatalogPath = OutputRoot + "/source-catalog.json",
            sourceCatalogSha256 = Sha256File(root, OutputRoot + "/source-catalog.json"),
            machineEvaluableGoldCount = 5,
            provider = "OpenRouter endpoint; backend identity only after telemetry",
            model = "qwen/qwen3.7-flash (current live runner configuration)",
            candidateGenerator = HdsaDeterministicIdentityCandidateGenerator.Version,
            candidateGeneratorSourceSha256 = Sha256File(root, "src/DocxHeaderExtractor.Core/Models/HdsaDeterministicIdentityCandidateGenerator.cs"),
            candidateSetSha256 = candidateHash,
            requestManifestSha256 = requestHash,
            plannedCalls = requestEntries.Count,
            replayedCalls = 0,
            actualNewCalls = 0,
            goldReadCountBeforePredictionFreeze = 0,
            goldConsumedBeforePredictionFreeze = false,
            providerCallsUsedAsGoldAuthority = 0,
            candidateOrPromotionOutputsUsedAsGoldAuthority = false,
            productionBehaviorChanged = false,
            sourceUniverse = sources.Select(x => new { x.DocumentId, x.SourceSha256, occurrenceCount = x.Nodes.Count }).ToArray(),
            candidateCount = candidates.Length,
            requestsFrozen = true,
            predictionsFrozen = false,
            promotionDecisionsFrozen = false,
            providerExecutionAuthorizedByCurrentTurn = false,
            stopGate = "READY_FOR_PROVIDER_EXECUTION",
            noGoldBeforePredictionFreeze = true,
        };

        WriteJson(Path.Combine(output, "manifest.json"), manifest);
        WriteJson(Path.Combine(output, "source-binding-manifest.json"), sourceBindingManifest);
        WriteJson(Path.Combine(output, "candidate-set.json"), candidateArtifact);
        File.WriteAllText(Path.Combine(output, "candidate-set.sha256.txt"), candidateHash + "  candidate-set.json" + Environment.NewLine);
        WriteJson(Path.Combine(output, "request-manifest.json"), requestManifest);
        WriteJson(Path.Combine(output, "firewall.json"), new
        {
            schemaVersion = "a99-identity-promotion-firewall-v1",
            goldReadCountBeforePredictionFreeze = 0,
            goldConsumedBeforePredictionFreeze = false,
            goldUsedForCandidateGeneration = false,
            goldUsedForRequests = false,
            goldUsedForPromotion = false,
            candidateOrPromotionOutputsUsedAsGoldAuthority = false,
            candidateSetFrozen = true,
            requestsFrozen = true,
            predictionsFrozen = false,
            promotionDecisionsFrozen = false,
        });
        WriteJson(Path.Combine(output, "promotion-decisions.json"), new { status = "NOT_RUN_BEFORE_PROVIDER_PREDICTION_FREEZE", promotionGate = "semantic-identity-promotion-v1" });
        WriteJson(Path.Combine(output, "scoring.json"), new { status = "NOT_RUN_GOLD_FIREWALL", goldOpened = false });
        File.WriteAllText(Path.Combine(output, "report.md"), BuildReport(manifest, sources, candidates.Length, requestEntries.Count, candidateHash, requestHash));
        Console.WriteLine($"STATUS=READY_FOR_PROVIDER_EXECUTION");
        Console.WriteLine($"SOURCE_OCCURRENCES={allSourceFacts.Length}");
        Console.WriteLine($"CANDIDATES={candidates.Length}");
        Console.WriteLine($"REQUESTS={requestEntries.Count}");
        Console.WriteLine($"CANDIDATE_SHA256={candidateHash}");
        Console.WriteLine($"REQUEST_MANIFEST_SHA256={requestHash}");
        Console.WriteLine("MODEL_CALLS=0");
        Console.WriteLine("PROVIDER_CALLS=0");
    }

    private static SourceSet BuildDocxNodes(string root, string documentId, string packetPath, string _)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Full(root, packetPath)));
        var nodes = doc.RootElement.GetProperty("occurrences").EnumerateArray()
            .Where(x => !string.IsNullOrWhiteSpace(x.GetProperty("rawText").GetString()))
            .Select(x => new SourceNode(
                $"{documentId}:{x.GetProperty("sourceId").GetString()}",
                x.GetProperty("rawText").GetString()!, x.GetProperty("sourceOrdinal").GetInt32())).ToArray();
        return new(documentId, doc.RootElement.GetProperty("sourceSha256").GetString()!, packetPath, nodes);
    }

    private static SourceSet BuildPdfNodes(string root, string documentId, string? packetPath, string pdfPath)
    {
        if (packetPath is not null)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Full(root, packetPath)));
            var nodes = doc.RootElement.GetProperty("occurrences").EnumerateArray()
                .Where(x => !string.IsNullOrWhiteSpace(x.GetProperty("literalSourceText").GetString()))
                .Select(x => new SourceNode(
                    $"{documentId}:{x.GetProperty("sourceOccurrenceId").GetString()}",
                    x.GetProperty("literalSourceText").GetString()!,
                    x.GetProperty("sourceOrdinal").GetInt32())).ToList();
            if (documentId == "DOC-0133")
                AddIr018VisualSourceNode(root, nodes);
            return new(documentId, doc.RootElement.GetProperty("frozenSourceSha256").GetString()!, packetPath, nodes.ToArray());
        }
        var fullPath = Full(root, pdfPath);
        var review = IsolatedPdfSourceBuilder.Build(fullPath);
        var pdfNodes = review.Blocks
            .Where(x => !string.IsNullOrWhiteSpace(x.LiteralSourceText))
            .Select(x => new SourceNode(
                $"{documentId}:{x.SourceOccurrenceId}", x.LiteralSourceText, x.SourceOrdinal)).ToList();
        return new(documentId, Sha256File(root, pdfPath), pdfPath, pdfNodes.ToArray());
    }

    private static void AddIr018VisualSourceNode(string root, List<SourceNode> nodes)
    {
        using var match = JsonDocument.Parse(File.ReadAllText(Full(root, Ir018VisualMatch)));
        var candidate = match.RootElement.GetProperty("Candidates").EnumerateArray().Single();
        var stableId = candidate.GetProperty("StableOccurrenceId").GetString()!;
        var text = candidate.GetProperty("RecognizedText").GetString() ?? string.Empty;
        var matchingOrdinal = nodes.FirstOrDefault(node =>
            node.Text.Contains("INDEPENDENT AUDITOR", StringComparison.OrdinalIgnoreCase))?.DocumentOrder
            ?? int.MaxValue;
        nodes.Add(new SourceNode($"DOC-0133:{stableId}", text, matchingOrdinal));
    }

    private static string BuildReport(object manifest, IReadOnlyList<SourceSet> sources, int candidateCount, int requestCount, string candidateHash, string requestHash) => string.Join(Environment.NewLine, new[]
    {
        "# A99 Identity Promotion Benchmark v1",
        "",
        "Status: `READY_FOR_PROVIDER_EXECUTION`",
        "",
        "This is a Gold-blind candidate and request freeze. No provider/model call was made.",
        "",
        "## Frozen input",
        "",
        "- Machine-evaluable identity Gold: `5/5` (evaluation-only; relation labels were not loaded for preparation).",
        $"- Source universe: `{sources.Sum(x => x.Nodes.Count)}` source occurrences across `{sources.Count}` documents.",
        $"- Candidate generator: `{HdsaDeterministicIdentityCandidateGenerator.Version}`.",
        $"- Candidate pairs: `{candidateCount}`.",
        $"- Candidate SHA256: `{candidateHash}`.",
        $"- Verifier requests: `{requestCount}`.",
        $"- Request manifest SHA256: `{requestHash}`.",
        "- Exact request bytes: per-request SHA256 and byte length frozen; bodies are reconstructible from the frozen source catalog.",
        "- Source catalog: `source-catalog.json`; relation Gold is not loaded in this phase.",
        "- Provider/model: OpenRouter endpoint / `qwen/qwen3.7-flash` from the current live runner configuration.",
        $"- Planned provider calls: `{requestCount}`; new calls: `0`.",
        "",
        "## Firewall",
        "",
        "`GoldReadCountBeforePredictionFreeze=0`, `GoldConsumedBeforePredictionFreeze=false`.",
        "Candidate generation and request construction use source facts only. Relation labels, Gold confidence, model outputs, and residual hints are absent. The five cases were not injected as candidate pairs; natural retrieval is measured by joining after freeze.",
        "",
        "## Gate",
        "",
        "`READY_FOR_PROVIDER_EXECUTION`",
        "",
        "This task stops before provider execution because the current turn does not explicitly authorize a new external benchmark call. Existing frozen raw responses will be replayed only where request hashes are compatible; otherwise each request requires a new call. No production behavior, promotion policy, prompt, or model configuration was changed."
    }) + Environment.NewLine;

    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static string Serialize(object value) => JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine;
    private static void WriteJson(string path, object value) => File.WriteAllText(path, Serialize(value));
    private static string Sha256File(string root, string relative)
    {
        using var stream = File.OpenRead(Full(root, relative));
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static (string Sha256, int Length) SerializeRequestBytes(
        string catalogFingerprint,
        byte[] requestNodesJson,
        HdsaIdentityCandidatePair target)
    {
        var targetJson = JsonSerializer.SerializeToUtf8Bytes(target, JsonOptions);
        var prefix = Encoding.UTF8.GetBytes($"{{\"catalogFingerprint\":{JsonSerializer.Serialize(catalogFingerprint, JsonOptions)},\"nodes\":");
        var targetPrefix = Encoding.UTF8.GetBytes(",\"targetPair\":");
        var suffix = Encoding.UTF8.GetBytes(",\"goldDerivedInput\":false}");
        var length = prefix.Length + requestNodesJson.Length + targetPrefix.Length + targetJson.Length + suffix.Length;

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(prefix);
        hash.AppendData(requestNodesJson);
        hash.AppendData(targetPrefix);
        hash.AppendData(targetJson);
        hash.AppendData(suffix);
        return (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), length);
    }
    private static string Git(string root, string command) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
    {
        FileName = "git", WorkingDirectory = root, Arguments = command, RedirectStandardOutput = true,
        UseShellExecute = false, CreateNoWindow = true,
    }) is { } process ? ReadProcess(process) : throw new InvalidOperationException("git unavailable");
    private static string ReadProcess(System.Diagnostics.Process p) { p.WaitForExit(); return p.StandardOutput.ReadToEnd().Trim(); }

    private sealed record SourceSet(string DocumentId, string SourceSha256, string SourcePath, IReadOnlyList<SourceNode> Nodes);
    private sealed record SourceNode(string NodeId, string Text, int DocumentOrder);
    private sealed record CandidateRecord(string PairId, string DocumentId, string Left, string Right, IReadOnlyList<string> Reasons);
    private sealed record RequestEntry(string RequestId, string DocumentId, string RequestSha256, int RequestBytes, string Left, string Right);
}
