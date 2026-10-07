using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using UglyToad.PdfPig;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Gate B: PDF bytes to source universe is one function on every platform. The frozen hashes were
/// recorded identically on Windows and Ubuntu (see PdfSourceDeterminismLedgerProbe), and this test
/// asserts them wherever it runs, with no platform exemption. A document that embeds all of its
/// fonts keeps the parser's glyph boxes; one that relies on host-installed fonts uses the
/// font-independent glyph geometry, so installed fonts cannot change the result.
/// </summary>
public sealed class PdfSourceUniverseReproducibilityTests
{
    private const string Manifest = "eval/a99-closed-loop/pdf-source-determinism-v2/universe-hashes.v1.json";

    public static TheoryData<string> Documents()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(Manifest)));
        var data = new TheoryData<string>();
        foreach (var row in manifest.RootElement.GetProperty("rows").EnumerateArray())
            data.Add(row.GetProperty("documentId").GetString()!);
        return data;
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public void The_live_parse_reproduces_the_frozen_source_universe(string documentId)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(Manifest)));
        var row = manifest.RootElement.GetProperty("rows").EnumerateArray()
            .Single(item => item.GetProperty("documentId").GetString() == documentId);
        var path = TestRepository.Path(row.GetProperty("pdf").GetString()!);

        using (var document = PdfDocument.Open(path))
        {
            Assert.Equal(row.GetProperty("geometryMode").GetString(), PdfFontEmbedding.ModeFor(document).ToString());
            Assert.Equal(
                row.GetProperty("hostResolvedFonts").EnumerateArray().Select(item => item.GetString()).ToArray(),
                PdfFontEmbedding.HostResolvedFonts(document).ToArray());
        }

        var universe = PdfSourceAdapter.Build(path);
        Assert.Equal(row.GetProperty("sourceSha256").GetString(), universe.SourceSha256);
        Assert.Equal(row.GetProperty("atomCount").GetInt32(), universe.Atoms.Count);
        Assert.Equal(row.GetProperty("sourceAliasUniverseHash").GetString(), universe.SourceAliasUniverseHash);
        Assert.Equal(row.GetProperty("modelVisibleEvidenceHash").GetString(), universe.ModelVisibleEvidenceHash);
    }
}
