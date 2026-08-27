using System.Text;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// Uses a clean tagged-PDF tree as partial author evidence and supplements it with a generic,
/// consecutive labelled-number sequence. A sequence alone never activates this route: it must be
/// grounded beneath a clean declared structural tree in the same document.
/// </summary>
internal static class PdfTaggedEvidenceOutline
{
    public const string Basis = "pdf_tagged_structural";

    public static PdfTaggedEvidenceOutlineResult TryBuild(string inputPath, SlimDocument slim)
    {
        var pdf = PdfTextbookOutline.FindSiblingPdf(inputPath);
        if (pdf is null) return PdfTaggedEvidenceOutlineResult.NotApplicable("no-pdf");

        var tags = PdfTaggedHeadingProbe.Analyze(pdf, slim);
        var aligned = tags.Candidates.Where(c => c.DocxParagraphIndex is not null && c.HeadingSpan is not null).ToList();
        var levels = aligned.Select(c => c.Level).Distinct().OrderBy(x => x).ToList();
        // The route is deliberately narrow: automatic taggers commonly emit deep, dense /H* trees.
        if (aligned.Count < 5 || aligned.Count != tags.Candidates.Count || levels.Count > 2)
            return PdfTaggedEvidenceOutlineResult.NotApplicable(
                $"untrusted-tag-tree:{aligned.Count}/{tags.Candidates.Count},levels={levels.Count}");

        var titleByTaggedElement = PdfTaggedTitleGroundingProbe.Analyze(pdf, tags).Candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.GroundedTitle))
            .ToDictionary(candidate => (candidate.Page, candidate.Mcid), candidate => candidate.GroundedTitle!);
        if (titleByTaggedElement.Count != aligned.Count)
            return PdfTaggedEvidenceOutlineResult.NotApplicable(
                $"low-title-line-grounding:{titleByTaggedElement.Count}/{aligned.Count}");

        var headings = aligned.Select(candidate => ToHeading(candidate, slim, titleByTaggedElement[(candidate.Page, candidate.MarkedContentIdentifier)]))
            .OrderBy(h => h.Index).ThenBy(h => h.HeadingSpan!.Start).ToList();
        var markers = PdfRepeatedLabelMarkerProbe.Analyze(pdf).Series
            .Where(s => s.Markers.Count >= 3)
            .SelectMany(s => s.Markers)
            .OrderBy(m => m.Page).ThenByDescending(m => m.Y)
            .ToList();
        var added = AddGroundedMarkers(markers, headings, slim);
        headings.AddRange(added);
        headings = headings.OrderBy(h => h.Index).ThenBy(h => h.HeadingSpan!.Start).ToList();

        return new PdfTaggedEvidenceOutlineResult(true, headings,
            $"pdf={Path.GetFileName(pdf)}, tags={aligned.Count}, titleLines={titleByTaggedElement.Count}, sequentialMarkers={added.Count}");
    }

    private static HeadingRecord ToHeading(PdfTaggedHeadingCandidate candidate, SlimDocument slim, string title)
    {
        var index = candidate.DocxParagraphIndex!.Value;
        var paragraph = slim.Paragraphs.First(p => p.Index == index);
        var titleSpan = FindSourceSpan(paragraph.Text, title) ?? candidate.HeadingSpan!;
        return new HeadingRecord
        {
        Index = index,
        StableId = candidate.DocxStableId!,
        Level = candidate.Level,
        Text = DisplayTitle(paragraph.Text[titleSpan.Start..titleSpan.End]),
        OriginalText = paragraph.Text,
        HeadingSpan = titleSpan,
        BoundarySource = "PdfTaggedStructure",
        Source = HeadingSource.Structure,
        Confidence = 0.99,
        DecisionStatus = HeadingDecisionStatus.AutoAcceptedEvidence,
            ConfidenceBasis = Basis,
        };
    }

    private static List<HeadingRecord> AddGroundedMarkers(
        IReadOnlyList<PdfRepeatedLabelMarker> markers,
        IReadOnlyList<HeadingRecord> declared,
        SlimDocument slim)
    {
        var paragraphs = slim.Paragraphs
            .Where(p => p.Role != ParagraphRole.Empty && !p.InTableOfContents && !string.IsNullOrWhiteSpace(p.Text))
            .OrderBy(p => p.Index)
            .Select(p => new CanonParagraph(p, CanonicalMap.For(p.Text)))
            .ToList();
        var result = new List<HeadingRecord>();
        var cursor = 0;
        foreach (var marker in markers)
        {
            var needle = CanonicalMap.For(marker.Text).Canonical;
            if (needle.Length < 4) continue;
            var match = paragraphs.Where(p => p.Paragraph.Index >= cursor)
                .Select(p => new { Paragraph = p, At = p.Map.Canonical.IndexOf(needle, StringComparison.Ordinal) })
                .FirstOrDefault(x => x.At >= 0);
            if (match is null) continue;

            var start = match.Paragraph.Map.SourceIndexes[match.At];
            var end = match.Paragraph.Map.SourceIndexes[match.At + needle.Length - 1] + 1;
            if (declared.Concat(result).Any(h => h.StableId == match.Paragraph.Paragraph.StableId && h.HeadingSpan!.Start == start))
                continue;

            var parent = declared.Where(h => h.Index <= match.Paragraph.Paragraph.Index)
                .OrderByDescending(h => h.Index).ThenByDescending(h => h.HeadingSpan!.Start).FirstOrDefault();
            var level = parent is null ? declared.Min(h => h.Level) ?? 1 : Math.Min(9, (parent.Level ?? 0) + 1);
            result.Add(new HeadingRecord
            {
                Index = match.Paragraph.Paragraph.Index,
                StableId = match.Paragraph.Paragraph.StableId,
                Level = level,
                Text = DisplayTitle(match.Paragraph.Paragraph.Text[start..end]),
                OriginalText = match.Paragraph.Paragraph.Text,
                HeadingSpan = new TextOffsetSpan(start, end),
                BoundarySource = "PdfSequentialLabelMarker",
                StyleId = match.Paragraph.Paragraph.StyleId,
                Source = HeadingSource.Structure,
                Confidence = 0.97,
                DecisionStatus = HeadingDecisionStatus.AutoAcceptedEvidence,
                ConfidenceBasis = Basis,
            });
            cursor = match.Paragraph.Paragraph.Index;
        }
        return result;
    }

    private static TextOffsetSpan? FindSourceSpan(string source, string title)
    {
        var sourceMap = CanonicalMap.For(source);
        var titleCanon = CanonicalMap.For(title).Canonical;
        var at = sourceMap.Canonical.IndexOf(titleCanon, StringComparison.Ordinal);
        return at < 0
            ? null
            : new TextOffsetSpan(sourceMap.SourceIndexes[at], sourceMap.SourceIndexes[at + titleCanon.Length - 1] + 1);
    }

    private static string DisplayTitle(string source) => source
        .Replace('\u2010', '-')
        .Replace('\u2011', '-')
        .Replace('\u2012', '-')
        .Replace('\u2013', '-')
        .Replace('\u2014', '-')
        .Replace('\u2212', '-');

    private sealed record CanonParagraph(SlimParagraph Paragraph, CanonicalMap Map);
    private sealed record CanonicalMap(string Canonical, IReadOnlyList<int> SourceIndexes)
    {
        public static CanonicalMap For(string text)
        {
            var canonical = new StringBuilder(text.Length);
            var indexes = new List<int>(text.Length);
            for (var index = 0; index < text.Length; index++)
            {
                if (!char.IsLetterOrDigit(text[index])) continue;
                canonical.Append(char.ToLowerInvariant(text[index]));
                indexes.Add(index);
            }
            return new CanonicalMap(canonical.ToString(), indexes);
        }
    }
}

internal sealed record PdfTaggedEvidenceOutlineResult(bool Accepted, IReadOnlyList<HeadingRecord> Headings, string Reason)
{
    public static PdfTaggedEvidenceOutlineResult NotApplicable(string reason) => new(false, [], reason);
}
