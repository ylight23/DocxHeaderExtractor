using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Policy;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// Builds an evaluation-only document view. Every non-empty source occurrence is serialized;
/// candidate policy can annotate a row but can never remove it from context membership.
/// </summary>
public static class ReasoningContextBuilder
{
    public static ReasoningContextPack Build(
        SourceDocument source,
        DocxPolicyState policyState,
        int maxContextCharacters = 80_000,
        int windowCharacters = 48_000,
        int overlapOccurrences = 2)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(policyState);
        if (maxContextCharacters < 1 || windowCharacters < 1)
            throw new ArgumentOutOfRangeException(nameof(windowCharacters));

        var policyBySourceId = policyState.Paragraphs.ToDictionary(p => p.StableId, StringComparer.Ordinal);
        var occurrences = source.Paragraphs
            .Where(p => !string.IsNullOrWhiteSpace(p.Text))
            .Select(p =>
            {
                policyBySourceId.TryGetValue(p.SourceId, out var policy);
                var reasons = policy is null ? [] : EvidenceReasons(policy);
                return new ReasoningSourceOccurrence
                {
                    SourceOccurrenceId = $"{source.DocumentId}:{p.SourceId}:{p.SourceOrdinal}:{p.Text.Length}",
                    SourceId = p.SourceId,
                    SourceOrdinal = p.SourceOrdinal,
                    RawText = p.Text,
                    SourceSpan = new StructuralSpan(0, p.Text.Length),
                    CandidateHint = new CandidateHint(policy?.IsCandidate == true, policy?.Score ?? 0, reasons),
                    StyleFacts = new Dictionary<string, object?>
                    {
                        ["styleId"] = p.Style.StyleId,
                        ["styleName"] = p.Style.StyleName,
                        ["builtInHeadingStyleLevel"] = p.Style.BuiltInHeadingStyleLevel,
                        ["outlineLevel"] = p.Style.OutlineLevel,
                        ["bold"] = p.Style.Bold,
                        ["italic"] = p.Style.Italic,
                        ["underline"] = p.Style.Underline,
                        ["fontSizePt"] = p.Style.FontSizePt,
                        ["alignment"] = p.Style.Alignment,
                    },
                    LayoutFacts = new Dictionary<string, object?>
                    {
                        ["tableDepth"] = p.Layout.TableDepth,
                        ["sectionIndex"] = p.Layout.SectionIndex,
                        ["keepNext"] = p.Layout.KeepNext,
                        ["pageBreakBefore"] = p.Layout.PageBreakBefore,
                        ["inContentControl"] = p.Layout.InContentControl,
                        ["inTableOfContents"] = p.InTableOfContents,
                    },
                    NumberingFacts = new Dictionary<string, object?>
                    {
                        ["numberingId"] = p.Numbering.NumberingId,
                        ["numberingLevel"] = p.Numbering.NumberingLevel,
                        ["numberLabel"] = p.Numbering.NumberLabel,
                        ["numberingFormat"] = p.Numbering.NumberingFormat,
                        ["numberingStyleHeadingLevel"] = p.Numbering.NumberingStyleHeadingLevel,
                    },
                };
            }).ToArray();

        var lines = occurrences.Select(SerializeOccurrence).ToArray();
        var sourceCharacters = occurrences.Sum(o => o.RawText.Length);
        var totalCharacters = lines.Sum(line => line.Length + 1);
        var segments = totalCharacters <= maxContextCharacters
            ? [BuildSegment(occurrences, 0, lines.Length)]
            : BuildWindows(occurrences, lines, windowCharacters, overlapOccurrences);

        var visibleIds = segments.SelectMany(s => s.SourceOccurrenceIds).ToHashSet(StringComparer.Ordinal);
        var visibleCharacters = segments.Sum(segment => segment.Text.Length);
        var overlapCharacters = Math.Max(0, visibleCharacters - totalCharacters);
        return new ReasoningContextPack
        {
            DocumentId = source.DocumentId,
            ContextStrategy = segments.Count == 1 ? "SINGLE_FULL_CONTEXT" : "HIERARCHICAL_FULL_COVERAGE",
            SourceCharacters = sourceCharacters,
            ModelVisibleCharacters = visibleCharacters,
            SourceOccurrenceCoverage = occurrences.Length == 0 ? 1d : (double)visibleIds.Count / occurrences.Length,
            WindowCount = segments.Count,
            OverlapCharacters = overlapCharacters,
            GlobalConsolidationUsed = segments.Count > 1,
            Occurrences = occurrences,
            Segments = segments,
        };
    }

    public static string SerializeOccurrence(ReasoningSourceOccurrence occurrence) =>
        JsonSerializer.Serialize(new
        {
            occurrence.SourceOccurrenceId,
            occurrence.SourceId,
            occurrence.SourceOrdinal,
            occurrence.RawText,
            occurrence.SourceSpan,
            occurrence.CandidateHint,
            occurrence.StyleFacts,
            occurrence.LayoutFacts,
            occurrence.NumberingFacts,
        }, JsonOptions);

    private static IReadOnlyList<string> EvidenceReasons(DocxPolicyParagraph paragraph)
    {
        var reasons = new List<string>();
        if (paragraph.TrustedHeadingStyle) reasons.Add("BUILT_IN_HEADING_STYLE");
        if (paragraph.OutlineLevel is not null) reasons.Add("OUTLINE_LEVEL");
        if (paragraph.NumberingId is not null || paragraph.NumberLabel is not null) reasons.Add("NUMBERING");
        if (paragraph.Bold) reasons.Add("BOLD");
        if (paragraph.KeepNext) reasons.Add("KEEP_NEXT");
        if (paragraph.InTableOfContents) reasons.Add("TOC_MEMBERSHIP");
        return reasons;
    }

    private static ReasoningContextSegment BuildSegment(
        IReadOnlyList<ReasoningSourceOccurrence> occurrences,
        int start,
        int end)
    {
        var selected = occurrences.Skip(start).Take(end - start).ToArray();
        var body = new StringBuilder();
        body.AppendLine("DOCUMENT_CONTEXT");
        foreach (var occurrence in selected)
        {
            body.AppendLine("SOURCE_OCCURRENCE");
            body.AppendLine(SerializeOccurrence(occurrence));
            body.AppendLine("END_SOURCE_OCCURRENCE");
        }
        body.Append("END_DOCUMENT_CONTEXT");
        return new ReasoningContextSegment
        {
            ContextSegmentId = $"segment-{start + 1}-{end}",
            Ordinal = start + 1,
            SourceOccurrenceIds = selected.Select(o => o.SourceOccurrenceId).ToArray(),
            Text = body.ToString(),
        };
    }

    private static IReadOnlyList<ReasoningContextSegment> BuildWindows(
        IReadOnlyList<ReasoningSourceOccurrence> occurrences,
        IReadOnlyList<string> lines,
        int windowCharacters,
        int overlapOccurrences)
    {
        var segments = new List<ReasoningContextSegment>();
        var start = 0;
        while (start < occurrences.Count)
        {
            var end = start;
            var size = "DOCUMENT_CONTEXT\nEND_DOCUMENT_CONTEXT".Length;
            while (end < occurrences.Count)
            {
                var next = lines[end].Length + 45;
                if (end > start && size + next > windowCharacters) break;
                size += next;
                end++;
            }
            if (end == start) end++;
            segments.Add(BuildSegment(occurrences, start, end));
            if (end == occurrences.Count) break;
            start = Math.Max(start + 1, end - Math.Max(0, overlapOccurrences));
        }
        return segments;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };
}
