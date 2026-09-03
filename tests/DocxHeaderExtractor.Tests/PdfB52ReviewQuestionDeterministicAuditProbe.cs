using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>Model-free audit for B5's 056 numbered review-question false accepts.</summary>
public sealed class PdfB52ReviewQuestionDeterministicAuditProbe
{
    [Fact]
    public void Report()
    {
        var artifactPath = Environment.GetEnvironmentVariable("BENCH_B52_ARTIFACT");
        var reviewPath = Environment.GetEnvironmentVariable("BENCH_B52_REVIEW");
        var output = Environment.GetEnvironmentVariable("BENCH_B52_REPORT");
        if (string.IsNullOrWhiteSpace(artifactPath) || string.IsNullOrWhiteSpace(reviewPath) || string.IsNullOrWhiteSpace(output)) return;

        using var artifact = JsonDocument.Parse(File.ReadAllText(artifactPath));
        using var review = JsonDocument.Parse(File.ReadAllText(reviewPath));
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var corpus = Path.Combine(root, "todo10_8", "heading_corpus_95_word");
        var population = PdfExtractorQualityBenchmarkProbe.Populations(corpus).Single(item => item.Stem == "056");
        var path = Path.Combine(corpus, population.Relative);
        var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(path);
        var contexts = PdfCandidateContextBuilder.Build(snapshot.CandidateBlocks, snapshot.Annotations);
        var decisions = review.RootElement.GetProperty("documents").GetProperty("056").GetProperty("decisions");
        var questions = artifact.RootElement.GetProperty("rows")[0].GetProperty("items").EnumerateArray()
            .Where(item => decisions.GetProperty(item.GetProperty("sourceFactId").GetString()!).GetString() == "NON_HEADING").ToArray();

        var report = new StringBuilder();
        report.AppendLine($"B5.2 model-free review-question audit: false outputs={questions.Length}");
        var deterministicRejects = 0;
        foreach (var item in questions)
        {
            var id = item.GetProperty("sourceFactId").GetString()!;
            var hasContext = contexts.TryGetValue(id, out var context);
            var predicate = hasContext ? PdfExtractorQualityBenchmarkProbe.DeterministicExclusionReason(context!) : "candidate-context-not-found";
            if (predicate is not null) deterministicRejects++;
            report.AppendLine($"{id}: context={hasContext}; deterministicPredicate={predicate ?? "none"}; scope={item.GetProperty("structuralScope").GetString()}; marker={item.GetProperty("markerFamily").GetString()}; text={item.GetProperty("sourceBlockText").GetString()}");
        }
        report.AppendLine($"summary deterministicRejects={deterministicRejects}/{questions.Length}");
        report.AppendLine("A none result means existing deterministic scope/domain/evidence-origin predicates cannot separate this question independent of analyst role/span.");
        File.WriteAllText(output, report.ToString());
    }
}
