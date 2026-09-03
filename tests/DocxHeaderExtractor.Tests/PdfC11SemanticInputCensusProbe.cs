using System.Text;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// C1.1 - model-free input census over the decision-relevant cohort of one document. Written for 001,
/// where B3 measured 0 of 162 conditional semantic recall and the owner is still unknown.
/// <para>
/// It asks one question: is there a deterministic or input-representation characteristic shared by
/// most or all of the cohort that would explain why the semantic lane returned nothing usable? It
/// reports facts the pipeline already computes - marker parse, structural marker, scope, domain role,
/// evidence origins, block shape, and the context excerpt the analyst is actually handed - and derives
/// no new predicate and no verdict.
/// </para>
/// <para>
/// The cohort is <em>decision-relevant</em>, which already means the deterministic gates would not
/// have discarded these occurrences whatever the analyst answered. So a shared characteristic found
/// here is a hypothesis about the analyst's input, not a proven owner: distinguishing "not proposed"
/// from "wrong role" from "wrong span" needs the analyst's actual decisions, which only a
/// checkpoint-instrumented replication persists. This probe exists to make that call unnecessary if a
/// uniform input defect turns up first.
/// </para>
/// </summary>
public sealed class PdfC11SemanticInputCensusProbe
{
    [Fact]
    public void Report()
    {
        var stem = Environment.GetEnvironmentVariable("C11_DOC");
        var output = Environment.GetEnvironmentVariable("C11_REPORT");
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
        var classifications = PdfExtractorQualityBenchmarkProbe.Classify(docx, population.Occurrences);
        var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(docx);
        var contexts = PdfCandidateContextBuilder.Build(snapshot.CandidateBlocks, snapshot.Annotations);
        var blocks = snapshot.CandidateBlocks.ToDictionary(b => b.Id, StringComparer.Ordinal);

        // Decision-relevant: selected, with a covering candidate, and no deterministic gate against it.
        var cohort = classifications
            .Where(c => c.Selected && c.CoveringCandidateId is not null && c.DeterministicExclusionReason is null)
            .ToArray();

        var report = new StringBuilder();
        void Line(string value) => report.AppendLine(value);

        Line($"C1.1 input census - doc={stem}. Model-free; no analyst decision is read or inferred.");
        Line($"decision-relevant cohort: {cohort.Length} of {population.Occurrences.Count} reviewed");
        if (cohort.Length == 0) { File.WriteAllText(output, report.ToString()); return; }

        void Tally(string title, Func<PdfExtractorQualityBenchmarkProbe.OccurrenceClassification, string> key)
        {
            Line("");
            Line($"-- {title}");
            foreach (var group in cohort.GroupBy(key).OrderByDescending(g => g.Count()))
                Line($"   {group.Key,-46} {group.Count(),5}  {group.Count() / (double)cohort.Length,7:P1}");
        }

        string Text(PdfExtractorQualityBenchmarkProbe.OccurrenceClassification c) =>
            blocks.TryGetValue(c.CoveringCandidateId!, out var b) ? b.DisplayText : "";

        Tally("strict marker parse (PdfMarkerFactsParser)", c =>
        {
            var marker = PdfMarkerFactsParser.Parse(PdfTextUtilities.Readable(Text(c)));
            return marker is null ? "none" : $"{marker.Value.Family} depth={marker.Value.Depth} isPath={marker.Value.IsPath}";
        });
        Tally("HasStructuralMarker", c => PdfLineBlockAnnotation.HasStructuralMarker(Text(c)).ToString());
        Tally("loose labelled marker", c =>
            PdfLayoutEvidenceOutline.ParseLooseLabelledMarkerForAudit(Text(c)) is null ? "none" : "present");
        Tally("structural scope", c =>
            contexts.TryGetValue(c.CoveringCandidateId!, out var ctx) ? ctx.Source.StructuralScope : "-");
        Tally("domain role", c =>
            contexts.TryGetValue(c.CoveringCandidateId!, out var ctx) ? ctx.Source.DomainRole.ToString() : "-");
        Tally("regime", c => contexts.TryGetValue(c.CoveringCandidateId!, out var ctx) ? ctx.DocumentRegime : "-");
        Tally("block line count", c =>
            blocks.TryGetValue(c.CoveringCandidateId!, out var b) ? Bucket(b.LineCount) : "-");
        Tally("evidence origins", c => contexts.TryGetValue(c.CoveringCandidateId!, out var ctx)
            ? string.Join(",", ctx.Source.EvidenceDetails.Select(e => e.Origin).Distinct().OrderBy(x => x))
            : "-");
        Tally("observed evidence", c => contexts.TryGetValue(c.CoveringCandidateId!, out var ctx)
            ? string.Join(",", ctx.Source.ObservedEvidence.OrderBy(x => x))
            : "-");
        Tally("context excerpt shape", c =>
        {
            if (!contexts.TryGetValue(c.CoveringCandidateId!, out var ctx)) return "-";
            return $"prev={ctx.PreviousBlocks.Count} next={ctx.NextBlocks.Count} " +
                   $"allowedParents={ctx.AllowedParentIds.Count} stack={ctx.ActiveHeadingStack.Count}";
        });
        Tally("candidate text length", c => Bucket(Text(c).Length, 16, 32, 64, 128));

        // Token shape, reported raw rather than as a damage verdict. Extraction is known to drop
        // inter-word spaces; whether that reaches this cohort is a measurement, and it only means
        // anything next to a document whose cohort the semantic lane did survive.
        Tally("longest whitespace-separated token", c => Bucket(LongestToken(Text(c)), 6, 10, 15, 25));
        Tally("has a token longer than 12 characters", c => (LongestToken(Text(c)) > 12).ToString());

        var longTokens = cohort.Count(c => LongestToken(Text(c)) > 12);
        Line("");
        Line($"   tokens > 12 chars: {longTokens}/{cohort.Length} = {longTokens / (double)cohort.Length:P1}");

        Line("");
        Line("-- first 30 of the cohort, as the analyst would have seen them");
        foreach (var item in cohort.Take(30))
        {
            var ctx = contexts.TryGetValue(item.CoveringCandidateId!, out var found) ? found : null;
            Line($"   [{item.CoveringRank,5}] {item.CoveringCandidateId,-14} " +
                 $"scope={ctx?.Source.StructuralScope ?? "-"} role={ctx?.Source.DomainRole.ToString() ?? "-"} " +
                 $"lines={(blocks.TryGetValue(item.CoveringCandidateId!, out var block) ? block.LineCount : 0)}");
            Line($"          text: {Trim(Text(item))}");
        }

        File.WriteAllText(output, report.ToString());
    }

    /// <summary>Longest run of non-space characters, ignoring the leading marker token.</summary>
    private static int LongestToken(string text)
    {
        var readable = PdfTextUtilities.Readable(text);
        var tokens = readable.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length == 0 ? 0 : tokens.Max(t => t.Trim('.', ',', ':', ';', ')', '(').Length);
    }

    private static string Bucket(int value, int a = 1, int b = 2, int c = 3, int d = 5) =>
        value <= a ? $"<={a}" : value <= b ? $"{a + 1}-{b}" : value <= c ? $"{b + 1}-{c}" :
        value <= d ? $"{c + 1}-{d}" : $">{d}";

    private static string Trim(string value)
    {
        var single = Regex.Replace(value, @"\s+", " ").Trim();
        return single.Length <= 96 ? single : single[..96] + "...";
    }
}
