using System.Security.Cryptography;
using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// N1.1 materializes a blind source-review packet for the population frozen by N0. The packet is
/// intentionally built from source lines only: no candidate, rank, scope, model, or validated output
/// is read or serialized. Labels belong to a later, separately committed N1.2 artifact.
/// </summary>
public sealed class PdfN1SourceFirstReviewPacketProbe
{
    private const int ContextRadius = 2;

    [Fact]
    public void WritePackets()
    {
        var outputDirectory = Environment.GetEnvironmentVariable("BENCH_N1_PACKET_DIRECTORY");
        if (string.IsNullOrWhiteSpace(outputDirectory)) return;

        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var manifestPath = Path.Combine(root, "keys", "benchmark-n0", "manifest.json");
        var manifestSha256 = BenchmarkManifestHash.ComputeCanonicalSha256(manifestPath);
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        Directory.CreateDirectory(outputDirectory);

        foreach (var document in manifest.RootElement.GetProperty("documents").EnumerateArray())
        {
            var stem = document.GetProperty("stem").GetString()!;
            var relativePath = document.GetProperty("file").GetString()!;
            var documentPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var documentSha256 = document.GetProperty("sourceDocumentSha256").GetString()!;
            Assert.Equal(documentSha256, Sha256(documentPath));

            // BuildCandidateRankingSnapshot is the established PDF-source reader. Only Lines is read;
            // candidate blocks, provenance, annotations, ranking, and all downstream decisions remain
            // deliberately outside this packet.
            var lines = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(documentPath).Lines;
            var payload = new
            {
                schemaVersion = 1,
                artifactKind = "n1_blind_source_review_packet",
                parentManifestSha256 = manifestSha256,
                documentId = Path.GetFileNameWithoutExtension(documentPath),
                documentSha256,
                sourceLineExtractionFingerprint = SourceFingerprint(lines),
                reviewInstructions = new
                {
                    question = "Which source occurrences are document outline headings?",
                    allowedLabels = new[] { "REVIEWED_HEADING", "REVIEWED_NON_HEADING", "UNCERTAIN" },
                    occurrenceRule = "A wrapped heading is one occurrence made of every participating source line; preserve all line ids and its source span.",
                    prohibitedEvidence = new[] { "candidate id", "candidate score", "rank", "selected status", "pipeline-inferred scope", "domain role", "analyst output", "validated output" },
                },
                items = lines.Select((line, index) => new
                {
                    reviewItemId = $"{stem}/line/{index:D6}",
                    page = line.Page,
                    lineId = PdfCandidateProvenance.LineId(line),
                    sourceSpan = new { startLineId = PdfCandidateProvenance.LineId(line), endLineId = PdfCandidateProvenance.LineId(line) },
                    text = line.Text,
                    previousLines = Neighbors(lines, index, -1),
                    nextLines = Neighbors(lines, index, 1),
                }),
            };

            var output = Path.Combine(outputDirectory, $"{stem}-blind-source-review.v1.json");
            File.WriteAllText(output, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    [Fact]
    public void CommittedPacketsBindToN0AndContainNoPipelineFields()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var packetDirectory = Path.Combine(root, "eval", "benchmark-n0", "source-packets");
        if (!Directory.Exists(packetDirectory)) return;

        var manifestSha256 = BenchmarkManifestHash.ComputeCanonicalSha256(Path.Combine(root, "keys", "benchmark-n0", "manifest.json"));
        var packets = Directory.GetFiles(packetDirectory, "*-blind-source-review.v1.json").OrderBy(path => path).ToArray();
        Assert.Equal(4, packets.Length);

        foreach (var path in packets)
        {
            using var packet = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal("n1_blind_source_review_packet", packet.RootElement.GetProperty("artifactKind").GetString());
            Assert.Equal(manifestSha256, packet.RootElement.GetProperty("parentManifestSha256").GetString());
            Assert.Equal(JsonValueKind.Array, packet.RootElement.GetProperty("items").ValueKind);

            AssertNoForbiddenPipelineProperty(packet.RootElement);
        }
    }

    private static object[] Neighbors(IReadOnlyList<PdfLine> lines, int index, int direction)
    {
        var result = new List<object>();
        for (var offset = 1; offset <= ContextRadius; offset++)
        {
            var neighborIndex = index + (direction * offset);
            if (neighborIndex < 0 || neighborIndex >= lines.Count) break;
            var neighbor = lines[neighborIndex];
            result.Add(new { page = neighbor.Page, lineId = PdfCandidateProvenance.LineId(neighbor), text = neighbor.Text });
        }
        return result.ToArray();
    }

    private static string SourceFingerprint(IReadOnlyList<PdfLine> lines) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            string.Join('\n', lines.Select((line, index) => $"{index}|{PdfCandidateProvenance.LineId(line)}"))))).ToLowerInvariant();

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static void AssertNoForbiddenPipelineProperty(JsonElement value)
    {
        var forbidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "candidateId", "candidateScore", "rank", "selected", "selectedAt160", "structuralScope",
            "domainRole", "analystOutput", "validatedOutput", "validatedSpan",
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
