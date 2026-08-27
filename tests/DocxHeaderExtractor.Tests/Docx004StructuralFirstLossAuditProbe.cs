using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Source-observable audit for the 004 legal hierarchy gap. This deliberately replays the
/// no-LLM web contract and changes no extraction behavior.
/// </summary>
public sealed class Docx004StructuralFirstLossAuditProbe
{
    [Fact]
    public async Task WriteAudit()
    {
        var output = Environment.GetEnvironmentVariable("DOCX004_STRUCTURAL_FIRST_LOSS_AUDIT");
        if (string.IsNullOrWhiteSpace(output)) return;

        var report = await BuildReportAsync(PdfExtractorQualityBenchmarkProbe.RepositoryRoot());
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public async Task AuditContractAccountsForAllExpectedStructuralOccurrences()
    {
        var report = await BuildReportAsync(PdfExtractorQualityBenchmarkProbe.RepositoryRoot());
        Assert.Equal(15, report.Rows.Count);
        Assert.Equal(7, report.Rows.Count(row => row.Family == "chapter"));
        Assert.Equal(8, report.Rows.Count(row => row.Family == "section"));
        Assert.Equal(15, report.FirstLossCounts.Values.Sum());
    }

    private static async Task<AuditReport> BuildReportAsync(string root)
    {
        var docxPath = Path.Combine(root, "todo10_8", "heading_corpus_95_word", "01_phap_quy",
            "004_Luat_Dau_tu_61-2020-QH14_EN.docx");
        var silverPath = Path.Combine(root, "eval", "benchmark-n3", "silver-labels",
            "004-n3.2-silver-model-assisted.v1.json");
        var expected = LoadExpected(silverPath);

        var extraction = new ExtractionOptions { UseLexicalRules = false, SplitMergedParagraphs = false };
        var slim = new DocxSlimExtractor(extraction).Extract(docxPath);
        var mode = slim.Mode ?? DocumentModeClassifier.Measure(slim.Paragraphs);
        var mergedParagraphs = slim.Paragraphs.Count(p => !string.IsNullOrWhiteSpace(p.Text) &&
            ParagraphHeadingSplitter.Segments(p.Text).Count > 1);
        var autoRouteCanActivate = mergedParagraphs > 0;
        var legalBuilder = LegalStructuredOutline.Build(slim, splitMergedParagraphs: autoRouteCanActivate);

        var options = new PipelineOptions
        {
            DisableLlm = true,
            AutoDetectDocumentMode = true,
            Extraction = extraction,
        };
        using var pipeline = new HeaderExtractionPipeline(options);
        var outline = await pipeline.RunAsync(docxPath);

        var lastSourceIndex = -1;
        var rows = expected.Select(item =>
        {
            var source = FindSourceWindow(item, slim, lastSourceIndex);
            if (source is not null) lastSourceIndex = source.StartIndex;
            return BuildRow(item, source, slim, legalBuilder, outline, autoRouteCanActivate);
        }).ToArray();
        var firstLossCounts = rows.GroupBy(row => row.FirstLoss, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        return new AuditReport(
            SchemaVersion: 1,
            ArtifactKind: "004_structural_first_loss_audit",
            UsesModel: false,
            ProductionChanges: false,
            ExecutionContract: "HeaderExtractionPipeline with the web no-LLM defaults: structuralOnly=true, autoMode=true, splitMerged=false",
            ReferenceAuthority: "source-observed title plus MODEL_ASSISTED_SILVER structural occurrences; not independent human gold",
            Mode: mode.Mode.ToString(),
            AutoRoute: "auto:vietnamese-legal",
            MergedParagraphCount: mergedParagraphs,
            AutoRouteCanActivate: autoRouteCanActivate,
            OutputHeadingCount: outline.Headings.Count,
            ExpectedStructuralOccurrences: rows.Length,
            SurvivedStructuralOccurrences: rows.Count(row => row.Output),
            MissingStructuralOccurrences: rows.Count(row => !row.Output),
            SourceObservedOutsideFrozenAuthority: ["LAW ON INVESTMENT"],
            FirstLossCounts: firstLossCounts,
            Rows: rows);
    }

    private static AuditRow BuildRow(ExpectedOccurrence expected, SourceWindow? source, SlimDocument slim,
        IReadOnlyList<HeadingRecord> legalBuilder, DocumentOutline outline, bool autoRouteCanActivate)
    {
        var sourceIndexes = source?.Paragraphs.Select(paragraph => paragraph.Index).ToArray() ?? [];
        var candidate = source is not null && source.Paragraphs.All(paragraph => slim.Candidates.Any(candidate => candidate.Index == paragraph.Index));
        var candidatePartial = source is not null && source.Paragraphs.Any(paragraph => slim.Candidates.Any(candidate => candidate.Index == paragraph.Index));
        var legal = source is not null && legalBuilder.Any(heading => heading.Index == source.StartIndex &&
            CoversExpectedText(heading.Text, expected.SourceText));
        var output = source is not null && outline.Headings.Any(heading => heading.Index == source.StartIndex &&
            CoversExpectedText(heading.Text, expected.SourceText));

        var firstLoss = output
            ? "SURVIVED"
            : source is null
                ? "SOURCE_REPRESENTATION_MISSING"
                : source.Paragraphs.Count > 1 && !legal
                    ? "LEGAL_STRUCTURED_OUTLINE_MULTIPARAGRAPH_BOUNDARY"
                : legal && !autoRouteCanActivate
                    ? "DECLARED_ROUTE_GUARD_CO_DOAN_GOP"
                    : !candidate && candidatePartial
                        ? "CANDIDATE_BOUNDARY_MISMATCH"
                        : !candidate
                            ? "CANDIDATE_GENERATION"
                        : "POST_CANDIDATE_NO_LLM_HEURISTIC";

        return new AuditRow(
            expected.StableId, expected.Family, expected.Marker, expected.SourceText, expected.Page,
            expected.SourceLineIds, source is not null, sourceIndexes, source?.Paragraphs.Select(paragraph => paragraph.StableId).ToArray() ?? [],
            source?.Text, candidate, candidatePartial, legal, output,
            Role: "not_applicable_no_llm", Span: "not_applicable_no_llm",
            Validator: "not_applicable_no_llm", Grounding: "not_applicable_no_llm",
            output ? "EMITTED" : "NOT_EMITTED", firstLoss);
    }

    private static IReadOnlyList<ExpectedOccurrence> LoadExpected(string silverPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(silverPath));
        var occurrences = document.RootElement.GetProperty("headingOccurrences").EnumerateArray()
            .Where(item => item.GetProperty("goldStableId").GetString()!.Contains("/chapter/", StringComparison.Ordinal) ||
                           item.GetProperty("goldStableId").GetString()!.Contains("/section/", StringComparison.Ordinal))
            .Select(item => new ExpectedOccurrence(
                item.GetProperty("goldStableId").GetString()!,
                item.GetProperty("goldStableId").GetString()!.Contains("/chapter/", StringComparison.Ordinal) ? "chapter" : "section",
                item.GetProperty("marker").GetString()!, item.GetProperty("sourceText").GetString()!,
                item.GetProperty("page").GetInt32(),
                item.GetProperty("sourceLineIds").EnumerateArray().Select(value => value.GetString()!).ToArray()))
            .ToList();

        return occurrences;
    }

    private static SourceWindow? FindSourceWindow(ExpectedOccurrence expected, SlimDocument slim, int afterIndex)
    {
        var candidates = slim.Paragraphs
            .Where(paragraph => paragraph.Index > afterIndex && ContainsMarker(paragraph.Text, expected.Marker))
            .Select(paragraph => BuildSourceWindow(paragraph, slim, expected.SourceText))
            .OrderByDescending(window => window.Score)
            .ThenBy(window => window.StartIndex)
            .ToArray();
        return candidates.FirstOrDefault(window => window.Score >= 0.55);
    }

    private static SourceWindow BuildSourceWindow(SlimParagraph start, SlimDocument slim, string expectedText)
    {
        var paragraphs = slim.Paragraphs.Where(paragraph => paragraph.Index >= start.Index && paragraph.Index <= start.Index + 3).ToArray();
        var best = paragraphs.Take(1).ToArray();
        var score = TextCoverage(string.Join(" ", best.Select(paragraph => paragraph.Text)), expectedText);
        for (var length = 2; length <= paragraphs.Length; length++)
        {
            var window = paragraphs.Take(length).ToArray();
            var nextScore = TextCoverage(string.Join(" ", window.Select(paragraph => paragraph.Text)), expectedText);
            if (nextScore > score)
            {
                best = window;
                score = nextScore;
            }
        }
        return new SourceWindow(start.Index, best, string.Join(" ", best.Select(paragraph => paragraph.Text)), score);
    }

    private static double TextCoverage(string actual, string expected)
    {
        var actualTokens = Tokens(actual).ToHashSet(StringComparer.Ordinal);
        var expectedTokens = Tokens(expected).Where(token => token.Length > 1).ToHashSet(StringComparer.Ordinal);
        return expectedTokens.Count == 0 ? 0 : (double)expectedTokens.Count(actualTokens.Contains) / expectedTokens.Count;
    }

    private static bool CoversExpectedText(string actual, string expected) => TextCoverage(actual, expected) >= 0.75;

    private static IEnumerable<string> Tokens(string value) => Canonical(value)
        .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool StartsWithMarker(string? text, string marker)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var normalizedText = Canonical(text);
        var normalizedMarker = Canonical(marker);
        return normalizedText.StartsWith(normalizedMarker, StringComparison.Ordinal);
    }

    private static bool ContainsMarker(string? text, string marker) => !string.IsNullOrWhiteSpace(text) &&
        Canonical(text).Contains(Canonical(marker), StringComparison.Ordinal);

    private static string Canonical(string value) => new(value.Select(ch => char.IsLetterOrDigit(ch)
        ? char.ToUpperInvariant(ch)
        : ' ').ToArray());

    private sealed record ExpectedOccurrence(string StableId, string Family, string Marker, string SourceText,
        int Page, IReadOnlyList<string> SourceLineIds);

    private sealed record AuditRow(
        string ExpectedOccurrenceId, string Family, string Marker, string SourceText, int Page,
        IReadOnlyList<string> SourceLineIds, bool SourceFactExists, IReadOnlyList<int> SourceParagraphIndexes,
        IReadOnlyList<string> SourceParagraphStableIds, string? SourceParagraphText, bool Candidate,
        bool CandidatePartial, bool LegalStructuredBuilderEmits, bool Output, string Role, string Span, string Validator,
        string Grounding, string OutputStatus, string FirstLoss);

    private sealed record SourceWindow(int StartIndex, IReadOnlyList<SlimParagraph> Paragraphs, string Text, double Score);

    private sealed record AuditReport(
        int SchemaVersion, string ArtifactKind, bool UsesModel, bool ProductionChanges, string ExecutionContract,
        string ReferenceAuthority, string Mode, string AutoRoute, int MergedParagraphCount, bool AutoRouteCanActivate,
        int OutputHeadingCount, int ExpectedStructuralOccurrences, int SurvivedStructuralOccurrences,
        int MissingStructuralOccurrences, IReadOnlyList<string> SourceObservedOutsideFrozenAuthority,
        IReadOnlyDictionary<string, int> FirstLossCounts, IReadOnlyList<AuditRow> Rows);
}
