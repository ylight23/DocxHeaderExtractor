using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Application.Features;
using DocxHeaderExtractor.Core.Application.Policy;
using DocxHeaderExtractor.Core.Llm;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Vision;
using UglyToad.PdfPig;

namespace DocxHeaderExtractor.Core.Pipeline;

public sealed record PdfClusterProbeReport(
    [property: JsonPropertyName("input")] string Input,
    [property: JsonPropertyName("pdf")] string? Pdf,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("pages")] int Pages,
    [property: JsonPropertyName("lines")] int Lines,
    [property: JsonPropertyName("docxSignals")] DocxDeterministicSignalDto? DocxSignals,
    [property: JsonPropertyName("textCoverage")] PdfTextCoverageDto? TextCoverage,
    [property: JsonPropertyName("tocDictionary")] PdfTocDictionaryDto? TocDictionary,
    [property: JsonPropertyName("lineFilter")] PdfLineFilterSummaryDto? LineFilter,
    [property: JsonPropertyName("blockSummary")] PdfSemanticBlockSummaryDto? BlockSummary,
    [property: JsonPropertyName("blocks")] IReadOnlyList<PdfSemanticBlockDto> Blocks,
    [property: JsonPropertyName("candidateBlocks")] IReadOnlyList<PdfSemanticBlockDto> CandidateBlocks,
    [property: JsonPropertyName("bodyStyle")] PdfClusterStyleDto? BodyStyle,
    [property: JsonPropertyName("styleClusters")] IReadOnlyList<PdfStyleClusterStatsDto> StyleClusters,
    [property: JsonPropertyName("clusters")] IReadOnlyList<PdfClusterSampleDto> Clusters,
    [property: JsonPropertyName("decisions")] IReadOnlyList<PdfClusterDecisionDto> Decisions,
    [property: JsonPropertyName("blockDecisions")] IReadOnlyList<PdfBlockDecisionDto> BlockDecisions,
    [property: JsonPropertyName("visualBlockDecisions")] IReadOnlyList<PdfVisualBlockDecisionDto> VisualBlockDecisions,
    [property: JsonPropertyName("groundedHeadings")] IReadOnlyList<PdfGroundedBlockHeadingDto> GroundedHeadings,
    [property: JsonPropertyName("rejectedBlockHeadings")] IReadOnlyList<PdfRejectedBlockHeadingDto> RejectedBlockHeadings,
    [property: JsonPropertyName("visualAnalystRaw")] IReadOnlyList<string> VisualAnalystRaw,
    [property: JsonPropertyName("blockAnalystRaw")] IReadOnlyList<string> BlockAnalystRaw);

public sealed record PdfTextCoverageDto(
    [property: JsonPropertyName("pageTextChars")] int PageTextChars,
    [property: JsonPropertyName("letterChars")] int LetterChars,
    [property: JsonPropertyName("lineTextChars")] int LineTextChars,
    [property: JsonPropertyName("lineToLetterRatio")] double LineToLetterRatio,
    [property: JsonPropertyName("lineToPageTextRatio")] double LineToPageTextRatio,
    [property: JsonPropertyName("linesPerPage")] double LinesPerPage);

public sealed record DocxDeterministicSignalDto(
    [property: JsonPropertyName("paragraphs")] int Paragraphs,
    [property: JsonPropertyName("candidates")] int Candidates,
    [property: JsonPropertyName("styledHeadings")] int StyledHeadings,
    [property: JsonPropertyName("outlineLevelParagraphs")] int OutlineLevelParagraphs,
    [property: JsonPropertyName("numberedParagraphs")] int NumberedParagraphs,
    [property: JsonPropertyName("tableParagraphs")] int TableParagraphs,
    [property: JsonPropertyName("corruptParagraphs")] int CorruptParagraphs,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("status")] string Status);

public sealed record PdfTocDictionaryDto(
    [property: JsonPropertyName("tocPage")] int TocPage,
    [property: JsonPropertyName("entries")] int Entries,
    [property: JsonPropertyName("exactPageAnchors")] int ExactPageAnchors,
    [property: JsonPropertyName("relaxedPageAnchors")] int RelaxedPageAnchors,
    [property: JsonPropertyName("atOrAfterPageAnchors")] int AtOrAfterPageAnchors,
    [property: JsonPropertyName("items")] IReadOnlyList<PdfTocDictionaryItemDto> Items);

public sealed record PdfTocDictionaryItemDto(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("canonicalText")] string CanonicalText,
    [property: JsonPropertyName("exactAnchorPage")] int? ExactAnchorPage,
    [property: JsonPropertyName("relaxedAnchorPage")] int? RelaxedAnchorPage,
    [property: JsonPropertyName("atOrAfterAnchorPage")] int? AtOrAfterAnchorPage);

public sealed record PdfLineFilterSummaryDto(
    [property: JsonPropertyName("totalLines")] int TotalLines,
    [property: JsonPropertyName("semanticCandidateLines")] int SemanticCandidateLines,
    [property: JsonPropertyName("repeatedLines")] int RepeatedLines,
    [property: JsonPropertyName("headerFooterZoneLines")] int HeaderFooterZoneLines,
    [property: JsonPropertyName("tableLikeLines")] int TableLikeLines,
    [property: JsonPropertyName("pageNumberLines")] int PageNumberLines);

public sealed record PdfSemanticBlockSummaryDto(
    [property: JsonPropertyName("totalBlocks")] int TotalBlocks,
    [property: JsonPropertyName("singleLineBlocks")] int SingleLineBlocks,
    [property: JsonPropertyName("multiLineBlocks")] int MultiLineBlocks,
    [property: JsonPropertyName("maxLinesPerBlock")] int MaxLinesPerBlock);

public sealed record PdfSemanticBlockDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("lineCount")] int LineCount,
    [property: JsonPropertyName("style")] PdfClusterStyleDto Style,
    [property: JsonPropertyName("topY")] double TopY,
    [property: JsonPropertyName("bottomY")] double BottomY,
    [property: JsonPropertyName("left")] double Left,
    [property: JsonPropertyName("right")] double Right,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("sourceText")] string SourceText,
    [property: JsonPropertyName("canonicalText")] string CanonicalText);

public sealed record PdfClusterStyleDto(
    [property: JsonPropertyName("fontSize")] double FontSize,
    [property: JsonPropertyName("font")] string Font,
    [property: JsonPropertyName("color")] string Color);

public sealed record PdfStyleClusterStatsDto(
    [property: JsonPropertyName("style")] PdfClusterStyleDto Style,
    [property: JsonPropertyName("characters")] int Characters,
    [property: JsonPropertyName("lines")] int Lines,
    [property: JsonPropertyName("pages")] int Pages,
    [property: JsonPropertyName("titleLikeLines")] int TitleLikeLines,
    [property: JsonPropertyName("groupLikeLines")] int GroupLikeLines,
    [property: JsonPropertyName("averageFontSize")] double AverageFontSize,
    [property: JsonPropertyName("averageBoldRatio")] double AverageBoldRatio,
    [property: JsonPropertyName("examples")] IReadOnlyList<string> Examples);

public sealed record PdfClusterSampleDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("style")] PdfClusterStyleDto Style,
    [property: JsonPropertyName("lines")] int Lines,
    [property: JsonPropertyName("pages")] int Pages,
    [property: JsonPropertyName("characters")] int Characters,
    [property: JsonPropertyName("lineFilter")] PdfLineFilterSummaryDto LineFilter,
    [property: JsonPropertyName("examples")] IReadOnlyList<string> Examples);

public sealed record PdfClusterDecisionDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("reason")] string Reason);

public sealed record PdfBlockDecisionDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("reason")] string Reason);

public sealed record PdfVisualBlockDecisionDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("evidence")] string Evidence);

public sealed record PdfGroundedBlockHeadingDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("visualLevel")] int VisualLevel,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("sourceText")] string SourceText,
    [property: JsonPropertyName("canonicalText")] string CanonicalText,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("evidence")] string Evidence);

public sealed record PdfRejectedBlockHeadingDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("reason")] string Reason);

/// <summary>
/// Experimental PDF cluster probe: reads a PDF, learns visual style clusters, and optionally asks
/// the LLM semantic analyst to classify clusters. It does not emit headings and is not used by the
/// production extractor.
/// </summary>
public static class PdfClusterProbe
{
    public static async Task<PdfClusterProbeReport> RunAsync(
        string inputPath,
        IHeaderClassifier? analyst = null,
        IPdfVisualQuestion? visualAnalyst = null,
        int visualDpi = 120,
        CancellationToken ct = default)
    {
        var docxSignals = TryReadDocxSignals(inputPath);
        var pdf = ResolvePdf(inputPath);
        if (pdf is null)
            return Empty(inputPath, "no-pdf", "Không tìm thấy PDF cùng stem hoặc input không phải PDF.", null, docxSignals);

        IReadOnlyList<PdfLine> lines;
        PdfTextCoverageDto coverage;
        try
        {
            using var doc = PdfDocument.Open(pdf);
            lines = PdfLineExtraction.ExtractLines(doc);
            coverage = MeasureCoverage(doc, lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Empty(inputPath, "pdf-read-failed", ex.Message, pdf, docxSignals);
        }

        if (lines.Count == 0)
            return Empty(inputPath, "no-lines", "PDF không có dòng text đọc được.", pdf, docxSignals);

        var profile = PdfStyleClusterProfile.Learn(lines);
        var tocDictionary = PdfTocDictionaryProbe.Analyze(lines);
        var annotations = PdfLineBlockFilter.Analyze(lines);
        var excluded = annotations
            .Where(a => a.ExcludeFromSemanticSamples)
            .Select(a => a.Line)
            .ToHashSet();
        var blocks = PdfSemanticBlockGrouper.Build(annotations);
        var samples = PdfSemanticClusterAnalyst.BuildSamples(profile, lines, excluded);
        var decisions = Array.Empty<PdfClusterDecisionDto>();
        var candidateBlocks = PdfLayoutEvidenceOutline.BuildBroadCandidates(blocks, profile)
            .Take(120)
            .ToArray();
        var blockDecisions = Array.Empty<PdfBlockDecisionDto>();
        var visualBlockDecisions = Array.Empty<PdfVisualBlockDecisionDto>();
        var groundedHeadings = Array.Empty<PdfGroundedBlockHeadingDto>();
        var rejectedBlockHeadings = Array.Empty<PdfRejectedBlockHeadingDto>();
        var visualAnalystRaw = Array.Empty<string>();
        var blockAnalystRaw = Array.Empty<string>();
        if (analyst is not null && samples.Count > 0)
        {
            var analysis = await PdfSemanticClusterAnalyst.AnalyzeAsync(analyst, profile, lines, ct);
            decisions = analysis.Decisions.Select(ToDto).ToArray();
            var blockAnalysis = await PdfBlockAnalyst.AnalyzeAsync(analyst, candidateBlocks, ct: ct);
            blockDecisions = blockAnalysis.Decisions.Select(ToDto).ToArray();
            var grounding = PdfBlockGrounder.Ground(
                candidateBlocks,
                blockAnalysis.Decisions,
                profile,
                samples,
                analysis.Decisions);
            groundedHeadings = grounding.Headings.Select(ToDto).ToArray();
            rejectedBlockHeadings = grounding.Rejected.Select(ToDto).ToArray();
            blockAnalystRaw = blockAnalysis.RawResponses.Select(SafeRaw).ToArray();
        }
        if (visualAnalyst is not null && candidateBlocks.Length > 0)
        {
            var visual = await PdfVisualBlockAnalyst.AnalyzeAsync(
                visualAnalyst,
                pdf,
                candidateBlocks,
                lines,
                visualDpi,
                ct: ct);
            visualBlockDecisions = visual.Decisions.Select(ToDto).ToArray();
            visualAnalystRaw = visual.RawResponses.Select(SafeRaw).ToArray();
        }

        return new PdfClusterProbeReport(
            inputPath,
            pdf,
            "ok",
            analyst is null ? "deterministic-cluster-samples" : "deterministic-clusters-with-llm-analyst",
            lines.Select(l => l.Page).Distinct().Count(),
            lines.Count,
            docxSignals,
            coverage,
            ToDto(tocDictionary),
            ToDto(PdfLineBlockFilter.Summarize(annotations)),
            ToDto(PdfSemanticBlockGrouper.Summarize(blocks)),
            blocks.Take(80).Select(ToDto).ToArray(),
            candidateBlocks.Select(ToDto).ToArray(),
            ToDto(profile.BodyStyle),
            profile.Clusters.Select(c => ToDto(c, lines)).ToArray(),
            samples.Select(s => ToDto(s, annotations)).ToArray(),
            decisions,
            blockDecisions,
            visualBlockDecisions,
            groundedHeadings,
            rejectedBlockHeadings,
            visualAnalystRaw,
            blockAnalystRaw);
    }

    private static string? ResolvePdf(string inputPath)
    {
        if (Path.GetExtension(inputPath).Equals(".pdf", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(inputPath))
            return Path.GetFullPath(inputPath);

        return PdfTextbookOutline.FindSiblingPdf(inputPath);
    }

    private static PdfClusterProbeReport Empty(
        string input,
        string status,
        string reason,
        string? pdf = null,
        DocxDeterministicSignalDto? docxSignals = null) =>
        new(input, pdf, status, reason, 0, 0, docxSignals, null, null, null, null, [], [], null, [], [], [], [], [], [], [], [], []);

    private static DocxDeterministicSignalDto? TryReadDocxSignals(string inputPath)
    {
        if (!Path.GetExtension(inputPath).Equals(".docx", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(inputPath))
            return null;

        try
        {
            var source = new OpenXmlDocumentSource().Read(inputPath);
            var features = NumberingStyleFeatures.FromSourceDocument(source);
            var policyState = DocxPolicyStateBuilder.Build(
                source, features, new DocumentFeatureDeriver().Derive(source), new ExtractionOptions());
            var paragraphs = policyState.Paragraphs.Where(p => p.Role != ParagraphRole.Empty).ToList();
            var mode = policyState.Mode ?? DocumentModeClassifier.Measure(
                policyState.Paragraphs.Cast<IPolicyParagraph>().ToArray());
            return new DocxDeterministicSignalDto(
                paragraphs.Count,
                policyState.Candidates.Count(),
                paragraphs.Count(p => p.TrustedHeadingStyle || p.Role == ParagraphRole.StyledHeading),
                paragraphs.Count(p => p.OutlineLevel is not null),
                paragraphs.Count(p => p.NumberingId is not null || p.NumberingLevel is not null),
                paragraphs.Count(p => p.TableDepth > 0),
                paragraphs.Count(p => p.Corrupt),
                mode.Mode.ToString(),
                mode.Status.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new DocxDeterministicSignalDto(0, 0, 0, 0, 0, 0, 0, "unknown", $"docx-read-failed:{ex.Message}");
        }
    }

    private static PdfTextCoverageDto MeasureCoverage(PdfDocument doc, IReadOnlyList<PdfLine> lines)
    {
        var pages = doc.GetPages().ToList();
        var pageTextChars = pages.Sum(p => CountNonWhitespace(p.Text));
        var letterChars = pages.Sum(p => p.Letters.Sum(l => CountNonWhitespace(l.Value)));
        var lineTextChars = lines.Sum(l => CountNonWhitespace(l.Text));
        var pageCount = pages.Count;
        return new PdfTextCoverageDto(
            pageTextChars,
            letterChars,
            lineTextChars,
            letterChars == 0 ? 0 : lineTextChars / (double)letterChars,
            pageTextChars == 0 ? 0 : lineTextChars / (double)pageTextChars,
            pageCount == 0 ? 0 : lines.Count / (double)pageCount);
    }

    private static PdfTocDictionaryDto? ToDto(PdfTocDictionaryProbeResult result) =>
        result.Entries == 0
            ? null
            : new PdfTocDictionaryDto(
                result.TocPage,
                result.Entries,
                result.ExactPageAnchors,
                result.RelaxedPageAnchors,
                result.AtOrAfterPageAnchors,
                result.Items.Select(i => new PdfTocDictionaryItemDto(
                    i.Title,
                    i.Page,
                    i.CanonicalText,
                    i.ExactAnchorPage,
                    i.RelaxedAnchorPage,
                    i.AtOrAfterAnchorPage)).ToArray());

    private static int CountNonWhitespace(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : text.Count(c => !char.IsWhiteSpace(c));

    private static PdfClusterSampleDto ToDto(
        PdfSemanticClusterSample sample,
        IReadOnlyList<PdfLineBlockAnnotation> annotations) =>
        new(
            sample.Id,
            ToDto(sample.Style),
            sample.Lines,
            sample.Pages,
            sample.Characters,
            ToDto(PdfLineBlockFilter.Summarize(
                annotations.Where(a => PdfStyleClusterProfile.StyleOf(a.Line) == sample.Style))),
            sample.Examples);

    private static PdfLineFilterSummaryDto ToDto(PdfLineFilterSummary summary) =>
        new(
            summary.TotalLines,
            summary.SemanticCandidateLines,
            summary.RepeatedLines,
            summary.HeaderFooterZoneLines,
            summary.TableLikeLines,
            summary.PageNumberLines);

    private static PdfSemanticBlockSummaryDto ToDto(PdfSemanticBlockSummary summary) =>
        new(
            summary.TotalBlocks,
            summary.SingleLineBlocks,
            summary.MultiLineBlocks,
            summary.MaxLinesPerBlock);

    private static PdfSemanticBlockDto ToDto(PdfSemanticBlock block) =>
        new(
            block.Id,
            block.Page,
            block.LineCount,
            ToDto(block.PrimaryStyle),
            Math.Round(block.TopY, 1),
            Math.Round(block.BottomY, 1),
            Math.Round(block.Left, 1),
            Math.Round(block.Right, 1),
            block.DisplayText,
            block.Text,
            block.CanonicalText);

    private static PdfClusterDecisionDto ToDto(PdfSemanticClusterDecision decision) =>
        new(decision.Id, RoleName(decision.Role), decision.Confidence, decision.Reason);

    private static PdfBlockDecisionDto ToDto(PdfBlockDecision decision) =>
        new(decision.Id, RoleName(decision.Role), decision.Confidence, decision.Reason);

    private static PdfVisualBlockDecisionDto ToDto(PdfVisualBlockDecision decision) =>
        new(decision.Id, RoleName(decision.Role), Math.Round(decision.Confidence, 3), decision.Evidence);

    private static PdfGroundedBlockHeadingDto ToDto(PdfGroundedBlockHeading heading) =>
        new(
            heading.Id,
            heading.Page,
            heading.VisualLevel,
            heading.Text,
            heading.SourceText,
            heading.CanonicalText,
            heading.Confidence,
            heading.Evidence);

    private static PdfRejectedBlockHeadingDto ToDto(PdfRejectedBlockHeading rejected) =>
        new(rejected.Id, rejected.Role, rejected.Confidence, rejected.Reason);

    private static PdfClusterStyleDto ToDto(PdfStyleKey style) =>
        new(style.FontSizeBucket, style.FontName, style.FillColorKey);

    private static PdfStyleClusterStatsDto ToDto(PdfStyleClusterStats stats, IReadOnlyList<PdfLine> lines) =>
        new(
            ToDto(stats.Style),
            stats.Characters,
            stats.Lines,
            stats.Pages,
            stats.TitleLikeLines,
            stats.GroupLikeLines,
            Math.Round(stats.AverageFontSize, 2),
            Math.Round(stats.AverageBoldRatio, 3),
            lines
                .Where(l => PdfStyleClusterProfile.StyleOf(l) == stats.Style)
                .OrderBy(l => l.Page)
                .ThenByDescending(l => l.Y)
                .Select(l => PdfTextUtilities.HeadingReadable(l.Text))
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Take(8)
                .ToArray());

    private static string RoleName(PdfSemanticClusterRole role) => role switch
    {
        PdfSemanticClusterRole.HeadingTopic => "heading_topic",
        PdfSemanticClusterRole.BodySentence => "body_sentence",
        PdfSemanticClusterRole.TableOrChartLabel => "table_or_chart_label",
        _ => "uncertain",
    };

    private static string RoleName(PdfBlockRole role) => role switch
    {
        PdfBlockRole.HeadingTopic => "heading_topic",
        PdfBlockRole.ListItem => "list_item",
        PdfBlockRole.BodySentence => "body_sentence",
        PdfBlockRole.TableOrChartLabel => "table_or_chart_label",
        PdfBlockRole.DecorativeNoise => "decorative_noise",
        _ => "uncertain",
    };

    private static string SafeRaw(string raw)
    {
        raw = raw.Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
        return raw.Length <= 2000 ? raw : raw[..2000] + "...";
    }
}
