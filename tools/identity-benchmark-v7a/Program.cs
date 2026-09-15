using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IdentityBenchmarkV7A;

internal static class Program
{
    private const string SourceRelative = "artifacts/identity-benchmark/v6/owner-evidence/source-only-freeze-v1";
    private const string OutputRelative = "artifacts/identity-benchmark/v7/identity-proposal/preflight-v1-sparse-positive";
    private const int ExpectedOccurrences = 226;
    private static readonly string[] ExpectedDocuments = ["DOC-0123", "DOC-0133", "DOC-0252"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        var root = Path.GetFullPath(args.FirstOrDefault(x => !x.StartsWith("--", StringComparison.Ordinal)) ?? Directory.GetCurrentDirectory());
        try { await PrepareAsync(root); Console.WriteLine("V7A_STATUS=SPARSE_IDENTITY_PROPOSAL_PREFLIGHT_FROZEN PROVIDER_CALLS=0 GOLD_READ_COUNT=0"); return 0; }
        catch (Exception ex) { Console.Error.WriteLine($"V7A_ERROR={ex.Message}"); return 2; }
    }

    private static async Task PrepareAsync(string root)
    {
        var source = Full(root, SourceRelative);
        var output = Full(root, OutputRelative);
        Directory.CreateDirectory(output);
        using var sourceManifest = Read(Path.Combine(source, "manifest.json"));
        using var packetsDoc = Read(Path.Combine(source, "packets.json"));
        ValidateSource(sourceManifest.RootElement, packetsDoc.RootElement);
        var packets = packetsDoc.RootElement.EnumerateArray().ToArray();
        var requests = packets.GroupBy(x => x.GetProperty("documentId").GetString()!, StringComparer.Ordinal).OrderBy(x => x.Key, StringComparer.Ordinal).Select(g => BuildRequest(g.Key, g.ToArray())).ToArray();
        var requestJson = requests.Select(x => JsonSerializer.Serialize(x, JsonOptions)).ToArray();
        var hashes = requestJson.Select(Sha256Text).ToArray();
        var sourceFingerprint = new { sourceManifestSha256 = Sha256File(Path.Combine(source, "manifest.json")), packetsSha256 = Sha256File(Path.Combine(source, "packets.json")) };
        await WriteAsync(Path.Combine(output, "request-contract.json"), new
        {
            schemaVersion = "a99-v7a-sparse-positive-identity-proposal-contract-v1",
            input = new[] { "sourceOccurrences", "documentOrder", "sourceContainerIdentity", "localContext", "parserOwnedEvidence", "deterministicCandidatePairReasons" },
            output = new[] { "positiveProposals", "unresolvedPairIds" },
            proposalRelations = new[] { "SAME_SEMANTIC_REPEAT", "CONTINUATION_OF" },
            defaultPolicy = "KEEP_SPLIT",
            modelPositiveStatus = "MODEL_PROPOSED_ONLY",
            autoCollapse = false,
            forbidden = new[] { "Gold", "V4H/V5/V6D predictions", "parent", "ROOT", "level", "pair labels for omitted pairs", "canonical occurrence IDs in output", "automatic merge authority" },
            validation = new[] { "known pair handle", "known occurrence handles", "no self pair", "one proposal per pair", "valid relation enum", "continuation direction only for CONTINUATION_OF", "no automatic component collapse" },
        });
        await WriteAsync(Path.Combine(output, "source-fingerprint.json"), sourceFingerprint);
        var candidatePairCount = requests.Sum(CountCandidatePairs);
        await WriteAsync(Path.Combine(output, "requests.json"), new { schemaVersion = "a99-v7a-sparse-positive-identity-requests-v1", status = "FROZEN_SOURCE_ONLY_SPARSE_POSITIVE_REQUESTS", requestCount = requests.Length, occurrenceCount = packets.Length, candidatePairCount, sourceFingerprint, goldDerivedInput = false, v4hEvaluationIncluded = false, v5PredictionIncluded = false, v6dPredictionIncluded = false, autoCollapse = false, requests = requests.Select((x, i) => new { request = x, requestHash = hashes[i] }).ToArray() });
        await WriteAsync(Path.Combine(output, "manifest.json"), new { schemaVersion = "a99-v7a-preflight-manifest-v1", status = "READY_FOR_SEPARATE_PROVIDER_AUTHORIZATION", requestCount = requests.Length, documentCount = requests.Length, occurrenceCount = packets.Length, candidatePairCount, requestHashes = hashes, sourceFingerprint, modelCalls = 0, providerCalls = 0, goldReadCount = 0, goldDerivedInput = false, v4hEvaluationIncluded = false, v5PredictionIncluded = false, v6dPredictionIncluded = false, autoCollapse = false, generalizationClaim = false, candidatePolicy = "Source-only deterministic attention candidates; candidate pairs are not merge evidence." });
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), $"# A99 V7A — sparse positive identity proposal preflight\n\nFrozen document-global requests: **{requests.Length}**. Source occurrences: **{packets.Length}**. Candidate pairs: **{candidatePairCount}**.\n\nThe model may propose only positive identity relations. Omitted pairs remain `NO_CLAIM / KEEP_SPLIT`; proposals remain `MODEL_PROPOSED_ONLY` and cannot collapse nodes. Gold, V4H/V5/V6D predictions, parent, ROOT, and level are excluded.\n", new UTF8Encoding(false));
    }

    private static object BuildRequest(string documentId, JsonElement[] packets)
    {
        var ordered = packets.OrderBy(x => x.GetProperty("documentOrder").GetInt32()).ThenBy(x => x.GetProperty("occurrenceId").GetString(), StringComparer.Ordinal).ToArray();
        var handles = ordered.Select((x, i) => new { packet = x, handle = $"U{i + 1:000}" }).ToArray();
        var occurrences = handles.Select(x => new
        {
            @ref = x.handle,
            sourceOccurrenceId = x.packet.GetProperty("occurrenceId").GetString(),
            text = x.packet.GetProperty("text").GetString(),
            documentOrder = x.packet.GetProperty("documentOrder").GetInt32(),
            sourceContainerIdentity = x.packet.GetProperty("sourceContainerIdentity").GetString(),
            sourceUnitKind = x.packet.GetProperty("sourceUnitKind").GetString(),
            previousSourceOccurrences = x.packet.GetProperty("previousSourceOccurrences"),
            nextSourceOccurrences = x.packet.GetProperty("nextSourceOccurrences"),
            parserEvidence = new { packetClasses = x.packet.GetProperty("packetClasses"), sourceContainerIdentity = x.packet.GetProperty("sourceContainerIdentity"), sourceUnitKind = x.packet.GetProperty("sourceUnitKind") },
        }).ToArray();
        var pairs = new List<object>();
        for (var i = 0; i < handles.Length; i++)
        for (var j = i + 1; j < handles.Length; j++)
        {
            var left = handles[i].packet; var right = handles[j].packet;
            var reasons = new List<string>();
            var distance = Math.Abs(left.GetProperty("documentOrder").GetInt32() - right.GetProperty("documentOrder").GetInt32());
            var sameScope = left.GetProperty("sourceContainerIdentity").GetString() == right.GetProperty("sourceContainerIdentity").GetString();
            if (sameScope && distance <= 4) reasons.Add("ADJACENT_SAME_SCOPE");
            if (sameScope && distance <= 12 && left.GetProperty("sourceUnitKind").GetString() == right.GetProperty("sourceUnitKind").GetString()) reasons.Add("NEARBY_SAME_SOURCE_UNIT_KIND");
            if (Normalize(left.GetProperty("text").GetString()) == Normalize(right.GetProperty("text").GetString()) && Normalize(left.GetProperty("text").GetString()).Length >= 3) reasons.Add("NORMALIZED_TEXT_EQUAL");
            if (left.GetProperty("text").GetString()!.Contains("cont'd", StringComparison.OrdinalIgnoreCase) || right.GetProperty("text").GetString()!.Contains("cont'd", StringComparison.OrdinalIgnoreCase)) reasons.Add("EXPLICIT_CONTINUATION_TEXT_SIGNAL");
            if (reasons.Count > 0) pairs.Add(new { pairId = $"P{pairs.Count + 1:0000}", left = handles[i].handle, right = handles[j].handle, reasons = reasons.ToArray() });
        }
        return new
        {
            schemaVersion = "a99-v7a-sparse-positive-identity-proposal-v1", documentId, identityDefault = "EACH_OCCURRENCE_SEPARATE_UNLESS_PROMOTION_LATER_ACCEPTS_PROPOSAL",
            occurrences, candidatePairs = pairs.ToArray(), outputContract = new { positiveProposals = "MODEL_PROPOSED_ONLY", unresolvedPairIds = "optional", relations = new[] { "SAME_SEMANTIC_REPEAT", "CONTINUATION_OF" }, continuationDirection = "required_for_CONTINUATION_OF", autoCollapse = false, parentOrHierarchyRequested = false },
            goldDerivedInput = false, v4hEvaluationIncluded = false, v5PredictionIncluded = false, v6dPredictionIncluded = false,
        };
    }

    private static void ValidateSource(JsonElement manifest, JsonElement packets)
    {
        Require(manifest.GetProperty("status").GetString() == "SOURCE_ONLY_OWNER_EVIDENCE_FROZEN", "V7A_SOURCE_STATUS");
        Require(manifest.GetProperty("goldReadCount").GetInt32() == 0 && manifest.GetProperty("providerCalls").GetInt32() == 0, "V7A_SOURCE_FIREWALL");
        Require(packets.GetArrayLength() == ExpectedOccurrences, "V7A_OCCURRENCE_COUNT");
    }

    private static int CountCandidatePairs(object request)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(request, JsonOptions));
        return doc.RootElement.GetProperty("candidatePairs").GetArrayLength();
    }

    private static string Normalize(string? value) => new string((value ?? string.Empty).Normalize(NormalizationForm.FormKC).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Sha256Text(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static JsonDocument Read(string path) => JsonDocument.Parse(File.ReadAllText(path));
    private static async Task WriteAsync(string path, object value) => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
    private static void Require(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
