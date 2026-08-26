using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>N3 is a source-first holdout bootstrap. Selection consumes corpus location and stable
/// identifiers only; it deliberately does not inspect candidate/ranking/model outcome data.</summary>
public sealed class PdfN3FreshHoldoutBootstrapProbe
{
    private const int ContextRadius = 2;
    private static readonly string[] Strata = ["01_phap_quy", "02_hop_dong_mua_sam", "03_tai_chinh_ke_toan", "04_giao_trinh"];
    private static readonly HashSet<string> Excluded = new(StringComparer.Ordinal)
    {
        "001", "003", "028", "029", "032", "041", "042", "054", "056", "057", "091", "092",
    };

    [Fact]
    public void WriteBootstrapAndPackets()
    {
        var outputRoot = Environment.GetEnvironmentVariable("BENCH_N3_OUTPUT_ROOT");
        if (string.IsNullOrWhiteSpace(outputRoot)) return;
        WriteAll(PdfExtractorQualityBenchmarkProbe.RepositoryRoot(), outputRoot);
    }

    [Fact]
    public void CommittedBootstrapAndPacketsReproduce()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var expected = new[]
        {
            Path.Combine(root, "keys", "benchmark-n3", "manifest.v1.json"),
            Path.Combine(root, "eval", "benchmark-n3", "source-packets", "002-blind-source-review.v1.json"),
            Path.Combine(root, "eval", "benchmark-n3", "source-packets", "026-blind-source-review.v1.json"),
            Path.Combine(root, "eval", "benchmark-n3", "source-packets", "043-blind-source-review.v1.json"),
            Path.Combine(root, "eval", "benchmark-n3", "source-packets", "058-blind-source-review.v1.json"),
        };
        if (expected.Any(path => !File.Exists(path))) return;

        var temp = Path.Combine(Path.GetTempPath(), "dhx-n3-" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteAll(root, temp);
            Assert.Equal(Normalize(File.ReadAllText(expected[0])), Normalize(File.ReadAllText(Path.Combine(temp, "keys", "benchmark-n3", "manifest.v1.json"))));
            foreach (var path in expected.Skip(1))
            {
                var name = Path.GetFileName(path);
                Assert.Equal(Normalize(File.ReadAllText(path)), Normalize(File.ReadAllText(Path.Combine(temp, "eval", "benchmark-n3", "source-packets", name))));
            }
        }
        finally { if (Directory.Exists(temp)) Directory.Delete(temp, true); }
    }

    private static void WriteAll(string root, string outputRoot)
    {
        var documents = Select(root);
        var manifest = new
        {
            schemaVersion = 1,
            artifactKind = "n3_fresh_holdout_population",
            supersededBy = new
            {
                artifact = "keys/benchmark-n3/manifest.v2.json",
                reason = "SELECTION_RULE_IMPLEMENTATION_ERROR - this selector never enforced the A3 usability predicate, so it picked 002 and 026, both already known (before N3 existed) to have zero extractable candidates. v2 keeps 043/058 and replaces only the two ineligible slots (004, 030), using the same frozen tie-break with the missing usability check added. This file is kept unmodified as the original (buggy) selection, not deleted or rewritten.",
            },
            purpose = "Fresh source-first holdout, independent from N0/N2 diagnosis. It is not selected for a historical failure shape or candidate outcome.",
            selectionRule = new
            {
                targetStrata = Strata,
                excludedDocumentStems = Excluded.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                tieBreak = "lowest remaining numeric document id within each stratum",
                prohibitedInputs = new[] { "candidate counts", "candidate rank", "selected status", "gold/silver label", "model output", "historical failure outcome" },
            },
            documents = documents.Select(document => new
            {
                stem = document.Stem,
                domain = document.Domain,
                file = Path.GetRelativePath(root, document.Path).Replace('\\', '/'),
                sourceDocumentSha256 = Sha256(document.Path),
            }),
            reviewContract = new
            {
                identity = "documentSha256 + page + sourceLineIds + sourceSpan; candidateId is diagnostics-only and never cross-run identity",
                sourceFirst = true,
                modelCalls = "prohibited until a later separately frozen phase",
            },
        };
        var manifestPath = Path.Combine(outputRoot, "keys", "benchmark-n3", "manifest.v1.json");
        Write(manifestPath, manifest);
        var manifestSha = Sha256(manifestPath);

        foreach (var document in documents)
        {
            var lines = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(document.Path).Lines;
            var packet = new
            {
                schemaVersion = 1,
                artifactKind = "n3_blind_source_review_packet",
                parentManifestSha256 = manifestSha,
                document.Stem,
                document.Domain,
                documentSha256 = Sha256(document.Path),
                sourceLineExtractionFingerprint = SourceFingerprint(lines),
                reviewInstructions = new
                {
                    question = "Which source occurrences are structural document-outline headings?",
                    allowedLabels = new[] { "REVIEWED_HEADING", "REVIEWED_NON_HEADING", "UNCERTAIN" },
                    prohibitedEvidence = new[] { "candidate id", "candidate score", "rank", "selected status", "scope", "model output", "validated output" },
                },
                items = lines.Select((line, index) => new
                {
                    reviewItemId = $"{document.Stem}/line/{index:D6}",
                    page = line.Page,
                    lineId = PdfCandidateProvenance.LineId(line),
                    sourceSpan = new { startLineId = PdfCandidateProvenance.LineId(line), endLineId = PdfCandidateProvenance.LineId(line) },
                    text = line.Text,
                    previousLines = Neighbors(lines, index, -1),
                    nextLines = Neighbors(lines, index, 1),
                }),
            };
            Write(Path.Combine(outputRoot, "eval", "benchmark-n3", "source-packets", $"{document.Stem}-blind-source-review.v1.json"), packet);
        }
    }

    private static Document[] Select(string root)
    {
        var corpus = Path.Combine(root, "todo10_8", "heading_corpus_95_word");
        return Strata.Select(domain => Directory.EnumerateFiles(Path.Combine(corpus, domain), "*.docx")
                .OrderBy(Path.GetFileName, StringComparer.Ordinal)
                .Select(path => new Document(domain, Path.GetFileNameWithoutExtension(path).Split('_', 2)[0], path))
                .First(document => !Excluded.Contains(document.Stem)))
            .ToArray();
    }

    private static object[] Neighbors(IReadOnlyList<PdfLine> lines, int index, int direction)
    {
        var result = new List<object>();
        for (var offset = 1; offset <= ContextRadius; offset++)
        {
            var neighborIndex = index + direction * offset;
            if (neighborIndex < 0 || neighborIndex >= lines.Count) break;
            var neighbor = lines[neighborIndex];
            result.Add(new { page = neighbor.Page, lineId = PdfCandidateProvenance.LineId(neighbor), text = neighbor.Text });
        }
        return result.ToArray();
    }

    private static void Write(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Normalize(string value) => value.Replace("\r\n", "\n");
    private static string SourceFingerprint(IReadOnlyList<PdfLine> lines) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines.Select((line, index) => $"{index}|{PdfCandidateProvenance.LineId(line)}"))))).ToLowerInvariant();
    private sealed record Document(string Domain, string Stem, string Path);
}
