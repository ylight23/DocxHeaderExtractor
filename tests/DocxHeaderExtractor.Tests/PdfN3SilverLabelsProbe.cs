using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// N3.2 freezes source-first silver labels without granting them human-gold authority or exposing
/// baseline/R1 candidate and outcome facts to the label artifact.
/// </summary>
public sealed class PdfN3SilverLabelsProbe
{
    private static readonly string[] Stems = ["004", "030", "043", "058"];

    [Fact]
    public void SilverLabelsBindToFrozenV2PopulationAndBlindSourcePackets()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "keys", "benchmark-n3", "manifest.v2.json")));
        var manifestDocuments = manifest.RootElement.GetProperty("documents").EnumerateArray()
            .ToDictionary(document => document.GetProperty("stem").GetString()!, StringComparer.Ordinal);
        Assert.Equal(Stems, manifestDocuments.Keys.OrderBy(stem => stem, StringComparer.Ordinal));

        foreach (var stem in Stems)
        {
            var labelPath = Path.Combine(root, "eval", "benchmark-n3", "silver-labels", $"{stem}-n3.2-silver-model-assisted.v1.json");
            Assert.True(File.Exists(labelPath), $"Missing N3.2 silver label artifact for {stem}.");

            using var label = JsonDocument.Parse(File.ReadAllText(labelPath));
            var labelRoot = label.RootElement;
            var sourcePacket = labelRoot.GetProperty("sourcePacket");
            Assert.Equal("n3_model_assisted_silver_occurrence_labels", labelRoot.GetProperty("artifactKind").GetString());
            Assert.Equal(stem, sourcePacket.GetProperty("stem").GetString());

            var packetRelative = sourcePacket.GetProperty("sourcePacketPath").GetString()!;
            var packetPath = Path.Combine(root, packetRelative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(packetPath), $"Missing blind source packet for {stem}.");
            using var packet = JsonDocument.Parse(File.ReadAllText(packetPath));
            Assert.Equal(stem, packet.RootElement.GetProperty("Stem").GetString());
            Assert.Equal(manifestDocuments[stem].GetProperty("sourceDocumentSha256").GetString(), packet.RootElement.GetProperty("documentSha256").GetString());

            var provenance = labelRoot.GetProperty("provenance");
            Assert.Equal("MODEL_ASSISTED_SILVER", provenance.GetProperty("labelSource").GetString());
            Assert.False(provenance.GetProperty("humanAdjudicated").GetBoolean());
            Assert.Equal("SILVER_PROXY_ONLY", provenance.GetProperty("accuracyClaim").GetString());

            var packetItems = packet.RootElement.GetProperty("items").EnumerateArray().ToArray();
            var packetLineIds = packetItems.Select(item => item.GetProperty("lineId").GetString()!).ToHashSet(StringComparer.Ordinal);
            var headings = labelRoot.GetProperty("headingOccurrences").EnumerateArray().ToArray();
            var uncertain = labelRoot.GetProperty("uncertainOccurrences").EnumerateArray().ToArray();
            var occurrences = headings.Concat(uncertain).ToArray();
            var stableIds = occurrences.Select(occurrence => occurrence.GetProperty("goldStableId").GetString()).ToArray();

            var summary = labelRoot.GetProperty("summary");
            Assert.Equal(packetItems.Length, summary.GetProperty("totalSourceItemsReviewed").GetInt32());
            Assert.Equal(headings.Length, summary.GetProperty("headingOccurrenceCount").GetInt32());
            Assert.Equal(uncertain.Length, summary.GetProperty("uncertainOccurrenceCount").GetInt32());
            Assert.Equal(stableIds.Length, stableIds.Distinct(StringComparer.Ordinal).Count());

            foreach (var occurrence in headings)
            {
                var lineIds = occurrence.GetProperty("sourceLineIds").EnumerateArray().Select(line => line.GetString()!).ToArray();
                Assert.NotEmpty(lineIds);
                Assert.All(lineIds, lineId => Assert.Contains(lineId, packetLineIds));
                var span = occurrence.GetProperty("sourceSpan");
                Assert.Equal(lineIds[0], span.GetProperty("startLineId").GetString());
                Assert.Equal(lineIds[^1], span.GetProperty("endLineId").GetString());
            }

            foreach (var occurrence in uncertain)
            {
                var lineIds = occurrence.GetProperty("sourceLineIds").EnumerateArray().Select(line => line.GetString()!).ToArray();
                Assert.NotEmpty(lineIds);
                Assert.All(lineIds, lineId => Assert.Contains(lineId, packetLineIds));
            }

            AssertNoForbiddenPipelineFields(labelRoot);
        }
    }

    private static void AssertNoForbiddenPipelineFields(JsonElement value)
    {
        var forbidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "candidateId", "candidateScore", "rank", "selected", "selectedAt160", "structuralScope",
            "domainRole", "analystOutput", "validatedOutput", "validatedSpan", "baselineOutput", "r1Output",
        };

        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                Assert.DoesNotContain(property.Name, forbidden);
                AssertNoForbiddenPipelineFields(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) AssertNoForbiddenPipelineFields(item);
        }
    }
}
