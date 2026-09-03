using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// M10.5-A gold-free, model-free corpus discovery. This is deliberately an exposure survey, not
/// an outcome audit: it asks which documents independently exercise the existing
/// short_numbered + HasStructuralMarker interaction before anyone opens a key or reviews a line.
/// </summary>
public sealed class PdfTableLikeCrossDocumentDiscoveryProbe
{
    private const int CandidateBudget = 160;

    [Fact]
    public void Report()
    {
        var output = Environment.GetEnvironmentVariable("M10_5_A_REPORT");
        if (string.IsNullOrWhiteSpace(output)) return;

        var corpus = Path.Combine(RepositoryRoot(), "todo10_8", "heading_corpus_95_word");
        var rows = Directory.EnumerateFiles(corpus, "*.docx", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => Measure(corpus, path))
            .OrderByDescending(row => row.ShortNumberedStructuralMarkerLines)
            .ThenByDescending(row => row.AffectedCandidateBlocks)
            .ThenByDescending(row => row.CandidatePoolExceedsBudget)
            .ThenByDescending(row => row.CandidateCount)
            .ThenBy(row => row.RelativePath, StringComparer.Ordinal)
            .ToArray();

        var report = new DiscoveryReport(
            Contract: "gold_free_model_free_exposure_only",
            CandidateBudget,
            Documents: rows.Length,
            Rows: rows,
            SelectionRule: "Rank by short_numbered AND HasStructuralMarker exposure before any gold, manual outcome review, model call, or production counterfactual.");

        var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        File.WriteAllText(output, JsonSerializer.Serialize(report, options));
    }

    private static DiscoveryRow Measure(string corpus, string documentPath)
    {
        var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(documentPath);
        var annotations = snapshot.Annotations;
        var shortNumbered = annotations
            .Select((annotation, index) => new
            {
                annotation,
                index,
                IsShortNumbered = PdfLineBlockFilter.ClassifyTableLine(annotation.Line.Text) == "short_numbered",
                HasStructuralMarker = PdfLineBlockAnnotation.HasStructuralMarker(annotation.Line.Text),
            })
            .Where(item => item.IsShortNumbered)
            .ToArray();

        var structuralLineIndexes = shortNumbered.Where(item => item.HasStructuralMarker)
            .Select(item => item.index)
            .ToHashSet();
        var lineIndexByIdentity = annotations.Select((annotation, index) => (Identity: LineIdentity(annotation.Line), index))
            .ToDictionary(item => item.Identity, item => item.index, StringComparer.Ordinal);
        var affectedBlocks = snapshot.CandidateBlocks
            .Where(block => block.Lines.Any(line => lineIndexByIdentity.TryGetValue(LineIdentity(line), out var index) &&
                                                   structuralLineIndexes.Contains(index)))
            .ToArray();

        var contexts = PdfCandidateContextBuilder.Build(snapshot.CandidateBlocks, annotations);
        var ranked = PdfCandidateRanker.Rank(snapshot.CandidateBlocks, contexts)
            .ToDictionary(item => item.SourceId, StringComparer.Ordinal);
        var affectedIds = affectedBlocks.Select(block => block.Id).ToHashSet(StringComparer.Ordinal);

        return new DiscoveryRow(
            RelativePath: Path.GetRelativePath(corpus, documentPath).Replace(Path.DirectorySeparatorChar, '/'),
            CandidateCount: snapshot.CandidateBlocks.Count,
            CandidatePoolExceedsBudget: snapshot.CandidateBlocks.Count > CandidateBudget,
            ShortNumberedLines: shortNumbered.Length,
            ShortNumberedStructuralMarkerLines: structuralLineIndexes.Count,
            ShortNumberedWithoutStructuralMarkerLines: shortNumbered.Length - structuralLineIndexes.Count,
            AffectedCandidateBlocks: affectedBlocks.Length,
            AffectedBlocksWithTableScope: affectedIds.Count(id => contexts.TryGetValue(id, out var context) && context.Source.StructuralScope == "table"),
            AffectedBlocksWithTableScopePenalty: affectedIds.Count(id => ranked.TryGetValue(id, out var candidate) && candidate.NegativeSignals.Contains("table_scope")));
    }

    private static string LineIdentity(PdfLine line) => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"{line.Page}|{line.Y:R}|{line.Left:R}|{line.Right:R}|{line.Text}");

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DocxHeaderExtractor.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? AppContext.BaseDirectory;
    }

    private sealed record DiscoveryReport(
        string Contract,
        int CandidateBudget,
        int Documents,
        IReadOnlyList<DiscoveryRow> Rows,
        string SelectionRule);

    private sealed record DiscoveryRow(
        string RelativePath,
        int CandidateCount,
        bool CandidatePoolExceedsBudget,
        int ShortNumberedLines,
        int ShortNumberedStructuralMarkerLines,
        int ShortNumberedWithoutStructuralMarkerLines,
        int AffectedCandidateBlocks,
        int AffectedBlocksWithTableScope,
        int AffectedBlocksWithTableScopePenalty);
}
