using System.Text;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// C1.2a - where the missing inter-word spaces come from. Provenance only: no OCR, no text repair, no
/// reconstruction attempt.
/// <para>
/// <c>PdfLineExtraction</c> does not read spaces from the PDF; it synthesises them from letter
/// geometry, inserting one when the horizontal gap exceeds
/// <c>max(1.2, max(fontSize, glyphHeight) * 0.18)</c>. So a document whose word spacing is narrower
/// than that threshold loses every inter-word space, and the question is whether the source had them
/// to begin with.
/// </para>
/// <para>
/// Three representations are compared per line: what the PDF's own text layer carries (space glyphs
/// among the letters, and PdfPig's independent word segmentation), what our line extraction produced,
/// and the longest resulting token. Each line is classified only as NO_GLUE, SOURCE_ALREADY_GLUE or
/// EXTRACTION_INTRODUCED_GLUE - the long-token count stays a symptom beside that, never a substitute
/// for the direct comparison.
/// </para>
/// </summary>
public sealed class PdfC12aGlueProvenanceProbe
{
    [Fact]
    public void Report()
    {
        var stem = Environment.GetEnvironmentVariable("C12A_DOC");
        var output = Environment.GetEnvironmentVariable("C12A_REPORT");
        if (string.IsNullOrWhiteSpace(stem) || string.IsNullOrWhiteSpace(output)) return;

        var corpus = Path.Combine(PdfExtractorQualityBenchmarkProbe.RepositoryRoot(),
            "todo10_8", "heading_corpus_95_word");
        var population = PdfExtractorQualityBenchmarkProbe.Populations(corpus).FirstOrDefault(p => p.Stem == stem);
        if (population.Occurrences is null || population.Occurrences.Count == 0)
        {
            File.WriteAllText(output, $"doc={stem}: no reviewed population");
            return;
        }

        var docx = Path.Combine(corpus, population.Relative);
        var pdf = PdfTextbookOutline.FindSiblingPdf(docx);
        if (pdf is null) { File.WriteAllText(output, $"doc={stem}: no sibling PDF"); return; }

        var classifications = PdfExtractorQualityBenchmarkProbe.Classify(docx, population.Occurrences);
        var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(docx);

        // Occurrence identity: the required line indexes A1 already resolved, never a text join.
        var cohortLineIndexes = classifications
            .Where(c => c.Selected && c.CoveringCandidateId is not null && c.DeterministicExclusionReason is null)
            .SelectMany(c => c.RequiredIndexes)
            .Distinct()
            .Where(index => index >= 0 && index < snapshot.Lines.Count)
            .ToArray();

        using var document = PdfDocument.Open(pdf);
        var pages = document.GetPages().ToDictionary(p => p.Number);

        var report = new StringBuilder();
        void Line(string value) => report.AppendLine(value);

        Line($"C1.2a glue provenance - doc={stem}. Provenance only; no repair attempted.");
        Line($"extraction rule: space inserted when gap > max(1.2, max(fontSize, glyphHeight) * 0.18)");
        Line($"cohort source lines: {cohortLineIndexes.Length}");

        var tally = new Dictionary<string, int>(StringComparer.Ordinal);
        var samples = new List<string>();

        foreach (var index in cohortLineIndexes)
        {
            var line = snapshot.Lines[index];
            if (!pages.TryGetValue(line.Page, out var page)) continue;

            // Letters whose own value is whitespace: a space the PDF genuinely encodes.
            var band = page.Letters
                .Where(l => Math.Abs((l.GlyphRectangle.Bottom + l.GlyphRectangle.Top) / 2.0 - line.Y) < 2.0)
                .ToArray();

            var sourceSpaceGlyphs = band.Count(l => string.IsNullOrWhiteSpace(l.Value));
            var ourSpaces = line.Text.Count(char.IsWhiteSpace);
            var longest = LongestToken(line.Text);

            var pdfWords = page.GetWords()
                .Count(w => Math.Abs((w.BoundingBox.Bottom + w.BoundingBox.Top) / 2.0 - line.Y) < 2.0);

            string verdict;
            if (longest <= 12) verdict = "NO_GLUE";
            else if (sourceSpaceGlyphs == 0 && pdfWords <= ourSpaces + 1) verdict = "SOURCE_ALREADY_GLUE";
            else verdict = "EXTRACTION_INTRODUCED_GLUE";

            tally[verdict] = tally.GetValueOrDefault(verdict) + 1;
            if (samples.Count < 12)
                samples.Add($"   {verdict,-26} spaceGlyphs={sourceSpaceGlyphs,3} pdfWords={pdfWords,3} " +
                            $"ourSpaces={ourSpaces,3} longest={longest,3}  {Trim(line.Text)}");
        }

        Line("");
        Line("-- classification");
        var total = tally.Values.Sum();
        foreach (var entry in tally.OrderByDescending(e => e.Value))
            Line($"   {entry.Key,-28} {entry.Value,5}  {entry.Value / (double)Math.Max(1, total),7:P1}");

        Line("");
        Line("-- samples");
        foreach (var sample in samples) Line(sample);

        File.WriteAllText(output, report.ToString());
    }

    private static int LongestToken(string text)
    {
        var tokens = PdfTextUtilities.Readable(text)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length == 0 ? 0 : tokens.Max(t => t.Trim('.', ',', ':', ';', ')', '(').Length);
    }

    private static string Trim(string value)
    {
        var single = Regex.Replace(value, @"\s+", " ").Trim();
        return single.Length <= 64 ? single : single[..64] + "...";
    }
}
