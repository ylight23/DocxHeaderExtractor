using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Features;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Policy;

namespace DocxHeaderExtractor.Tests;

/// <summary>Throwaway diagnostic (0 calls): per remaining count-only document, the harness calls one
/// run would cost, and - for DOCX converted from a PDF - how many standalone PDF label lines the
/// conversion no longer keeps as a paragraph of their own.</summary>
public sealed class ScaleUpSurveyDiagnosticTests
{
    private static readonly (string Id, string? OriginalPdf)[] Documents =
    [
        ("DOC-0092", null), ("DOC-0123", null), ("DOC-0133", null),
        ("DOC-0202", null),
        ("DOC-0264", null),
        ("SRC-003", "todo10_8/heading_corpus_100/01_phap_quy/003_Luat_Doanh_nghiep_59-2020-QH14.pdf"),
        ("SRC-029", "todo10_8/heading_corpus_100/02_hop_dong_mua_sam/029_WB_RFP_Works_DesignBuild_2021.pdf"),
        ("SRC-041", null), ("SRC-042", null), ("SRC-044", null), ("SRC-053", null), ("SRC-054", null),
        ("SRC-057", "todo10_8/heading_corpus_100/04_giao_trinh/057_Quantitative_Methods_in_Finance_Lecture_Notes.pdf"),
    ];

    [Fact]
    public void Survey()
    {
        if (Environment.GetEnvironmentVariable("A99_SCALEUP_SURVEY") != "1") return;
        var lines = new List<string> { "id | total | media | calls/run | paragraphs-or-atoms | merged-standalone-lines (bold)" };
        foreach (var (id, originalPdf) in Documents)
        {
            try { SurveyOne(id, originalPdf, lines); }
            catch (Exception error) { lines.Add($"{id} | ERROR {error.GetType().Name}: {error.Message} @ {string.Join(" <- ", (error.StackTrace ?? "").Split('\n').Take(4).Select(s => s.Trim()))}"); }
        }
        Assert.Fail(string.Join("\n", lines));
    }

    private static void SurveyOne(string id, string? originalPdf, List<string> lines)
    {
        {
            using var gold = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"eval/a99-closed-loop/gold/{id}.gold.json")));
            var total = gold.RootElement.GetProperty("semanticHeadingTotal").GetInt32();
            var source = gold.RootElement.GetProperty("source");
            var path = TestRepository.Path(source.GetProperty("sourcePath").GetString()!);
            var overrideSpec = Environment.GetEnvironmentVariable("A99_SCALEUP_OVERRIDE");
            if (overrideSpec is not null && overrideSpec.StartsWith(id + "=", StringComparison.Ordinal)) path = overrideSpec[(id.Length + 1)..];
            var media = source.GetProperty("mediaType").GetString()!;
            if (media == "PDF")
            {
                var legacy = PdfCanonicalSourceUniverseBuilder.Build(path).Evidence.Count;
                var structured = PdfStructuredSourceAuthorityBuilder.Build(path);
                lines.Add($"{id} | {total} | PDF | legacy {Math.Ceiling(legacy / 120.0)} / structured {structured.Packs.Count} | atoms {structured.Atoms.Count} | -");
                return;
            }

            var document = new OpenXmlDocumentSource().Read(path);
            var state = DocxPolicyStateBuilder.Build(document, NumberingStyleFeatures.FromSourceDocument(document),
                new DocumentFeatureDeriver().Derive(document), new ExtractionOptions());
            var contexts = DocxAuthorityPipeline.BuildForAudit(state,
                DocumentModeClassifier.Measure(state.Paragraphs.Cast<IPolicyParagraph>().ToArray())).Contexts.Count;
            var paragraphs = Doc0258SourceRegenerationTests.ReadParagraphs(path);
            var merged = "-";
            if (originalPdf is not null)
            {
                var own = paragraphs.Select(p => Norm(p.Text)).ToHashSet();
                var shortBlocks = PdfLineDocxConverter.BuildBlocks(PdfLineDocxConverter.ReadLines(TestRepository.Path(originalPdf)))
                    .Where(b => b.Words.Count <= 14 && !b.Text.All(c => char.IsDigit(c) || char.IsWhiteSpace(c))).ToArray();
                var lost = shortBlocks.Where(b => !own.Contains(Norm(b.Text))).ToArray();
                merged = $"{lost.Length}/{shortBlocks.Length} ({lost.Count(b => b.Words.All(w => w.Bold))} bold) e.g. " + string.Join(" / ", lost.Where(b => b.Words.All(w => w.Bold)).Take(12).Select(b => b.Text));
            }
            lines.Add($"{id} | {total} | DOCX | docx {Math.Ceiling(contexts / 120.0)} | paragraphs {paragraphs.Count}, contexts {contexts} | {merged}");
        }
    }

    [Fact]
    public void Dump_converter_output()
    {
        var spec = Environment.GetEnvironmentVariable("A99_SCALEUP_DUMP");
        if (spec is null) return;
        var parts = spec.Split('|');
        var blocks = PdfLineDocxConverter.BuildBlocks(PdfLineDocxConverter.ReadLines(TestRepository.Path(parts[0])));
        File.WriteAllLines(parts[1], blocks.Select((b, i) => $"{i}\t{b.Page}\t{(b.Words.All(w => w.Bold) ? "B" : b.Words.Any(w => w.Bold) ? "b" : "-")}\t{b.Text}"));
    }

    [Fact]
    public void Dump_pdf_atoms()
    {
        var spec = Environment.GetEnvironmentVariable("A99_SCALEUP_ATOMS");
        if (spec is null) return;
        var parts = spec.Split('|');
        var authority = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(parts[0]));
        File.WriteAllLines(parts[1], authority.Evidence.Select(e => $"{e.SourceAlias}\t{e.ExactSourceText}"));
    }

    [Fact]
    public void Dump_docx_aliases()
    {
        var spec = Environment.GetEnvironmentVariable("A99_DOCX_ALIASES");
        if (spec is null) return;
        var parts = spec.Split('|');
        var document = new OpenXmlDocumentSource().Read(TestRepository.Path(parts[0]));
        var byId = document.Paragraphs.ToDictionary(p => p.SourceId, StringComparer.Ordinal);
        var aliases = DocxHeaderExtractor.Core.Models.SemanticSourceAliasCatalog.FromCatalog(
            DocumentSourceCatalogBuilder.FromSourceDocument(document));
        File.WriteAllLines(parts[1], aliases.Select(a =>
        {
            var p = byId.GetValueOrDefault(a.SourceId);
            return p is null
                ? $"{a.Alias}\t?\t\t\t\t\t\t\t\t\t{a.Text.Replace('\t', ' ').Replace('\n', ' ')}"
                : $"{a.Alias}\t{p.SourceOrdinal}\t{p.Style.StyleId}\t{p.Style.OutlineLevel}\t{(p.Style.Bold ? "B" : "-")}\t{(p.Style.AllCaps ? "C" : "-")}\t{p.Style.FontSizePt}\t{p.Numbering.NumberLabel}\t{p.Layout.TableDepth}\t{(p.InTableOfContents ? "T" : "-")}\t{a.Text.Replace('\t', ' ').Replace('\n', ' ')}";
        }));
        File.WriteAllLines(parts[1] + ".headers", document.PageHeaders.Concat(["--footers--"]).Concat(document.PageFooters));
    }

    [Fact]
    public void Dump_pdf_atom_facts()
    {
        var spec = Environment.GetEnvironmentVariable("A99_PDF_FACTS");
        if (spec is null) return;
        var parts = spec.Split('|');
        var pdf = TestRepository.Path(parts[0]);
        var atoms = PdfStructuredSourceAuthorityBuilder.Build(pdf).Atoms;
        Dictionary<string, PdfLine> lines;
        using (var document = UglyToad.PdfPig.PdfDocument.Open(pdf))
            lines = PdfLineExtraction.ExtractLines(document, PdfLineGrouping.VisualLineSegmentV3)
                .GroupBy(PdfLineIdentity.Of, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        File.WriteAllLines(parts[1], atoms.Select(a =>
        {
            var l = lines[a.SourceId];
            return $"{a.Alias}\t{a.Page}\t{a.Row}\t{a.Segment}\t{l.FontSize:0.0}\t{l.BoldRatio:0.00}\t{l.ItalicRatio:0.00}\t{l.Left:0}\t{l.Right:0}\t{l.Y:0}\t{a.Text.Replace('\t', ' ')}\t{l.LeadingBoldPrefix.Replace('\t', ' ')}";
        }));
    }

    /// <summary>
    /// For a PDF whose text matrix carries the type size (Tf 1, scaled by Tm), the line facts above read size 1 and
    /// no bold. This writes what the glyphs themselves say: their effective point size and their font names, taken
    /// from the letters inside each atom's box. Diagnostic only; it changes nothing the pipeline reads.
    /// </summary>
    [Fact]
    public void Dump_pdf_atom_glyph_facts()
    {
        var spec = Environment.GetEnvironmentVariable("A99_PDF_GLYPH_FACTS");
        if (spec is null) return;
        var parts = spec.Split('|');
        var pdf = TestRepository.Path(parts[0]);
        var atoms = PdfStructuredSourceAuthorityBuilder.Build(pdf).Atoms;
        using var document = UglyToad.PdfPig.PdfDocument.Open(pdf);
        var lines = PdfLineExtraction.ExtractLines(document, PdfLineGrouping.VisualLineSegmentV3)
            .GroupBy(PdfLineIdentity.Of, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var letters = document.GetPages().ToDictionary(p => p.Number, p => p.Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value)).ToArray());
        File.WriteAllLines(parts[1], atoms.Select(a =>
        {
            var l = lines[a.SourceId];
            var inside = letters[a.Page].Where(g =>
                g.BoundingBox.Left >= l.Left - 0.5 && g.BoundingBox.Right <= l.Right + 0.5 &&
                g.BoundingBox.Bottom >= l.Bottom - 0.5 && g.BoundingBox.Top <= l.Top + 0.5).ToArray();
            var sizes = inside.Select(g => g.PointSize).Order().ToArray();
            var size = sizes.Length == 0 ? 0 : sizes[sizes.Length / 2];
            var fonts = inside.GroupBy(g => (g.FontName ?? "").Split('+')[^1]).OrderByDescending(g => g.Count())
                .Select(g => $"{g.Key}:{g.Count()}");
            return $"{a.Alias}\t{a.Page}\t{a.Row}\t{a.Segment}\t{size:0.0}\t{string.Join(",", fonts)}\t{l.Left:0}\t{l.Right:0}\t{l.Y:0}\t{a.Text.Replace('\t', ' ')}";
        }));
    }

    private static string Norm(string s) => string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
