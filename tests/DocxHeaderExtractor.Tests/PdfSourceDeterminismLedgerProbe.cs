using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Diagnostic only. When A99_PDF_LEDGER_DIR is set, writes a per-page, per-stage ledger of how a PDF
/// becomes a source universe, so the same PDF can be diffed across operating systems and the first
/// diverging stage found. No Gold, no provider, no artifact is read or rewritten.
///
/// Stages: A PdfPig letters, B sorted letter sequence, C visual-line buckets, D extracted lines,
/// E source atoms. Every double is recorded as its round-trip text and its raw bit pattern.
/// </summary>
public sealed class PdfSourceDeterminismLedgerProbe
{
    private static readonly (string Id, string Path)[] Documents =
    [
        ("SRC-004", "todo10_8/heading_corpus_100/01_phap_quy/004_Luat_Dau_tu_61-2020-QH14_EN.pdf"),
        ("SRC-029", "todo10_8/heading_corpus_100/02_hop_dong_mua_sam/029_WB_RFP_Works_DesignBuild_2021.pdf"),
        ("SRC-089", "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf"),
        ("SRC-095", "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf"),
    ];

    [Fact]
    public void Write_the_cross_platform_source_ledger_when_requested()
    {
        var directory = Environment.GetEnvironmentVariable("A99_PDF_LEDGER_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;

        var platform = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows" : "linux";
        Directory.CreateDirectory(Path.Combine(directory, platform));
        foreach (var (id, relative) in Documents)
        {
            var ledger = Build(TestRepository.Path(relative));
            File.WriteAllText(
                Path.Combine(directory, platform, id + ".ledger.json"),
                JsonSerializer.Serialize(ledger, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static object Build(string pdfPath)
    {
        var pages = new List<object>();
        using (var document = PdfDocument.Open(pdfPath))
        {
            foreach (var page in document.GetPages())
            {
                var visible = page.Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value)).ToList();
                IReadOnlyList<Letter> sorted = visible
                    .OrderByDescending(l => l.StartBaseLine.Y)
                    .ThenBy(l => l.BoundingBox.Left)
                    .ThenBy(l => l.Value, StringComparer.Ordinal)
                    .ToList();
                var buckets = PdfVisualLineBucket.Split(sorted, PdfVisualLineBucket.Of);

                pages.Add(new
                {
                    page = page.Number,
                    pageWidth = Bits(page.Width),
                    pageHeight = Bits(page.Height),
                    A = Stage(visible.Select(Letter)),
                    B = Stage(sorted.Select(Letter)),
                    C = Stage(buckets.Select(bucket => string.Join(",", bucket.Select(l => Index(sorted, l))))),
                });
            }
        }

        IReadOnlyList<PdfLine> lines;
        using (var document = PdfDocument.Open(pdfPath)) lines = PdfLineExtraction.ExtractLines(document);
        var byPageLines = lines.GroupBy(l => l.Page).ToDictionary(g => g.Key, g => g.Select(Line).ToList());
        var universe = PdfSourceOccurrenceAdapter.Build(lines, CanonicalSemanticSourceHash.Compute(pdfPath));
        var byPageAtoms = universe.Atoms.GroupBy(a => a.Page)
            .ToDictionary(g => g.Key, g => g.Select(a => $"{a.Alias}|{a.SourceId}|{a.Ordinal}|{a.Row}|{a.Segment}|{a.Text}").ToList());

        return new
        {
            sourceSha256 = CanonicalSemanticSourceHash.Compute(pdfPath),
            universeHash = universe.SourceAliasUniverseHash,
            evidenceHash = universe.ModelVisibleEvidenceHash,
            atomCount = universe.Atoms.Count,
            pages = pages.Select(p =>
            {
                var number = (int)p.GetType().GetProperty("page")!.GetValue(p)!;
                return new
                {
                    page = p,
                    D = Stage(byPageLines.GetValueOrDefault(number) ?? []),
                    E = Stage(byPageAtoms.GetValueOrDefault(number) ?? []),
                };
            }).ToArray(),
        };
    }

    private static int Index(IReadOnlyList<Letter> sorted, Letter letter)
    {
        for (var i = 0; i < sorted.Count; i++) if (ReferenceEquals(sorted[i], letter)) return i;
        return -1;
    }

    /// <summary>Count, stage hash and the ordered identities, so a diff can name the first differing row.</summary>
    private static object Stage(IEnumerable<string> identities)
    {
        var rows = identities.ToList();
        return new { count = rows.Count, hash = Hash(rows), rows };
    }

    private static string Letter(Letter l) => string.Join("|",
        l.Value,
        Bits(l.StartBaseLine.Y), Bits(l.StartBaseLine.X),
        Bits(l.BoundingBox.Left), Bits(l.BoundingBox.Right), Bits(l.BoundingBox.Top), Bits(l.BoundingBox.Bottom),
        Bits(l.FontSize), Bits(l.PointSize), l.FontName);

    private static string Line(PdfLine l) => string.Join("|",
        l.Text, Bits(l.Y), Bits(l.Left), Bits(l.Right), Bits(l.Top ?? double.NaN), Bits(l.Bottom ?? double.NaN));

    private static string Bits(double value) =>
        value.ToString("R", CultureInfo.InvariantCulture) + "#" + BitConverter.DoubleToInt64Bits(value).ToString("x16", CultureInfo.InvariantCulture);

    private static string Hash(IReadOnlyList<string> rows) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", rows))));
}
