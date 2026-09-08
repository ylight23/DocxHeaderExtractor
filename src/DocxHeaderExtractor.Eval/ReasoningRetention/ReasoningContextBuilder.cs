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
        int overlapOccurrences = 2,
        bool expandOwnedPerOccurrence = true)
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
        var baseSegments = totalCharacters <= maxContextCharacters
            ? [BuildSegment(occurrences, 0, lines.Length, 0, lines.Length)]
            : BuildWindows(occurrences, lines, windowCharacters, overlapOccurrences);
        var segments = ExpandOwnedCharacterScopes(
            baseSegments,
            occurrences,
            windowCharacters,
            expandOwnedPerOccurrence);

        var visibleIds = baseSegments.SelectMany(s => s.SourceOccurrenceIds).ToHashSet(StringComparer.Ordinal);
        var visibleCharacters = baseSegments.Sum(segment => segment.Text.Length);
        var overlapCharacters = Math.Max(0, visibleCharacters - totalCharacters);
        return new ReasoningContextPack
        {
            DocumentId = source.DocumentId,
            ContextStrategy = baseSegments.Count == 1 ? "SINGLE_FULL_CONTEXT" : "HIERARCHICAL_FULL_COVERAGE",
            SourceCharacters = sourceCharacters,
            ModelVisibleCharacters = visibleCharacters,
            SourceOccurrenceCoverage = occurrences.Length == 0 ? 1d : (double)visibleIds.Count / occurrences.Length,
            WindowCount = segments.Count,
            OverlapCharacters = overlapCharacters,
            GlobalConsolidationUsed = baseSegments.Count > 1,
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
        int end,
        int ownedStart,
        int ownedEnd,
        string? idSuffix = null)
    {
        var selected = occurrences.Skip(start).Take(end - start).ToArray();
        var owned = occurrences.Skip(ownedStart).Take(ownedEnd - ownedStart).ToArray();
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
            ContextSegmentId = $"segment-{start + 1}-{end}{idSuffix}",
            Ordinal = start + 1,
            SourceOccurrenceIds = selected.Select(o => o.SourceOccurrenceId).ToArray(),
            OwnedSourceOccurrenceIds = owned.Select(o => o.SourceOccurrenceId).ToArray(),
            VisibleStartOrdinal = selected.Length == 0 ? null : selected[0].SourceOrdinal,
            VisibleEndOrdinal = selected.Length == 0 ? null : selected[^1].SourceOrdinal,
            OwnedStartOrdinal = owned.Length == 0 ? null : owned[0].SourceOrdinal,
            OwnedEndOrdinal = owned.Length == 0 ? null : owned[^1].SourceOrdinal,
            Text = body.ToString(),
        };
    }

    private static IReadOnlyList<ReasoningContextSegment> BuildWindows(
        IReadOnlyList<ReasoningSourceOccurrence> occurrences,
        IReadOnlyList<string> lines,
        int windowCharacters,
        int overlapOccurrences)
    {
        var windows = new List<(int Start, int End)>();
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
            windows.Add((start, end));
            if (end == occurrences.Count) break;
            start = Math.Max(start + 1, end - Math.Max(0, overlapOccurrences));
        }

        var segments = new List<ReasoningContextSegment>(windows.Count);
        for (var index = 0; index < windows.Count; index++)
        {
            var window = windows[index];
            var ownedStart = window.Start;
            var ownedEnd = index + 1 < windows.Count ? windows[index + 1].Start : window.End;
            segments.Add(BuildSegment(
                occurrences,
                window.Start,
                window.End,
                ownedStart,
                ownedEnd,
                $"-owned-{ownedStart + 1}-{ownedEnd}"));
        }
        return segments;
    }

    private static IReadOnlyList<ReasoningContextSegment> ExpandOwnedCharacterScopes(
        IReadOnlyList<ReasoningContextSegment> segments,
        IReadOnlyList<ReasoningSourceOccurrence> occurrences,
        int windowCharacters,
        bool expandOwnedPerOccurrence)
    {
        var occurrenceById = occurrences.ToDictionary(item => item.SourceOccurrenceId, StringComparer.Ordinal);
        var expanded = new List<ReasoningContextSegment>();
        foreach (var segment in segments)
        {
            var oversized = segment.SourceOccurrenceIds
                .Select(id => occurrenceById[id])
                .Where(item => item.RawText.Length > windowCharacters)
                .ToArray();
            if (!expandOwnedPerOccurrence && oversized.Length == 0)
            {
                expanded.Add(segment);
                continue;
            }

            foreach (var occurrenceId in segment.OwnedSourceOccurrenceIds)
            {
                if (!occurrenceById.TryGetValue(occurrenceId, out var occurrence))
                    throw new InvalidOperationException($"reasoning-owned-source-occurrence-missing:{occurrenceId}");

                if (occurrence.RawText.Length > windowCharacters)
                {
                    expanded.AddRange(BuildCharacterSegments(occurrence, windowCharacters));
                    continue;
                }

                expanded.Add(segment with
                {
                    ContextSegmentId = $"{segment.ContextSegmentId}-owned-{occurrence.SourceOrdinal}",
                    OwnedSourceOccurrenceIds = [occurrence.SourceOccurrenceId],
                    OwnedSourceOccurrenceId = occurrence.SourceOccurrenceId,
                    OwnedStartCharacter = 0,
                    OwnedEndCharacter = occurrence.RawText.Length,
                    OwnedStartOrdinal = occurrence.SourceOrdinal,
                    OwnedEndOrdinal = occurrence.SourceOrdinal,
                });
            }
        }
        return expanded;
    }

    private static IReadOnlyList<ReasoningContextSegment> BuildCharacterSegments(
        ReasoningSourceOccurrence occurrence,
        int visibleBudget)
    {
        var ownedBudget = Math.Max(1, visibleBudget * 2 / 3);
        var haloBudget = Math.Max(0, visibleBudget - ownedBudget);
        var result = new List<ReasoningContextSegment>();
        var ordinal = 0;
        for (var ownedStart = 0; ownedStart < occurrence.RawText.Length; ownedStart += ownedBudget)
        {
            var ownedEnd = Math.Min(occurrence.RawText.Length, ownedStart + ownedBudget);
            var visibleStart = Math.Max(0, ownedStart - haloBudget);
            var visibleEnd = Math.Min(occurrence.RawText.Length, ownedEnd + haloBudget);
            var visibleText = occurrence.RawText[visibleStart..visibleEnd];
            var body = new StringBuilder();
            body.AppendLine("DOCUMENT_CONTEXT");
            body.AppendLine("SOURCE_OCCURRENCE");
            body.AppendLine(JsonSerializer.Serialize(new
            {
                occurrence.SourceOccurrenceId,
                occurrence.SourceId,
                occurrence.SourceOrdinal,
                rawText = visibleText,
                sourceSpan = new StructuralSpan(visibleStart, visibleEnd),
                ownedCharacterRange = new StructuralSpan(ownedStart, ownedEnd),
                occurrence.CandidateHint,
                occurrence.StyleFacts,
                occurrence.LayoutFacts,
                occurrence.NumberingFacts,
            }, JsonOptions));
            body.AppendLine("END_SOURCE_OCCURRENCE");
            body.Append("END_DOCUMENT_CONTEXT");
            ordinal++;
            result.Add(new ReasoningContextSegment
            {
                ContextSegmentId = $"segment-{occurrence.SourceOrdinal}-chars-{ownedStart}-{ownedEnd}",
                Ordinal = occurrence.SourceOrdinal * 1_000_000 + ordinal,
                SourceOccurrenceIds = [occurrence.SourceOccurrenceId],
                OwnedSourceOccurrenceIds = [occurrence.SourceOccurrenceId],
                VisibleStartOrdinal = occurrence.SourceOrdinal,
                VisibleEndOrdinal = occurrence.SourceOrdinal,
                OwnedStartOrdinal = occurrence.SourceOrdinal,
                OwnedEndOrdinal = occurrence.SourceOrdinal,
                OwnedSourceOccurrenceId = occurrence.SourceOccurrenceId,
                OwnedStartCharacter = ownedStart,
                OwnedEndCharacter = ownedEnd,
                VisibleStartCharacter = visibleStart,
                VisibleEndCharacter = visibleEnd,
                Text = body.ToString(),
            });
        }
        return result;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };
}
