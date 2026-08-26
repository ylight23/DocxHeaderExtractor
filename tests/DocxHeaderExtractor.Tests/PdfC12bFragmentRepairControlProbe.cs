using System.Text;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// C1.2b - control census for the fragment-repair transform. C1.2a showed 001's source lines are
/// clean and the glue appears in <c>DisplayText</c>, i.e. in
/// <c>PdfTextUtilities.HeadingReadable</c>'s kerning-fragment repair. This measures what that
/// transform does to clean source lines across documents, so "it is language-dependent" is a
/// measurement rather than an inference from reading the rule.
/// <para>
/// Documents are fixed in this file by corpus position, not chosen after looking at the result, and
/// the metric is applied to the same population in every one: source lines carrying a structural
/// marker, which is the shape the transform is aimed at.
/// </para>
/// <para>
/// This reports what the transform changes. Whether the change is harmful, and to whom, is a separate
/// question - the repair exists because kerning damage in the RFC documents is real.
/// </para>
/// </summary>
public sealed class PdfC12bFragmentRepairControlProbe
{
    private static readonly (string Stem, string Relative, string Language)[] Documents =
    [
        ("001", @"01_phap_quy\001_Bo_luat_Dan_su_91-2015-QH13.docx", "vi"),
        ("010", @"01_phap_quy\010_Luat_An_ninh_mang_24-2018-QH14.docx", "vi"),
        ("032", @"02_hop_dong_mua_sam\032_WB_Plant_TwoStage_2020.docx", "en"),
        ("041", @"03_tai_chinh_ke_toan\041_IBRD_Financial_Statements_June_2025.docx", "en"),
        ("056", @"04_giao_trinh\056_OpenStax_Business_Law_I_Essentials.docx", "en"),
        ("092", @"07_system_generated\092_RFC9111_HTTP_Caching.docx", "en"),
    ];

    [Fact]
    public void Report()
    {
        var output = Environment.GetEnvironmentVariable("C12B_REPORT");
        if (string.IsNullOrWhiteSpace(output)) return;

        var corpus = Path.Combine(PdfExtractorQualityBenchmarkProbe.RepositoryRoot(),
            "todo10_8", "heading_corpus_95_word");
        var report = new StringBuilder();
        void Line(string value) => report.AppendLine(value);

        Line("C1.2b - what HeadingReadable's fragment repair does to clean source lines.");
        Line("population: source lines carrying a structural marker. No model, no gold.");
        Line("");
        Line($"{"doc",-6} {"lang",5} {"marked lines",13} {"changed",8} {"glued>12",9} {"rate",8}");

        var samples = new List<string>();
        foreach (var (stem, relative, language) in Documents)
        {
            var path = Path.Combine(corpus, relative);
            if (!File.Exists(path)) { Line($"{stem,-6} {language,5} not found"); continue; }

            var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(path);
            var marked = snapshot.Lines
                .Where(l => PdfLineBlockAnnotation.HasStructuralMarker(l.Text))
                .ToArray();
            if (marked.Length == 0) { Line($"{stem,-6} {language,5} no marked lines"); continue; }

            var changed = 0;
            var glued = 0;
            foreach (var line in marked)
            {
                var before = PdfTextUtilities.Readable(line.Text);
                var after = PdfTextUtilities.HeadingReadable(line.Text);
                if (!string.Equals(before, after, StringComparison.Ordinal)) changed++;
                if (LongestToken(after) > 12 && LongestToken(before) <= 12)
                {
                    glued++;
                    if (samples.Count < 10 && glued <= 2)
                        samples.Add($"   {stem} [{language}] \"{Trim(before)}\"  ->  \"{Trim(after)}\"");
                }
            }

            Line($"{stem,-6} {language,5} {marked.Length,13} {changed,8} {glued,9} " +
                 $"{glued / (double)marked.Length,8:P1}");
        }

        Line("");
        Line("-- lines the transform glued (clean before, long token after)");
        foreach (var sample in samples) Line(sample);

        File.WriteAllText(output, report.ToString());
    }

    private static int LongestToken(string text)
    {
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length == 0 ? 0 : tokens.Max(t => t.Trim('.', ',', ':', ';', ')', '(').Length);
    }

    private static string Trim(string value)
    {
        var single = Regex.Replace(value, @"\s+", " ").Trim();
        return single.Length <= 52 ? single : single[..52] + "...";
    }
}
