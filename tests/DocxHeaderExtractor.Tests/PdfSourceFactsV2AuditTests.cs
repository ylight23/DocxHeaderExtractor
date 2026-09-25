using System.Diagnostics;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// PDF_SOURCE_FACTS_V2_AUDIT: what moving from PDF_SOURCE_FACTS_V1 to V2 changes, PDF by PDF, over every PDF the
/// repository tracks - except SRC-054, held out for the first measurement of V2 and not inspected before its
/// preregistration.
/// <para>
/// The atom universe must not move: V2 changes typography facts, never text or geometry, so every structured
/// alias universe hash is asserted equal. What may move is recorded, not judged: line sizes and weights, the
/// evidence each weight came from, the legacy block universe (its blocks are split by size, so a matrix-scaled
/// PDF that read flat under V1 regroups under V2), and the model-visible evidence hash (V2 adds the raw facts).
/// Written once with A99_PDF_FACTS_AUDIT=1, or when the artifact is missing.
/// </para>
/// </summary>
public sealed class PdfSourceFactsV2AuditTests
{
    private const string Dir = "eval/a99-closed-loop/pdf-source-facts-v2";
    private const string HeldOut = "todo10_8/heading_corpus_100/03_tai_chinh_ke_toan/054_IBRD_Information_Statement_FY25.pdf";

    [Fact]
    public void Audit_every_tracked_pdf()
    {
        var artifact = TestRepository.Path($"{Dir}/pdf-source-facts-v2-audit.v1.json");
        if (File.Exists(artifact) && Environment.GetEnvironmentVariable("A99_PDF_FACTS_AUDIT") != "1") return;

        var pdfs = TrackedPdfs().Where(p => p != HeldOut).ToArray();
        Assert.True(pdfs.Length > 90);
        var rows = pdfs.Select(Audit).ToArray();
        Assert.All(rows, r => Assert.True(r.atomUniverseUnchanged, $"{r.path}: V2 moved the atom universe"));

        FreezeArtifact.AssertJson(Dir, "pdf-source-facts-v2-audit.v1.json", new
        {
            artifactKind = "a99_pdf_source_facts_audit",
            study = "PDF_SOURCE_FACTS_V2_AUDIT",
            from = PdfSourceFactsVersions.Id(PdfSourceFactsVersion.V1_NominalFontSize),
            to = PdfSourceFactsVersions.Id(PdfSourceFactsVersion.V2_EffectivePointSize),
            modelProviderVlmCalls = 0,
            excluded = new[] { new { path = HeldOut, reason = "held out for the first measurement of PDF_SOURCE_FACTS_V2; not inspected before its preregistration" } },
            summary = new
            {
                pdfs = rows.Length,
                atomUniverseUnchanged = rows.Count(r => r.atomUniverseUnchanged),
                typographyEquivalent = rows.Count(r => r.@class == "TYPOGRAPHY_EQUIVALENT"),
                boldByFontNameOnly = rows.Count(r => r.@class == "BOLD_BY_FONT_NAME"),
                sizeRead = rows.Count(r => r.@class is "SIZE_READ" or "SIZE_READ_AND_BOLD_BY_FONT_NAME"),
                legacyUniverseChanged = rows.Count(r => !r.legacyUniverseUnchanged),
                unreadable = rows.Count(r => r.@class == "UNREADABLE"),
            },
            rows,
        });
    }

    private static IEnumerable<string> TrackedPdfs()
    {
        var git = Process.Start(new ProcessStartInfo("git", "ls-files -z -- *.pdf")
        {
            WorkingDirectory = TestRepository.Path("."),
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        var output = git.StandardOutput.ReadToEnd();
        git.WaitForExit();
        return output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal);
    }

    private sealed record Row(
        string path, string sha256, string @class, bool atomUniverseUnchanged, bool legacyUniverseUnchanged,
        int lines, int linesSizeChanged, int linesBoldChanged, double? medianV1Size, double? medianV2Size,
        IReadOnlyDictionary<string, int> boldEvidenceSources,
        string atomUniverse, string modelVisibleV1, string modelVisibleV2, string? error);

    private static Row Audit(string relative)
    {
        var path = TestRepository.Path(relative);
        var sha = CanonicalArtifactHash.OfBytes(path);
        try
        {
            IReadOnlyList<PdfLine> v1, v2;
            using (var document = UglyToad.PdfPig.PdfDocument.Open(path))
            {
                v1 = PdfLineExtraction.ExtractLines(document, PdfLineGrouping.VisualLineSegmentV3, PdfSourceFactsVersion.V1_NominalFontSize);
                v2 = PdfLineExtraction.ExtractLines(document, PdfLineGrouping.VisualLineSegmentV3, PdfSourceFactsVersion.V2_EffectivePointSize);
            }
            Assert.Equal(v1.Count, v2.Count);
            var structuredV1 = PdfStructuredSourceAuthorityBuilder.Build(v1);
            var structuredV2 = PdfStructuredSourceAuthorityBuilder.Build(v2);
            var legacyV1 = PdfCanonicalSourceUniverseBuilder.Build(path, PdfSourceFactsVersion.V1_NominalFontSize).SourceUniverseSha256;
            var legacyV2 = PdfCanonicalSourceUniverseBuilder.Build(path, PdfSourceFactsVersion.V2_EffectivePointSize).SourceUniverseSha256;

            var pairs = v1.Zip(v2).ToArray();
            var sizeChanged = pairs.Count(p => Math.Abs(p.First.FontSize - p.Second.FontSize) > 0.05);
            var boldChanged = pairs.Count(p => p.First.BoldRatio >= 0.5 != p.Second.BoldRatio >= 0.5);
            var boldByName = pairs.Any(p => p.First.BoldRatio < 0.5 && p.Second.BoldRatio >= 0.5);
            var cls = sizeChanged > 0 && boldByName ? "SIZE_READ_AND_BOLD_BY_FONT_NAME"
                : sizeChanged > 0 ? "SIZE_READ"
                : boldByName ? "BOLD_BY_FONT_NAME"
                : boldChanged > 0 ? "BOLD_CHANGED"
                : "TYPOGRAPHY_EQUIVALENT";
            return new Row(relative, sha, cls,
                structuredV1.SourceAliasUniverseHash == structuredV2.SourceAliasUniverseHash, legacyV1 == legacyV2,
                v1.Count, sizeChanged, boldChanged,
                Median(v1.Select(l => l.FontSize)), Median(v2.Select(l => l.FontSize)),
                v2.GroupBy(l => l.Typography?.BoldEvidenceSource ?? "UNTYPED").OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
                structuredV1.SourceAliasUniverseHash, structuredV1.ModelVisibleEvidenceHashV2, structuredV2.ModelVisibleEvidenceHashV2, null);
        }
        catch (Exception exception) when (exception is not Xunit.Sdk.XunitException)
        {
            return new Row(relative, sha, "UNREADABLE", true, true, 0, 0, 0, null, null,
                new Dictionary<string, int>(), "", "", "", exception.GetType().Name + ": " + exception.Message);
        }
    }

    private static double? Median(IEnumerable<double> values)
    {
        var ordered = values.Where(v => v > 0).Order().ToArray();
        return ordered.Length == 0 ? null : Math.Round(ordered[ordered.Length / 2], 2);
    }
}
