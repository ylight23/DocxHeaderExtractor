using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>Offline candidate retrieval POC. Gold is deliberately not read by this runner.</summary>
public static class HdsaDeterministicIdentityCandidateGeneratorPocRunner
{
    private const string DocumentId = "DOC-0205";
    private const string CatalogPath = "eval/a99-closed-loop/hdsa-semantic-node-production-live/DOC-0205/semantic-catalog.v1.json";
    private const string GoldPath = "eval/a99-closed-loop/hdsa-parent-relation-live/DOC-0205/structural-gold.v1.json";
    private const string OutputRoot = "eval/a99-closed-loop/hdsa-global-identity-deterministic-candidate-generator-poc/DOC-0205";
    private const string ExpectedCatalogFingerprint = "5948fb130cdf730660991a7a83ceb5aab461374a1eb86ec5f40f112b7f64480e";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Full(repoRoot, OutputRoot);
        Directory.CreateDirectory(output);
        var catalog = ReadCatalog(Full(repoRoot, CatalogPath));
        if (catalog.CatalogFingerprint != ExpectedCatalogFingerprint)
            return await BlockAsync(output, "CATALOG_FINGERPRINT_MISMATCH", ct);

        var nodes = catalog.Entries.OrderBy(item => item.SourceOrder)
            .ThenBy(item => item.SemanticNodeId, StringComparer.Ordinal)
            .Select(item => new HdsaCandidateSourceNode(item.SemanticNodeId,
                item.MemberOccurrenceIds, item.CanonicalText, item.SourceOrder)).ToArray();
        var generation = HdsaDeterministicIdentityCandidateGenerator.Generate(nodes);
        var candidatePath = Path.Combine(output, "candidate-set.v1.json");
        var candidateArtifact = new
        {
            schemaVersion = "a99-hdsa-global-identity-deterministic-candidate-set-v1",
            documentId = DocumentId, sourceSha256 = catalog.SourceSha256,
            catalogFingerprint = catalog.CatalogFingerprint,
            generatorVersion = generation.GeneratorVersion,
            nodes, candidates = generation.Candidates,
            candidateCount = generation.Candidates.Count,
            candidateGenerationIsRecallGate = false,
            goldUsed = generation.GoldUsed, goldReadBeforeFreeze = false,
        };
        await WriteJsonAsync(candidatePath, candidateArtifact, ct);
        var candidateHash = Sha256File(candidatePath);
        await WriteJsonAsync(Path.Combine(output, "freeze.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-identity-deterministic-candidate-freeze-v1",
            documentId = DocumentId, sourceSha256 = catalog.SourceSha256,
            catalogFingerprint = catalog.CatalogFingerprint,
            generatorVersion = generation.GeneratorVersion,
            candidateSetSha256 = candidateHash, candidateCount = generation.Candidates.Count,
            goldUsed = false, goldReadBeforeFreeze = false, frozenBeforeGold = true,
        }, ct);
        await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-identity-deterministic-candidate-generator-poc-summary-v1",
            status = "CANDIDATE_SET_FROZEN_NO_GOLD_EVALUATION",
            documentId = DocumentId, sourceSha256 = catalog.SourceSha256,
            catalogFingerprint = catalog.CatalogFingerprint,
            sourceNodeCount = nodes.Length, candidateCount = generation.Candidates.Count,
            generatorVersion = generation.GeneratorVersion,
            candidateSetSha256 = candidateHash,
            modelCalls = 0, providerCalls = 0, goldUsed = false,
            goldReadBeforeFreeze = false, productionMergePerformed = false,
        }, ct);
        Console.WriteLine("HDSA_DETERMINISTIC_IDENTITY_CANDIDATE_POC_STATUS=CANDIDATE_SET_FROZEN_NO_GOLD_EVALUATION");
        Console.WriteLine($"SOURCE_NODES={nodes.Length}");
        Console.WriteLine($"CANDIDATE_PAIRS={generation.Candidates.Count}");
        Console.WriteLine("MODEL_CALLS=0");
        Console.WriteLine("PROVIDER_CALLS=0");
        Console.WriteLine("GOLD_READ_BEFORE_FREEZE=0");
        return 0;
    }

    public static async Task<int> RunFrozenEvaluationAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Full(repoRoot, OutputRoot);
        var candidatePath = Path.Combine(output, "candidate-set.v1.json");
        var freezePath = Path.Combine(output, "freeze.v1.json");
        var goldPath = Full(repoRoot, GoldPath);
        if (!File.Exists(candidatePath) || !File.Exists(freezePath))
            return await BlockAsync(output, "CANDIDATE_SET_NOT_FROZEN", ct);
        if (!File.Exists(goldPath))
            return await BlockAsync(output, "STRUCTURAL_GOLD_MISSING_AFTER_FREEZE", ct);

        var candidateDocument = JsonDocument.Parse(await File.ReadAllTextAsync(candidatePath, ct));
        var candidateRoot = candidateDocument.RootElement;
        var sourceSha = candidateRoot.GetProperty("sourceSha256").GetString()!;
        var catalogFingerprint = candidateRoot.GetProperty("catalogFingerprint").GetString()!;
        var candidates = candidateRoot.GetProperty("candidates").EnumerateArray()
            .Select(item => (Left: item.GetProperty("left").GetString()!, Right: item.GetProperty("right").GetString()!))
            .Select(item => PairKey(item.Left, item.Right)).ToHashSet(StringComparer.Ordinal);
        var catalog = ReadCatalog(Full(repoRoot, CatalogPath));
        using var goldDocument = JsonDocument.Parse(await File.ReadAllTextAsync(goldPath, ct));
        var goldRoot = goldDocument.RootElement;
        if (goldRoot.GetProperty("sourceSha256").GetString() != sourceSha)
            return await BlockAsync(output, "GOLD_SOURCE_MISMATCH_AFTER_FREEZE", ct);
        var aliasToNode = catalog.Entries.SelectMany(entry => entry.MemberOccurrenceIds
            .Select(alias => (Alias: alias, NodeId: entry.SemanticNodeId)))
            .ToDictionary(item => item.Alias, item => item.NodeId, StringComparer.Ordinal);
        var resolved = goldRoot.GetProperty("occurrences").EnumerateArray()
            .Where(item => item.GetProperty("reviewStatus").GetString() == "RESOLVED")
            .Select(item => new
            {
                Alias = item.GetProperty("sourceAlias").GetString()!,
                GoldNode = item.GetProperty("semanticNodeId").GetString()!,
                Relation = item.GetProperty("occurrenceRelation").GetString()!
            }).ToArray();
        var goldPositivePairs = resolved.GroupBy(item => item.GoldNode, StringComparer.Ordinal)
            .Where(group => group.Key is not null && group.Count() > 1)
            .SelectMany(group => group.SelectMany((left, i) => group.Skip(i + 1)
                .Select(right => new
                {
                    Left = left.Alias, Right = right.Alias,
                    Relation = right.Relation == "CONTINUATION" ? "CONTINUATION_OF" : "SAME_SEMANTIC_REPEAT",
                    PairKey = PairKey(aliasToNode[left.Alias], aliasToNode[right.Alias])
                })))
            .ToArray();
        var covered = goldPositivePairs.Where(item => candidates.Contains(item.PairKey)).ToArray();
        await WriteJsonAsync(Path.Combine(output, "candidate-recall.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-identity-deterministic-candidate-recall-v1",
            documentId = DocumentId, sourceSha256 = sourceSha, catalogFingerprint,
            candidateSetSha256 = Sha256File(candidatePath), freezeSha256 = Sha256File(freezePath),
            candidateSetFrozenBeforeGold = true, goldOpenedAfterFreeze = true,
            goldPath = GoldPath, goldDerivedInput = false, modelCalls = 0, providerCalls = 0,
            candidateCount = candidates.Count,
            pairUniverseCount = catalog.Entries.Count * (catalog.Entries.Count - 1) / 2,
            candidateDensity = candidates.Count / (double)(catalog.Entries.Count * (catalog.Entries.Count - 1) / 2),
            goldPositivePairCount = goldPositivePairs.Length,
            goldPositivePairs, coveredGoldPositivePairs = covered,
            candidateRecall = goldPositivePairs.Length == 0 ? (double?)null : covered.Length / (double)goldPositivePairs.Length,
            s0014S0015Generated = goldPositivePairs.Any(item =>
                (item.Left == "S0014" && item.Right == "S0015") || (item.Left == "S0015" && item.Right == "S0014")),
            s0014S0015CandidateCovered = goldPositivePairs.Any(item =>
                ((item.Left == "S0014" && item.Right == "S0015") || (item.Left == "S0015" && item.Right == "S0014")) && candidates.Contains(item.PairKey)),
        }, ct);
        Console.WriteLine("HDSA_DETERMINISTIC_IDENTITY_CANDIDATE_EVAL_STATUS=GOLD_EVALUATED_AFTER_FREEZE");
        Console.WriteLine($"GOLD_POSITIVE_PAIRS={goldPositivePairs.Length}");
        Console.WriteLine($"CANDIDATE_RECALL={(goldPositivePairs.Length == 0 ? "NA" : (covered.Length / (double)goldPositivePairs.Length).ToString("F6", System.Globalization.CultureInfo.InvariantCulture))}");
        Console.WriteLine("S0014_S0015_GENERATED=" + goldPositivePairs.Any(item =>
            (item.Left == "S0014" && item.Right == "S0015") || (item.Left == "S0015" && item.Right == "S0014")));
        Console.WriteLine("MODEL_CALLS=0");
        Console.WriteLine("PROVIDER_CALLS=0");
        return 0;
    }

    private static HdsaFrozenSemanticNodeCatalog ReadCatalog(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("CANDIDATE_POC_CATALOG_MISSING", path);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var entries = root.GetProperty("entries").EnumerateArray().Select(item => new
        {
            Id = item.GetProperty("semanticNodeId").GetString()!,
            Aliases = item.GetProperty("memberOccurrenceIds").EnumerateArray().Select(value => value.GetString()!).ToArray(),
            Text = item.GetProperty("canonicalText").GetString()!, Order = item.GetProperty("sourceOrder").GetInt32(),
        }).ToArray();
        var input = new HdsaSemanticNodeResolutionInput(root.GetProperty("sourceSha256").GetString()!,
            root.GetProperty("preprocessingSnapshotHash").GetString()!,
            entries.SelectMany(item => item.Aliases.Select(alias => new HdsaSemanticNodeSourceOccurrence(alias, item.Order, item.Text))).ToArray(), false);
        var version = root.GetProperty("resolverVersion").GetString()!;
        var predictions = entries.Select(item => new HdsaSemanticNodePrediction(item.Id, item.Aliases, item.Text,
            "FROZEN_SAFE_CATALOG", version, false)).ToArray();
        var result = new HdsaSemanticNodeResolutionV3Result(input.SourceSha256, input.PreprocessingSnapshotHash,
            predictions, [], [], [], version, false);
        var catalog = HdsaSemanticNodeCatalogBuilder.Build(input, result);
        if (catalog.CatalogFingerprint != root.GetProperty("catalogFingerprint").GetString())
            throw new InvalidDataException("CANDIDATE_POC_CATALOG_RECONSTRUCTION_MISMATCH");
        return catalog;
    }

    private static async Task<int> BlockAsync(string output, string reason, CancellationToken ct)
    {
        await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-hdsa-global-identity-deterministic-candidate-generator-poc-summary-v1",
            status = "BLOCKED", reason, modelCalls = 0, providerCalls = 0, goldReadBeforeFreeze = false,
        }, ct);
        Console.WriteLine("HDSA_DETERMINISTIC_IDENTITY_CANDIDATE_POC_STATUS=BLOCKED");
        Console.WriteLine("REASON=" + reason);
        Console.WriteLine("MODEL_CALLS=0");
        Console.WriteLine("PROVIDER_CALLS=0");
        return 1;
    }

    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static string Sha256Text(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static string Sha256File(string path) => Sha256Text(File.ReadAllText(path));
    private static string PairKey(string left, string right) =>
        string.CompareOrdinal(left, right) < 0 ? left + "\u001f" + right : right + "\u001f" + left;
    private static async Task WriteJsonAsync(string path, object value, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, ct);
    }
}
