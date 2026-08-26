using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// N1.2 is the human labeling authority boundary: for each N1.1 blind source packet, a reviewer who
/// never saw candidate/rank/selection/scope/role/model output assigns REVIEWED_HEADING /
/// REVIEWED_NON_HEADING / UNCERTAIN to every source line, in occurrence-level identity (source line
/// ids and span, never candidate id). This locks that the committed gold for a document is still
/// bound to that document's exact N1.1 packet and is internally consistent - not that the labels
/// themselves are correct, which is a human judgment outside this test's reach.
/// </summary>
public sealed class PdfN12ReviewedOccurrenceGoldProbe
{
    private static readonly string[] AllowedLabels = ["REVIEWED_HEADING", "REVIEWED_NON_HEADING", "UNCERTAIN"];

    [Theory]
    [InlineData("003")]
    public void CommittedGoldBindsToItsPacketAndIsInternallyConsistent(string stem)
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var packetPath = Path.Combine(root, "eval", "benchmark-n0", "source-packets", $"{stem}-blind-source-review.v1.json");
        var goldPath = Path.Combine(root, "eval", "benchmark-n0", "reviewed-gold", $"{stem}-n1.2-reviewed-occurrence-gold.v1.json");
        Assert.True(File.Exists(packetPath));
        Assert.True(File.Exists(goldPath));

        using var packet = JsonDocument.Parse(File.ReadAllText(packetPath));
        using var gold = JsonDocument.Parse(File.ReadAllText(goldPath));
        var packetRoot = packet.RootElement;
        var goldRoot = gold.RootElement;

        // Identity: the gold must name the exact packet it labeled, not a stem match by convention.
        var sourcePacket = goldRoot.GetProperty("sourcePacket");
        Assert.Equal(packetRoot.GetProperty("documentSha256").GetString(), sourcePacket.GetProperty("documentSha256").GetString());
        Assert.Equal(packetRoot.GetProperty("parentManifestSha256").GetString(), sourcePacket.GetProperty("parentManifestSha256").GetString());
        Assert.Equal(packetRoot.GetProperty("sourceLineExtractionFingerprint").GetString(), sourcePacket.GetProperty("sourceLineExtractionFingerprint").GetString());

        // Every packet item is labeled, in the same order, with none invented and none dropped.
        var packetItems = packetRoot.GetProperty("items").EnumerateArray().ToArray();
        var reviewedItems = goldRoot.GetProperty("reviewedItems").EnumerateArray().ToArray();
        Assert.Equal(packetItems.Length, reviewedItems.Length);
        for (var i = 0; i < packetItems.Length; i++)
        {
            Assert.Equal(packetItems[i].GetProperty("reviewItemId").GetString(), reviewedItems[i].GetProperty("reviewItemId").GetString());
            Assert.Equal(packetItems[i].GetProperty("lineId").GetString(), reviewedItems[i].GetProperty("lineId").GetString());
            Assert.Equal(packetItems[i].GetProperty("page").GetInt32(), reviewedItems[i].GetProperty("page").GetInt32());
            Assert.Contains(reviewedItems[i].GetProperty("label").GetString(), AllowedLabels);
        }

        // Occurrence-level identity: goldStableId is unique, and every source line id it names is a
        // real line from this packet - the gold cannot invent identity outside the blind packet.
        var occurrences = goldRoot.GetProperty("headingOccurrences").EnumerateArray().ToArray();
        var stableIds = occurrences.Select(o => o.GetProperty("goldStableId").GetString()).ToArray();
        Assert.Equal(stableIds.Length, stableIds.Distinct(StringComparer.Ordinal).Count());

        var packetLineIds = packetItems.Select(item => item.GetProperty("lineId").GetString()).ToHashSet(StringComparer.Ordinal);
        foreach (var occurrence in occurrences)
            foreach (var lineId in occurrence.GetProperty("sourceLineIds").EnumerateArray())
                Assert.Contains(lineId.GetString(), packetLineIds);

        // Mutual coverage: a REVIEWED_HEADING line belongs to exactly the occurrences that name it,
        // and no other line is silently swept into an occurrence's span.
        var occurrenceLineIds = occurrences
            .SelectMany(o => o.GetProperty("sourceLineIds").EnumerateArray().Select(l => l.GetString()!))
            .ToHashSet(StringComparer.Ordinal);
        var reviewedHeadingLineIds = reviewedItems
            .Where(item => item.GetProperty("label").GetString() == "REVIEWED_HEADING")
            .Select(item => item.GetProperty("lineId").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(reviewedHeadingLineIds, occurrenceLineIds);

        // The summary is a derived cache, not a second authority - it must reproduce from reviewedItems.
        var summary = goldRoot.GetProperty("summary");
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

        // No pipeline-inferred field leaked into a document meant to stay source-only.
        AssertNoForbiddenPipelineProperty(goldRoot);
    }

    [Fact]
    public void FourthDocumentGoldIsNotYetCommitted()
    {
        // Persist-per-document is deliberate: freeze and commit 003/029/042/057 independently as each
        // is reviewed, rather than holding all four in an uncommitted working state. This lock simply
        // records where that sequence currently stands so a future run cannot silently assume all four
        // are done.
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var goldDirectory = Path.Combine(root, "eval", "benchmark-n0", "reviewed-gold");
        var committed = Directory.Exists(goldDirectory)
            ? Directory.GetFiles(goldDirectory, "*-n1.2-reviewed-occurrence-gold.v1.json").Select(Path.GetFileName).ToArray()
            : [];

        Assert.Contains("003-n1.2-reviewed-occurrence-gold.v1.json", committed);
        Assert.DoesNotContain("029-n1.2-reviewed-occurrence-gold.v1.json", committed);
        Assert.DoesNotContain("042-n1.2-reviewed-occurrence-gold.v1.json", committed);
        Assert.DoesNotContain("057-n1.2-reviewed-occurrence-gold.v1.json", committed);
    }

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
