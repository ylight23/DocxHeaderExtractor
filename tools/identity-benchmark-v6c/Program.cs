using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IdentityBenchmarkV6C;

internal static class Program
{
    private const string V6BRelative = "artifacts/identity-benchmark/v6/owner-evidence/source-only-freeze-v1";
    private const string OutputRelative = "artifacts/identity-benchmark/v6/owner-induction/preflight-v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        var root = Path.GetFullPath(args.FirstOrDefault(x => !x.StartsWith("--", StringComparison.Ordinal)) ?? Directory.GetCurrentDirectory());
        try
        {
            await RunAsync(root);
            Console.WriteLine("V6C_STATUS=OWNER_INDUCTION_PREFLIGHT_FROZEN REQUESTS=3 MODEL_CALLS=0 PROVIDER_CALLS=0 GOLD_READ_COUNT=0 V5C_READ_COUNT=0 V6A_READ_COUNT=0");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"V6C_ERROR={ex.Message}");
            return 2;
        }
    }

    private static async Task RunAsync(string root)
    {
        var v6b = Full(root, V6BRelative);
        using var manifest = Read(Path.Combine(v6b, "manifest.json"));
        using var packets = Read(Path.Combine(v6b, "packets.json"));
        using var groups = Read(Path.Combine(v6b, "scope-groups.json"));
        ValidateV6B(manifest.RootElement, packets.RootElement, groups.RootElement);
        var packetRows = packets.RootElement.EnumerateArray().ToArray();
        var groupRows = groups.RootElement.EnumerateArray().ToArray();
        var requestObjects = packetRows.GroupBy(x => x.GetProperty("documentId").GetString()!, StringComparer.Ordinal).OrderBy(x => x.Key, StringComparer.Ordinal).Select(g => BuildRequest(g.Key, g.ToArray(), groupRows, root)).ToArray();
        var requestJson = requestObjects.Select(x => JsonSerializer.Serialize(x, JsonOptions)).ToArray();
        var requestHashes = requestJson.Select(Sha256Text).ToArray();
        var output = Full(root, OutputRelative);
        Directory.CreateDirectory(output);
        var sourceFingerprint = new { v6bManifestSha256 = Sha256File(Path.Combine(v6b, "manifest.json")), packetsSha256 = Sha256File(Path.Combine(v6b, "packets.json")), groupsSha256 = Sha256File(Path.Combine(v6b, "scope-groups.json")) };
        await WriteAsync(Path.Combine(output, "request-contract.json"), new
        {
            schemaVersion = "a99-v6c-structural-owner-induction-v1",
            input = new[] { "documentId", "frozenOccurrencePackets", "sourceContainerIdentity", "parserOwnedScopeGroups", "documentOrder", "localContext", "containerAncestry", "clusterProvenance" },
            forbidden = new[] { "V4H_Gold", "V5C_evaluation", "V6A_false_merge_cases", "pairLabels", "semanticNodeId", "parent", "ROOT", "level", "shouldMerge", "shouldSplit" },
            output = new { owners = new[] { "ownerLocalId", "memberOccurrenceIds", "autonomous", "ownerDescription", "evidenceRefs" }, assignments = new[] { "occurrenceId", "ownerLocalId" }, unresolvedOccurrenceIds = "allowed" },
            validation = new[] { "unknown occurrence => INVALID", "missing assignment => INVALID", "multiple owner assignment => INVALID", "unknown member => INVALID", "empty owner => INVALID", "duplicate ownerLocalId conflict => INVALID", "fabricated evidence reference => INVALID", "malformed schema => INVALID" },
            note = "autonomous is descriptive output only; it is not a semantic merge veto. Parser scope groups are evidence, not owner truth."
        });
        await WriteAsync(Path.Combine(output, "source-fingerprint.json"), sourceFingerprint);
        await WriteAsync(Path.Combine(output, "requests.json"), new { schemaVersion = "a99-v6c-owner-induction-requests-v1", status = "FROZEN_SOURCE_ONLY_OWNER_INDUCTION_REQUESTS", requestCount = requestObjects.Length, sourceFingerprint, goldDerivedInput = false, v5cEvaluationIncluded = false, v6aDiagnosisIncluded = false, semanticNodeRequested = false, hierarchyRequested = false, requests = requestObjects.Select((x, i) => new { request = x, requestHash = requestHashes[i] }).ToArray() });
        await WriteAsync(Path.Combine(output, "manifest.json"), new { schemaVersion = "a99-v6c-preflight-manifest-v1", status = "READY_FOR_PROVIDER_EXECUTION", documentCount = requestObjects.Length, occurrenceCount = packetRows.Length, parserScopeGroupCount = groupRows.Length, requestCount = requestObjects.Length, requestHashes, sourceFingerprint, modelCalls = 0, providerCalls = 0, goldReadCount = 0, v5cReadCount = 0, v6aReadCount = 0, semanticOwnerAssignments = 0, semanticNodeAssignments = 0, hierarchyRequested = false, note = "Document-global owner induction boundary. No provider execution has occurred." });
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), "# A99 V6C — structural owner induction preflight\n\n" + $"Frozen document-global requests: **{requestObjects.Length}**. Source occurrence packets: **{packetRows.Length}**. Parser-owned scope groups: **{groupRows.Length}**.\n\n" + "Gold, V5C evaluation, V6A diagnosis, semantic-node labels, pair labels, parent/ROOT/level hints are excluded. This is ready for a separately authorized provider execution.\n", new UTF8Encoding(false));
    }

    private static object BuildRequest(string documentId, JsonElement[] packets, JsonElement[] groups, string root)
    {
        var groupKeys = packets.Select(x => x.GetProperty("sourceContainerIdentity").GetString()!).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var visibleGroups = groups.Where(x => groupKeys.Contains(x.GetProperty("sourceContainerIdentity").GetString()!, StringComparer.Ordinal)).Select(x => new { sourceContainerIdentity = x.GetProperty("sourceContainerIdentity").GetString(), sourceUnitKind = x.GetProperty("sourceUnitKind").GetString(), occurrenceCount = x.GetProperty("occurrenceCount").GetInt32(), occurrenceIds = x.GetProperty("occurrenceIds").EnumerateArray().Select(y => y.GetString()!).ToArray() }).ToArray();
        return new
        {
            schemaVersion = "a99-v6c-structural-owner-induction-v1",
            documentId,
            occurrences = packets.OrderBy(x => x.GetProperty("documentOrder").GetInt32()).ThenBy(x => x.GetProperty("occurrenceId").GetString(), StringComparer.Ordinal).Select(x => new
            {
                occurrenceId = x.GetProperty("occurrenceId").GetString(),
                text = x.GetProperty("text").GetString(),
                documentOrder = x.GetProperty("documentOrder").GetInt32(),
                sourceContainerIdentity = x.GetProperty("sourceContainerIdentity").GetString(),
                sourceUnitKind = x.GetProperty("sourceUnitKind").GetString(),
                previousSourceOccurrences = x.GetProperty("previousSourceOccurrences"),
                nextSourceOccurrences = x.GetProperty("nextSourceOccurrences"),
                clusterIds = x.GetProperty("clusterIds"),
                candidateIds = x.GetProperty("candidateIds"),
                evidenceReasons = x.GetProperty("evidenceReasons"),
                packetClasses = x.GetProperty("packetClasses")
            }).ToArray(),
            parserOwnedScopeGroups = visibleGroups,
            goldDerivedInput = false,
            v5cEvaluationIncluded = false,
            v6aDiagnosisIncluded = false,
            semanticNodeRequested = false,
            hierarchyRequested = false
        };
    }

    private static void ValidateV6B(JsonElement manifest, JsonElement packets, JsonElement groups)
    {
        Require(manifest.GetProperty("status").GetString() == "SOURCE_ONLY_OWNER_EVIDENCE_FROZEN", "V6C_V6B_STATUS");
        Require(manifest.GetProperty("goldReadCount").GetInt32() == 0 && manifest.GetProperty("modelCalls").GetInt32() == 0 && manifest.GetProperty("providerCalls").GetInt32() == 0, "V6C_V6B_FIREWALL");
        Require(manifest.GetProperty("semanticOwnerAssignments").GetInt32() == 0 && manifest.GetProperty("semanticNodeAssignments").GetInt32() == 0, "V6C_V6B_SEMANTIC_CONTAMINATION");
        Require(packets.GetArrayLength() == 226 && groups.GetArrayLength() == 22, "V6C_V6B_COUNTS");
        Require(packets.EnumerateArray().All(x => !x.GetProperty("semanticOwnerAssigned").GetBoolean()), "V6C_V6B_OWNER_ASSIGNMENT");
    }

    private static JsonDocument Read(string path) => JsonDocument.Parse(File.ReadAllText(path));
    private static async Task WriteAsync(string path, object value) => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Sha256Text(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
