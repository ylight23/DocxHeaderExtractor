using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>Freezes the currently available raw-only G2A/H2 conflict inventory before Gold stratification.</summary>
public sealed class V5P6TH3PopulationConflictInventoryTests
{
    private const string ArtifactRoot = "artifacts/v5-p6t-function-membership/p6th3-population-conflict-inventory";
    private const string Root = "artifacts/v5-p6t-function-membership";

    private static readonly LedgerPair[] PairedLedgers =
    [
        new(
            "SRC-089",
            "p6tg2a-anchor-existence-canary-20261004/SRC-089.raw-capture.v1.json",
            "p6th2-function-conditioned-continuation-canary-20261004/SRC-089.raw-capture.v1.json",
            "CHALLENGE_SLICE_15_G2A_PRIMARIES_AND_18_H2_EDGES"),
        new(
            "SRC-041",
            "p6te-src041-e-challenge/g2a.raw-capture.v1.json",
            "p6te-src041-h2-challenge/h2.raw-capture.v1.json",
            "CHALLENGE_SLICE_3_G2A_PRIMARIES_AND_3_H2_EDGES"),
    ];

    private static readonly G2AOnlyLedger[] G2AOnlyLedgers =
    [
        new("DOC-0252", "p6te-doc0252-e-challenge/g2a.raw-capture.v1.json", 19),
        new("DOC-0256", "p6te-doc0256-e-challenge/g2a.raw-capture.v1.json", 7),
    ];

    [Fact]
    public void Available_frozen_G2A_H2_ledgers_have_a_complete_raw_only_conflict_inventory()
    {
        var documents = new List<object>();
        var conflicts = new List<Conflict>();
        var totalH2Edges = 0;
        var totalComparableEdges = 0;

        foreach (var pair in PairedLedgers)
        {
            var g2aPath = PathFor(pair.G2ARawPath);
            var h2Path = PathFor(pair.H2RawPath);
            var g2aBytes = File.ReadAllBytes(g2aPath);
            var h2Bytes = File.ReadAllBytes(h2Path);
            using var g2a = JsonDocument.Parse(g2aBytes);
            using var h2 = JsonDocument.Parse(h2Bytes);

            Assert.Equal(pair.DocumentId, ReadString(g2a.RootElement, "documentId"));
            Assert.Equal(pair.DocumentId, ReadString(h2.RootElement, "documentId"));
            Assert.Equal("stop", ReadString(h2.RootElement, "finishReason"));
            Assert.Equal(0, ReadInt(h2.RootElement, "retryCount"));

            var anchors = ReadCaptureDecisions(g2a.RootElement)
                .Select(item => new
                {
                    Occurrence = ReadString(item, "primary"),
                    Anchor = ReadString(item, "anchor"),
                })
                .ToDictionary(item => item.Occurrence, item => item.Anchor, StringComparer.Ordinal);

            var edges = ReadH2Decisions(h2.RootElement).ToArray();
            var paired = edges.Where(edge => anchors.ContainsKey(edge.Right)).ToArray();
            var localConflicts = paired
                .Where(edge => edge.Boundary == "CONTINUES_STRUCTURAL_UNIT"
                    && anchors[edge.Right] == "HAS_STRUCTURAL_EXTENT")
                .ToArray();

            totalH2Edges += edges.Length;
            totalComparableEdges += paired.Length;
            foreach (var edge in localConflicts)
            {
                conflicts.Add(new Conflict(pair.DocumentId, edge.Anchor, edge.Left, edge.Right,
                    edge.Boundary, anchors[edge.Right], edge.Ordinal));
            }

            documents.Add(new
            {
                documentId = pair.DocumentId,
                coverage = pair.Coverage,
                g2aRawPath = Relative(g2aPath),
                g2aRawSha256 = Hash(g2aBytes),
                g2aRawResponseSha256 = ReadString(g2a.RootElement, "rawResponseSha256"),
                g2aDecisionCount = anchors.Count,
                h2RawPath = Relative(h2Path),
                h2RawSha256 = Hash(h2Bytes),
                h2RawResponseSha256 = ReadString(h2.RootElement, "rawResponseSha256"),
                h2EdgeCount = edges.Length,
                comparableEdges = paired.Length,
                edgesWithoutRightAnchorDecision = edges.Length - paired.Length,
                conflictCount = localConflicts.Length,
                f1Authority = pair.DocumentId == "SRC-041"
                    ? "F1_RAW_HASH_PINNED_BY_G2A_CAPTURE_AND_EXECUTION_RECEIPT"
                    : "F1_REQUEST_AUTHORITY_HASH_PINNED_BY_G2A_PREFLIGHT; F1_RAW_LEDGER_NOT_PRESENT_IN_THIS_CAPTURE_SET",
            });
        }

        var h2Unavailable = G2AOnlyLedgers.Select(item => new
        {
            documentId = item.DocumentId,
            g2aRawPath = item.RawPath,
            g2aRawSha256 = Hash(File.ReadAllBytes(PathFor(item.RawPath))),
            g2aDecisionCount = item.DecisionCount,
            h2Ledger = "NOT_PRESENT",
            conclusion = "NO_H2_CONFLICT_ABSENCE_INFERENCE",
        }).ToArray();

        Assert.Equal(2, conflicts.Count);
        Assert.Contains(conflicts, row => row.DocumentId == "SRC-089" && row.Left == "O18" && row.Right == "O19");
        Assert.Contains(conflicts, row => row.DocumentId == "SRC-041" && row.Left == "O4" && row.Right == "O5");

        FreezeArtifact.AssertJson(ArtifactRoot, "population-conflict-inventory.v1.json", new
        {
            schemaVersion = "v5-p6th3-raw-population-conflict-inventory-v1",
            status = "FROZEN_RAW_ONLY_BEFORE_GOLD_STRATIFICATION",
            method = "join each available frozen H2 edge to the G2A decision for its right occurrence; conflict iff H2=CONTINUES_STRUCTURAL_UNIT and G2A(right)=HAS_STRUCTURAL_EXTENT",
            rawOnly = true,
            goldRead = false,
            goldUsedForConflictDiscovery = false,
            providerCallsDuringAudit = 0,
            rawCaptureMutation = "NONE",
            runtimeChanged = false,
            coverageSummary = new
            {
                availablePairedDocumentSlices = PairedLedgers.Length,
                availableH2Edges = totalH2Edges,
                edgesWithRightSideG2ADecision = totalComparableEdges,
                conflicts = conflicts.Count,
                fullDocumentAuthorityCompleteLedgers = 0,
                note = "Current captures are targeted challenge/canary slices, not document-wide F1+G2A+H2 ledgers; inventory is complete only over these frozen captures.",
            },
            documents,
            g2aWithoutH2 = h2Unavailable,
            conflicts = conflicts.OrderBy(item => item.DocumentId, StringComparer.Ordinal)
                .ThenBy(item => item.H2Ordinal)
                .Select(item => new
                {
                    documentId = item.DocumentId,
                    anchor = item.Anchor,
                    left = item.Left,
                    right = item.Right,
                    h2Boundary = item.H2Boundary,
                    g2aRight = item.G2ARight,
                    h2Ordinal = item.H2Ordinal,
                }).ToArray(),
            conclusion = "TWO_RAW_CONFLICTS_FOUND_IN_AVAILABLE_CHALLENGE_SLICES; THIS_IS_NOT_A_CORPUS_POPULATION_ESTIMATE",
        });
    }

    private static IEnumerable<JsonElement> ReadDecisions(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var property in path) current = GetProperty(current, property);
        return current.EnumerateArray().Select(item => item.Clone());
    }

    private static IEnumerable<JsonElement> ReadCaptureDecisions(JsonElement root)
    {
        if (TryGetProperty(root, "parsed", out var parsed)
            && TryGetProperty(parsed, "decisions", out var parsedDecisions))
            return parsedDecisions.EnumerateArray().Select(item => item.Clone());

        using var response = JsonDocument.Parse(ReadString(root, "rawResponse"));
        return ReadDecisions(response.RootElement, "decisions").ToArray();
    }

    private static IEnumerable<Edge> ReadH2Decisions(JsonElement root)
    {
        var decisions = ReadCaptureDecisions(root).ToArray();

        return decisions.Select((item, index) => new Edge(
            ReadString(item, "anchor"),
            ReadString(item, "left"),
            ReadString(item, "right"),
            ReadString(item, "boundary"),
            index));
    }

    private static JsonElement GetProperty(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        throw new InvalidDataException($"Missing JSON property {name}.");
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        value = default;
        return false;
    }

    private static string ReadString(JsonElement element, string name) => GetProperty(element, name).GetString()!;
    private static int ReadInt(JsonElement element, string name) => GetProperty(element, name).GetInt32();
    private static string PathFor(string relative) => TestRepository.Path($"{Root}/{relative}");
    private static string Relative(string path) => Path.GetRelativePath(TestRepository.Root(), path).Replace('\\', '/');
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed record LedgerPair(string DocumentId, string G2ARawPath, string H2RawPath, string Coverage);
    private sealed record G2AOnlyLedger(string DocumentId, string RawPath, int DecisionCount);
    private sealed record Edge(string Anchor, string Left, string Right, string Boundary, int Ordinal);
    private sealed record Conflict(string DocumentId, string Anchor, string Left, string Right,
        string H2Boundary, string G2ARight, int H2Ordinal);
}
