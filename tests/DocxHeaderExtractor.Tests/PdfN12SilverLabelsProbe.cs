using System.Security.Cryptography;
using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// N1.2's earlier committed artifacts named `003`'s labels human-reviewed gold; that claim was false
/// (the labels were model-assisted) and is withdrawn. N1.2 is now N1.2-S: every document's occurrence
/// labels are `MODEL_ASSISTED_SILVER`, `humanAdjudicated: false`, and their accuracy claim is
/// explicitly `SILVER_PROXY_ONLY`. This locks that each committed silver file still binds to its
/// exact N1.1 packet and is internally consistent - not that the labels themselves are correct, which
/// requires the human audit sample N1.2-S does not itself provide.
/// </summary>
public sealed class PdfN12SilverLabelsProbe
{
    private static readonly string[] Stems = ["003", "029", "042", "057"];
    private static readonly string[] AllowedLabels = ["REVIEWED_HEADING", "REVIEWED_NON_HEADING", "UNCERTAIN"];

    [Theory]
    [InlineData("003")]
    [InlineData("029")]
    [InlineData("042")]
    [InlineData("057")]
    public void CommittedSilverBindsToItsPacketAndIsInternallyConsistent(string stem)
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var packetPath = Path.Combine(root, "eval", "benchmark-n0", "source-packets", $"{stem}-blind-source-review.v1.json");
        var silverPath = Path.Combine(root, "eval", "benchmark-n0", "silver-labels", $"{stem}-n1.2-silver-model-assisted.v1.json");
        Assert.True(File.Exists(packetPath));
        Assert.True(File.Exists(silverPath));

        using var packet = JsonDocument.Parse(File.ReadAllText(packetPath));
        using var silver = JsonDocument.Parse(File.ReadAllText(silverPath));
        var packetRoot = packet.RootElement;
        var silverRoot = silver.RootElement;

        // Identity: 003 nests these under `sourcePacket`; 029/042/057 keep them at the top level. Both
        // shapes must still name the exact packet this file was labeled from.
        var (documentSha, fingerprint, parentManifestSha) = silverRoot.TryGetProperty("sourcePacket", out var sp)
            ? (sp.GetProperty("documentSha256").GetString(), sp.GetProperty("sourceLineExtractionFingerprint").GetString(), sp.GetProperty("parentManifestSha256").GetString())
            : (silverRoot.GetProperty("documentSha256").GetString(), silverRoot.GetProperty("sourceLineExtractionFingerprint").GetString(), silverRoot.GetProperty("sourcePacketParentManifestSha256").GetString());
        Assert.Equal(packetRoot.GetProperty("documentSha256").GetString(), documentSha);
        Assert.Equal(packetRoot.GetProperty("sourceLineExtractionFingerprint").GetString(), fingerprint);
        Assert.Equal(packetRoot.GetProperty("parentManifestSha256").GetString(), parentManifestSha);

        // Provenance must say silver, explicitly, not gold.
        var authority = silverRoot.GetProperty("labelingAuthority");
        Assert.Equal("MODEL_ASSISTED_SILVER", authority.GetProperty("labelSource").GetString());
        Assert.False(authority.GetProperty("humanAdjudicated").GetBoolean());
        Assert.Equal("SILVER_PROXY_ONLY", authority.GetProperty("accuracyClaim").GetString());

        // Every packet item is labeled, in the same order, with none invented and none dropped.
        var packetItems = packetRoot.GetProperty("items").EnumerateArray().ToArray();
        var reviewedItems = silverRoot.GetProperty("reviewedItems").EnumerateArray().ToArray();
        Assert.Equal(packetItems.Length, reviewedItems.Length);
        for (var i = 0; i < packetItems.Length; i++)
        {
            Assert.Equal(packetItems[i].GetProperty("reviewItemId").GetString(), reviewedItems[i].GetProperty("reviewItemId").GetString());
            Assert.Equal(packetItems[i].GetProperty("lineId").GetString(), reviewedItems[i].GetProperty("lineId").GetString());
            Assert.Contains(reviewedItems[i].GetProperty("label").GetString(), AllowedLabels);
        }

        // Occurrence identity: stable id is unique, and every source line it names is a real packet line.
        var occurrences = silverRoot.GetProperty("headingOccurrences").EnumerateArray().ToArray();
        var stableIds = occurrences
            .Select(o => o.TryGetProperty("goldStableId", out var g) ? g.GetString() : o.GetProperty("silverStableId").GetString())
            .ToArray();
        Assert.Equal(stableIds.Length, stableIds.Distinct(StringComparer.Ordinal).Count());

        var packetLineIds = packetItems.Select(item => item.GetProperty("lineId").GetString()).ToHashSet(StringComparer.Ordinal);
        foreach (var occurrence in occurrences)
            foreach (var lineId in occurrence.GetProperty("sourceLineIds").EnumerateArray())
                Assert.Contains(lineId.GetString(), packetLineIds);

        // Mutual coverage: a REVIEWED_HEADING line belongs to exactly the occurrences that name it.
        var occurrenceLineIds = occurrences
            .SelectMany(o => o.GetProperty("sourceLineIds").EnumerateArray().Select(l => l.GetString()!))
            .ToHashSet(StringComparer.Ordinal);
        var reviewedHeadingLineIds = reviewedItems
            .Where(item => item.GetProperty("label").GetString() == "REVIEWED_HEADING")
            .Select(item => item.GetProperty("lineId").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(reviewedHeadingLineIds, occurrenceLineIds);

        // The summary is a derived cache, not a second authority - it must reproduce from reviewedItems.
        var summary = silverRoot.GetProperty("summary");
        Assert.Equal(reviewedItems.Length, summary.GetProperty("totalSourceItemsReviewed").GetInt32());
        Assert.Equal(occurrences.Length, summary.GetProperty("headingOccurrenceCount").GetInt32());
        Assert.Equal(
            reviewedItems.Count(item => item.GetProperty("label").GetString() == "REVIEWED_HEADING"),
            summary.GetProperty("headingSourceLineCount").GetInt32());
        Assert.Equal(
            reviewedItems.Count(item => item.GetProperty("label").GetString() == "REVIEWED_NON_HEADING"),
            summary.GetProperty("nonHeadingSourceLineCount").GetInt32());
        Assert.Equal(
            reviewedItems.Count(item => item.GetProperty("label").GetString() == "UNCERTAIN"),
            summary.GetProperty("uncertainSourceLineCount").GetInt32());

        AssertNoForbiddenPipelineProperty(silverRoot);
    }

    [Fact]
    public void BundleManifestHashesBindToTheCommittedFourAndAllFourAreSilverNotGold()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var manifestPath = Path.Combine(root, "eval", "benchmark-n0", "silver-labels", "n1.2-silver-bundle-manifest.v1.json");
        Assert.True(File.Exists(manifestPath));

        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var manifestRoot = manifest.RootElement;
        Assert.Equal("MODEL_ASSISTED_SILVER", manifestRoot.GetProperty("labelSource").GetString());
        Assert.False(manifestRoot.GetProperty("humanAdjudicated").GetBoolean());
        Assert.Equal("SILVER_PROXY_ONLY", manifestRoot.GetProperty("accuracyClaim").GetString());

        var documents = manifestRoot.GetProperty("documents").EnumerateArray().ToArray();
        Assert.Equal(Stems.Length, documents.Length);
        Assert.Equal(Stems, documents.Select(d => d.GetProperty("stem").GetString()).OrderBy(s => s, StringComparer.Ordinal));

        foreach (var doc in documents)
        {
            var stem = doc.GetProperty("stem").GetString()!;
            var silverPath = Path.Combine(root, "eval", "benchmark-n0", "silver-labels", doc.GetProperty("file").GetString()!);
            Assert.True(File.Exists(silverPath));
            Assert.Equal(Sha256(silverPath), doc.GetProperty("sha256").GetString());

            using var silver = JsonDocument.Parse(File.ReadAllText(silverPath));
            var silverSummary = silver.RootElement.GetProperty("summary");
            var manifestSummary = doc.GetProperty("summary");
            Assert.Equal(silverSummary.GetProperty("headingOccurrenceCount").GetInt32(), manifestSummary.GetProperty("headingOccurrenceCount").GetInt32());
            Assert.Equal(silverSummary.GetProperty("headingSourceLineCount").GetInt32(), manifestSummary.GetProperty("headingSourceLineCount").GetInt32());
        }
    }

    /// <summary>No gold artifact remains for 003. Its earlier claim was retracted, not left standing.</summary>
    [Fact]
    public void NoHumanGoldArtifactIsCommittedForAnyDocument()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        Assert.False(Directory.Exists(Path.Combine(root, "eval", "benchmark-n0", "reviewed-gold")));
        Assert.False(Directory.Exists(Path.Combine(root, "eval", "benchmark-n0", "phase-manifests")));
    }

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static void AssertNoForbiddenPipelineProperty(JsonElement value)
    {
        var forbidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "candidateId", "candidateScore", "rank", "selected", "selectedAt160", "structuralScope",
            "domainRole", "analystOutput", "validatedOutput", "validatedSpan", "semanticLaneStatus",
            "spanLaneStatus",
        };

        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                Assert.DoesNotContain(property.Name, forbidden);
                AssertNoForbiddenPipelineProperty(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) AssertNoForbiddenPipelineProperty(item);
        }
    }
}
