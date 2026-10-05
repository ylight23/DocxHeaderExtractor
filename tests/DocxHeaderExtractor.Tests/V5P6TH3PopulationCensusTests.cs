using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Normalizes available F1, G2A and H2 captures into an edge census.  This is deliberately
/// provider-free and reports missing upstream ledgers rather than treating their absence as a
/// non-conflict.
/// </summary>
public sealed class V5P6TH3PopulationCensusTests
{
    private const string Root = "artifacts/v5-p6t-function-membership";
    private const string ArtifactRoot = Root + "/p6th3-population-census";

    private static readonly Source[] Sources =
    [
        new("SRC-089", "p6tf1-preflight/retry-src089-result.v1.json", F1RowKind.ResultRow,
            "p6tg2a-anchor-existence-canary-20261004/SRC-089.raw-capture.v1.json",
            "p6th2-function-conditioned-continuation-canary-20261004/SRC-089.raw-capture.v1.json"),
        new("SRC-041", "p6te-src041-e-challenge/f1.raw-capture.v1.json", F1RowKind.RawCapture,
            "p6te-src041-e-challenge/g2a.raw-capture.v1.json",
            "p6te-src041-h2-challenge/h2.raw-capture.v1.json"),
        new("SRC-095", "p6tf1-preflight/result.v1.json", F1RowKind.ResultRows,
            null, null),
        new("DOC-0252", "p6te-doc0252-e-challenge/f1.raw-capture.v1.json", F1RowKind.RawCapture,
            "p6te-doc0252-e-challenge/g2a.raw-capture.v1.json", null),
        new("DOC-0256", "p6te-doc0256-e-challenge/f1.raw-capture.v1.json", F1RowKind.RawCapture,
            "p6te-doc0256-e-challenge/g2a.raw-capture.v1.json", null),
    ];

    [Fact]
    public void Frozen_F1_G2A_H2_captures_are_joined_by_document_pack_left_and_right_without_Gold()
    {
        var allEdges = new List<EdgeRow>();
        var documents = new List<DocumentRow>();

        foreach (var source in Sources)
        {
            var f1 = LoadF1(source);
            var g2a = LoadG2A(source.G2APath);
            var h2 = LoadH2(source.H2Path);
            var edges = f1.Occurrences.Zip(f1.Occurrences.Skip(1), (left, right) => new { left, right })
                .Select((pair, ordinal) => Classify(source.DocumentId, f1.PackId, pair.left, pair.right, ordinal, g2a, h2))
                .ToArray();
            allEdges.AddRange(edges);

            documents.Add(new DocumentRow(
                source.DocumentId, f1.PackId,
                f1.Occurrences.Count, edges.Length,
                f1.RawPath, f1.RawSha256,
                g2a.Path, g2a.RawSha256, g2a.Decisions.Count,
                h2.Path, h2.RawSha256, h2.UniqueEdges.Count,
                g2a.Decisions.Count == f1.Occurrences.Count ? "FULL_PACK" : g2a.Decisions.Count == 0 ? "MISSING" : "CHALLENGE_SLICE",
                h2.UniqueEdges.Count == edges.Length ? "FULL_PACK" : h2.UniqueEdges.Count == 0 ? "MISSING" : "CHALLENGE_SLICE",
                edges.Count(item => item.Pairable),
                edges.Count(item => item.Status == "UNPAIRED_NO_G2A_RIGHT"),
                edges.Count(item => item.Status == "UNPAIRED_NO_H2"),
                edges.Count(item => item.Status == "CONFLICT"),
                edges.Count(item => item.Status == "NON_CONFLICT")));
        }

        Assert.Equal(5, documents.Count);
        Assert.Equal(0, documents.Count(item => item.G2ACoverage == "FULL_PACK" && item.H2Coverage == "FULL_PACK"));
        Assert.Contains(documents, item => item.DocumentId == "SRC-095" && item.G2ACoverage == "MISSING" && item.H2Coverage == "MISSING");
        Assert.Equal(2, allEdges.Count(item => item.Status == "CONFLICT"));
        Assert.Equal(0, allEdges.Count(item => (item.Status is "CONFLICT" or "NON_CONFLICT")
            && new[] { "SRC-095", "DOC-0252", "DOC-0256" }.Contains(item.DocumentId, StringComparer.Ordinal)));

        var pairable = allEdges.Count(item => item.Pairable);
        FreezeArtifact.AssertJson(ArtifactRoot, "f1-g2a-h2-edge-census.v1.json", new
        {
            schemaVersion = "v5-p6th3-f1-g2a-h2-edge-census-v1",
            status = "FROZEN_PROVIDER_FREE_CENSUS",
            joinKey = "(documentId, packId, leftOccurrence, rightOccurrence)",
            inputs = new { f1 = "TOTAL_FUNCTION_MEMBERSHIP_LEDGER", g2a = "ANCHOR_EXISTENCE_LEDGER_ON_RIGHT", h2 = "CONTINUATION_BOUNDARY_LEDGER_ON_LEFT_TO_RIGHT" },
            noGold = new { goldRead = false, goldMutation = "NONE", goldUsedForCohortConstruction = false },
            providerCallsDuringCensus = 0,
            runtimeChanged = false,
            statusDefinitions = new
            {
                PAIRABLE_EDGE = "Both G2A(right) and H2(left,right) are present; the mutually exclusive outcome is CONFLICT or NON_CONFLICT.",
                UNPAIRED_NO_G2A_RIGHT = "H2 may be present but there is no G2A decision for right; not eligible for conflict classification.",
                UNPAIRED_NO_H2 = "G2A(right) exists but no H2 decision is present for the edge.",
                CONFLICT = "PAIRABLE_EDGE with G2A(right)=HAS_STRUCTURAL_EXTENT and H2=CONTINUES_STRUCTURAL_UNIT.",
                NON_CONFLICT = "PAIRABLE_EDGE that does not meet conflict predicate.",
            },
            denominator = new
            {
                pairableEdges = pairable,
                conflicts = allEdges.Count(item => item.Status == "CONFLICT"),
                conflictRate = "NOT_QUALIFICATION_VALID: no document has full-pack G2A and H2 ledgers",
            },
            gapMatrix = documents.OrderBy(item => item.DocumentId, StringComparer.Ordinal).Select(item => new
            {
                documentId = item.DocumentId, packId = item.PackId,
                f1 = new { status = "FULL_PACK", decisions = item.F1Occurrences, rawPath = item.F1Path, rawSha256 = item.F1RawSha256 },
                g2a = new { coverage = item.G2ACoverage, decisions = item.G2ADecisions, rawPath = item.G2APath, rawSha256 = item.G2ARawSha256 },
                h2 = new { coverage = item.H2Coverage, uniqueEdges = item.H2Edges, rawPath = item.H2Path, rawSha256 = item.H2RawSha256 },
                categories = new
                {
                    pairableEdge = item.PairableEdges,
                    unpairedNoG2ARight = item.NoG2ARight,
                    unpairedNoH2 = item.NoH2,
                    conflict = item.Conflicts,
                    nonConflict = item.NonConflicts,
                },
            }).ToArray(),
            edges = allEdges.OrderBy(item => item.DocumentId, StringComparer.Ordinal).ThenBy(item => item.Ordinal).Select(item => new
            {
                documentId = item.DocumentId, packId = item.PackId, left = item.Left, right = item.Right,
                ordinal = item.Ordinal, pairableEdge = item.Pairable, status = item.Status, g2aRight = item.G2ARight, h2Boundary = item.H2Boundary,
            }).ToArray(),
            conclusion = "SRC095_HAS_F1_BUT_NO_G2A_OR_H2_LEDGER; DOC0252_AND_DOC0256_HAVE_G2A_CHALLENGE_SLICES_BUT_NO_H2; NO_FULL_PACK_DENOMINATOR_EXISTS_YET",
        });
    }

    private static EdgeRow Classify(string documentId, string packId, string left, string right, int ordinal,
        G2ALedger g2a, H2Ledger h2)
    {
        var hasG2A = g2a.Decisions.TryGetValue(right, out var anchor);
        var key = $"{left}|{right}";
        var hasH2 = h2.UniqueEdges.TryGetValue(key, out var boundary);
        var status = !hasG2A ? "UNPAIRED_NO_G2A_RIGHT"
            : !hasH2 ? "UNPAIRED_NO_H2"
            : anchor == "HAS_STRUCTURAL_EXTENT" && boundary == "CONTINUES_STRUCTURAL_UNIT" ? "CONFLICT"
            : "NON_CONFLICT";
        return new EdgeRow(documentId, packId, left, right, ordinal, hasG2A && hasH2, status, anchor, boundary);
    }

    private static F1Ledger LoadF1(Source source)
    {
        var path = PathOf(source.F1Path);
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var root = json.RootElement;
        var row = source.Kind switch
        {
            F1RowKind.RawCapture => root,
            F1RowKind.ResultRow => root.GetProperty("row"),
            F1RowKind.ResultRows => root.GetProperty("rows").EnumerateArray().Single(item => item.GetProperty("documentId").GetString() == source.DocumentId),
            _ => throw new InvalidOperationException("unknown-f1-row-kind"),
        };
        var response = row.GetProperty("rawResponse").GetString();
        Assert.False(string.IsNullOrWhiteSpace(response), $"missing accepted F1 response:{source.DocumentId}");
        using var decisions = JsonDocument.Parse(response!);
        var occurrences = decisions.RootElement.GetProperty("decisions").EnumerateArray()
            .Select(item => item.GetProperty("occurrence").GetString()!).ToArray();
        Assert.Equal(96, occurrences.Length);
        Assert.Equal(96, occurrences.Distinct(StringComparer.Ordinal).Count());
        var packId = row.TryGetProperty("packId", out var pack) ? pack.GetString()! : ReadString(root, "sourcePack");
        return new F1Ledger(packId, occurrences, Relative(path), Hash(File.ReadAllBytes(path)));
    }

    private static G2ALedger LoadG2A(string? relative)
    {
        if (relative is null) return new G2ALedger(null, null, new Dictionary<string, string>(StringComparer.Ordinal));
        var path = PathOf(relative);
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var decisions = ReadDecisions(json.RootElement).ToDictionary(
            item => ReadString(item, "primary"), item => ReadString(item, "anchor"), StringComparer.Ordinal);
        return new G2ALedger(Relative(path), Hash(File.ReadAllBytes(path)), decisions);
    }

    private static H2Ledger LoadH2(string? relative)
    {
        if (relative is null) return new H2Ledger(null, null, new Dictionary<string, string>(StringComparer.Ordinal));
        var path = PathOf(relative);
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var decisions = ReadDecisions(json.RootElement).ToArray();
        var edges = decisions.GroupBy(item => $"{ReadString(item, "left")}|{ReadString(item, "right")}", StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group =>
            {
                var boundaries = group.Select(item => ReadString(item, "boundary")).Distinct(StringComparer.Ordinal).ToArray();
                Assert.Single(boundaries); // challenge H2 is anchor-scoped, but must not disagree for one normalized edge.
                return boundaries[0];
            }, StringComparer.Ordinal);
        return new H2Ledger(Relative(path), Hash(File.ReadAllBytes(path)), edges);
    }

    private static IEnumerable<JsonElement> ReadDecisions(JsonElement root)
    {
        if (root.TryGetProperty("parsed", out var parsed) && parsed.TryGetProperty("decisions", out var materialized))
            return materialized.EnumerateArray().Select(item => item.Clone()).ToArray();
        using var response = JsonDocument.Parse(root.GetProperty("rawResponse").GetString()!);
        return response.RootElement.GetProperty("decisions").EnumerateArray().Select(item => item.Clone()).ToArray();
    }

    private static string ReadString(JsonElement element, string property)
    {
        foreach (var item in element.EnumerateObject())
            if (string.Equals(item.Name, property, StringComparison.OrdinalIgnoreCase)) return item.Value.GetString()!;
        throw new InvalidDataException($"missing-json-property:{property}");
    }

    private static string PathOf(string relative) => TestRepository.Path($"{Root}/{relative}");
    private static string Relative(string path) => Path.GetRelativePath(TestRepository.Root(), path).Replace('\\', '/');
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed record Source(string DocumentId, string F1Path, F1RowKind Kind, string? G2APath, string? H2Path);
    private enum F1RowKind { RawCapture, ResultRow, ResultRows }
    private sealed record F1Ledger(string PackId, IReadOnlyList<string> Occurrences, string RawPath, string RawSha256);
    private sealed record G2ALedger(string? Path, string? RawSha256, IReadOnlyDictionary<string, string> Decisions);
    private sealed record H2Ledger(string? Path, string? RawSha256, IReadOnlyDictionary<string, string> UniqueEdges);
    private sealed record EdgeRow(string DocumentId, string PackId, string Left, string Right, int Ordinal, bool Pairable, string Status, string? G2ARight, string? H2Boundary);
    private sealed record DocumentRow(string DocumentId, string PackId, int F1Occurrences, int TotalEdges,
        string F1Path, string F1RawSha256, string? G2APath, string? G2ARawSha256, int G2ADecisions,
        string? H2Path, string? H2RawSha256, int H2Edges, string G2ACoverage, string H2Coverage,
        int PairableEdges, int NoG2ARight, int NoH2, int Conflicts, int NonConflicts);
}
